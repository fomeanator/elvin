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
