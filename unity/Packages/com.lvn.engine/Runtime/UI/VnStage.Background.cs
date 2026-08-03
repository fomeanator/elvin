using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The scene backdrop: self-healing sprite acquisition (retry + reconnect
    /// wake), bg apply with generation guards, the last-scene memory and the
    /// CG gallery unlocks.
    /// </summary>
    public sealed partial class VnStage
    {
        // ── scene-critical sprite acquisition ────────────────────────────────
        // A backdrop or actor layer is not an optional decoration: if its fetch
        // hits a bad moment (mobile networks flap for seconds at a time — live
        // field case: a mid-warm connection reset pinned the offline flag for
        // 2s and the chapter played on a black stage forever), the element must
        // keep trying and dress itself the moment the world allows. Exponential
        // backoff, an instant wake on the offline→online transition, and a
        // stillWanted predicate so a superseded element never zombie-applies.
        private async Task<Sprite> LoadSceneSpriteAsync(string url, string what, Func<bool> stillWanted)
        {
            const int MaxAttempts = 8; // backoff sums to ~2 min — a real outage, not a flap
            string lastErr = null;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (Assets == null || _cts == null || _cts.IsCancellationRequested || !stillWanted()) return null;
                try
                {
                    var s = await Assets.LoadSpriteAsync(url, _cts.Token);
                    if (s != null)
                    {
                        if (attempt > 1) Debug.Log($"[stage] {what} {url} recovered (attempt {attempt})");
                        return s;
                    }
                    lastErr = "no data (404 or decode failed)";
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex) { lastErr = ex.Message; }
                if (attempt == MaxAttempts) break;
                float delay = Lvn.Content.LvnBackoff.DelaySeconds(attempt + 1);
                Debug.LogWarning($"[stage] {what} {url} unavailable (attempt {attempt}): {lastErr} — retry in {delay:F0}s or on reconnect");
                await WaitRetryWindowAsync(delay);
            }
            Debug.LogWarning($"[stage] {what} {url} gave up after {MaxAttempts} attempts: {lastErr}");
            return null;
        }

        // The backoff delay, cut short the instant connectivity returns — the
        // scene re-dresses within a frame of the network healing instead of
        // sitting out the rest of a 30s backoff window.
        private async Task WaitRetryWindowAsync(float seconds)
        {
            var wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<bool> onChange = online => { if (online) wake.TrySetResult(true); };
            Lvn.Content.LvnNetworkStatus.Changed += onChange;
            try
            {
                await Task.WhenAny(
                    Task.Delay(TimeSpan.FromSeconds(Math.Max(0.5f, seconds)), _cts.Token),
                    wake.Task);
            }
            catch (OperationCanceledException) { }
            finally { Lvn.Content.LvnNetworkStatus.Changed -= onChange; }
        }

        // Monotonic backdrop generation: a retrying older bg must never paint
        // over a newer one that already landed (or is in flight).
        private int _bgGen;
        private Lvn3DSetAsset _active3DSet;
        private string _active3DSetId;

        private void ReleaseActive3DSet()
        {
            _active3DSet?.Dispose();
            _active3DSet = null;
            _active3DSetId = null;
        }

        private async Task ApplyBgAsync(JObject cmd)
        {
            // Путь фона тоже с подстановкой: `bg sprite_url="/bg/{погода}_двор.jpg"`.
            // Это была задокументированная грабля («в bg sprite_url не
            // интерполируется»), из-за которой смена времени суток писалась
            // ветками на каждый вариант.
            var url = (string)cmd["sprite_url"];
            if (!string.IsNullOrEmpty(url) && url.IndexOf('{') >= 0)
                url = TextInterpolation.Apply(url, _player?.Vars);
            // bg id="porch" — resolve the catalog entity to its (first) layer url.
            if (string.IsNullOrEmpty(url))
            {
                var id = (string)cmd["id"];
                if (Catalog != null && Catalog.Has(id))
                {
                    var urls = Catalog.Resolve(id, AxesFrom(cmd), CatalogCond());
                    if (urls.Count > 0) url = urls[0];
                }
            }
            if (string.IsNullOrEmpty(url)) return;
            // Remember the latest scene backdrop across scenes/sessions — the
            // hub wardrobe reopens "where the player last was" on this canvas.
            PlayerPrefs.SetString(LastBgKey, url);
            // The script reached this bg — that's the unlock moment, independent of
            // whether the sprite itself loads (a cache miss doesn't unsee the CG).
            UnlockGalleryFor(url);
            if (Assets == null) return;
            int epoch = _stageEpoch;
            int gen = ++_bgGen;
            var sprite = await LoadSceneSpriteAsync(url, "bg",
                () => StageCurrent(epoch) && _bgGen == gen);
            // Молчаливый выход здесь и есть «фон иногда не догружается»: сцена
            // остаётся с прежним задником, и в логе НИЧЕГО. Разделяем два случая:
            // картинка не пришла (сеть/404/кэш) и её обогнала более новая команда.
            if (sprite == null)
            {
                LvnPlayer.Log?.Invoke($"[lvn-bg] MISS {url} (спрайт не загрузился; фон остался прежним)");
                return;
            }
            if (!StageCurrent(epoch) || _bgGen != gen)
            {
                LvnPlayer.Log?.Invoke($"[lvn-bg] skip {url} (обогнала смена главы/следующий bg)");
                return; // a chapter change / newer bg won
            }
            LvnPlayer.Log?.Invoke($"[lvn-bg] ok {url}");
            _renderer?.SetBackground(sprite);
            ReleaseActive3DSet();
            HasBackdrop = true; // the entry reveal (host) waits for the first one
        }

        /// <summary>`bg3d` — stand a built 3D set behind the scene and frame it.
        /// The set replaces painted backgrounds until `bg3d off` (or the next
        /// ordinary `bg`), and every later `bg3d` on the same set just moves the
        /// camera: one built room gives as many angles as the story asks for.
        ///
        /// <para>Degrades quietly: without a prefab loader (or on the UI Toolkit
        /// path) the scene keeps the background it already had, so a script
        /// written for 3D still plays.</para></summary>
        private async Task ApplyBg3DAsync(JObject cmd)
        {
            if (BoolOr(cmd["off"], false) || (string)cmd["id"] == "off")
            {
                ++_bgGen; // supersede a bundle still downloading
                _renderer?.Set3DBackdrop(null);
                ReleaseActive3DSet();
                return;
            }

            // `build=1` — ПУСТАЯ сцена под сборку из скрипта: дальше её
            // наполняют `o3d` и `light`. Так 3D перестаёт требовать Unity:
            // место в игре можно описать текстом целиком.
            if (BoolOr(cmd["build"], false)) _renderer?.Build3D();

            var id = (string)cmd["id"] ?? (string)cmd["prefab"] ?? (string)cmd["scene"];
            if (!string.IsNullOrEmpty(id))
            {
                // The built-in room: `bg3d id=demo` shows the feature working
                // before a project owns any 3D art. No loader, no assets.
                if (id == Lvn.UI.World.Lvn3DDemoSet.Id)
                {
                    _renderer?.Set3DBackdrop(Lvn.UI.World.Lvn3DDemoSet.Build());
                    ReleaseActive3DSet();
                    _active3DSetId = id;
                    HasBackdrop = true;
                    _renderer?.Frame3D(
                        Num(cmd["x"]), Num(cmd["y"]), Num(cmd["z"]),
                        Num(cmd["pitch"]), Num(cmd["yaw"]), Num(cmd["fov"]),
                        Num(cmd["dur"]) ?? 0f);
                    return;
                }
                // Repeating the current id is a camera cut, not a second bundle
                // load. This is the authored "one room, many angles" loop.
                if (_active3DSetId != id)
                {
                    // Каждый выход отсюда — это «3D-фон не догрузился»: сцена
                    // остаётся с прежним задником. Раньше все они были немыми.
                    if (Assets == null || !UseCanvasScene)
                    {
                        LvnPlayer.Log?.Invoke($"[lvn-bg3d] SKIP '{id}': " +
                            (Assets == null ? "нет провайдера ассетов" : "рендерер не Canvas (3D только на нём)"));
                        return;
                    }
                    LvnPlayer.Log?.Invoke($"[lvn-bg3d] load '{id}' (был '{_active3DSetId ?? "—"}')");
                    int epoch = _stageEpoch;
                    int gen = ++_bgGen;
                    Lvn3DSetAsset loaded = null;
                    try { loaded = await Assets.Load3DSetAsync(id, _cts.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"[stage] 3D set '{id}' не загрузился: {e.Message}");
                        LvnPlayer.Log?.Invoke($"[lvn-bg3d] FAIL '{id}': {e.Message}");
                    }
                    if (loaded?.Prefab == null)
                    {
                        LvnPlayer.Log?.Invoke($"[lvn-bg3d] MISS '{id}': " +
                            (loaded == null ? "набор не найден (каталог sets3d / бандл / fallback)"
                                            : "бандл пришёл, но префаб внутри пустой"));
                        loaded?.Dispose();
                        return; // keep the flat background
                    }
                    if (!StageCurrent(epoch) || _bgGen != gen)
                    {
                        LvnPlayer.Log?.Invoke($"[lvn-bg3d] skip '{id}': обогнала смена главы/следующий фон");
                        loaded.Dispose(); // a newer background won while this downloaded
                        return;
                    }

                    var old = _active3DSet;
                    _renderer?.Set3DBackdrop(loaded.Prefab); // instantiate before releasing old assets
                    _active3DSet = loaded;
                    _active3DSetId = id;
                    old?.Dispose();
                    HasBackdrop = true;
                    Debug.Log($"[stage] 3D set '{id}' ready ({(loaded.Remote ? "server/cache" : "bundled fallback")})");
                    LvnPlayer.Log?.Invoke($"[lvn-bg3d] ok '{id}' ({(loaded.Remote ? "сервер/кэш" : "встроенный fallback")})");
                }
            }

            // `live=` overrides how the set is filmed: on for motion the engine
            // can't see (a shader that scrolls water), off to pin a still shot.
            // Расфокус дальнего плана — главный кинематографический признак:
            // объектив держит резкой одну плоскость, остальное мягко тает.
            if (cmd["focus"] != null || cmd["dof"] != null || cmd["dof_range"] != null)
                _renderer?.Dof3D(Num(cmd["focus"]), Num(cmd["dof_range"]), Num(cmd["dof"]));

            // ТОНАЛЬНАЯ КОМПРЕССИЯ. Экран умеет одну яркость — «белое», а свет
            // в сцене потолка не имеет. Кривая переводит второе в первое мягко:
            // яркое подводится к белому плавно и сохраняет форму, вместо того
            // чтобы срезаться в плоское пятно. Выключена по умолчанию — уже
            // собранные новеллы не должны поменяться от обновления движка.
            if (Num(cmd["shadows"]) is float shd) _renderer?.Shadows3D(shd);

            if (cmd["bloom"] != null || cmd["bloom_threshold"] != null || cmd["bloom_knee"] != null)
                _renderer?.Bloom3D(Num(cmd["bloom"]), Num(cmd["bloom_threshold"]), Num(cmd["bloom_knee"]));

            if (cmd["tone"] != null || cmd["exposure"] != null || cmd["saturation"] != null
                || cmd["contrast"] != null || cmd["dither"] != null
                || cmd["knee"] != null || cmd["white"] != null)
            {
                Lvn.UI.World.Lvn3DPostStack.Tone? tone = null;
                switch (((string)cmd["tone"] ?? "").Trim().ToLowerInvariant())
                {
                    case "neutral": case "мягкая": tone = Lvn.UI.World.Lvn3DPostStack.Tone.Neutral; break;
                    case "khronos": tone = Lvn.UI.World.Lvn3DPostStack.Tone.Khronos; break;
                    case "off": case "выкл": tone = Lvn.UI.World.Lvn3DPostStack.Tone.Off; break;
                }
                _renderer?.Tone3D(tone, Num(cmd["exposure"]), Num(cmd["saturation"]),
                                  Num(cmd["contrast"]), Num(cmd["dither"]),
                                  Num(cmd["knee"]), Num(cmd["white"]));
            }
            // СТИЛЬ СЦЕНЫ. Ободок и тёплая кайма — то, что делает кадр
            // «нарисованным», и они же первыми выжигают ночь: их вклад не
            // зависит от источников света и складывается поверх. Дневной сцене
            // нужен один набор, ночной — другой, поэтому профиль живёт в
            // скрипте, а не в коде движка.
            if (cmd["rim"] != null || cmd["warm"] != null || cmd["steps"] != null
                || cmd["shadow_tint"] != null || cmd["rim_color"] != null)
            {
                var pf = Lvn.UI.World.Lvn3DStyle.Current;
                if (Num(cmd["rim"]) is float rim) pf.RimStrength = rim;
                if (Num(cmd["warm"]) is float warm) pf.WarmEdge = warm;
                if (Num(cmd["steps"]) is float st) pf.Steps = st;
                if (Col(cmd["shadow_tint"]) is Color sc) pf.ShadowTint = sc;
                if (Col(cmd["rim_color"]) is Color rc) pf.RimColor = rc;
                Lvn.UI.World.Lvn3DStyle.SetProfile(pf);
            }

            // `stats=1` — числа сцены поверх кадра, прямо на устройстве.
            if (cmd["stats"] != null) _renderer?.Stats3D(BoolOr(cmd["stats"], true));
            if (cmd["live"] != null) _renderer?.Set3DLive(BoolOr(cmd["live"], true));
            // `sway` — съёмка с рук: кадр перестаёт быть фотографией, а спрайты,
            // привязанные к сцене через `world`, едут вместе с ним. Ставится
            // ПОСЛЕ загрузки набора: постановка набора возвращает камеру в покой.
            // `walk=1` — свободная ходьба по набору: WASD и экранный джойстик.
            // Нужна и автору (подобрать ракурс, пройдясь по сцене), и игроку
            // там, где место само по себе содержание.
            if (cmd["walk"] != null)
            {
                bool on = BoolOr(cmd["walk"], false);
                _renderer?.SetWalk3D(on);
                SetWalkEnabled(on);
            }
            if (cmd["sway"] != null || cmd["sway_speed"] != null)
                _renderer?.Set3DSway(Num(cmd["sway"]), Num(cmd["sway_speed"]));

            // Framing rides on the same op: `bg3d x=… yaw=…` without an id moves
            // the camera of the set already standing.
            _renderer?.Frame3D(
                Num(cmd["x"]), Num(cmd["y"]), Num(cmd["z"]),
                Num(cmd["pitch"]), Num(cmd["yaw"]), Num(cmd["fov"]),
                Num(cmd["dur"]) ?? 0f);
        }

        /// <summary>A command number, or null when the author left the field out —
        /// "unset" has to survive as a distinct value so framing keeps what it had.
        ///
        /// <para>Идёт через общий разборщик чисел: кадр задаётся выражениями не
        /// реже, чем спрайт (<c>bg3d pitch={кам_боль}</c> — камера клюёт от
        /// удара), а примитивное приведение молча роняло такую строку в null, и
        /// команда тихо ничего не делала.</para></summary>
        private float? Num(JToken t) => NumOrNull(t, _player?.Vars);

        private const string LastBgKey = "lvn_last_bg";

        /// <summary>The most recent scene backdrop url shown on ANY stage —
        /// persisted, so a hub-opened wardrobe can dress its canvas with the
        /// scene the player last saw. Empty when nothing has been staged yet.</summary>
        public static string LastSceneBgUrl => PlayerPrefs.GetString(LastBgKey, "");

        /// <summary>True once the CURRENT scene has an applied background — the
        /// host holds its opaque chapter loader until this flips, so the fade
        /// always reveals a dressed stage, never a black frame.</summary>
        public bool HasBackdrop { get; private set; }

        /// <summary>The title's curated CG list (manifest title.gallery), set by the
        /// host per chapter entry. Non-empty ⇒ the quick menu shows a Gallery item;
        /// a shown <c>bg</c> whose url matches an item unlocks it forever.</summary>
        public System.Collections.Generic.IReadOnlyList<Lvn.Content.LvnGalleryItem> Gallery { get; set; }

        private void UnlockGalleryFor(string url)
        {
            if (Gallery == null) return;
            foreach (var g in Gallery)
                if (g != null && g.url == url)
                    LvnGalleryStore.Unlock(_saveTitleId, g.id);
        }

        // Evaluates a layer's `when` condition against the player's vars, so a
        // conditional sprite layer appears only when its expression holds.
        private System.Func<string, bool> CatalogCond() => expr =>
        {
            if (_player == null || string.IsNullOrEmpty(expr)) return false;
            try { return LvnExpression.EvaluateBool(expr, _player.Vars); }
            catch { return false; }
        };
    }
}
