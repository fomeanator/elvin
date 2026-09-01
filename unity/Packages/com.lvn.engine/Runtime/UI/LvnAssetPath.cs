namespace Lvn.UI
{
    /// <summary>
    /// АДРЕС КОНТЕНТА → ПУТЬ У ПОСТАВЩИКА.
    ///
    /// <para>Автор пишет в скрипте <c>/content/bg/room.png</c>. Поставщик
    /// ассетов держит своё: папка на диске, каталог Addressables, набор в
    /// сборке. Правило перевода одно — срезать приставку контента и ведущую
    /// косую, — и стояло оно ДВУМЯ копиями: у файлового поставщика и у
    /// адресуемого.</para>
    ///
    /// <para>Копии совпадали до буквы, включая умолчание приставки. Само
    /// умолчание — соглашение ЯЗЫКА, а не деталь поставщика: так пишут адреса в
    /// .lvn. Написанное дважды, оно означало бы, что сменить соглашение можно
    /// наполовину.</para>
    /// </summary>
    public static class LvnAssetPath
    {
        /// <summary>Приставка адресов контента в .lvn.</summary>
        public const string ContentPrefix = "/content";

        /// <summary>Путь относительно корня поставщика: без приставки и без
        /// ведущей косой. Пустой адрес остаётся пустым — «нечего показывать»
        /// решает зовущий, а не преобразование пути.</summary>
        public static string Relative(string url, string prefix = ContentPrefix)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var rel = url;
            if (!string.IsNullOrEmpty(prefix) && rel.StartsWith(prefix))
                rel = rel.Substring(prefix.Length);
            return rel.TrimStart('/');
        }
    }
}
