using Lvn.Content;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ЧИСЛА ОБЛИКА «СЦЕНА» — В ОДНОМ ДОМЕ, А НЕ В КОДЕ ЭКРАНОВ.
    ///
    /// <para>Облик рисуют художники: ширина макета, запас свечения по краю,
    /// размеры каждой рамки и её нарезка, ширина столбиков, домашняя полоса
    /// телефона. Всё это жило константами в трёх файлах сразу — и одно и то же
    /// число («полоса 34 dp», «экспорт 3 px на dp») было записано дважды и
    /// трижды. Пока арт один, это незаметно; на втором облике пришлось бы
    /// править движок ради картинок.</para>
    ///
    /// <para>Здесь эти числа названы один раз, а <c>ui.browse.skin_metrics</c>
    /// из манифеста их переопределяет: новый арт описывается рядом с самим
    /// артом. Пусто — остаётся макет, с которого облик начинали.</para>
    /// </summary>
    internal static class LvnStageSkin
    {
        /// <summary>Ширина холста макета, dp.</summary>
        public static float DesignWidth { get; private set; } = 390f;

        /// <summary>Сколько пикселей картинки на dp макета при экспорте.</summary>
        public static float PxPerDp { get; private set; } = 3f;

        /// <summary>Насколько свечение выходит за край элемента, dp.</summary>
        public static float Bleed { get; private set; } = 12f;

        /// <summary>Домашняя полоса телефона в макете, dp: от неё считают низ
        /// столбиков и высоту нижней ленты.</summary>
        public static float HomeBar { get; private set; } = 34f;

        /// <summary>Отступ содержимого от рамки листа (гардероб, профиль), dp.</summary>
        public static float SheetPad { get; private set; } = 20f;

        /// <summary>Панель новостей: место на экране и нарезка её картинки —
        /// плашка сверху и нарисованная кнопка снизу остаются своего размера,
        /// тянется середина.</summary>
        public static Frame Panel { get; private set; } =
            new Frame(width: 200f, height: 124f, imageW: 672f, imageH: 444f, topPx: 30f, bottomPx: 46f);

        /// <summary>Задник карточки: рамка с угловыми скобами, середина
        /// штрихованная.</summary>
        public static Frame CardBack { get; private set; } =
            new Frame(width: 263f, height: 241f, imageW: 861f, imageH: 795f, cornerPx: 84f);

        /// <summary>Пакет магазина: та же рамка, что у панели новостей, но выше —
        /// в неё встаёт спайн-фигура, и в низкой её резало бы по грудь.</summary>
        public static Frame Pack { get; private set; } =
            new Frame(width: 200f, height: 230f);

        /// <summary>Перёд карточки: кромка поверх обложки.</summary>
        public static Frame CardFront { get; private set; } =
            new Frame(width: 257f, height: 255f);

        /// <summary>Кнопка рекламы.</summary>
        public static Frame Adv { get; private set; } =
            new Frame(width: 106f, height: 39f);

        /// <summary>Столбик панелей на главной.</summary>
        public static Column Home { get; private set; } =
            new Column(right: 15f, width: 257f, bottom: 117f);

        /// <summary>Столбик магазина: уже главной, панели выше — в них встаёт
        /// спайн-фигура.</summary>
        public static Column Shop { get; private set; } =
            new Column(right: 15f, width: 232f, top: 70f, bottom: 117f);

        /// <summary>ЛИСТ ВИТРИНЫ — один стандарт для детали, профиля, гардероба
        /// и списка. Раньше у каждого экрана низ и поля были свои числа: деталь
        /// кончалась в 58 dp и лента наезжала, профиль поднимали «на 50»,
        /// гардероб мерил ленту сам (Илья 09.09: «каша, нет стандарта»).</summary>
        public static SheetBox Sheet { get; private set; } =
            new SheetBox(side: 15f, top: 70f, tabTop01: 0.39f, bottom: 150f, under: 40f);

        /// <summary>Принять паспорт из манифеста. Зовётся перед сборкой каждого
        /// экрана облика, поэтому начинает с движковых чисел: убранное из
        /// манифеста поле обязано вернуться к умолчанию, а не донашиваться от
        /// прошлой новеллы. Пустые поля паспорта молчат.</summary>
        public static void Apply(StageSkinMetrics m)
        {
            Reset();
            if (m == null) return;
            DesignWidth = Pick(m.design_width, DesignWidth, min: 120f);
            PxPerDp = Pick(m.px_per_dp, PxPerDp, min: 0.5f);
            Bleed = Pick(m.bleed, Bleed, min: 0f);
            HomeBar = Pick(m.home_bar, HomeBar, min: 0f);
            SheetPad = Pick(m.sheet_pad, SheetPad, min: 0f);
            Panel = Merge(Panel, Named(m, "panel"));
            Pack = Merge(Pack, Named(m, "pack"));
            CardBack = Merge(CardBack, Named(m, "card-back"));
            CardFront = Merge(CardFront, Named(m, "card-front"));
            Adv = Merge(Adv, Named(m, "adv"));
            Home = Merge(Home, m.column);
            Shop = Merge(Shop, m.shop);
            Sheet = Merge(Sheet, m.sheet);
        }

        /// <summary>Вернуть числа макета к движковым — нужно тестам и смене
        /// новеллы: облик следующей не должен донашивать чужие размеры.</summary>
        public static void Reset()
        {
            DesignWidth = 390f; PxPerDp = 3f; Bleed = 12f; HomeBar = 34f; SheetPad = 20f;
            Sheet = new SheetBox(side: 15f, top: 70f, tabTop01: 0.39f, bottom: 150f, under: 40f);
            Panel = new Frame(200f, 124f, 672f, 444f, topPx: 30f, bottomPx: 46f);
            Pack = new Frame(200f, 230f);
            CardBack = new Frame(263f, 241f, 861f, 795f, cornerPx: 84f);
            CardFront = new Frame(257f, 255f);
            Adv = new Frame(106f, 39f);
            Home = new Column(15f, 257f, bottom: 117f);
            Shop = new Column(15f, 232f, top: 70f, bottom: 117f);
        }

        private static StageFrame Named(StageSkinMetrics m, string key)
            => m.frames != null && m.frames.TryGetValue(key, out var f) ? f : null;

        private static float Pick(float? given, float now, float min)
            => given.HasValue && given.Value >= min ? given.Value : now;

        private static Frame Merge(Frame now, StageFrame f)
            => f == null ? now : new Frame(
                Pick(f.width, now.Width, 1f), Pick(f.height, now.Height, 1f),
                Pick(f.image_w, now.ImageW, 1f), Pick(f.image_h, now.ImageH, 1f),
                Pick(f.corner_px, now.CornerPx, 0f),
                Pick(f.top_px, now.TopPx, 0f), Pick(f.bottom_px, now.BottomPx, 0f));

        private static Column Merge(Column now, StageColumn c)
            => c == null ? now : new Column(
                Pick(c.right, now.Right, 0f), Pick(c.width, now.Width, 1f),
                Pick(c.top, now.Top, 0f), Pick(c.bottom, now.Bottom, 0f));

        /// <summary>Рамка облика: место на экране (dp макета) и размеры самой
        /// картинки с её нарезкой (px картинки).</summary>
        internal readonly struct Frame
        {
            public readonly float Width, Height, ImageW, ImageH, CornerPx, TopPx, BottomPx;

            public Frame(float width, float height, float imageW = 0f, float imageH = 0f,
                         float cornerPx = 0f, float topPx = 0f, float bottomPx = 0f)
            {
                Width = width; Height = height; ImageW = imageW; ImageH = imageH;
                CornerPx = cornerPx; TopPx = topPx; BottomPx = bottomPx;
            }

            /// <summary>Пикселей картинки на dp ЭТОЙ рамки: она экспортирована
            /// с запасом свечения по краям, поэтому делится не на ширину места,
            /// а на ширину места вместе с запасом.</summary>
            public float PxPerDp(float bleed)
                => ImageW > 0f && Width > 0f ? ImageW / (Width + bleed * 2f) : LvnStageSkin.PxPerDp;
        }

        /// <summary>Столбик панелей: где стоит и какой ширины, dp макета.</summary>
        private static SheetBox Merge(SheetBox now, StageSheet s)
        {
            if (s == null) return now;
            float tab = Pick(s.tab_top, now.TabTop01, 0f);
            if (tab > 1f) tab = 1f;
            return new SheetBox(Pick(s.side, now.Side, 0f), Pick(s.top, now.Top, 0f), tab,
                                Pick(s.bottom, now.Bottom, 0f), Pick(s.under, now.Under, 0f));
        }

        /// <summary>Геометрия листа витрины: поля и верх в dp макета, верх
        /// вкладки — долей высоты, низ вкладки — dp над низом экрана. Попап
        /// (деталь новеллы) идёт до домашней полосы, под ленту: <see cref="Under"/>
        /// — на сколько dp лента ложится на него, настолько же поднимаются
        /// его кнопки («чтобы нижнее меню закрывало на 50–70» — Илья 09.09).</summary>
        internal readonly struct SheetBox
        {
            public readonly float Side, Top, TabTop01, Bottom, Under;
            public SheetBox(float side, float top, float tabTop01, float bottom, float under)
            { Side = side; Top = top; TabTop01 = tabTop01; Bottom = bottom; Under = under; }
        }

        internal readonly struct Column
        {
            public readonly float Right, Width, Top, Bottom;

            public Column(float right, float width, float top = 0f, float bottom = 0f)
            {
                Right = right; Width = width; Top = top; Bottom = bottom;
            }
        }
    }
}
