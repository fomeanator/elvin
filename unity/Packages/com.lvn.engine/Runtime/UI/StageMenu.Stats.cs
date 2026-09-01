using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// СТАТЫ — часть <see cref="StageMenu"/>: живые переменные новеллы, их
    /// показ и (в отладочном режиме) правка прямо на ходу.
    /// </summary>
    public sealed partial class StageMenu
    {
        // One panel answers "are my stats actually accruing?": every variable of
        // the RUNNING story, nested objects (global.*) flattened to dotted keys.
        // With ui.menu.stats_edit the rows become writable — the QA loop for a
        // stat-driven novel (nudge courage, reopen, watch the gate) without
        // replaying a chapter.
        private void ShowStats()
        {
            _pane = ShowStats;
            var p = Panel(L("stats", "Stats"));
            var scroll = LvnScroll.Vertical();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            var vars = _stage.Player?.Vars;
            if (vars == null || vars.Count == 0)
            {
                scroll.Add(Text(L("empty", "— empty —"), 24, FontStyle.Italic, dim: true));
                return;
            }
            var flat = new List<(string key, JToken val)>();
            foreach (var kv in vars) FlattenVar(kv.Key, kv.Value, flat);
            // Curation: the PLAYER's stats, not the import's plumbing. With a
            // whitelist only its subtrees survive; the blacklist prunes after.
            if (_theme.MenuStatsShow != null && _theme.MenuStatsShow.Count > 0)
                flat.RemoveAll(e => !MatchesAny(e.key, _theme.MenuStatsShow));
            if (_theme.MenuStatsHide != null && _theme.MenuStatsHide.Count > 0)
                flat.RemoveAll(e => MatchesAny(e.key, _theme.MenuStatsHide));
            flat.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
            if (flat.Count == 0)
            {
                scroll.Add(Text(L("empty", "— empty —"), 24, FontStyle.Italic, dim: true));
                return;
            }
            foreach (var (key, val) in flat) scroll.Add(StatRow(key, val));
        }

        // "Way" matches the root itself and everything under it (Way.Moral),
        // never a lookalike sibling (Wayward).
        private static bool MatchesAny(string key, List<string> prefixes)
        {
            foreach (var p in prefixes)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (key == p || (key.Length > p.Length && key[p.Length] == '.' && key.StartsWith(p, StringComparison.Ordinal)))
                    return true;
            }
            return false;
        }

        // Leaves become rows; JObject nodes recurse into "parent.child" keys —
        // the exact dotted paths SetVar/GetVar/expressions read, so a row's key
        // is also its write address.
        private static void FlattenVar(string key, JToken val, List<(string, JToken)> into)
        {
            if (val is JObject o && o.Count > 0)
                foreach (var prop in o.Properties()) FlattenVar(key + "." + prop.Name, prop.Value, into);
            else into.Add((key, val));
        }

        private VisualElement StatRow(string key, JToken val)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.height = 66;
            row.style.marginBottom = LvnTokens.Space1;

            var name = Text(key, 28, FontStyle.Normal);
            name.style.flexGrow = 1;
            name.style.flexShrink = 1;
            name.style.overflow = Overflow.Hidden;
            row.Add(name);

            bool edit = _theme.MenuStatsEdit;
            var type = val?.Type ?? JTokenType.Null;
            if (type == JTokenType.Boolean)
            {
                if (edit)
                {
                    var t = new Toggle { value = val.Value<bool>() };
                    t.RegisterValueChangedCallback(e => _stage.Player.SetVar(key, new JValue(e.newValue)));
                    row.Add(t);
                }
                else row.Add(Text(val.Value<bool>() ? "true" : "false", 28, FontStyle.Bold));
            }
            else if (type == JTokenType.Integer || type == JTokenType.Float)
            {
                double d = val.Value<double>();
                if (edit)
                {
                    // − [value] + : steppers for the common nudge, the field for
                    // an exact number. Garbage input just doesn't commit.
                    var field = StatField(FormatNum(d), 130);
                    field.RegisterCallback<FocusOutEvent>(_ =>
                    {
                        if (double.TryParse(field.value.Replace(',', '.'),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var v))
                            _stage.Player.SetVar(key, new JValue(v % 1 == 0 ? (long)v : v));
                        else field.value = FormatNum(ReadNum(key, d));
                    });
                    row.Add(StatStep("−", () => Nudge(key, field, -1)));
                    row.Add(field);
                    row.Add(StatStep("+", () => Nudge(key, field, +1)));
                }
                else row.Add(Text(FormatNum(d), 28, FontStyle.Bold));
            }
            else if (type == JTokenType.String)
            {
                if (edit)
                {
                    var field = StatField((string)val, 230);
                    field.RegisterCallback<FocusOutEvent>(_ =>
                        _stage.Player.SetVar(key, new JValue(field.value ?? "")));
                    row.Add(field);
                }
                else row.Add(Text("«" + Trunc((string)val ?? "", 20) + "»", 28, FontStyle.Bold));
            }
            else
            {
                // null / arrays: show, don't edit — nothing in a story reads them
                // in a way a stepper could sensibly write.
                var s = val == null || type == JTokenType.Null ? "null"
                    : Trunc(val.ToString(Newtonsoft.Json.Formatting.None), 24);
                row.Add(Text(s, 24, FontStyle.Normal, dim: true));
            }
            return row;
        }

        private void Nudge(string key, TextField field, double by)
        {
            double v = ReadNum(key, 0) + by;
            _stage.Player.SetVar(key, new JValue(v % 1 == 0 ? (long)v : v));
            field.value = FormatNum(v);
        }

        // Re-read through the player so a stale row (story code changed the value
        // underneath an open panel) nudges the REAL current number, not the text.
        private double ReadNum(string key, double fallback)
        {
            if (_stage.Player == null) return fallback;
            try
            {
                var t = Lvn.LvnExpression.Evaluate(key, _stage.Player.Vars);
                return t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
                    ? t.Value<double>() : fallback;
            }
            catch { return fallback; }
        }

        private static string FormatNum(double d) => d % 1 == 0
            ? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private TextField StatField(string value, int width)
        {
            var f = new TextField { value = value };
            f.style.width = width;
            f.style.height = 56;
            LvnAir.MarginX(f, LvnTokens.Space1);
            var input = f.Q("unity-text-input");
            if (input != null)
            {
                var tint = _theme.MenuTextColor;
                input.style.backgroundColor = UiColor.WithAlpha(tint, 0.08f);
                input.style.color = _theme.MenuTextColor;
                input.style.unityTextAlign = TextAnchor.MiddleCenter;
                input.style.fontSize = LvnTokens.TextBase;
                LvnChrome.ClearBorder(input);
                LvnChrome.Round(input, LvnTokens.RadiusXs);
            }
            LvnFonts.Apply(f, _theme.Font);
            return f;
        }

        private Button StatStep(string glyph, Action onClick)
        {
            var b = new Button(onClick) { text = glyph };
            b.style.width = 60; b.style.height = 56;
            b.style.fontSize = LvnTokens.TextBase;
            b.style.color = _theme.MenuTextColor;
            var tint = _theme.MenuTextColor;
            b.style.backgroundColor = UiColor.WithAlpha(tint, 0.08f);
            LvnChrome.ClearBorder(b);
            LvnChrome.Round(b, LvnTokens.RadiusXs);
            LvnFonts.Apply(b, _theme.Font);
            return b;
        }
    }
}
