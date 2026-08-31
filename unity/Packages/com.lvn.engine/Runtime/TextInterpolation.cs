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
                return (v == null || v.Type == JTokenType.Null) ? "" : Show(v);

            // Dotted path into a nested object: `Wardrobe.mainCh_Clothes` /
            // `global.rep`. The store nests these under the root key (SetVarPath), so a
            // flat lookup above misses them and the expression engine has no dot member
            // access — resolve by navigating the root object here.
            if (vars != null && key.IndexOf('.') > 0)
            {
                // ПОПЫТКА, А НЕ ПРИГОВОР. Точка в ключе ещё не значит путь:
                // `{global.rep + 1}` — это ВЫРАЖЕНИЕ, и разбор пути на нём
                // бросает JsonException — чужое исключение, мимо перехвата
                // ниже. Оно уходило наверх из шага чтеца и РОНЯЛО ГЛАВУ посреди
                // сцены, а автор всего лишь поставил пробелы вокруг плюса:
                // `{global.rep+1}` работал, `{global.rep + 1}` убивал игру.
                // Не путь — значит пробуем выражением, как и обещано шапкой.
                JToken nested = null;
                try { nested = ResolvePath(key, vars); }
                catch { /* ключ оказался выражением, а не путём */ }
                if (nested != null)
                    return nested.Type == JTokenType.Null ? "" : nested.ToString();
            }

            // not a known plain var — try it as an expression
            try
            {
                var r = LvnExpression.Evaluate(key, vars);
                if (r == null || r.Type == JTokenType.Null)
                    return IsPlainIdentifier(key) ? "{" + key + "}" : "";
                return Show(r);
            }
            catch (LvnException)
            {
                return "{" + key + "}"; // surface the bad/missing placeholder to the writer
            }
        }

        /// <summary>
        /// КАК ЗНАЧЕНИЕ ВЫГЛЯДИТ В РЕПЛИКЕ. Подстановка идёт в текст ДЛЯ ИГРОКА,
        /// и печатать его отладочным видом нельзя.
        ///
        /// <para>Здесь стоял <c>ToString()</c> — то есть JSON-вид значения:
        /// <c>{2/3}</c> давало «0.6666666666666666», а список — многострочный
        /// список — одной строкой JSON прямо посреди фразы. Браузерный плеер печатал то
        /// же самое иначе (два знака после запятой, список через запятую), и обе
        /// реализации считали себя правыми: правила формата не было НИГДЕ.</para>
        ///
        /// <para>Корпус этого не ловил: все его подстановки целочисленные.
        /// Расхождение жило в зазоре между «проверено» и «бывает» — самом
        /// дорогом месте, потому что находит его автор на своей новелле.</para>
        ///
        /// <para>Правило простое и одинаковое в обоих рантаймах: целое печатается
        /// целым, дробное округляется до сотых (дальше игроку не нужно), список —
        /// через запятую, всё прочее как есть.</para>
        /// </summary>
        internal static string Show(JToken v)
        {
            if (v == null || v.Type == JTokenType.Null) return "";
            if (v is JArray arr)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Show(arr[i]));
                }
                return sb.ToString();
            }
            if (v.Type == JTokenType.Float)
            {
                double d = (double)v;
                double r = LvnNum.RoundHalfUp(d, 2);
                return r == System.Math.Floor(r)
                    ? ((long)r).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : r.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return v.ToString();
        }

        // Navigate a dotted path: the first segment is a root var, the remainder a
        // JSON path into it (Wardrobe.mainCh_Clothes → vars["Wardrobe"] → .mainCh_Clothes).
        // Null when any segment is missing.
        private static JToken ResolvePath(string key, IReadOnlyDictionary<string, JToken> vars)
        {
            int dot = key.IndexOf('.');
            var root = key.Substring(0, dot);
            if (!vars.TryGetValue(root, out var tok) || tok == null) return null;
            return tok.SelectToken(key.Substring(dot + 1));
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
