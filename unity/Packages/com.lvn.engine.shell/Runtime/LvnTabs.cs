using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ВКЛАДКА НИЖНЕГО МЕНЮ — одно понятие вместо пяти списков.
    ///
    /// <para>Вкладка была размазана: её место в ряду задавала сборка навбара,
    /// её подпись — свой switch по номеру, её страницу — второй switch в
    /// оболочке, её цвет полотна — массив на четыре ячейки, а «перейти на
    /// магазин» писалось как <c>TabGoTo(1)</c> с пояснением в комментарии.
    /// Пять перечней одного набора, и никакой из них не знал про остальные:
    /// галерея, например, есть в подписях и в ряду, но страницы у неё нет
    /// вовсе — она дверь в модаль, и оболочка про это не знала.</para>
    ///
    /// <para>Здесь вкладка описана один раз: номер, значок, слово, поле автора
    /// и есть ли у неё страница. Порядок в <see cref="Shown"/> — это порядок
    /// ПОКАЗА, и он намеренно не совпадает с номерами: галерея встаёт между
    /// гардеробом и профилем, а номер у неё последний, потому что номера —
    /// это история появления, а ряд — это то, что видит игрок.</para>
    /// </summary>
    public readonly struct LvnTab
    {
        public readonly int Index;
        public readonly LvnIcon Icon;
        /// <summary>Ключ перевода: он сильнее авторского поля.</summary>
        public readonly string Word;
        /// <summary>Подпись движка, когда нет ни перевода, ни поля автора.</summary>
        public readonly string Fallback;
        /// <summary>Есть ли у вкладки СТРАНИЦА ленты. Нет — значит вкладка
        /// открывает модаль и лента с места не двигается.</summary>
        public readonly bool HasPage;

        public LvnTab(int index, LvnIcon icon, string word, string fallback, bool hasPage)
        { Index = index; Icon = icon; Word = word; Fallback = fallback; HasPage = hasPage; }
    }

    /// <summary>Набор вкладок нижнего меню — см. <see cref="LvnTab"/>.</summary>
    public static class LvnTabs
    {
        public const int Home = 0;
        public const int Store = 1;
        public const int Wardrobe = 2;
        public const int Profile = 3;
        /// <summary>Галерея — вкладка БЕЗ страницы: открывает модаль.</summary>
        public const int Gallery = 4;

        /// <summary>Вкладки В ПОРЯДКЕ ПОКАЗА (не в порядке номеров).</summary>
        public static readonly IReadOnlyList<LvnTab> Shown = new[]
        {
            new LvnTab(Home,     LvnIcon.Home,     "nav.home",     "Home",     hasPage: true),
            new LvnTab(Store,    LvnIcon.Store,    "nav.store",    "Store",    hasPage: true),
            new LvnTab(Wardrobe, LvnIcon.Wardrobe, "nav.wardrobe", "Wardrobe", hasPage: true),
            new LvnTab(Gallery,  LvnIcon.Gallery,  "nav.gallery",  "Gallery",  hasPage: false),
            new LvnTab(Profile,  LvnIcon.Profile,  "nav.profile",  "Profile",  hasPage: true),
        };

        /// <summary>Сколько у ленты страниц — столько, сколько вкладок со
        /// страницей. Раньше это число стояло зашитым в ограничитель цвета
        /// полотна и разошлось бы с набором молча.</summary>
        public static int PageCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Shown.Count; i++) if (Shown[i].HasPage) n++;
                return n;
            }
        }

        /// <summary>Вкладка по номеру. Чужой номер даёт НЕ вкладку: у пустого
        /// значения номер −1, а не ноль — иначе сохранённый мусор молча
        /// объявлял бы себя главной.</summary>
        public static LvnTab Of(int index)
        {
            for (int i = 0; i < Shown.Count; i++) if (Shown[i].Index == index) return Shown[i];
            return new LvnTab(-1, default, null, null, hasPage: false);
        }

        /// <summary>Подпись вкладки: перевод сильнее авторского поля, оно
        /// сильнее умолчания движка. Раньше это правило стояло пять раз в
        /// одном switch, а обновить подписи было негде.</summary>
        public static string Label(int index, BrowseConfig cfg)
        {
            var tab = Of(index);
            if (string.IsNullOrEmpty(tab.Word)) return "";
            return LvnWords.Pick(tab.Word, Authored(index, cfg), tab.Fallback);
        }

        /// <summary>Что вписал автор новеллы. Отдельно от подписи: правило
        /// «кто кого сильнее» одно на всех, а поля у каждой вкладки свои.</summary>
        public static string Authored(int index, BrowseConfig cfg)
        {
            if (cfg == null) return null;
            switch (index)
            {
                case Home: return cfg.nav_home;
                case Store: return cfg.nav_store;
                case Wardrobe: return cfg.nav_wardrobe;
                case Profile: return cfg.nav_profile;
                case Gallery: return cfg.nav_gallery;
                default: return null;
            }
        }
    }
}
