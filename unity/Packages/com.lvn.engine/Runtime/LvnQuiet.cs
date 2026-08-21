using System;

namespace Lvn
{
    /// <summary>
    /// «СДЕЛАТЬ, А НЕ ВЫЙДЕТ — И ЛАДНО» — уборка на пути ошибки.
    ///
    /// <para>В движке набралось три десятка пустых <c>catch { }</c>. Почти все
    /// честные: удалить недокачанный файл, оборвать запрос, разбудить ожидание.
    /// Если уборка не удалась, делать с этим нечего — но по коду не отличить
    /// осознанное молчание от забытого, а разница между ними огромная: первое
    /// решение, второе потерянная ошибка.</para>
    ///
    /// <para>Вызов через <c>LvnQuiet.Try</c> — это ПОДПИСЬ под тем, что
    /// молчание здесь намеренное. Всё остальное обязано либо обработать
    /// исключение, либо о нём сказать.</para>
    /// </summary>
    public static class LvnQuiet
    {
        /// <summary>Выполнить, проглотив любую неудачу. Возвращает, получилось
        /// ли, — на случай если вызывающему это всё-таки важно.</summary>
        public static bool Try(Action action)
        {
            if (action == null) return false;
            try { action(); return true; }
            catch { return false; }
        }

        /// <summary>То же для значения: не вышло — запасное.</summary>
        public static T Try<T>(Func<T> get, T fallback = default)
        {
            if (get == null) return fallback;
            try { return get(); }
            catch { return fallback; }
        }
    }
}
