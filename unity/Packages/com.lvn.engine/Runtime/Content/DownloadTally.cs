namespace Lvn.Content
{
    /// <summary>
    /// ЧТО ПОКАЗЫВАЕТ ИНДИКАТОР ЗАГРУЗКИ — одна арифметика на все его числа.
    ///
    /// <para>Причина существования: числа расходились между собой на глазах у
    /// игрока. «Скачано 296 МБ из 298 МБ» и рядом «Осталось ≈114 МБ» — снимок
    /// живой игры, где два соседних поля отвечали на РАЗНЫЕ вопросы: первое про
    /// текущий пакет, второе про весь недостающий контент. Кольцо при этом
    /// стояло почти полным с первых процентов, потому что знаменатель считался
    /// догадкой «принято + 64 КБ × непочатые», а настоящая медиана файла —
    /// четверть мегабайта. Замер: при реальных 5 % кольцо рисовало 36 %, при
    /// 26 % — 72 % (qa/download-progress-check.sh).</para>
    ///
    /// <para>Отсюда правило: у индикатора ОДИН источник правды — план. План —
    /// это сумма весов файлов, поставленных в работу; веса известны из
    /// манифеста ещё до первого байта. Доля, остаток и подпись считаются
    /// отсюда и только отсюда, а вопрос «сколько всего не на устройстве» —
    /// другой вопрос, у него своё место на экране.</para>
    ///
    /// <para>Тип НАРОЧНО без единой ссылки на UnityEngine: он проверяется
    /// прогоном вне редактора (qa/download-progress-check.sh компилирует его
    /// вместе со сценарием и запускает), а редактор занят или долог.</para>
    /// </summary>
    public readonly struct DownloadTally
    {
        /// <summary>Что происходит с загрузкой прямо сейчас — словами, а не
        /// числами: игроку важнее «идёт или встало», чем третий знак после
        /// запятой.</summary>
        public enum Phase
        {
            /// <summary>Работы нет.</summary>
            Idle,
            /// <summary>Байты идут.</summary>
            Running,
            /// <summary>Работа есть, а байтов нет дольше порога: сеть молчит,
            /// сервер думает, файл встал. Именно это раньше выглядело как
            /// «качается на 21,9 МБ/с» — скорость показывалась ПОСЛЕДНЯЯ
            /// известная и не гасла никогда.</summary>
            Stalled,
            /// <summary>Сети нет вовсе — загрузка продолжится сама.</summary>
            Offline,
            /// <summary>Скачивать нечего, но есть события к отправке.</summary>
            Syncing,
        }

        /// <summary>Сколько байт плана уже принято.</summary>
        public readonly long DoneBytes;
        /// <summary>Вес всей поставленной работы, байт. Ноль — план неизвестен
        /// (веса не дали): тогда доли нет, и показывать надо движение, а не
        /// число.</summary>
        public readonly long PlanBytes;
        /// <summary>Файлов закрыто и всего в работе.</summary>
        public readonly int DoneFiles, TotalFiles;
        /// <summary>Байт в секунду, сглажено. Ноль — не идёт.</summary>
        public readonly float BytesPerSecond;
        public readonly Phase State;

        public DownloadTally(long doneBytes, long planBytes, int doneFiles, int totalFiles,
            float bytesPerSecond, Phase state)
        {
            DoneBytes = doneBytes < 0 ? 0 : doneBytes;
            PlanBytes = planBytes < 0 ? 0 : planBytes;
            DoneFiles = doneFiles < 0 ? 0 : doneFiles;
            TotalFiles = totalFiles < 0 ? 0 : totalFiles;
            BytesPerSecond = bytesPerSecond < 0f ? 0f : bytesPerSecond;
            State = state;
        }

        /// <summary>Есть ли план в байтах. Без него кольцу нечего показывать —
        /// и оно обязано КРУТИТЬСЯ, а не стоять полным: полное кольцо читается
        /// как «всё скачано», и это была самая частая жалоба.</summary>
        public bool PlanKnown => PlanBytes > 0;

        /// <summary>Доля выполненного, 0..1; −1 значит «плана нет, крути
        /// спиннер». Принято больше плана (файл вырос с прошлой редакции
        /// манифеста) — это единица, а не полтора.</summary>
        public float Fraction
        {
            get
            {
                if (!PlanKnown) return -1f;
                if (DoneBytes >= PlanBytes) return 1f;
                return (float)((double)DoneBytes / PlanBytes);
            }
        }

        /// <summary>Сколько осталось по ЭТОМУ плану, байт. Единственный ответ
        /// на «осталось» в индикаторе: второй источник ровно здесь и
        /// расходился с первым на глазах у игрока.</summary>
        public long LeftBytes => !PlanKnown ? 0 : (PlanBytes > DoneBytes ? PlanBytes - DoneBytes : 0);

        /// <summary>Оценка времени, секунд; отрицательное — «сказать нечего»
        /// (нет плана, нет скорости или загрузка стоит). Врать «осталось 2
        /// секунды» на вставшей загрузке хуже, чем молчать.</summary>
        public float EtaSeconds
        {
            get
            {
                if (!PlanKnown || State != Phase.Running || BytesPerSecond <= 1f) return -1f;
                return LeftBytes / BytesPerSecond;
            }
        }

        /// <summary>Кончено ли: план есть и он выбран целиком.</summary>
        public bool Complete => PlanKnown && DoneBytes >= PlanBytes;

        /// <summary>Собрать состояние из того, что знает загрузчик.
        ///
        /// <para><paramref name="quietSeconds"/> — сколько времени байты не
        /// прибавлялись. Порог намеренно большой (несколько секунд): короткие
        /// паузы между файлами — норма, и мигать словом «встало» на каждой
        /// паузе значит приучить игрока это слово не читать.</para></summary>
        public static Phase PhaseOf(bool hasWork, bool offline, int pendingOps,
            float quietSeconds, float stallAfterSeconds = 4f)
        {
            if (offline && hasWork) return Phase.Offline;
            if (!hasWork) return pendingOps > 0 ? Phase.Syncing : Phase.Idle;
            return quietSeconds >= stallAfterSeconds ? Phase.Stalled : Phase.Running;
        }
    }
}
