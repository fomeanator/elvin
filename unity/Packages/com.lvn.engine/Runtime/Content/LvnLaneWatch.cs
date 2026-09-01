using System.Collections.Generic;

namespace Lvn.Content
{
    /// <summary>
    /// СКОЛЬКО ЖДАЛИ МЕСТА И КТО ИМЕННО ПРОСИЛ.
    ///
    /// <para>Полоса пропускания устроена вокруг ступени: живому берегут места,
    /// фон уступает. Всё это проверяемо текстом ровно до одной границы — до
    /// вопроса «а ту ли ступень ей назвали». 01.09 расписание главы объявляло
    /// ступень СВОЕЙ полосе, у которой брони нет, а в полосу сети уходило
    /// молча, то есть КАК ЖИВОЕ. Строка была на месте, имя роли правильное,
    /// комментарий объяснял замысел — не совпадал только адресат.</para>
    ///
    /// <para>Ни один страж этого не увидел: все спрашивали «объявлена?» и
    /// получали «да». Забытое видно по отсутствию, объявленное не тому — по
    /// наличию, и текстового признака у него нет.</para>
    ///
    /// <para><b>Зато есть числовой.</b> Живых входов в полосу сети за главу
    /// столько, сколько актёров и фонов на экране, — восемь, десять. Сорок
    /// шесть живых входов — это не «медленно», это неправда о природе работы, и
    /// её видно с первого взгляда на строку отчёта.</para>
    ///
    /// <para>Дом устроен по образцу счётчика запинок кадра
    /// (<see cref="LvnFrameWatch"/>), который однажды уже превратил «дёргается»
    /// в величину: копим, снимаем на конце главы, отправляем тем же событием.
    /// Ничего не логируем по ходу — диагностика не смеет стоить кадров.</para>
    /// </summary>
    public static class LvnLaneWatch
    {
        /// <summary>Итог по одной паре «полоса + ступень».</summary>
        public struct Tally
        {
            public string Lane;
            public LvnRung Rung;
            public int Entries;      // сколько раз заходили
            public long WaitedMs;    // сколько ждали всего
            public int WorstMs;      // худшее одиночное ожидание
            public int Yields;       // сколько раз уступили место живому
        }

        private static readonly Dictionary<(string, LvnRung), Tally> _tally
            = new Dictionary<(string, LvnRung), Tally>();
        private static readonly object _lock = new object();

        /// <summary>Место занято. <paramref name="waitedMs"/> — сколько простоял
        /// в очереди; ноль означает «место было свободно».</summary>
        public static void Entered(string lane, LvnRung rung, long waitedMs)
            => Amend(lane, rung, t =>
            {
                t.Entries++;
                t.WaitedMs += waitedMs;
                if (waitedMs > t.WorstMs) t.WorstMs = (int)waitedMs;
                return t;
            });

        /// <summary>НАЙТИ ИЛИ ЗАВЕСТИ ЗАПИСЬ и поправить её под замком.
        ///
        /// <para>Эти восемь строк — поиск по паре «полоса + ступень», починка
        /// отсутствующей записи и возврат её на место — стояли в обоих
        /// счётчиках дословно, различаясь одной строкой посередине. Заметил я
        /// это не глазами: сплошной обход почти-двойников нашёл мой же
        /// сегодняшний код. Механизм здесь — учёт по паре ключей; что именно
        /// прибавить, знает вызывающий.</para></summary>
        private static void Amend(string lane, LvnRung rung, System.Func<Tally, Tally> change)
        {
            if (string.IsNullOrEmpty(lane)) return;
            lock (_lock)
            {
                var key = (lane, rung);
                _tally.TryGetValue(key, out var t);
                t.Lane = lane; t.Rung = rung;
                _tally[key] = change(t);
            }
        }

        /// <summary>Место вернули живому по просьбе.</summary>
        public static void Yielded(string lane, LvnRung rung)
            => Amend(lane, rung, t => { t.Yields++; return t; });

        /// <summary>Сколько живых входов и сколько худшее их ожидание — два
        /// числа, которые едут в отчёт главы. Первое ловит «объявлено не тому»,
        /// второе — «бронь не работает».</summary>
        /// <param name="lane">Пусто — считать по всем полосам (так снимает
        /// глава). Имя — только по ней.
        ///
        /// <para>Отбор по полосе появился из красного теста: счёт общий, а
        /// работа асинхронная, и хвост соседнего испытания долетал уже после
        /// того, как это очистило счётчик. Тест мерил СВОЮ полосу, а получал
        /// чужие входы — и краснел не о том. Спрашивать про одну полосу дом
        /// умел с рождения: ключ у него и так пара «полоса + ступень».</para>
        /// </param>
        public static (int liveEntries, int worstLiveWaitMs, int backgroundEntries, int yields) Take(string lane = null)
        {
            lock (_lock)
            {
                int live = 0, worst = 0, back = 0, yields = 0;
                foreach (var kv in _tally)
                {
                    if (lane != null && kv.Key.Item1 != lane) continue;
                    var t = kv.Value;
                    yields += t.Yields;
                    if (t.Rung == LvnRung.Live) { live += t.Entries; if (t.WorstMs > worst) worst = t.WorstMs; }
                    else back += t.Entries;
                }
                if (lane == null) _tally.Clear();
                else foreach (var kv in new List<(string, LvnRung)>(_tally.Keys))
                    if (kv.Item1 == lane) _tally.Remove(kv);
                return (live, worst, back, yields);
            }
        }

        /// <summary>Разбор по полосам и ступеням — для лога, когда число из
        /// отчёта показалось странным.</summary>
        public static string Report()
        {
            lock (_lock)
            {
                if (_tally.Count == 0) return "[lvn-lane] заходов не было";
                var sb = new System.Text.StringBuilder("[lvn-lane]");
                foreach (var kv in _tally)
                {
                    var t = kv.Value;
                    sb.Append(' ').Append(t.Lane).Append('/').Append(t.Rung)
                      .Append('=').Append(t.Entries)
                      .Append(" (ждали ").Append(t.WaitedMs).Append(" мс, худшее ")
                      .Append(t.WorstMs).Append(" мс");
                    if (t.Yields > 0) sb.Append(", уступок ").Append(t.Yields);
                    sb.Append(')');
                }
                return sb.ToString();
            }
        }
    }
}
