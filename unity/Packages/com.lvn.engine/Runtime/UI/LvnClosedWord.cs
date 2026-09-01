using System.Collections.Generic;

namespace Lvn.UI
{
    /// <summary>
    /// ЗАКРЫТОЕ СЛОВО, КОТОРОГО НЕТ В СПИСКЕ.
    ///
    /// <para>Часть авторских значений — закрытый перечень: <c>justify=center</c>,
    /// <c>align=stretch</c>, <c>dir=row</c>. Разбираются они перечислением
    /// случаев, и у перечисления есть тихий исход: слово не совпало ни с одним,
    /// значит не произошло НИЧЕГО. Автор написал <c>justify=middle</c>, увидел
    /// вёрстку по умолчанию и ушёл искать ошибку в другом месте.</para>
    ///
    /// <para>Движок уже умеет так про КОМАНДЫ: незнакомый op считается и уходит
    /// в отчёт, потому что «узнавать об этом надо не от игрока». Здесь то же
    /// самое для значений — и по той же причине.</para>
    ///
    /// <para>Один раз на пару «поле + слово» за сессию: опечатка в цикле
    /// перерисовки повторится сотни раз, а сказать надо однажды и внятно.</para>
    /// </summary>
    public static class LvnClosedWord
    {
        private static readonly Dictionary<string, int> _seen = new Dictionary<string, int>();

        /// <summary>Сколько раз встретилось каждое незнакомое слово (ключ —
        /// «поле=слово»). Для отчёта и тестов.</summary>
        public static IReadOnlyDictionary<string, int> Unclaimed => _seen;

        /// <summary>Забыть счёт — конец главы, начало новой сессии.</summary>
        public static void Reset() => _seen.Clear();

        /// <summary>
        /// Слово не из списка. Возвращает false — чтобы вызывающий мог написать
        /// <c>default: Unknown(...); break;</c> и не думать о возвращаемом
        /// значении, либо использовать его в условии.
        /// </summary>
        public static bool Unknown(string field, string value, string allowed)
        {
            if (string.IsNullOrEmpty(value)) return false;   // «не сказано» — не ошибка
            var key = field + "=" + value;
            if (_seen.TryGetValue(key, out int n)) { _seen[key] = n + 1; return false; }
            _seen[key] = 1;
            UnityEngine.Debug.LogWarning(
                $"[lvn] {field}=\"{value}\" — такого значения нет, и команда в этой части " +
                $"не сделала НИЧЕГО. Допустимые: {allowed}. Сказано один раз за сессию; " +
                "полный счёт — в LvnClosedWord.Unclaimed.");
            return false;
        }
    }
}
