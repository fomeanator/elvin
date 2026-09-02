using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// СКОЛЬКО ШАГОВ ОСТАЛОСЬ ДО КОНЦА ГЛАВЫ — по САМОМУ КОРОТКОМУ пути.
    ///
    /// <para>Полоса прогресса не может считаться от номера команды в файле.
    /// Импорт линеаризует ветки: тела выборов дописываются в ХВОСТ файла, куда
    /// одно прохождение не заходит. В живой главе партнёрской новеллы
    /// (<c>cold-ch08</c>) спина кончается на 1847-й команде из 2295 — то есть
    /// пройдя главу целиком, игрок видел 80% и рывок на 100% в самом конце.
    /// Ровно это и сообщил партнёр.</para>
    ///
    /// <para>Правило владельца: показывать по кратчайшему пути. Тогда доля
    /// пройденного = <c>1 − остаток/полный_кратчайший</c>. Свойства ровно те,
    /// которых ждёт читатель: в начале ноль, В КОНЦЕ РОВНО СТО — каким бы
    /// маршрутом он ни шёл, — а выбрав длинную ветку, он просто получает
    /// проценты медленнее, потому что до конца ему и правда дальше.</para>
    ///
    /// <para>Считается один раз на главу обходом в ширину ОТ КОНЦА: за шаг
    /// берётся команда, рёбра — обычный переход к следующей и все прыжки
    /// (<c>goto</c>, обе ветки <c>if</c>, цели вариантов <c>choice</c>).</para>
    /// </summary>
    public static class LvnFlowDistance
    {
        /// <summary>Расстояние до конца для каждой команды. <see cref="int.MaxValue"/>
        /// у команд, из которых конец недостижим (мёртвый код).</summary>
        public static int[] ToEnd(JArray script, IReadOnlyDictionary<string, int> labels)
        {
            int n = script?.Count ?? 0;
            var dist = new int[n];
            for (int i = 0; i < n; i++) dist[i] = int.MaxValue;
            if (n == 0) return dist;

            // Обратные рёбра: кто может ПРИВЕСТИ в эту команду.
            var back = new List<int>[n];
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                foreach (var next in Successors(script, labels, i))
                {
                    if (next >= n)
                    {
                        // Ребро «в конец главы»: сама команда стоит в шаге от него.
                        if (dist[i] > 1) { dist[i] = 1; queue.Enqueue(i); }
                        continue;
                    }
                    if (next < 0) continue;
                    (back[next] ??= new List<int>()).Add(i);
                }
            }

            while (queue.Count > 0)
            {
                int at = queue.Dequeue();
                var from = back[at];
                if (from == null) continue;
                foreach (var prev in from)
                    if (dist[prev] > dist[at] + 1)
                    {
                        dist[prev] = dist[at] + 1;
                        queue.Enqueue(prev);
                    }
            }
            return dist;
        }

        /// <summary>Куда управление может уйти с команды <paramref name="i"/>.
        /// Индекс, равный длине скрипта, означает «в конец главы».</summary>
        private static IEnumerable<int> Successors(JArray script,
            IReadOnlyDictionary<string, int> labels, int i)
        {
            int n = script.Count;
            if (!(script[i] is JObject c)) { yield return i + 1; yield break; }
            var op = (string)c["op"];
            switch (op)
            {
                case "goto":
                case "jump":
                    yield return Target(labels, (string)c["label"] ?? (string)c["goto"], n);
                    break;
                case "if":
                    yield return Target(labels, (string)c["then"], n);
                    // `if` без `else` проваливается на следующую команду — так
                    // обещают справочник языка и оба рантайма.
                    var els = (string)c["else"];
                    yield return string.IsNullOrEmpty(els) ? i + 1 : Target(labels, els, n);
                    break;
                case "choice":
                    bool anyTarget = false;
                    if (c["options"] is JArray opts)
                        foreach (var o in opts)
                        {
                            var g = (string)o?["goto"];
                            if (string.IsNullOrEmpty(g)) continue;
                            anyTarget = true;
                            yield return Target(labels, g, n);
                        }
                    // Вариант без `goto` продолжает главу с места выбора.
                    if (!anyTarget) yield return i + 1;
                    break;
                case "end":
                case "return":
                    yield return n; // конец главы / выход из подпрограммы
                    break;
                default:
                    // `call` идёт сюда намеренно: подпрограмма ВОЗВРАЩАЕТСЯ, и на
                    // кратчайшем пути главы она — один шаг в сторону, а не ветка.
                    yield return i + 1;
                    break;
            }
        }

        private static int Target(IReadOnlyDictionary<string, int> labels, string label, int n)
        {
            if (string.IsNullOrEmpty(label)) return n;
            // `__end` — служебная метка конца; её может не быть в файле вовсе.
            if (labels != null && labels.TryGetValue(label, out var at)) return at;
            return n;
        }
    }
}
