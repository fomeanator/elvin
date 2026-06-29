using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// Replaces <c>{expr}</c> placeholders in text with the current value of an
    /// expression over the player's variables — a bare var (<c>{hp}</c>), an
    /// arithmetic/expression (<c>{hp}/{maxhp}</c>, <c>{floor(hp/maxhp*100)}</c>) or
    /// a collection query (<c>{len(inv)}</c>). This is the reactive substitution
    /// engine: a live label re-runs <see cref="Apply"/> on a tick, so a value shown
    /// on screen tracks the variable as it changes. An unknown bare var renders as
    /// the literal <c>{key}</c> so missing data is visible; a malformed expression
    /// does the same. Doubled braces escape: <c>{{</c> → <c>{</c>, <c>}}</c> → <c>}</c>.
    /// Runs after <see cref="TextAlternatives"/> (which leaves <c>{…}</c> untouched).
    /// </summary>
    public static class TextInterpolation
    {
        public static string Apply(string template, IReadOnlyDictionary<string, JToken> vars)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0) return template;
            var sb = new StringBuilder(template.Length + 16);
            for (int i = 0; i < template.Length; i++)
            {
                var c = template[i];
                if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
                {
                    sb.Append('{');
                    i++;
                    continue;
                }
                if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
                {
                    sb.Append('}');
                    i++;
                    continue;
                }
                if (c == '{')
                {
                    var end = template.IndexOf('}', i + 1);
                    if (end < 0)
                    {
                        sb.Append(template, i, template.Length - i);
                        break;
                    }
                    var key = template.Substring(i + 1, end - i - 1).Trim();
                    sb.Append(Resolve(key, vars));
                    i = end;
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Evaluate one placeholder. A bare var is the fast path; anything else goes
        // through the expression engine (so {len(inv)}, {hp/maxhp} etc. work). An
        // unknown plain identifier or a malformed expression renders as "{key}".
        private static string Resolve(string key, IReadOnlyDictionary<string, JToken> vars)
        {
            if (vars != null && vars.TryGetValue(key, out var v))
                return (v == null || v.Type == JTokenType.Null) ? "" : v.ToString();

            // not a known plain var — try it as an expression
            try
            {
                var r = LvnExpression.Evaluate(key, vars);
                if (r == null || r.Type == JTokenType.Null)
                    return IsPlainIdentifier(key) ? "{" + key + "}" : "";
                return r.ToString();
            }
            catch (LvnException)
            {
                return "{" + key + "}"; // surface the bad/missing placeholder to the writer
            }
        }

        private static bool IsPlainIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var ch in s)
                if (!char.IsLetterOrDigit(ch) && ch != '_') return false;
            return true;
        }
    }
}
