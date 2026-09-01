using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ЧЕМ КРАСИТЬ, ПОКА ТЕМЫ НЕТ.
    ///
    /// <para>Тема приезжает с манифестом — на второй секунде запуска. Вуаль
    /// встаёт на семидесятой миллисекунде, выбор сервера показывается ещё
    /// раньше манифеста по определению: он и решает, откуда манифест брать.
    /// Значит два экрана обязаны иметь цвета ДО темы, и это не небрежность, а
    /// нерешённая задача.</para>
    ///
    /// <para>Решали её раздельно, и получилось ровно то, что получается всегда:
    /// <b>два экрана, идущие подряд, красились по разным палитрам.</b> Вуаль —
    /// холодной сталью (#d4dee8) на почти-чёрном #101015; выбор сервера —
    /// тёплым золотом (#c7a14f) и кремом на другом почти-чёрном #1c1c21.
    /// Двадцать литералов на два файла, и ни один не знал про остальные: при
    /// передаче эстафеты от вуали к экрану весь тон сдвигался.</para>
    ///
    /// <para>Здесь эти цвета названы один раз. Названы <b>ролями</b>, а не
    /// оттенками, — чтобы менять их можно было, не переписывая экраны.</para>
    ///
    /// <para><b>Тема главнее.</b> Как только она приехала, спрашивать надо её:
    /// экран выбора сервера игрок может открыть и позже, из настроек, и там он
    /// обязан выглядеть как остальная игра. Поэтому каждая роль сначала
    /// смотрит на тему и лишь потом отдаёт своё умолчание — тот же приём, что у
    /// <see cref="Lvn.Content.LvnCaptions"/> со словами.</para>
    /// </summary>
    public static class LvnDawn
    {
        /// <summary>Тема уже приехала? До этого её значения — заводские,
        /// одинаковые у всех продуктов, и рассвет честнее.</summary>
        public static bool ThemeArrived;

        /// <summary>Земля: то, на чём всё лежит. Почти чёрный с синевой —
        /// он же цвет камеры, чтобы кадр не мигал при первой отрисовке.</summary>
        public static Color Ground => ThemeArrived ? LvnTheme.Current.Bg : Hex(0x101015);

        /// <summary>Поверхность над землёй: поле ввода, карточка.</summary>
        public static Color Surface => ThemeArrived ? LvnTheme.Current.Surface : Hex(0x1c1c21);

        /// <summary>Чернила: основной текст.</summary>
        public static Color Ink => ThemeArrived ? LvnTheme.Current.Text : Hex(0xe8e3da);

        /// <summary>Приглушённые чернила: подписи, состояние, версия.</summary>
        public static Color InkDim => ThemeArrived ? LvnTheme.Current.TextDim : Hex(0x9a948a);

        /// <summary>Совсем тихое: то, что на экране есть, но взгляда не просит.</summary>
        public static Color InkFaint => Hex(0x616a6e);

        /// <summary>Марка движка. Полированная сталь — ELVIN узнаётся по ней,
        /// и это цвет ЕГО, а не продукта: до темы мы ещё не знаем, чья игра.</summary>
        public static Color Brand => Hex(0xd4dee8);

        /// <summary>Действие: кнопка, которую ждут нажатой.</summary>
        public static Color Accent => ThemeArrived ? LvnTheme.Current.Accent : Hex(0xc7a14f);

        /// <summary>Текст на действии.</summary>
        public static Color OnAccent => Hex(0x14141a);

        /// <summary>Живое: проверка прошла.</summary>
        public static Color Ok => Hex(0x66d966);

        /// <summary>Мёртвое: проверка не прошла.</summary>
        public static Color Bad => Hex(0xd95959);

        /// <summary>Полоса, по которой едет заполнение.</summary>
        public static Color Track => new Color(1f, 1f, 1f, 0.10f);

        /// <summary>Тень под текстом на неизвестном фоне.</summary>
        public static Color TextShadow => new Color(0f, 0f, 0f, 0.85f);

        private static Color Hex(int rgb) => new Color(
            ((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
    }
}
