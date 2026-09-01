using System;

namespace Lvn
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
        /// <summary>
        /// БАЗОВЫЙ АДРЕС — без хвостовой косой.
        ///
        /// <para>Правило на одну строку и потому написанное ДЕВЯТЬ раз:
        /// загрузчик контента, хранилище состояния, сид APK, сетевые ассеты,
        /// комната, экран выбора сервера (дважды) и разбор параметров запуска.
        /// Забывший его получает <c>host//v1/…</c> — двойную косую, на которой
        /// одни серверы отвечают, а другие дают 404, и разницу видно только на
        /// чужом хосте.</para>
        ///
        /// <para>Дом переехал в ядро ради этого же: сервисы слоя контента не
        /// видят, и комната писала правило своим кодом — иначе не могла.</para>
        /// </summary>
        public static string Base(string url) => (url ?? "").TrimEnd('/');

        /// <summary>Адрес без строки запроса. Пустое остаётся пустым.</summary>
        public static string Bare(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int q = url.IndexOf('?');
            return q >= 0 ? url.Substring(0, q) : url;
        }

        /// <summary>
        /// ЗАПРОС ЗА АДРЕСОМ — вторая половина того же факта, что и
        /// <see cref="Bare"/>: где кончается путь и начинается запрос.
        ///
        /// <para>Обе половины нужны в разных местах — путь чистят перед
        /// сравнением файлов, запрос читают у ссылки-диплинка, — и жили они
        /// врозь: дом знал первую, разбор ссылки писал вторую сам. Пока факт
        /// один, а мест два, они расходятся молча: у ссылки вида
        /// «…?title=cold#top» якорь надо отбросить, и помнить об этом должен
        /// дом, а не каждый читающий.</para>
        ///
        /// <para>Пусто — запроса нет вовсе.</para>
        /// </summary>
        public static string Query(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int q = url.IndexOf('?');
            if (q < 0) return "";
            var tail = url.Substring(q + 1);
            int hash = tail.IndexOf('#');
            return hash >= 0 ? tail.Substring(0, hash) : tail;
        }

        /// <summary>Файл-СОСЕД: тот же адрес с другим хвостом («ch1.lvn» + «.ru.json»
        /// → «ch1.ru.json»). Запрос за адресом переезжает в конец, а не в середину
        /// имени: приклеенный к имени «?v=7» давал файл, которого нет, — каталог
        /// перевода не находился, и глава молча оставалась на языке автора.</summary>
        public static string Sibling(string url, string suffix)
        {
            if (string.IsNullOrEmpty(url)) return url;
            var bare = Bare(url);
            var query = url.Length > bare.Length ? url.Substring(bare.Length) : "";
            int dot = bare.LastIndexOf('.');
            int slash = bare.LastIndexOfAny(new[] { '/', '\\' });
            if (dot > slash) bare = bare.Substring(0, dot);
            return bare + suffix + query;
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
            var (_, ext) = SplitExtension(url);
            return ext.Length == 0 ? "" : ext.Substring(1).ToLowerInvariant();
        }

        /// <summary>ГДЕ КОНЧАЕТСЯ ИМЯ И НАЧИНАЕТСЯ РАСШИРЕНИЕ — одно правило на
        /// обоих спрашивающих.
        ///
        /// <para>Правило кажется в одну строку, и потому его писали второй раз
        /// руками — там, где нужны обе половины, а не только расширение. В
        /// рукописной копии не было ГЛАВНОГО: точка в имени ПАПКИ расширением
        /// не считается. Адрес вида <c>content/v1.2/cover</c> рукописный разбор
        /// делит по точке в «v1.2», и дальше рассуждает про несуществующее
        /// расширение «.2/cover».</para>
        ///
        /// <para>Расширение возвращается С ТОЧКОЙ: половинки должны склеиваться
        /// обратно в исходный адрес без догадок о том, кто её потерял.</para>
        /// </summary>
        public static (string Stem, string Ext) SplitExtension(string url)
        {
            var u = Bare(url);
            int dot = u.LastIndexOf('.');
            int slash = u.LastIndexOf('/');
            if (dot <= 0 || dot < slash) return (u, "");
            return (u.Substring(0, dot), u.Substring(dot));
        }
    }
}
