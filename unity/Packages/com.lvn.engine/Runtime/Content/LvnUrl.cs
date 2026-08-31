using System;

namespace Lvn.Content
{
    /// <summary>
    /// АДРЕС РЕСУРСА — общие правила разбора.
    ///
    /// <para>Обрезка строки запроса делалась в четырёх местах под тремя
    /// именами (<c>Bare</c>, <c>StripQuery</c> и дважды по месту). Это ключ
    /// кэша: разойдись правило на один символ — и один и тот же файл считается
    /// то скачанным, то нет, а офлайн начинает вести себя по-разному в разных
    /// экранах.</para>
    ///
    /// <para>СХЕМА — та же история этажом выше. «Свой ли это адрес» решали в
    /// семи местах тремя разными способами: «начинается на http», «на http://
    /// или https://», «на file:// или jar:». Первый из трёх считал ЛОКАЛЬНЫЙ
    /// адрес относительным — приписывал к нему базу и кодировал, — а за
    /// <c>file://</c> и <c>jar:file://</c> стоит чтение с диска
    /// (<c>File.Exists</c>, распаковка из APK), где «%20» означает файл,
    /// которого нет. Сетевой адрес, наоборот, кодировать ОБЯЗАТЕЛЬНО: пробелы,
    /// скобки и кириллица в имени файла — обычное дело для арта от художника,
    /// а запрос их не экранирует.</para>
    /// </summary>
    public static class LvnUrl
    {
        /// <summary>Адрес без строки запроса. Пустое остаётся пустым.</summary>
        public static string Bare(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int q = url.IndexOf('?');
            return q >= 0 ? url.Substring(0, q) : url;
        }

        /// <summary>Сетевой адрес: за ним запрос, и путь в нём кодируют.</summary>
        public static bool Remote(string url)
            => !string.IsNullOrEmpty(url)
               && (url.StartsWith("http://") || url.StartsWith("https://"));

        /// <summary>Локальный адрес: за ним чтение с диска, и кодировать его
        /// НЕЛЬЗЯ. <c>jar:file://</c> — это содержимое APK.</summary>
        public static bool Local(string url)
            => !string.IsNullOrEmpty(url)
               && (url.StartsWith("file://") || url.StartsWith("jar:"));

        /// <summary>Адрес сам себе хозяин — базу к нему не приписывают.</summary>
        public static bool Absolute(string url) => Remote(url) || Local(url);

        /// <summary>Расширение в нижнем регистре, без точки. Считается по
        /// адресу БЕЗ запроса — иначе «.png?v=3» перестаёт быть картинкой.</summary>
        public static string Extension(string url)
        {
            var u = Bare(url);
            int dot = u.LastIndexOf('.');
            int slash = u.LastIndexOf('/');
            if (dot < 0 || dot < slash) return "";
            return u.Substring(dot + 1).ToLowerInvariant();
        }
    }
}
