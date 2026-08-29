using System;
using Lvn.Content;

namespace Lvn.UI
{
    /// <summary>
    /// КАК ЯЗЫК ПИШЕТ ВРЕМЯ игроку — обратный отсчёт, давность и метка.
    ///
    /// <para>Одно и то же ожидание показывалось игроку двумя видами: плашка
    /// кошелька писала <c>1:12:30</c>, а всплывающее окно кассира про ту же
    /// энергию — «+1 через 1 ч 12 мин». Оба вида законны (в шапке нужен
    /// компактный, в объяснении — словесный), но правило перевода секунд в
    /// подпись было записано дважды, и разойтись им ничего не мешало.</para>
    ///
    /// <para>С сохранениями хуже: список слотов писал <c>27.08 14:32</c>, а
    /// экран новеллы про ТОТ ЖЕ слот — «2 h ago». Игрок видит два ответа на
    /// вопрос «когда это было» и не может их сложить.</para>
    ///
    /// <para>Слова — из новеллы (<see cref="LvnWords"/>), как и всё остальное,
    /// что игрок читает: движок знает только форму. Формат метки тоже слово
    /// (<c>time.stamp_format</c>) — «дд.ММ чч:мм» верен не в каждом языке.</para>
    ///
    /// <para>Секунды здесь всегда ЦЕЛЫЕ и всегда округляются ВНИЗ. Это не
    /// придирка: «осталось 1 мин» при 59 секундах честнее, чем «2 мин», потому
    /// что подпись обновится через секунду и число уменьшится — а выросшее
    /// ожидание читается как обман.</para>
    /// </summary>
    public static class LvnTimeWords
    {
        /// <summary>Компактный отсчёт: «3:07», а больше часа — «1:12:30».
        /// Цифровой вид языку не подчиняется — двоеточие читают везде.</summary>
        public static string Clock(long seconds)
        {
            if (seconds < 0) seconds = 0;
            long h = seconds / 3600, m = (seconds % 3600) / 60, s = seconds % 60;
            return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
        }

        /// <summary>Словесный отсчёт: «1 ч 12 мин», меньше часа — «12 мин»,
        /// меньше минуты — «1 мин». Ноль минут не пишем: «через 0 мин» читается
        /// как поломка, хотя означает «вот-вот».
        ///
        /// <para>Правило про ноль действовало только ниже часа, и ровно час
        /// показывался как «1 ч 0 мин» — та же поломка, только на час позже.
        /// Целый час называется часом.</para></summary>
        public static string Coarse(long seconds)
        {
            if (seconds < 0) seconds = 0;
            long h = seconds / 3600, m = (seconds % 3600) / 60;
            string hours = LvnWords.Of("unit.hours", "h");
            string minutes = LvnWords.Of("unit.minutes", "min");
            if (h > 0) return m > 0 ? $"{h} {hours} {m} {minutes}" : $"{h} {hours}";
            return $"{Math.Max(1, m)} {minutes}";
        }

        /// <summary>Давность события: «только что», «5 мин назад», «3 ч назад»,
        /// «2 дн назад». Пустая строка, если времени нет — подпись «01.01.1970»
        /// хуже отсутствующей.</summary>
        public static string Ago(long unixMs)
        {
            if (unixMs <= 0) return "";
            var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            if (span.TotalMinutes < 1) return LvnWords.Of("time.just_now", "just now");
            if (span.TotalMinutes < 60) return Fill("time.minutes_ago", "{n} min ago", (int)span.TotalMinutes);
            if (span.TotalHours < 24) return Fill("time.hours_ago", "{n} h ago", (int)span.TotalHours);
            return Fill("time.days_ago", "{n} d ago", (int)span.TotalDays);
        }

        /// <summary>Метка времени в местном поясе: «27.08 14:32». Формат —
        /// слово новеллы: порядок дня и месяца у языков разный, и жёсткая
        /// строка в коде читалась бы как ошибка ровно там, где её никто не
        /// ищет.</summary>
        public static string Stamp(long unixMs)
        {
            if (unixMs <= 0) return "";
            var when = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
            try { return when.ToString(LvnWords.Of("time.stamp_format", "dd.MM HH:mm")); }
            catch (FormatException)
            {
                // Кривой формат из манифеста не повод показать пустоту вместо
                // времени: жалуемся и пишем умолчанием.
                UnityEngine.Debug.LogWarning(
                    "[lvn-time] time.stamp_format не понят — метка написана умолчанием");
                return when.ToString("dd.MM HH:mm");
            }
        }

        // Подстановку числа знает дом слов — обе записи места, «{0}» и «{n}».
        private static string Fill(string key, string fallback, int n)
            => LvnWords.Of(key, fallback, n);
    }
}
