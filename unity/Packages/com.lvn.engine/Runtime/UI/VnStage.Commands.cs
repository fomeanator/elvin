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
    /// Stage command dispatch (ApplyStage) and the simple op appliers: FX
    /// veils, camera, reactive text labels, hints, waits, preloads and
    /// script-driven anims — plus the tolerant JSON token readers they share.
    /// </summary>
    public sealed partial class VnStage
    {
        // A persistent reactive text label (`text id=… x= y= anchor= «{expr}»`): a
        // HUD/stat readout placed like an actor but living in the UITK overlay. Its
        // {expr} template is re-evaluated on the reactive tick, so the shown value
        // tracks the variable. Re-issuing the same id updates it; `hide` removes it.
        private void ApplyText(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id) || _labelLayer == null) return;

            if (BoolOr(cmd["hide"], false))
            {
                if (_labelEls.TryGetValue(id, out var old)) { old.RemoveFromHierarchy(); _labelEls.Remove(id); }
                _labelTmpl.Remove(id);
                return;
            }

            bool fresh = !_labelEls.TryGetValue(id, out var el);
            if (fresh)
            {
                el = new Label { name = "lbl-" + id, pickingMode = PickingMode.Ignore };
                el.style.position = Position.Absolute;
                el.style.whiteSpace = WhiteSpace.Normal;
                _labelLayer.Add(el);
                _labelEls[id] = el;
            }

            // A repeat `text <id>` MERGES into the live label — omitted fields keep
            // their current values (actor-op semantics: later fields win). So a
            // label is styled ONCE and then driven with bare `text code «…»`
            // updates, instead of re-stating x/y/size/color on every beat.
            // Save/load is safe: ReplayVisuals re-runs text ops in order, so the
            // styled declaration always lands before its bare updates.

            // placement: x/y are screen percents; anchor picks the label's reference point
            var xN = NumOrNull(cmd["x"]);
            if (fresh || xN != null) el.style.left = Length.Percent(Mathf.Clamp(xN ?? 3f, 0f, 100f));
            var yN = NumOrNull(cmd["y"]);
            if (fresh || yN != null) el.style.top = Length.Percent(Mathf.Clamp(yN ?? 3f, 0f, 100f));
            // width: explicit `w` (screen %), else capped at the right screen edge —
            // an absolute label otherwise grows past the screen instead of wrapping.
            var wN = NumOrNull(cmd["w"]);
            if (fresh || wN != null || xN != null)
                el.style.maxWidth = Length.Percent(Mathf.Clamp(wN ?? (97f - (xN ?? 3f)), 1f, 100f));
            if (fresh || cmd["anchor"] != null)
            {
                var (tx, ty) = LabelAnchor((string)cmd["anchor"]);
                el.style.translate = new Translate(Length.Percent(tx), Length.Percent(ty));
            }

            // look: per-label font / size / colour, falling back to the theme
            if (fresh || cmd["color"] != null)
                el.style.color = UiColor.Parse((string)cmd["color"], Theme.TextColor);
            if (fresh || cmd["size"] != null)
                el.style.fontSize = (int)NumOr(cmd["size"], Theme.BodyFontSize);
            var fontPath = (string)cmd["font"];
            if (fresh || !string.IsNullOrEmpty(fontPath))
            {
                // Same dual form as the theme font: "/content/…" = a font served
                // with the content (fetched into the cache, applied when ready);
                // anything else = a Resources name baked into the build.
                if (!string.IsNullOrEmpty(fontPath) && fontPath.StartsWith("/"))
                    LvnAsync.Fire(ApplyContentFontAsync(el, fontPath), "ApplyContentFont");
                else
                {
                    Font font = !string.IsNullOrEmpty(fontPath) ? Resources.Load<Font>(fontPath) : Theme.Font;
                    LvnFonts.Apply(el, font); // SDF path; no-op when null (theme default)
                }
            }

            if (fresh || cmd["text"] != null)
            {
                var tmpl = (string)cmd["text"] ?? "";
                if (tmpl.Length != 0 && _strings != null && _strings.TryGetValue(tmpl, out var trTmpl))
                    tmpl = trTmpl; // localization catalog, keyed by the source template
                _labelTmpl[id] = tmpl;
                el.text = TextInterpolation.Apply(tmpl, _player?.Vars); // immediate paint; tick keeps it live
            }
        }

        // Re-evaluate every live label's template against the current variables.
        private void RefreshLabels()
        {
            if (_labelTmpl.Count == 0) return;
            var vars = _player?.Vars;
            foreach (var kv in _labelTmpl)
                if (_labelEls.TryGetValue(kv.Key, out var el))
                {
                    var t = TextInterpolation.Apply(kv.Value, vars);
                    if (el.text != t) el.text = t;
                }
        }

        private static float NumOr(JToken t, float dflt) => NumOrNull(t) ?? dflt;

        // Что считается числом — решает Lvn.LvnNum: там же живёт разбор
        // процентов, и там же он покрыт тестом.
        private static float? NumOrNull(JToken t) => LvnNum.Parse(t);

        private static int? IntOrNull(JToken t)
        {
            var f = NumOrNull(t);
            return f == null ? (int?)null : (int)Mathf.Round(f.Value);
        }

        // Tolerant boolean read: absent → dflt, and true/false/1/0 written as a
        // string or number are all accepted rather than throwing an invalid cast.
        private static bool BoolOr(JToken t, bool dflt)
        {
            if (t == null) return dflt;
            try { return (bool)t; } catch { }   // поле не разобралось — идём с прежним значением
            switch (t.ToString().Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": return true;
                case "false": case "0": case "no": return false;
                default: return dflt;
            }
        }

        // Translate fractions for a label anchor (default top-left, so x/y read as an
        // inset from the corner). center → -50%, right/bottom → -100%.
        private static (float, float) LabelAnchor(string anchor)
        {
            string a = string.IsNullOrEmpty(anchor) ? "top-left" : anchor.ToLowerInvariant();
            float tx = a.Contains("left") ? 0f : a.Contains("right") ? -100f : -50f;
            float ty = a.Contains("top") ? 0f : a.Contains("bottom") ? -100f : -50f;
            return (tx, ty);
        }

        // A script-driven `anim` command: deserialize its LvnAnim payload and play
        // it on the named channel (default "script") of an already-shown entity, so
        // .lvns can tween any prop/layer or move a sprite along a path live.
        // ── ui: дерево интерфейса ───────────────────────────────────────────
        //
        // Слой создаётся при первом же `ui` и живёт до конца главы. У него ДВА
        // этажа, и это не украшение: `layer=hud` (по умолчанию) уходит под
        // окно реплики, `layer=over` ложится поверх всего. Один этаж не
        // годится — на первой же живой проверке ряд кнопок боевого интерфейса
        // закрыл собой текст реплики.
        private LvnUiLayer _uiLayer;
        private VisualElement _uiHudHost;
        private VisualElement _uiOverHost;

        private void ApplyUi(JObject cmd)
        {
            if (_uiLayer == null)
            {
                var over = _labelLayer ?? _uiRoot;   // метки `text` стоят выше диалога
                _uiOverHost = over;
                var hud = UiHudHost() ?? over;
                if (hud == null) return;
                _uiLayer = new LvnUiLayer(
                    hud, over,
                    () => UiVars,
                    UiClick,
                    LoadUiImageAsync);
            }
            _uiLayer.Apply(cmd);
            NotifyUiStage();   // новое дерево обязано сразу знать, что на экране
        }

        /// <summary>
        /// КАТСЦЕНА — кадр без интерфейса.
        ///
        /// <para>Убирает разом реплику, выборы, метки, меню и деревья `ui`, и
        /// по желанию наезжает камерой. Это состояние, а не эффект: `cutscene
        /// off=1` возвращает всё на место.</para>
        ///
        /// <para>Раньше до этого можно было добраться ТОЛЬКО долгим нажатием
        /// (режим разглядывания арта) — из языка не вызвать, а игроку оно
        /// мешало. Теперь тем же выключателем пользуются оба: автор оператором,
        /// игрок жестом.</para>
        /// </summary>
        private void ApplyCutscene(JObject cmd)
        {
            bool on = !(BoolOr(cmd["off"], false) || !BoolOr(cmd["on"], true));
            SetChromeHidden(on);

            // Наезд — необязательная часть: `cutscene on=1 zoom=1.12 dur=3`.
            var zoom = NumOrNull(cmd["zoom"]);
            if (zoom != null)
            {
                var move = new JObject
                {
                    ["op"] = "camera",
                    ["action"] = "zoom",
                    ["factor"] = on ? zoom.Value : 1f,
                    ["duration"] = NumOr(cmd["dur"], 2.5f),
                };
                ApplyCamera(move);
            }
            else if (!on)
            {
                ApplyCamera(new JObject { ["op"] = "camera", ["action"] = "reset", ["duration"] = 0.4f });
            }
        }

        /// <summary>Этаж под окном диалога. Отдельный контейнер, а не позиция
        /// среди детей: пересборка оболочки при смене темы вставляет диалог и
        /// выборы перед слоем меток, и любой «просто индекс» после этого
        /// съезжает.</summary>
        private VisualElement UiHudHost()
        {
            if (_uiHudHost != null && _uiHudHost.panel != null) return _uiHudHost;
            var chrome = (VisualElement)_chromeSafe;
            if (chrome == null) return null;
            _uiHudHost = new VisualElement { name = "vn-ui-hud", pickingMode = PickingMode.Ignore };
            _uiHudHost.style.position = Position.Absolute;
            _uiHudHost.style.left = 0; _uiHudHost.style.right = 0;
            _uiHudHost.style.top = 0; _uiHudHost.style.bottom = 0;
            chrome.Insert(0, _uiHudHost);   // ниже диалога, выборов и меток
            return _uiHudHost;
        }

        /// <summary>Переменные истории — для живых значений в `ui`. Ставит
        /// ИГРОК при создании (см. BindStory), а не хост: иначе каждый, кто
        /// встраивает движок, обязан был бы про это помнить.</summary>
        public System.Func<System.Collections.Generic.IReadOnlyDictionary<string, JToken>> UiVarsProvider;

        /// <summary>
        /// Нажатие на кнопку дерева `ui` — ТОТ ЖЕ рецепт, что у клика по
        /// объекту сцены.
        ///
        /// <para>Прыжка мало. Игрок может стоять в `wait`, ждать касания или
        /// показывать выбор, и запись новой позиции без пробуждения оставляет
        /// его стоять — экран отвечает один раз, а дальше замирает. Ровно это
        /// и случилось на первой же проверке.</para>
        ///
        /// <para>Отсюда же берётся способ ЖДАТЬ НАЖАТИЯ БЕЗ ОКНА ДИАЛОГА:
        /// экран паркуется на длинном `wait`, а кнопка выигрывает гонку с
        /// таймером — как это давно работает у кликабельных объектов.</para>
        /// </summary>
        private void UiClick(string label)
        {
            if (_player == null) return;
            if (!string.IsNullOrEmpty(label)) _player.GoTo(label);
            CancelPendingWait();
            _awaitingTap = false;
            _curChoices = null;
            _choices?.Dismiss();
            _player.Advance();
        }

        /// <inheritdoc/>
        public void BindStory(System.Func<System.Collections.Generic.IReadOnlyDictionary<string, JToken>> vars,
                              System.Action<string> goTo)
        {
            UiVarsProvider = vars;
            UiGoTo = goTo;
        }
        private System.Collections.Generic.IReadOnlyDictionary<string, JToken> UiVars
            => UiVarsProvider != null ? UiVarsProvider() : null;
        /// <summary>Прыжок по нажатию на элемент `ui` — тот же путь, что у
        /// on_click у obj.</summary>
        public System.Action<string> UiGoTo;
        /// <summary>Откуда брать картинки для `image`.</summary>
        public ILvnAssets UiAssets;

        // Загрузка своим путём, а не помощником оболочки: слой живёт в ДВИЖКЕ,
        // а оболочка — пакет над ним. Тянуть её сюда значило бы перевернуть
        // зависимость и лишить движок права работать без неё.
        private async System.Threading.Tasks.Task LoadUiImageAsync(VisualElement el, string url)
        {
            if (el == null || string.IsNullOrEmpty(url) || UiAssets == null) return;
            try
            {
                var sprite = await UiAssets.LoadSpriteAsync(url, default);
                if (sprite != null) el.style.backgroundImage = new StyleBackground(sprite);
            }
            catch { /* картинки нет — элемент остаётся пустым, экран не падает */ }
        }

        private void ApplyAnim(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id)) return;
            // Stop form: `anim id=x stop=all` / `stop=<channel/prop>`.
            var stop = (string)cmd["stop"];
            if (!string.IsNullOrEmpty(stop)) { SceneStopAnim(id, stop); return; }
            var payload = cmd["anim"];
            if (payload == null) return;
            LvnAnim anim;
            try { anim = payload.ToObject<LvnAnim>(); }
            catch { return; }
            if (anim == null || anim.tracks == null || anim.tracks.Count == 0) return;
            // Channel: explicit if given, else derived from the first track's target
            // (e.g. "script:rotation", "script:face:y") — so distinct properties run
            // and compose at once, while re-animating the same property replaces it.
            var channel = (string)cmd["channel"];
            if (string.IsNullOrEmpty(channel))
            {
                var t0 = anim.tracks[0];
                channel = "script:" + (string.IsNullOrEmpty(t0.layer) ? "" : t0.layer + ":") + t0.prop;
            }
            // mode=queue → chain after the current anim on this channel (non-blocking)
            if ((string)cmd["mode"] == "queue") ScenePlayAnimQueued(id, channel, anim);
            else ScenePlayAnim(id, channel, anim);
        }

        public void ApplyStage(JObject command)
        {
            switch ((string)command["op"])
            {
                case "bg": _ = ApplyBgAsync(command); break;
                case "bg3d": _ = ApplyBg3DAsync(command); break;
                case "actor": _ = ApplyActorAsync(command); break;
                case "obj": _ = ApplyActorAsync(command); break; // any placeable sprite
                case "clear": ApplyClear(); break; // everyone off stage, scenery untouched
                case "ui": ApplyUi(command); break;  // дерево интерфейса из сценария
                case "cutscene": ApplyCutscene(command); break;  // кадр без интерфейса
                case "anim": ApplyAnim(command); break; // script-driven tween / path
                case "fade": ApplyFade(command); break;
                case "dim": ApplyDim(command); break;
                case "flash": ApplyFlash(command); break;
                case "tint": ApplyTint(command); break;
                case "blur": ApplyBlur(command); break;
                case "sfx":
                    // Спрайтовый эффект по id актёра; вне канвас-пути — no-op.
                    _renderer?.TrySpriteFx((string)command["id"], command);
                    break;
                case "fx":
                    // Мультиэффект кадра; без камеры (overlay-канвас, UITK-путь)
                    // честный no-op — сцена просто остаётся чистой.
                    _renderer?.TryFx(command);
                    break;
                case "camera": ApplyCamera(command); break;
                case "particles":
                    _particles.Set((string)command["type"], BoolOr(command["on"], true));
                    break;
                case "audio": _ = _audio.ApplyAsync(command, Assets, _cts.Token); break;
                case "text": ApplyText(command); break; // reactive HUD/stat label
                case "save": SaveSlot(command); break;
                case "load": LoadSlot(command); break;
                case "text_pace": ApplyTextPace(command); break;
                case "wait":
                    _awaitingWait = true;
                    StartCoroutine(WaitCoroutine(command));
                    break;
                case "input": ApplyInput(command); break; // text entry → story var
                case "preload":
                    LvnAsync.Fire(PreloadAssetsAsync(command), "PreloadAssets");
                    break;
                case "hint": ApplyHint(command); break;
                // unknown-but-registered ops are simply not drawn.
            }
        }

        // `hint text="…" show=true [duration=0]` — a small card that pops up
        // top-center over the scene: a tutorial nudge, a stat unlock, a note tied
        // to a specific beat. `show=false` (or empty text) dismisses it; a positive
        // `duration` auto-dismisses after that many seconds. Text interpolates
        // {vars} like dialogue. Lives on the HUD layer, ignores the pointer.
        private void ApplyHint(JObject cmd)
        {
            if (_labelLayer == null) return;
            var text = (string)cmd["text"] ?? "";
            bool show = BoolOr(cmd["show"], true) && text.Length > 0;

            _hintHide?.Pause();
            _hintHide = null;

            if (!show)
            {
                HideHint();
                return;
            }

            if (_hintCard == null)
            {
                _hintCard = new VisualElement { name = "vn-hint", pickingMode = PickingMode.Ignore };
                _hintCard.style.position = Position.Absolute;
                _hintCard.style.maxWidth = Length.Percent(72);
                _hintCard.style.paddingLeft = 22; _hintCard.style.paddingRight = 22;
                _hintCard.style.paddingTop = 12; _hintCard.style.paddingBottom = 12;
                // top-center pill at 12% — clear of the shell HUD strip (the
                // old 5% sat underneath it), per the mobile-VN standard.
                _hintCard.style.left = Length.Percent(50);
                _hintCard.style.top = Length.Percent(12);
                _hintCard.style.translate = new Translate(Length.Percent(-50), Length.Percent(0));
                _hintLabel = new Label { name = "vn-hint-text", pickingMode = PickingMode.Ignore };
                _hintLabel.style.whiteSpace = WhiteSpace.Normal;
                _hintLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                _hintCard.Add(_hintLabel);
                _labelLayer.Add(_hintCard);
            }

            var bg = Theme != null ? Theme.PanelColor : new Color(0.05f, 0.05f, 0.08f, 0.9f);
            // Табличка — то же окно, только меньше: и стекло у неё то же самое.
            // Разъехавшись, эти двое читаются как элементы из разных игр.
            float glass = Theme != null ? Theme.PanelGlass : 0f;
            UiGlass.Apply(_hintCard, glass, bg);
            _hintCard.style.backgroundColor = glass > 0.004f ? Color.clear : bg;
            float r = Theme != null ? Theme.PanelCornerRadius : 12f;
            _hintCard.style.borderTopLeftRadius = r; _hintCard.style.borderTopRightRadius = r;
            _hintCard.style.borderBottomLeftRadius = r; _hintCard.style.borderBottomRightRadius = r;

            _hintLabel.style.color = Theme != null ? Theme.TextColor : Color.white;
            _hintLabel.style.fontSize = Theme != null ? Theme.BodyFontSize : 30;
            if (Theme != null) LvnFonts.Apply(_hintLabel, Theme.Font);
            _hintLabel.text = TextInterpolation.Apply(text, _player?.Vars);

            // Всплывает и утопает, а не мигает: подсказка появляется поверх
            // сцены без всякого предупреждения, и резкий скачок в углу глаза
            // читается как сбой, а не как сообщение.
            bool wasHidden = _hintCard.resolvedStyle.display == DisplayStyle.None;
            _hintCard.style.display = DisplayStyle.Flex;
            if (wasHidden) LvnAppear.Play(_hintCard, LvnAppearKind.Rise, true, 260);

            float dur = NumOr(cmd["duration"], 0f);
            if (dur > 0f)
                _hintHide = _labelLayer.schedule
                    .Execute(HideHint)
                    .StartingIn((long)(dur * 1000f));
        }

        /// <summary>Убрать табличку — всегда одним способом. Два места гасили её
        /// по-разному (одно с анимацией, другое мгновенно), и подсказка по
        /// таймеру исчезала иначе, чем снятая сценарием.</summary>
        private void HideHint()
        {
            if (_hintCard == null || _hintCard.resolvedStyle.display == DisplayStyle.None) return;
            LvnAppear.Play(_hintCard, LvnAppearKind.Rise, false, 200,
                () => { if (_hintCard != null) _hintCard.style.display = DisplayStyle.None; });
        }

        // ── wait / preload ──────────────────────────────────────────────────

        private IEnumerator WaitCoroutine(JObject cmd)
        {
            int gen = ++_waitGen; // this wait owns the timer until something cancels it
            float ms = NumOr(cmd["ms"], 1000f);
            yield return new WaitForSecondsRealtime(ms / 1000f);
            if (gen != _waitGen) yield break; // cancelled by a hotspot jump / newer wait
            _awaitingWait = false;
            if (_player != null && !_player.Finished)
                _player.Advance();
        }

        // A hotspot click that jumps the story must kill a pending `wait`, or its
        // deferred Advance() lands mid-flight somewhere else and skips a beat.
        private void CancelPendingWait()
        {
            _waitGen++;
            _awaitingWait = false;
        }

        private async Task PreloadAssetsAsync(JObject cmd)
        {
            if (Assets == null) return;

            var spriteUrls = new List<string>();
            var audioUrls = new List<string>();

            void Add(string url, string kind)
            {
                if (string.IsNullOrEmpty(url)) return;
                if (kind == "audio") audioUrls.Add(url);
                else spriteUrls.Add(url); // a Spine texture warms as a sprite too
            }

            // Batch form (`assets=[…]`) OR the terse single-asset form
            // (`preload url=… kind=…`) — the latter is how a chapter warms one
            // heavy Spine texture before its actor appears, killing the pop-in.
            if (cmd["assets"] is JArray assetArray)
                foreach (var a in assetArray)
                    Add((string)((JObject)a)["url"], (string)((JObject)a)["kind"]);
            else
                Add((string)cmd["url"], (string)cmd["kind"]);

            if (spriteUrls.Count == 0 && audioUrls.Count == 0) return;

            var tasks = new List<Task>();
            if (spriteUrls.Count > 0)
                tasks.Add(Assets.PreloadAsync(spriteUrls, "sprite", _cts.Token));
            if (audioUrls.Count > 0)
                tasks.Add(Assets.PreloadAsync(audioUrls, "audio", _cts.Token));
            await Task.WhenAll(tasks);
        }

        // ── stage command helpers ─────────────────────────────────────────────

        private void ApplyFade(JObject cmd)
        {
            var to = (string)cmd["to"] ?? "black";
            float dur = NumOr(cmd["duration"], 0.5f);
            if (to == "clear" || to == "none") _fx.Clear(dur);
            else _fx.Fade(to == "white" ? Color.white : Color.black, dur);
        }

        private void ApplyDim(JObject cmd)
        {
            float alpha = NumOr(cmd["alpha"], 0.4f);
            float dur = NumOr(cmd["duration"], 0.5f);
            _fx.Dim(alpha, dur);
        }

        private void ApplyFlash(JObject cmd)
        {
            if (LvnPrefs.ReduceMotion) return; // vestibular/photosensitivity comfort
            var colour = ParseColor((string)cmd["color"], Color.white);
            float dur = NumOr(cmd["duration"], 0.2f);
            _fx.Flash(colour, dur);
        }

        private void ApplyTint(JObject cmd)
        {
            var colour = ParseColor((string)cmd["color"], Color.white);
            float alpha = NumOr(cmd["alpha"], 0.3f);
            float dur = NumOr(cmd["duration"], 0.5f);
            _fx.Tint(colour, alpha, dur);
        }

        private void ApplyBlur(JObject cmd)
        {
            float alpha = NumOr(cmd["alpha"], 0.5f);
            float dur = NumOr(cmd["duration"], 0.5f);
            // Real gaussian of the scene frame when the renderer can (canvas
            // path + built-in pipeline); the FxLayer white veil is the fallback
            // for platforms without a camera hook. Never both.
            if (_renderer != null && _renderer.TryBlur(Mathf.Clamp01(alpha), dur))
            {
                _fx.ClearBlur(0f);
                return;
            }
            if (alpha <= 0f) _fx.ClearBlur(dur);
            else _fx.Blur(alpha, dur);
        }

        private void ApplyTextPace(JObject cmd)
        {
            float cps = NumOr(cmd["cps"], 0f);
            TypewriterClock.GlobalCps = cps;
        }

        internal static TransitionType ParseTransition(string name)
        {
            if (string.IsNullOrEmpty(name)) return TransitionType.None;
            switch (name.ToLowerInvariant())
            {
                case "fade": return TransitionType.Fade;
                case "slide_left": return TransitionType.SlideLeft;
                case "slide_right": return TransitionType.SlideRight;
                case "pop": return TransitionType.Pop;
                // Виды из общего набора движка (LvnAppear): персонаж всплывает
                // из-под стекла и утопает обратно, как и любая панель.
                case "rise": case "sink": return TransitionType.Rise;
                case "drop": return TransitionType.Drop;
                case "unfold": return TransitionType.Unfold;
                case "dissolve": case "burn": return TransitionType.Dissolve;
                default: return TransitionType.None;
            }
        }

        internal static Color ParseColor(string name, Color fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            switch (name.ToLowerInvariant())
            {
                case "white": return Color.white;
                case "black": return Color.black;
                case "red": return Color.red;
                case "blue": return Color.blue;
                case "green": return Color.green;
                case "yellow": return Color.yellow;
                case "cyan": return Color.cyan;
                case "magenta": return Color.magenta;
                case "cold":
                case "tint_cold": return new Color(0.6f, 0.7f, 1f, 1f);
                case "warm":
                case "tint_warm": return new Color(1f, 0.85f, 0.7f, 1f);
                case "sepia": return new Color(0.76f, 0.6f, 0.42f, 1f);
                default: return fallback;
            }
        }

        private void ApplyCamera(JObject cmd)
        {
            float dur = NumOr(cmd["duration"], 0.3f);
            switch ((string)cmd["action"])
            {
                case "shake":
                {
                    if (LvnPrefs.ReduceMotion) break; // comfort setting: no screen shake
                    float amp = NumOr(cmd["amplitude"], 8f);
                    _renderer?.Shake(amp, dur);
                    break;
                }
                case "zoom":
                {
                    float factor = NumOr(cmd["factor"], 1.2f);
                    _renderer?.Zoom(factor, dur);
                    break;
                }
                case "pan":
                {
                    float px = NumOr(cmd["x"], 0f);
                    float py = NumOr(cmd["y"], 0f);
                    _renderer?.Pan(px, py, dur);
                    break;
                }
                case "reset":
                    _renderer?.ResetCamera(dur);
                    break;
            }
        }
    }
}
