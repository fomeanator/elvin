using System;

namespace Lvn
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
    ///
    /// <para>Живёт в ЯДРЕ, а не в сборке интерфейса: приставку пишет автор, и
    /// знать её должны все, кто держит в руках адрес контента, — поставщики
    /// ассетов, загрузчик, индекс версий, посев. Пока дом стоял в интерфейсе,
    /// слой контента до него не дотягивался и писал слово заново — так и
    /// завелась третья копия среза в <c>ContentLoader.Lookup</c>.</para>
    ///
    /// <para>Приставка — это СЛОВО «content» в голове адреса; ведущая косая при
    /// нём — знак препинания, а не часть имени. Поэтому срез принимает оба
    /// написания: <c>/content/bg/x.png</c> и <c>content/bg/x.png</c> дают один
    /// и тот же путь. Копии расходились именно здесь: индекс версий срезал
    /// бесслэшную форму, поставщики — только слэшную.</para>
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
            if (!string.IsNullOrEmpty(prefix))
            {
                // Слэшная форма — как пишет автор; бесслэшная — как остаётся от
                // адреса, у которого ведущую косую уже сняли (ключи индекса
                // версий, пути внутри архива).
                var bare = prefix.TrimStart('/');
                if (rel.StartsWith(prefix, StringComparison.Ordinal))
                    rel = rel.Substring(prefix.Length);
                else if (bare.Length > 0 && rel.StartsWith(bare + "/", StringComparison.Ordinal))
                    rel = rel.Substring(bare.Length);
            }
            return rel.TrimStart('/');
        }

        /// <summary>Обратная дорога: адрес известного файла ПОД корнем контента.
        /// Такие адреса движок строит сам (индекс версий, каталог слов, плитка
        /// темы) — и каждый писал приставку своей строкой.</summary>
        public static string Under(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            return ContentPrefix + "/" + relative.TrimStart('/');
        }
    }
}
