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
                        if (attempt > 1) LvnLog.Trace($"[lvn-stage] {what} {url} recovered (attempt {attempt})");
                        return s;
                    }
                    lastErr = "no data (404 or decode failed)";
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex) { lastErr = ex.Message; }
                if (attempt == MaxAttempts) break;
                float delay = Lvn.Content.LvnBackoff.DelaySeconds(attempt + 1);
                Debug.LogWarning($"[lvn-stage] {what} {url} unavailable (attempt {attempt}): {lastErr} — retry in {delay:F0}s or on reconnect");
                await WaitRetryWindowAsync(delay);
            }
            Debug.LogWarning($"[lvn-stage] {what} {url} gave up after {MaxAttempts} attempts: {lastErr}");
            return null;
        }

        // The backoff delay, cut short the instant connectivity returns — the
        // scene re-dresses within a frame of the network healing instead of
        // sitting out the rest of a 30s backoff window.
        private async Task WaitRetryWindowAsync(float seconds)
        {
            var wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<bool> onChange = online => { if (online) wake.TrySetResult(true); };
            Lvn.LvnNetworkStatus.Changed += onChange;
            try
            {
                await Task.WhenAny(
                    Task.Delay(TimeSpan.FromSeconds(Math.Max(0.5f, seconds)), _cts.Token),
                    wake.Task);
            }
            catch (OperationCanceledException) { }   // фон не загрузился — сцена остаётся с прежним, игра идёт
            finally { Lvn.LvnNetworkStatus.Changed -= onChange; }
        }

        // Monotonic backdrop generation: a retrying older bg must never paint
        // over a newer one that already landed (or is in flight).
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
            var url = (string)cmd["sprite_url"];
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
            // ПОВТОР ТОЙ ЖЕ КОМАНДЫ — NO-OP. Реплей восстановления (и любой
            // двойной вызов) переустанавливал фон: кроссфейд в самого себя и
            // рестарт пана с левого края — «фон дёргает туда-сюда» (живой
            // репорт). Авторский повтор bg с ДРУГИМИ параметрами (новый пан)
            // отличается содержимым команды и проходит как раньше.
            if (_lastBgCmd != null && HasBackdrop && JToken.DeepEquals(_lastBgCmd, cmd))
            {
                LvnLog.Trace($"[lvn-bg] bg no-op (та же команда): {url}");
                return;
            }
            LvnLog.Trace($"[lvn-bg] bg ставим: {url} (epoch={_stageEpoch}, HasBackdrop={HasBackdrop})");
            // Remember the latest scene backdrop across scenes/sessions — the
            // hub wardrobe reopens "where the player last was" on this canvas.
            // Карандашом: фон меняется десятки раз за главу, и метка нужна
            // лишь к следующему открытию гардероба — фиксация подождёт ухода
            // приложения в фон.
            LvnKeep.Jot(LastBgKey, url);
            // The script reached this bg — that's the unlock moment, independent of
            // whether the sprite itself loads (a cache miss doesn't unsee the CG).
            UnlockGalleryFor(url);
            if (Assets == null) return;
            // СЦЕНА МОЖЕТ БЫТЬ ЕЩЁ НЕ ПОСТРОЕНА. Build идёт в первом Update, а
            // хост ставит полотно меню сразу — команда уходила в никуда
            // (_renderer?.SetBackground у null), но флаг «фон стоит» всё равно
            // вставал ниже. Итог: чёрный экран, который сам не чинился, и
            // вернуть картинку мог только новый показ — заход в главу и выход
            // (Илья 26.08). Ждём рождения рендерера, а не молчим.
            for (int f = 0; _renderer == null && f < 300; f++) await Task.Yield();
            if (_renderer == null)
            {
                Debug.LogWarning($"[lvn-bg] сцена не построилась — полотно не поставлено: {url}");
                return; // БЕЗ HasBackdrop: врать про фон нельзя, страж досылает
            }
            int epoch = _stageEpoch;
            int gen = _clock.Claim(LvnStageClock.BackgroundLane);
            Sprite sprite;
            _bgUnderwayUrl = url; _bgUnderwayGen = gen;
            try
            {
                sprite = await LoadSceneSpriteAsync(url, "bg",
                    () => _clock.MayTouch(epoch, LvnStageClock.BackgroundLane, gen));
            }
            finally
            {
                // Только своё: пока мы ждали, полотно мог перехватить кто-то
                // новее — стереть его запись значило бы сказать «никто не
                // везёт» ровно в тот миг, когда везут.
                if (_bgUnderwayGen == gen) _bgUnderwayUrl = null;
            }
            if (sprite == null) { LvnLog.Trace($"[lvn-bg] bg НЕ ЗАГРУЗИЛСЯ: {url}"); return; }
            if (!_clock.MayTouch(epoch, LvnStageClock.BackgroundLane, gen))
            {
                LvnLog.Trace($"[lvn-bg] bg отменён на подлёте: {url} " +
                          $"(epoch {epoch}→{_clock.Epoch}, поколение фона {gen} устарело)");
                return; // a chapter change / newer bg won
            }
            // Смена фона растворяет прежний кадр (тема ui.stage.bg_fade;
            // авторское `fade=` на команде сильнее). Первый фон сцены проходит
            // мгновенно — под ним ещё занавес входа.
            float bgFade = NumOr(cmd["fade"], Theme?.BgCrossfadeSeconds ?? 0.35f);
            _renderer?.SetBackground(sprite, bgFade);
            RepinSceneSprites("bg", new[] { sprite }); // фон на экране — LRU не трогает
            // Пан по широкому фону: `bg … pan=left pan_to=right pan_dur=30` —
            // сцена начинается в левой части кадра и за pan_dur доезжает до
            // правой. Работает на горизонтальном слаке cover-кроя.
            var panFrom = ParsePan(cmd["pan"]);
            var panTo = ParsePan(cmd["pan_to"]);
            if (panFrom.HasValue || panTo.HasValue)
            {
                float from = panFrom ?? 0.5f;
                _renderer?.PanBackground(from, panTo ?? from, NumOr(cmd["pan_dur"], 20f));
            }
            ReleaseActive3DSet();
            _lastBgCmd = (JObject)cmd.DeepClone();
            HasBackdrop = true; // the entry reveal (host) waits for the first one
        }

        /// <summary>Прямая установка пана стоящего фона (0..1) БЕЗ анимации —
        /// хост ведёт полотно меню в такт СВОЕЙ UI-анимации, кадр в кадр.
        /// Собственный пан-таймер фона (bg-команда) стартует позже async-тракта
        /// и едет другой кривой — рассинхрон с переездом вкладок бросался в
        /// глаза (живой репорт 28.08).</summary>
        public void SetBackgroundPan(float pan01) => _renderer?.PanBackground(pan01, pan01, 0f);

        // ЧТО ВЕЗУТ ПРЯМО СЕЙЧАС. Не «мы что-то просили», а «загрузка этого
        // адреса идёт вот в эту секунду»: между просьбой и картинкой лежат
        // сеть, декод и до восьми попыток с разрежением.
        private string _bgUnderwayUrl;
        private int _bgUnderwayGen;

        /// <summary>
        /// ПОЛОТНО ВЕЗУТ ПРЯМО СЕЙЧАС. Без адреса — «везут хоть какое-то».
        ///
        /// <para>Вопрос завёлся ради Лекаря. Недуг «мы в меню, а полотна нет»
        /// отличал живую загрузку от настоящей поломки ЧИСЛОМ СЕКУНД — и на
        /// слабом телефоне терпение кончалось раньше, чем приезжала картинка.
        /// Лечение (поставить полотно заново) забирает у фона поколение, и
        /// лестница повторов начиналась с первой ступени: лекарь ломал ровно
        /// тот механизм, который и должен был пережить обрыв сети.</para>
        ///
        /// <para>Спрашивать надо у того, кто везёт. Здесь ответ — факт, а не
        /// догадка о его длительности.</para>
        /// </summary>
        public bool BringingBackdrop(string url = null)
            => _bgUnderwayUrl != null
               && (url == null || string.Equals(_bgUnderwayUrl, url, StringComparison.Ordinal));

        // Последняя применённая bg-команда — повтор той же не трогает сцену.
        private JObject _lastBgCmd;

        /// <summary>
        /// СТОИТ ЛИ НА СЦЕНЕ ИМЕННО ЭТО ПОЛОТНО — прямо сейчас, на экране.
        ///
        /// <para>Вопрос задавал ХОСТ, и отвечал себе сам: держал флажок «фон
        /// меню поставлен» и правил его в пяти местах — после передачи кадра,
        /// после подстановки полотна в катсцене, на входе в главу, при лечении
        /// пропавшего полотна. Две памяти об одном факте расходились: сцена
        /// теряла картинку (белый кадр, чужой bg), а флажок продолжал говорить
        /// «стоит» — ради этого расхождения и завели сторожа, который сбрасывал
        /// флажок со стороны.</para>
        ///
        /// <para>Спрашивать надо у того, кто рисует. Ответ здесь складывается из
        /// трёх правд разом: та же картинка в последней команде, фон применён и
        /// он ДЕЙСТВИТЕЛЬНО на экране (рендерер, а не наша память о нём). Если
        /// рендерер такого вопроса не понимает (объёмная сцена), считаем, что
        /// картинка на месте, — сегодня так и было.</para>
        /// </summary>
        public bool ShowsBackdrop(string url)
        {
            if (string.IsNullOrEmpty(url) || _lastBgCmd == null || !HasBackdrop) return false;
            if (!string.Equals((string)_lastBgCmd["sprite_url"], url, StringComparison.Ordinal)) return false;
            return (_renderer as CanvasSceneRenderer)?.BackdropHasArt ?? true;
        }

        /// <summary>Смена КАЧЕСТВА арта (настройки 2K/1440p/1K): пере-применить
        /// сцену — фон и все видимые актёры перезагружают спрайты, уже с новым
        /// суффиксом (его подставляет тракт загрузки). Без этого выбор качества
        /// действовал только на будущие показы, а видимая сцена жила в старом
        /// («героиню не перекачала», живой репорт 27.08).</summary>
        public void RefreshArtQuality()
        {
            // ОБЛИК ЗАБЫВАЕТСЯ НАМЕРЕННО. Правило «тот же облик — не
            // пересобирать» сравнивает СПИСОК СЛОЁВ, а качество меняет не его:
            // суффикс варианта (@2k/@1k) подставляет тракт загрузки уже под
            // капотом. Без этой строки выбор качества не тронул бы ни одну
            // видимую фигуру — «героиню не перекачала» вернулось бы тем же днём.
            _memory.ForgetLooks();
            if (_lastBgCmd != null)
            {
                var cmd = (JObject)_lastBgCmd.DeepClone();
                _lastBgCmd = null; // иначе дедуп «та же команда» съест повтор
                LvnAsync.Fire(ApplyBgAsync(cmd), "ApplyBg");
            }
            foreach (var id in ActorsOnStage()) RefreshActor(id);
        }

        // «left/center/right» или число 0..1 — куда смотрит кадр по ширине фона.
        private static float? ParsePan(Newtonsoft.Json.Linq.JToken t)
        {
            if (t == null) return null;
            var s = (string)t;
            switch ((s ?? "").ToLowerInvariant())
            {
                case "left": return 0f;
                case "center": return 0.5f;
                case "right": return 1f;
            }
            if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                return Mathf.Clamp01(v);
            // Не слово из списка и не число — панорамы не будет вовсе, и
            // сказать об этом надо здесь: снаружи это выглядит как «фон не
            // поехал», а не как непонятое слово.
            Lvn.UI.LvnClosedWord.Unknown("pan", s, "left | center | right | доля 0..1");
            return null;
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
            if (Turns3DOff(cmd))
            {
                _clock.Cancel(LvnStageClock.BackgroundLane); // отменяем ещё качающийся бандл
                _renderer?.Set3DBackdrop(null);
                ReleaseActive3DSet();
                return;
            }

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
                    if (Assets == null) return;
                    int epoch = _stageEpoch;
                    int gen = _clock.Claim(LvnStageClock.BackgroundLane);
                    Lvn3DSetAsset loaded = null;
                    try { loaded = await Assets.Load3DSetAsync(id, _cts.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"[lvn-stage] 3D set '{id}' не загрузился: {e.Message}");
                    }
                    if (loaded?.Prefab == null) { loaded?.Dispose(); return; } // keep the flat background
                    if (!_clock.MayTouch(epoch, LvnStageClock.BackgroundLane, gen))
                    {
                        loaded.Dispose(); // a newer background won while this downloaded
                        return;
                    }

                    var old = _active3DSet;
                    _renderer?.Set3DBackdrop(loaded.Prefab); // instantiate before releasing old assets
                    _active3DSet = loaded;
                    _active3DSetId = id;
                    old?.Dispose();
                    HasBackdrop = true;
                    LvnLog.Trace($"[lvn-stage] 3D set '{id}' ready ({(loaded.Remote ? "server/cache" : "bundled fallback")})");
                }
            }

            // `live=` overrides how the set is filmed: on for motion the engine
            // can't see (a shader that scrolls water), off to pin a still shot.
            if (cmd["live"] != null) _renderer?.Set3DLive(BoolOr(cmd["live"], true));

            // Framing rides on the same op: `bg3d x=… yaw=…` without an id moves
            // the camera of the set already standing.
            _renderer?.Frame3D(
                Num(cmd["x"]), Num(cmd["y"]), Num(cmd["z"]),
                Num(cmd["pitch"]), Num(cmd["yaw"]), Num(cmd["fov"]),
                Num(cmd["dur"]) ?? 0f);
        }

        /// <summary>A command number, or null when the author left the field out —
        /// "unset" has to survive as a distinct value so framing keeps what it had.</summary>
        private static float? Num(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return (float)t; } catch { return null; }
        }

        private const string LastBgKey = "lvn_last_bg";

        /// <summary>The most recent scene backdrop url shown on ANY stage —
        /// persisted, so a hub-opened wardrobe can dress its canvas with the
        /// scene the player last saw. Empty when nothing has been staged yet.</summary>
        public static string LastSceneBgUrl => LvnKeep.Get(LastBgKey, "");

        /// <summary>Забыть последний кадр. Это СЛЕД ИГРОКА, а не настройка и не
        /// кэш: по нему видно, какую сцену какой новеллы он смотрел последней.
        /// Стирается вместе с остальным личным — иначе игрок, попросивший себя
        /// забыть, откроет гардероб и увидит фон сцены, где был до
        /// удаления.</summary>
        public static void ForgetLastSceneBg() => LvnKeep.Drop(LastBgKey);

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
