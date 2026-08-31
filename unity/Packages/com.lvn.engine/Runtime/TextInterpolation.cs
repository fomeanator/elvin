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

            // ТОЧКА — РАБОТА ВЫЧИСЛИТЕЛЯ, а не вторая её реализация здесь.
            // (Промах по такому имени показывается автору как {key} —
            //  см. IsPlainIdentifier: точка внутри имени тоже имя.)
            //
            // Раньше `Wardrobe.mainCh_Clothes` и `global.rep` разбирались тут
            // же, своим проходом по JSON: комментарий объяснял это тем, что
            // «у вычислителя нет доступа к полям». С 2026 года он есть
            // (LvnExpression.ParsePostfix понимает и `bag.potion`, и
            // `bag["potion"]`), а второй разбор остался — и жил по СВОИМ
            // правилам, отличным от общих:
            //
            //   * печатал значение сырым ToString мимо правил показа:
            //     `{global.ratio}` давал «0.6666666666666666», а плоский
            //     `{ratio}` — «0.67». Одна величина, два вида, в зависимости
            //     от того, лежит она под корнем или нет;
            //   * на опечатке в корне (`{globl.rep}`) отдавал ПУСТОТУ вместо
            //     обещанного `{key}` — у автора число пропадало посреди фразы
            //     без единого следа;
            //   * бросал на выражении с точкой, и это роняло главу.
            //
            // Одна дорога вниз лечит все три сразу.

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

        /// <summary>
        /// ПРОСТОЕ ИМЯ — то, что автор МОГ ИМЕТЬ В ВИДУ как переменную. По нему
        /// решают, показать ли непонятое как <c>{key}</c>: имя — это опечатка,
        /// и её надо ВИДЕТЬ; выражение — замысел, и пустота честнее.
        ///
        /// <para>ТОЧКА ВНУТРИ — ТОЖЕ ИМЯ. Здесь она именем не считалась, и
        /// промах в корне (<c>{globl.rep}</c>) стирался в пустоту: у автора
        /// число пропадало посреди фразы без единого следа. Браузерный плеер на
        /// том же тексте показывал <c>{globl.rep}</c> — то есть два рантайма
        /// отвечали на один вопрос по-разному, и правым был второй.</para>
        ///
        /// <para>Кириллица здесь наравне с латиницей: имена переменных в русских
        /// новеллах пишут по-русски, и опечатку в них надо видеть так же.</para>
        /// </summary>
        private static bool IsPlainIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            bool afterDot = true;   // начало строки и место сразу за точкой ждут букву
            foreach (var ch in s)
            {
                if (ch == '.')
                {
                    if (afterDot) return false;   // «.a», «a..b», «a.» — не имя
                    afterDot = true;
                    continue;
                }
                if (afterDot && !(char.IsLetter(ch) || ch == '_')) return false;
                if (!char.IsLetterOrDigit(ch) && ch != '_') return false;
                afterDot = false;
            }
            return !afterDot;   // «a.» кончается точкой — не имя
        }
    }
}
