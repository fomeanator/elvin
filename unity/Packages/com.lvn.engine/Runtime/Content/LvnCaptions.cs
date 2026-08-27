namespace Lvn.Content
{
    /// <summary>
    /// ТИТРОВАЛЬЩИК — как называется глава на экране.
    ///
    /// <para>Правило простое: есть имя эпизода — показываем его; нет — «Глава
    /// N»; нет и номера — идентификатор. Записано оно было ЧЕТЫРЕЖДЫ: в
    /// карусели, в перезапуске с карточки новеллы, в титре главы и в подписи
    /// перехода. И четыре копии успели разойтись — не потенциально, а на живом
    /// экране: две писали «Chapter 3», две «Глава 3». Одно приложение звало
    /// одну и ту же главу по-разному, смотря с какого экрана на неё смотреть.
    /// Титр главы вдобавок терял имя эпизода — показывал номер даже там, где у
    /// автора есть название.</para>
    ///
    /// <para>Слово «Глава» принадлежит автору (docs/language-policy.md):
    /// движок держит английское умолчание, новелла задаёт своё в
    /// <c>ui.chapter_word</c> — «Эпизод», «Дело», «День».</para>
    /// </summary>
    public static class LvnCaptions
    {
        /// <summary>Умолчание движка — системное, английское.</summary>
        public const string DefaultChapterWord = "Chapter";

        /// <summary>Как эта новелла зовёт главу. Отдельное поле манифеста
        /// (<c>ui.chapter_word</c>) остаётся ради совместимости и удобства —
        /// оно старше словаря; пусто — слово спрашивается у СЛОВАРЯ по ключу
        /// «chapter.word». Титровальщик знает ПРАВИЛО (имя эпизода → номер →
        /// id), слово ему даёт Словарь: два хранилища одного слова разошлись
        /// бы на первой же правке.</summary>
        public static string ChapterWord;

        /// <summary>
        /// ПОЛНОЕ ИМЯ ГЛАВЫ для списков и кнопок: имя эпизода, иначе «Глава N»,
        /// иначе идентификатор. Пустая строка честнее выдуманного заголовка.
        /// </summary>
        public static string Chapter(LvnChapter c)
        {
            if (c == null) return string.Empty;
            if (!string.IsNullOrEmpty(c.name)) return c.name;
            return c.number > 0 ? Numbered(c.number) : (c.id ?? string.Empty);
        }

        /// <summary>ТОЛЬКО НОМЕР — «Глава 3». Нужен там, где имя эпизода стоит
        /// отдельной строкой и дублировать его в подзаголовке незачем (титр
        /// главы: номер сверху, название под ним).</summary>
        public static string ChapterNumberOnly(LvnChapter c)
            => c != null && c.number > 0 ? Numbered(c.number) : string.Empty;

        private static string Numbered(int number)
            => (string.IsNullOrEmpty(ChapterWord)
                    ? Lvn.UI.LvnWords.Of("chapter.word", DefaultChapterWord)
                    : ChapterWord)
               + " " + number;
    }
}
