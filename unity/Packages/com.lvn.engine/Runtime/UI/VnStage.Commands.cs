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
                _labelAnchor.Remove(id);
                _labelX.Remove(id);
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
            var xN = PercentOrNull(cmd["x"]);
            if (fresh || xN != null) el.style.left = Length.Percent(Mathf.Clamp(xN ?? 3f, 0f, 100f));
            var yN = PercentOrNull(cmd["y"]);
            if (fresh || yN != null) el.style.top = Length.Percent(Mathf.Clamp(yN ?? 3f, 0f, 100f));
            // width: explicit `w` (screen %), else whatever the ANCHOR leaves between
            // x and the screen edge. Budgeting `97 - x` regardless of anchor is what
            // shredded right-anchored labels: at x=95 a right-pinned label grows
            // LEFTWARDS across 95% of the screen, but was handed 2% and wrapped one
            // letter per line.
            var anchorStr = (string)cmd["anchor"];
            if (fresh || anchorStr != null) _labelAnchor[id] = anchorStr;
            _labelAnchor.TryGetValue(id, out var anchorForWidth);
            var (ax, _) = LabelAnchorFractions(anchorForWidth);
            if (fresh || xN != null) _labelX[id] = xN ?? 3f;
            _labelX.TryGetValue(id, out var xForWidth);
            var wN = PercentOrNull(cmd["w"]);
            if (fresh || wN != null || xN != null || anchorStr != null)
            {
                float room = ax <= 0f ? 97f - xForWidth              // grows right
                           : ax >= 1f ? xForWidth - 3f               // grows left
                           : 2f * Mathf.Min(xForWidth - 3f, 97f - xForWidth); // both ways
                el.style.maxWidth = Length.Percent(Mathf.Clamp(wN ?? room, 1f, 100f));
            }
            if (fresh || anchorStr != null)
            {
                var (tx, ty) = LabelAnchor(anchorStr);
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
                    _ = ApplyContentFontAsync(el, fontPath);
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

        /// <summary>Число ДЛЯ ЛЕЙБЛА, где единица измерения — процент экрана.
        /// «26» и «26%» значат одно и то же; «0.26» тоже принимается как доля,
        /// если записана с суффиксом. Так одна и та же запись `x=26%` работает и
        /// в лейбле, и в `obj`, где родные единицы — доли.</summary>
        private float? PercentOrNull(JToken t)
        {
            if (t == null) return null;
            string raw = null;
            try { raw = t.Type == JTokenType.String ? (string)t : null; } catch { }
            if (raw != null && raw.TrimEnd().EndsWith("%"))
                return NumOrNull(t, _player?.Vars) * 100f; // NumOrNull уже поделил на 100
            return NumOrNull(t, _player?.Vars);
        }

        private float NumOr(JToken t, float dflt) => NumOrNull(t, _player?.Vars) ?? dflt;
        private static float NumOr(JToken t, float dflt, IReadOnlyDictionary<string, JToken> vars)
            => NumOrNull(t, vars) ?? dflt;

        // Nullable numeric read: absent → null, malformed → null (never throws), so
        // one bad field can't abort the whole chapter. A number written as a string
        // ("0.5") is still accepted — and so is a TEMPLATE ("{hp / hp_max}"), which
        // is evaluated against the live variables exactly like the text of a label.
        //
        // Без этого числовое поле принимало только литерал, и любая величина,
        // зависящая от состояния, писалась ЛЕСТНИЦЕЙ ВЕТОК: полоса здоровья на
        // восемь делений — восемь почти одинаковых строк, отличавшихся одним
        // числом. Ветки — это не выразительность, а обходной путь.
        private float? NumOrNull(JToken t) => NumOrNull(t, _player?.Vars);

        private static float? NumOrNull(JToken t, IReadOnlyDictionary<string, JToken> vars)
        {
            if (t == null) return null;
            try { if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (float)t; } catch { }
            string raw = null;
            try { raw = (string)t; } catch { }
            if (string.IsNullOrEmpty(raw)) return null;
            if (raw.IndexOf('{') >= 0)
                raw = TextInterpolation.Apply(raw, vars);
            raw = raw.Trim();
            // ПРОЦЕНТ ЭКРАНА — везде. У `obj` координаты исторически доли
            // (x=0.26), у лейблов — проценты (x=26): один и тот же «четверть
            // экрана» писался двумя способами, и перепутать их было проще, чем
            // угадать. Суффикс «%» принимается в ЛЮБОМ числовом поле и означает
            // ровно то, что написано.
            bool pct = raw.EndsWith("%");
            if (pct) raw = raw.Substring(0, raw.Length - 1).TrimEnd();
            // Число из выражения приходит СТРОКОЙ, а Newtonsoft печатает его по
            // ТЕКУЩЕЙ локали: на русском устройстве 0.875 становится «0,875», и
            // инвариантный разбор молча отдавал null — поле «не задано», полоса
            // пустая. Принимаем оба разделителя: запись пришла из машины, а не
            // от пользователя, и спорить о культуре тут не о чем.
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                && !float.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out f)
                && !float.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out f))
            {
                LvnPlayer.Log?.Invoke($"[lvn-num] не разобрал число из «{raw}»");
                return null;
            }
            return pct ? f / 100f : f;
        }

        private int? IntOrNull(JToken t) => IntOrNull(t, _player?.Vars);

        private static int? IntOrNull(JToken t, IReadOnlyDictionary<string, JToken> vars)
        {
            var f = NumOrNull(t, vars);
            return f == null ? (int?)null : (int)Mathf.Round(f.Value);
        }

        // Tolerant boolean read: absent → dflt, and true/false/1/0 written as a
        // string or number are all accepted rather than throwing an invalid cast.
        private static bool BoolOr(JToken t, bool dflt)
        {
            if (t == null) return dflt;
            try { return (bool)t; } catch { }
            switch (t.ToString().Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on": return true;
                // «off» пишут в скриптах постоянно (`clear dialogue=off`,
                // `sfx … off`) — и оно молча падало в значение по умолчанию,
                // то есть команда делала ОБРАТНОЕ тому, что написано.
                case "false": case "0": case "no": case "off": return false;
                default: return dflt;
            }
        }

        // Translate fractions for a label anchor (default top-left, so x/y read as an
        // inset from the corner). center → -50%, right/bottom → -100%.
        /// <summary>Anchor of a label, in BOTH notations. `obj` has always taken a
        /// numeric pair ("0.5,0.5"), and scripts write labels the same way — but this
        /// only ever matched WORDS, so every numeric anchor silently fell through to
        /// "centre": `anchor="0,0"` did not pin the left edge, it centred the label on
        /// x, and half of it left the screen. Both forms are accepted now; the numeric
        /// one is the house convention.</summary>
        internal static (float, float) LabelAnchor(string anchor)
        {
            var (ax, ay) = LabelAnchorFractions(anchor);
            return (-ax * 100f, -ay * 100f);
        }

        /// <summary>The anchor as fractions of the label's own box (0 = left/top,
        /// 1 = right/bottom). Width budgeting needs the fraction, not the offset.</summary>
        internal static (float, float) LabelAnchorFractions(string anchor)
        {
            if (string.IsNullOrEmpty(anchor)) return (0f, 0f); // top-left, as before
            string a = anchor.ToLowerInvariant();
            int comma = a.IndexOf(',');
            if (comma > 0
                && float.TryParse(a.Substring(0, comma), System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out var nx)
                && float.TryParse(a.Substring(comma + 1), System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out var ny))
                return (Mathf.Clamp01(nx), Mathf.Clamp01(ny));
            float fx = a.Contains("left") ? 0f : a.Contains("right") ? 1f : 0.5f;
            float fy = a.Contains("top") ? 0f : a.Contains("bottom") ? 1f : 0.5f;
            return (fx, fy);
        }

        // A script-driven `anim` command: deserialize its LvnAnim payload and play
        // it on the named channel (default "script") of an already-shown entity, so
        // .lvns can tween any prop/layer or move a sprite along a path live.
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

        /// <summary>Команда с вычисленными выражениями в полях.
        ///
        /// <para>Драйверы эффектов читают числа приведением — строка вида
        /// <c>"{враг_тон}"</c> уходила прямо в разбор числа и роняла плеер
        /// исключением посреди сцены (ловили живьём: бой вставал на первом
        /// кадре). Спрайты умели выражения давно, эффекты — нет, и эта разница
        /// ничем не оправдана: <c>sfx tint={сила}</c> так же естественен, как
        /// <c>obj x={место}</c>. Клон делается ТОЛЬКО когда есть что вычислять,
        /// поэтому обычная команда не платит ничего.</para></summary>
        private JObject Evaluated(JObject command)
        {
            if (command == null) return null;
            JObject copy = null;
            foreach (var prop in command.Properties())
            {
                if (prop.Value.Type != JTokenType.String) continue;
                var raw = (string)prop.Value;
                if (string.IsNullOrEmpty(raw) || raw.IndexOf('{') < 0) continue;
                var done = TextInterpolation.Apply(raw, _player?.Vars);
                if (done == raw) continue;
                copy = copy ?? (JObject)command.DeepClone();
                copy[prop.Name] = done;
            }
            return copy ?? command;
        }

        public void ApplyStage(JObject command)
        {
            switch ((string)command["op"])
            {
                case "bg": _ = ApplyBgAsync(command); break;
                case "bg3d": _ = ApplyBg3DAsync(command); break;
                case "o3d": _ = ApplyO3DAsync(command); break;   // тело сцены
                case "light": ApplyLight(command); break;        // свет и атмосфера
                case "actor": _ = ApplyActorAsync(command); break;
                case "obj": _ = ApplyActorAsync(command); break; // any placeable sprite
                case "clear":
                    // `clear dialogue=off` убирает РАМКУ РЕПЛИКИ, не трогая сцену.
                    // Она нужна не всегда: боевой экран прокручивает такты сам,
                    // и окно с последней фразой висит над ним как чужое, а
                    // полноэкранная панель обязана перекрывать его целиком.
                    // Скрытая рамка возвращается сама при следующей реплике —
                    // «выключено» держится ровно до неё.
                    // `clear all=1` — граница сцены: ни спрайтов, ни лейблов.
                    // `clear labels=1` — только текст. `clear dialogue=off|on` —
                    // рамка реплики. Голый `clear` — как было, весь каст.
                    if (BoolOr(command["all"], false)) { ApplyClear(); ClearLabels(); }
                    else if (BoolOr(command["labels"], false)) ClearLabels();
                    else if (command["dialogue"] != null)
                        SetSayVisible(BoolOr(command["dialogue"], true));
                    else ApplyClear(); // everyone off stage, scenery untouched
                    break;
                case "anim": ApplyAnim(command); break; // script-driven tween / path
                case "fade": ApplyFade(command); break;
                case "dim": ApplyDim(command); break;
                case "flash": ApplyFlash(command); break;
                case "tint": ApplyTint(command); break;
                case "blur": ApplyBlur(command); break;
                case "sfx":
                    // Спрайтовый эффект по id актёра; вне канвас-пути — no-op.
                    _renderer?.TrySpriteFx((string)command["id"], Evaluated(command));
                    break;
                case "fx":
                    // Мультиэффект кадра; без камеры (overlay-канвас, UITK-путь)
                    // честный no-op — сцена просто остаётся чистой.
                    _renderer?.TryFx(Evaluated(command));
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
                    _ = PreloadAssetsAsync(command);
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
                if (_hintCard != null) _hintCard.style.display = DisplayStyle.None;
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
            _hintCard.style.backgroundColor = bg;
            float r = Theme != null ? Theme.PanelCornerRadius : 12f;
            _hintCard.style.borderTopLeftRadius = r; _hintCard.style.borderTopRightRadius = r;
            _hintCard.style.borderBottomLeftRadius = r; _hintCard.style.borderBottomRightRadius = r;

            _hintLabel.style.color = Theme != null ? Theme.TextColor : Color.white;
            _hintLabel.style.fontSize = Theme != null ? Theme.BodyFontSize : 30;
            if (Theme != null) LvnFonts.Apply(_hintLabel, Theme.Font);
            _hintLabel.text = TextInterpolation.Apply(text, _player?.Vars);

            _hintCard.style.display = DisplayStyle.Flex;

            float dur = NumOr(cmd["duration"], 0f);
            if (dur > 0f)
                _hintHide = _labelLayer.schedule
                    .Execute(() => { if (_hintCard != null) _hintCard.style.display = DisplayStyle.None; })
                    .StartingIn((long)(dur * 1000f));
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
