using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// Shared stat-bar rendering for <see cref="LvnStatDef"/> — one row per
    /// entry, reused by <see cref="TitleDetailScreen"/> and the in-game stats
    /// panel so both read the exact same layout off a live variable getter
    /// instead of duplicating the bar/label markup twice.
    /// </summary>
    internal static class StatRows
    {
        public static VisualElement Row(LvnStatDef s, System.Func<string, JToken> getVar) =>
            s.kind == "pair" ? Pair(s, getVar) : Single(s, getVar);

        private static VisualElement Single(LvnStatDef s, System.Func<string, JToken> getVar)
        {
            double value = Num(getVar, s.key);
            int max = s.max > 0 ? s.max : 10;

            var row = Shell();
            var head = Head();
            var name = new Label(s.label ?? s.key);
            name.style.color = LvnTokens.Text;
            name.style.fontSize = 24;
            head.Add(name);

            var valueLbl = new Label($"{Mathf.RoundToInt((float)value)}/{max}");
            valueLbl.style.color = LvnTokens.Accent;
            valueLbl.style.fontSize = 22;
            valueLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            head.Add(valueLbl);
            row.Add(head);
            row.Add(Meter(max > 0 ? Mathf.Clamp01((float)value / max) : 0f));
            return row;
        }

        // A bipolar trait axis ("Спокойствие" ↔ "Ярость"): no fixed max — the
        // bar fills by relative weight pos/(pos+neg), both raw numbers shown.
        private static VisualElement Pair(LvnStatDef s, System.Func<string, JToken> getVar)
        {
            double pos = Num(getVar, s.pos_key);
            double neg = Num(getVar, s.neg_key);
            double total = pos + neg;
            float frac = total > 0 ? (float)(pos / total) : 0.5f;

            var row = Shell();
            var head = Head();
            var posLbl = new Label($"{s.pos_label} {Mathf.RoundToInt((float)pos)}");
            posLbl.style.color = LvnTokens.Text;
            posLbl.style.fontSize = 22;
            head.Add(posLbl);

            var negLbl = new Label($"{Mathf.RoundToInt((float)neg)} {s.neg_label}");
            negLbl.style.color = LvnTokens.TextDim;
            negLbl.style.fontSize = 22;
            head.Add(negLbl);
            row.Add(head);
            row.Add(Meter(frac));
            return row;
        }

        private static VisualElement Shell()
        {
            var row = new VisualElement();
            row.style.flexShrink = 0;
            row.style.flexDirection = FlexDirection.Column;
            row.style.marginTop = 20;
            return row;
        }

        private static VisualElement Head()
        {
            var head = new VisualElement();
            head.style.flexShrink = 0;
            head.style.flexDirection = FlexDirection.Row;
            head.style.justifyContent = Justify.SpaceBetween;
            head.style.alignItems = Align.Center;
            head.style.marginBottom = 10;
            return head;
        }

        // Shared 0..1 filled track (an Accent-filled portion over SurfaceHi).
        public static VisualElement Meter(float frac)
        {
            var track = new VisualElement();
            track.style.height = 12;
            track.style.flexShrink = 0;
            track.style.backgroundColor = LvnTokens.SurfaceHi;
            LvnChrome.Round(track, 6f);
            track.style.overflow = Overflow.Hidden;

            var fill = new VisualElement();
            fill.style.height = Length.Percent(100f);
            fill.style.width = Length.Percent(Mathf.Clamp01(frac) * 100f);
            fill.style.backgroundColor = LvnTokens.Accent;
            LvnChrome.Round(fill, 6f);
            track.Add(fill);
            return track;
        }

        // Reads a dotted var path (e.g. "Relationships.Roman") through the
        // caller's getter; missing/non-numeric → 0 (never throws into the UI).
        private static double Num(System.Func<string, JToken> getVar, string key)
        {
            if (getVar == null || string.IsNullOrEmpty(key)) return 0;
            JToken tok;
            try { tok = getVar(key); } catch { return 0; }
            if (tok == null) return 0;
            try { return tok.Type == JTokenType.Boolean ? (tok.Value<bool>() ? 1 : 0) : tok.Value<double>(); }
            catch { return 0; }
        }
    }
}
