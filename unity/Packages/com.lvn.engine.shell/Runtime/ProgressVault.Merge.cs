using System;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json.Linq;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ДВА УСТРОЙСТВА ОДНОГО ИГРОКА — телефон и планшет под одним аккаунтом.
    ///
    /// <para>Правила не было: побеждала последняя запись. Копия, записавшаяся
    /// позже, затирала другую ЦЕЛИКОМ — прохождение, открытые картинки,
    /// гардероб. Игрок, читавший вечером на планшете, утром открывал телефон и
    /// «терял вечер», причём сервер честно хранил ровно то, что ему прислали.</para>
    ///
    /// <para>Правило здесь не одно на всё, а СВОЁ У КАЖДОГО ВИДА ДАННЫХ, потому
    /// что цена ошибки у них разная:</para>
    /// <list type="bullet">
    ///   <item>потолок глав и галерея — НАКОПИТЕЛЬНЫЕ: только растут. Отнять
    ///   открытое нельзя даже у отставшего устройства: второго показа не
    ///   будет.</item>
    ///   <item>точка продолжения, имя и надетое — ПОСЛЕДНЯЯ ВОЛЯ игрока: их
    ///   решает свежесть, а не порядок записи. Финал — тоже ход: свежий финал
    ///   снимает закладку, старый — не смеет тронуть живой повтор.</item>
    ///   <item>незнакомое поле переезжает как есть: свёрток не забывает, и
    ///   между устройствами тоже.</item>
    /// </list>
    ///
    /// <para>Чего здесь НЕТ намеренно: КОШЕЛЁК. Складывать два баланса значит
    /// печатать деньги, а брать свежий — терять покупку; он живёт
    /// сервер-авторитетным леджером операций и через свёрток не ходит.</para>
    /// </summary>
    internal static partial class ProgressVault
    {
        /// <summary>Слить два свёртка. Симметрично: порядок аргументов на
        /// ответ не влияет, решает штамп времени внутри.</summary>
        public static JObject Merge(JObject a, JObject b)
        {
            if (a == null) return b == null ? new JObject() : (JObject)b.DeepClone();
            if (b == null) return (JObject)a.DeepClone();

            long atA = Stamp(a, 0), atB = Stamp(b, 0);
            JObject fresh = atA >= atB ? a : b, stale = atA >= atB ? b : a;
            long atF = Math.Max(atA, atB), atS = Math.Min(atA, atB);

            // Незнакомое поле переживает слияние: за основу берём СТАРУЮ
            // сторону и накрываем свежей — всё, чего у свежей нет, остаётся.
            var res = (JObject)stale.DeepClone();
            foreach (var p in fresh.Properties())
                if (p.Name != "titles" && p.Name != "wardrobe" && p.Name != "name")
                    res[p.Name] = p.Value?.DeepClone();
            res["at"] = atF;

            // Имя спрашивают один раз за установку: пустая сторона не стирает
            // его, спор двух имён решает более позднее решение игрока.
            var nameF = (string)fresh["name"];
            res["name"] = string.IsNullOrEmpty(nameF) ? (string)stale["name"] : nameF;

            res["titles"] = MergeSections(fresh["titles"] as JObject, atF,
                                          stale["titles"] as JObject, atS, MergeTitleEntry);
            res["wardrobe"] = MergeSections(fresh["wardrobe"] as JObject, atF,
                                            stale["wardrobe"] as JObject, atS, MergeWardrobeEntry);
            return res;
        }

        /// <summary>Раздел свёртка: ключи объединяются, знакомое сводит правило
        /// вида данных. У записи может стоять СВОЙ штамп — новелла, в которую
        /// не заходили неделю, не должна считаться свежей только потому, что
        /// устройство писало свёрток минуту назад.</summary>
        private static JObject MergeSections(JObject fresh, long atFresh, JObject stale, long atStale,
                                             Action<JObject, JObject> merge)
        {
            var res = stale != null ? (JObject)stale.DeepClone() : new JObject();
            if (fresh == null) return res;
            foreach (var p in fresh.Properties())
            {
                if (!(res[p.Name] is JObject mine) || !(p.Value is JObject theirs))
                {
                    res[p.Name] = p.Value?.DeepClone();
                    continue;
                }
                long am = Stamp(mine, atStale), at = Stamp(theirs, atFresh);
                var winner = at >= am ? theirs : mine;
                var loser = at >= am ? mine : theirs;
                var one = (JObject)loser.DeepClone();
                foreach (var q in winner.Properties()) one[q.Name] = q.Value?.DeepClone();
                merge(one, loser);
                merge(one, winner);
                one["at"] = Math.Max(am, at);
                res[p.Name] = one;
            }
            return res;
        }

        /// <summary>Новелла: потолок только растёт, галерея только доливается.
        /// Точка и номер уже пришли от свежей стороны.</summary>
        private static void MergeTitleEntry(JObject into, JObject from)
        {
            if (from == null) return;
            int reached = (int?)from["reached"] ?? 0;
            if (reached > ((int?)into["reached"] ?? 0)) into["reached"] = reached;
            var gallery = UnionArray(into["gallery"] as JArray, from["gallery"] as JArray);
            if (gallery != null) into["gallery"] = gallery;
        }

        /// <summary>Гардероб: «встреченное» доливается по осям, надетое уже
        /// решено свежестью — но ось, тронутая только на отставшем устройстве,
        /// не снимается: она не спор, а чужая половина жизни игрока.</summary>
        private static void MergeWardrobeEntry(JObject into, JObject from)
        {
            if (from == null) return;
            if (from["worn"] is JObject worn)
            {
                if (!(into["worn"] is JObject mine)) into["worn"] = worn.DeepClone();
                else
                    foreach (var a in worn.Properties())
                        if (mine[a.Name] == null) mine[a.Name] = a.Value?.DeepClone();
            }
            if (from["seen"] is JObject seen)
            {
                if (!(into["seen"] is JObject mine)) into["seen"] = seen.DeepClone();
                else
                    foreach (var a in seen.Properties())
                    {
                        var axis = UnionArray(mine[a.Name] as JArray, a.Value as JArray);
                        if (axis != null) mine[a.Name] = axis;
                    }
            }
        }

        /// <summary>Объединение без дублей; null, когда объединять нечего.</summary>
        private static JArray UnionArray(JArray a, JArray b)
        {
            if (a == null && b == null) return null;
            var seen = new System.Collections.Generic.HashSet<string>();
            var res = new JArray();
            foreach (var src in new[] { a, b })
                if (src != null)
                    foreach (var v in src)
                    {
                        var id = (string)v;
                        if (!string.IsNullOrEmpty(id) && seen.Add(id)) res.Add(id);
                    }
            return res.Count > 0 ? res : null;
        }

        private static long Stamp(JToken t, long fallback) => (long?)t?["at"] ?? fallback;

        /// <summary>
        /// ВПИТАТЬ ЧУЖОЙ СВЁРТОК в живые сторы этого устройства.
        ///
        /// <para>Отличие от <see cref="Apply"/> — в праве двигать закладку.
        /// Восстановление садится только на пустое: оно поднимает устройство,
        /// у которого своего прогресса нет, и обгонять живую игру ему нельзя.
        /// Здесь же обе стороны настоящие, спор уже решён свежестью в
        /// <see cref="Merge"/> — и решение надо ИСПОЛНИТЬ, включая снятие
        /// закладки чужим финалом.</para>
        /// </summary>
        public static void Absorb(JObject other, LvnManifest manifest)
        {
            if (other == null || manifest == null) return;
            var merged = Merge(ReadLocal(), other);
            WriteLocal(merged);            // сейф устройства держит уже сведённую правду
            Plant(merged, manifest, authoritative: true);
        }
    }
}
