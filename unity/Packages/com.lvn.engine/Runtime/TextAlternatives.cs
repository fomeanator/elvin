using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// Ink-style text alternatives inside dialogue text — the say-side half of
    /// full Ink support. Runs BEFORE <see cref="TextInterpolation"/>, so whatever
    /// it leaves untouched (plain <c>{var}</c>) still interpolates as before.
    /// <code>
    ///   {cond: yes|no}    — conditional text (expr before the colon, else optional)
    ///   {a|b|c}           — sequence: advances once per showing, stops on c
    ///   {&amp;a|b|c}      — cycle: loops a,b,c,a,…
    ///   {!a|b|c}          — once: a,b,c then empty
    ///   {~a|b|c}          — shuffle: random pick each showing
    /// </code>
    /// Sequence/cycle/once counters persist in the player's Vars under
    /// <c>__alt_&lt;siteKey&gt;_&lt;n&gt;</c> (siteKey = command index), so they
    /// survive save/resume exactly like every other variable. Branches may nest.
    /// </summary>
    public static class TextAlternatives
    {
        /// <param name="mutate">When false, the visible variant is re-computed
        /// WITHOUT advancing any sequence/cycle/once counter or re-rolling a
        /// shuffle — a pure re-render (hot-reload of the on-screen line, a chrome
        /// rebuild) must show the SAME text it's showing, not the next variant.</param>
        public static string Apply(string template, IDictionary<string, JToken> vars,
            int siteKey, Random rng = null, bool mutate = true)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0) return template;
            int ordinal = 0;
            // A no-mutate re-render seeds shuffle deterministically from the site so
            // repeated re-renders are stable (the original random pick wasn't
            // recorded, so exact reproduction isn't possible — stability is).
            var effRng = mutate ? (rng ?? Shared) : new Random(siteKey);
            return Process(template, vars, siteKey.ToString(), ref ordinal, effRng, mutate);
        }

        private static readonly Random Shared = new Random();

        private static string Process(string s, IDictionary<string, JToken> vars,
            string site, ref int ordinal, Random rng, bool mutate)
        {
            var sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '{' && i + 1 < s.Length && s[i + 1] == '{') { sb.Append("{{"); i++; continue; }
                if (c == '}' && i + 1 < s.Length && s[i + 1] == '}') { sb.Append("}}"); i++; continue; }
                if (c != '{') { sb.Append(c); continue; }

                int end = FindClosing(s, i);
                if (end < 0) { sb.Append(s, i, s.Length - i); break; }

                var body = s.Substring(i + 1, end - i - 1);
                var replaced = Expand(body, vars, site, ref ordinal, rng, mutate);
                if (replaced == null)
                    sb.Append('{').Append(body).Append('}'); // plain {var} — not ours
                else
                    sb.Append(replaced);
                i = end;
            }
            return sb.ToString();
        }

        /// <summary>Expands one <c>{…}</c> body, or returns null when it is not an
        /// alternative (plain interpolation placeholder).</summary>
        private static string Expand(string body, IDictionary<string, JToken> vars,
            string site, ref int ordinal, Random rng, bool mutate)
        {
            char mode = '\0';
            var inner = body;
            if (inner.Length > 0 && (inner[0] == '~' || inner[0] == '&' || inner[0] == '!'))
            {
                mode = inner[0];
                inner = inner.Substring(1);
            }

            int colon = mode == '\0' ? TopLevelIndexOf(inner, ':') : -1;
            var parts = SplitTopLevel(colon >= 0 ? inner.Substring(colon + 1) : inner, '|');

            if (mode == '\0' && colon < 0 && parts.Count < 2)
                return null; // `{var}` — leave for TextInterpolation

            int myOrdinal = ordinal++;
            string chosen;
            if (colon >= 0)
            {
                // {expr: then|else}. If the pre-colon text isn't a valid expression
                // (e.g. a smiley {:-)} or a stray "{Note: ...}" in prose), don't
                // throw out of the player and abort the chapter — treat the whole
                // block as literal text by bailing to the null (not-ours) path.
                var cond = inner.Substring(0, colon).Trim();
                bool truthy;
                try { truthy = LvnExpression.EvaluateBool(cond, AsReadOnly(vars)); }
                catch { ordinal = myOrdinal; return null; }
                chosen = truthy ? parts[0] : (parts.Count > 1 ? parts[1] : "");
            }
            else
            {
                switch (mode)
                {
                    case '~': chosen = parts[rng.Next(parts.Count)]; break;
                    case '&': chosen = parts[(int)(NextCounter(vars, site, myOrdinal, mutate) % parts.Count)]; break;
                    case '!': chosen = PickOnce(parts, NextCounter(vars, site, myOrdinal, mutate)); break;
                    default: chosen = parts[(int)Math.Min(NextCounter(vars, site, myOrdinal, mutate), parts.Count - 1)]; break;
                }
            }

            // The chosen branch may itself contain alternatives.
            int sub = 0;
            return Process(chosen.Trim(), vars, site + "_" + myOrdinal, ref sub, rng, mutate);
        }

        private static string PickOnce(List<string> parts, long counter) =>
            counter < parts.Count ? parts[(int)counter] : "";

        /// <summary>Reads the per-site counter. When <paramref name="mutate"/> is
        /// true it increments+stores and returns the value to USE now; when false
        /// (a re-render) it returns the LAST-shown value (n-1, clamped) and leaves
        /// the stored counter untouched, so the same variant re-appears.</summary>
        private static long NextCounter(IDictionary<string, JToken> vars, string site, int ordinal, bool mutate)
        {
            var key = "__alt_" + site + "_" + ordinal;
            long n = 0;
            if (vars != null && vars.TryGetValue(key, out var v) && v != null && v.Type == JTokenType.Integer)
                n = (long)v;
            if (!mutate) return n > 0 ? n - 1 : 0; // last shown, no advance
            if (vars != null) vars[key] = n + 1;
            return n;
        }

        private static IReadOnlyDictionary<string, JToken> AsReadOnly(IDictionary<string, JToken> vars) =>
            vars as IReadOnlyDictionary<string, JToken> ?? new Dictionary<string, JToken>(vars ?? new Dictionary<string, JToken>());

        // ── Brace-aware scanning ────────────────────────────────────────────

        private static int FindClosing(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        private static int TopLevelIndexOf(string s, char target)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}') depth--;
                else if (s[i] == target && depth == 0) return i;
            }
            return -1;
        }

        private static List<string> SplitTopLevel(string s, char sep)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}') depth--;
                else if (s[i] == sep && depth == 0)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start));
            return parts;
        }
    }
}
