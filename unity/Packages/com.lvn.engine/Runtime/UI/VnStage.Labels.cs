using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ТАБЛО — живые метки поверх сцены (`text id=… «{expr}»`).
    ///
    /// <para>Показатель здоровья, счёт, таймер: значение ставится как актёр, но
    /// живёт в слое интерфейса и само пересчитывается на реактивном тике —
    /// поэтому число на экране следует за переменной, а не за командой.</para>
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

            // look: per-label font / size / colour, falling back to the theme.
            // Через ОБЩИЙ СЛОВАРЬ, а не hex-разбор: `color=` у метки — тот же
            // атрибут, что у вспышки и у узла в дереве `ui`, и редактор после
            // него подсказывает весь словарь (подсказки ключуются именем
            // атрибута, а не командой). На hex-разборе `text hud color=accent`
            // молча давал цвет текста темы — ловушка, вооружённая именно тем
            // словом, на котором её не ждут, и без строчки в журнале.
            if (fresh || cmd["color"] != null)
                el.style.color = UiColor.Named((string)cmd["color"], Theme.TextColor);
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


        // Translate fractions for a label anchor (default top-left, so x/y read as an
        // inset from the corner). center → -50%, right/bottom → -100%.
        private static (float, float) LabelAnchor(string anchor)
        {
            return LvnAnchor.Percent(anchor, "top-left");
        }
    }
}
