using System.Collections.Generic;

namespace Lvn.UI
{
    /// <summary>
    /// ЗАКРЫТОЕ СЛОВО АВТОРА — как читается настройка, у которой есть список
    /// допустимых значений.
    ///
    /// <para>Таких настроек в манифесте много, и каждая читалась одинаково
    /// плохо: <c>switch</c> с <c>default</c>, который МОЛЧА берёт умолчание.
    /// Опечатка в <c>ui.browse.theme</c> отдавала «Полночь», и киберпанковая
    /// игра открывалась в облике по умолчанию; опечатка в виде появления
    /// отдавала «никак», и панель просто возникала. Автор видит не ошибку, а
    /// «почему-то не так», и ищет причину глазами.</para>
    ///
    /// <para>Манифест не проходит через структурный гейт — в отличие от
    /// скриптов, — поэтому сказать об опечатке больше НЕКОМУ: ни компилятор,
    /// ни валидатор его не читают. Значит, говорит тот, кто исполняет.</para>
    ///
    /// <para>Жалоба одна на пару «поле + слово» за запуск: настройку читают на
    /// каждой пересборке экрана, и повтор превратил бы лог в шум.</para>
    /// </summary>
    public static class LvnAuthorWord
    {
        private static readonly HashSet<string> _said = new HashSet<string>();

        /// <summary>
        /// Привести слово к известному. Пустое — не опечатка (настройку просто
        /// не задали), для него возвращается <paramref name="fallback"/> молча.
        /// </summary>
        /// <param name="raw">что написал автор</param>
        /// <param name="field">имя настройки для жалобы — «ui.browse.theme»</param>
        /// <param name="fallback">что берём, когда слова нет или оно чужое</param>
        /// <param name="known">весь допустимый набор, включая синонимы</param>
        public static string Pick(string raw, string field, string fallback, params string[] known)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            string w = raw.Trim().ToLowerInvariant();
            if (w.Length == 0) return fallback;
            for (int i = 0; i < known.Length; i++)
                if (known[i] == w) return w;
            if (_said.Add(field + "=" + w))
                UnityEngine.Debug.LogWarning(
                    $"[lvn-cfg] {field}=\"{raw}\" — такого значения нет, беру \"{fallback}\". "
                    + "Известны: " + string.Join(", ", known));
            return fallback;
        }

        /// <summary>Только для тестов: забыть, о чём уже жаловались.</summary>
        internal static void ForgetComplaints() => _said.Clear();
    }
}
