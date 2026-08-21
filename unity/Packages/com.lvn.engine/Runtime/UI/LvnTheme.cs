using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// ТЕМА ОБОЛОЧКИ: палитра плюс огранка.
    ///
    /// <para>Тема — это не «набор цветов». Цвета переставить мало: между
    /// романтическим и киберпанковым экраном разница не в оттенке, а в том, что
    /// у одного углы скруглены и текст набран строчными, а у другого угол
    /// срезан, заголовки капсом с разрядкой, по контуру идёт светящаяся кромка,
    /// а за содержимым дышит сетка. Всё это здесь и лежит — рядом с палитрой и
    /// такими же данными.</para>
    ///
    /// <para>Поэтому новая тема не требует ни строчки в коде экранов: хаб,
    /// магазин и гардероб спрашивают у темы, а не решают сами. Проверка ровно
    /// такая — если для темы пришлось трогать экран, значит в теме не хватает
    /// поля.</para>
    ///
    /// <para>Ни одного файла с собой тема не тащит: атмосфера считается кодом
    /// (см. <see cref="LvnBackdrop"/>), иконки рисуются вектором (см.
    /// <see cref="LvnIcons"/>). Движок без арта обязан оставаться движком.</para>
    /// </summary>
    public sealed class LvnTheme
    {
        public string Name = "midnight";

        // ── палитра ─────────────────────────────────────────────────────────
        // Значения по умолчанию — «Полночь»: нейтраль Radix mauve с розовым
        // акцентом и тёплым золотом. Они лежат ЗДЕСЬ, а не в LvnTokens, иначе
        // получилось бы кольцо: токен спрашивает тему, тема спрашивает токен.
        public Color Bg = Hex("#171119");
        public Color Surface = Hex("#241a24");
        public Color SurfaceHi = Hex("#2c2130");
        public Color Border = Hex("#38293a");
        public Color Text = Hex("#f6ecf1");
        public Color TextDim = Hex("#b79caf");
        public Color Accent = Hex("#ec5a92");
        public Color OnAccent = Hex("#1a0f16");
        public Color Gold = Hex("#f0d9a0");
        /// <summary>Заливка кнопки-призрака.</summary>
        public Color Faint = new Color(1f, 1f, 1f, 0.08f);
        /// <summary>Затемнение под модальными окнами.</summary>
        public Color Scrim = new Color(0f, 0f, 0f, 0.72f);
        /// <summary>Фон панели диалога и нижних листов.</summary>
        public Color PanelBg = new Color(0.086f, 0.063f, 0.094f, 0.97f);
        /// <summary>Тревога и «новое». ОТДЕЛЬНЫЙ цвет, а не акцент: если
        /// предупреждение красить тем же, чем кнопку, экран теряет способность
        /// кричать.</summary>
        public Color Warn = Hex("#ec5a92");

        // ── огранка ─────────────────────────────────────────────────────────
        /// <summary>Скругление карточек. Малое значение читается как срез —
        /// именно так и делается фаска, которой в UI Toolkit нет.</summary>
        // ── ТИПОГРАФИКА ─────────────────────────────────────────────────
        // Шрифт по умолчанию — Onest (OFL, лежит в пакете): у него родная
        // кириллица, и он не безликий. Системный Arial, стоявший здесь до
        // этого, выдаёт «сделано на коленке» раньше, чем прочитан текст.
        public string FontPath = "Fonts/Onest-Regular";
        public string FontDisplayPath = "Fonts/Onest-ExtraBold";

        // Ступени кегля в единицах панели (1080×1920). ШКАЛА, а не свободные
        // числа: одинаковые вещи на разных экранах обязаны быть одного
        // размера, иначе интерфейс расползается — что и произошло, когда
        // каждый экран выбирал кегль на глаз.
        //                                     назначение
        public int TextXs      = 20;   // подписи под элементом, сноски
        public int TextSm      = 24;   // второстепенное: подсказки, единицы
        public int TextBase    = 30;   // основной текст интерфейса
        public int TextLg      = 38;   // подзаголовок, крупная цифра
        public int TextXl      = 48;   // заголовок экрана
        public int TextDisplay = 64;   // витринный заголовок

        // Ступени отступа. Все поля и зазоры берутся отсюда: «на глаз» даёт
        // 14, 15, 18 в соседних местах, и взгляд цепляется за разнобой.
        public float Space1 = 8f;
        public float Space2 = 12f;
        public float Space3 = 18f;
        public float Space4 = 26f;
        public float Space5 = 40f;
        public float Space6 = 60f;

        // ── ПОЯВЛЕНИЕ И УХОД ────────────────────────────────────────────
        // Опорный образ — телефон лежит на столе: элемент не включается, а
        // всплывает из-под стекла и утопает обратно. Уход короче прихода:
        // равные длительности читаются как задержка отклика.
        public int AppearMs = 220;
        public int DisappearMs = 150;
        // Масштаб «из глубины». Меньше, чем ждёшь: 0.94, а не 0.7 — крупный
        // скачок читается как всплывающее окно, а не как глубина.
        public float AppearScale = 0.94f;
        public float AppearShift = 22f;   // амплитуда выездов, в единицах панели

        // ── ГЛУБИНА НАЖАТИЯ ─────────────────────────────────────────────
        // У кнопки есть толщина: тёмная нижняя грань. При нажатии кнопка
        // проседает на неё же — палец видит, что вдавил, а не «мигнул цвет».
        // Тени в UI Toolkit нет, поэтому глубину делает грань, а не blur.
        public float ButtonLift = 6f;
        public Color ButtonShade = new Color(0f, 0f, 0f, 0.45f);

        public float Radius = 16f;
        /// <summary>Скругление кнопок и меток — мельче карточного.</summary>
        public float RadiusSm = 12f;
        /// <summary>Толщина светящейся кромки по контуру панелей. 0 — без неё.
        /// В пикселях холста: на 1080 единица — это треть точки, то есть
        /// невидимо, поэтому у киберпанка стоит 3.</summary>
        public float EdgeWidth = 0f;
        /// <summary>Насколько кромка яркая (доля от акцента).</summary>
        public float EdgeAlpha = 0.45f;
        /// <summary>Разрядка заголовков. Техническая типографика держится
        /// на ней сильнее, чем на начертании.</summary>
        public float Tracking = 0f;
        /// <summary>Заголовки капсом.</summary>
        public bool UpperHeadings = false;
        /// <summary>Свечение под линией иконок: 0 — нет, 1 — заметное.</summary>
        public float IconGlow = 0f;
        /// <summary>Заливать заглушку отсутствующей обложки акцентом.
        ///
        /// <para>В тёплой теме крупное акцентное пятно вместо ненаехавшей
        /// картинки выглядит нарядно. В технической — губительно: акцент там
        /// один на экран и служит указателем, а три постера во всю ширину
        /// отбирают у него всю силу. Поэтому у киберпанка заглушка тёмная, с
        /// одной светящейся кромкой.</para></summary>
        public bool AccentPlaceholders = true;
        /// <summary>Круглые плашки и аватар. Круг — примета тёплого,
        /// «человеческого» интерфейса; техническому он противоречит ровно так
        /// же, как срезанный угол противоречит романтическому.</summary>
        public bool RoundPills = true;

        /// <summary>
        /// Рамка окна диалога: имя текстуры в Resources/ui/. Пусто — рамки нет.
        ///
        /// <para>Единственное место, где тема всё-таки берёт файл. Сетку и
        /// свечение можно посчитать четырьмя строками, а вот такую рамку — со
        /// срезанными углами, разрывами, накладными пластинами и подписями
        /// мелким шрифтом — код рисовать не должен: это рисунок, а не приём.</para>
        ///
        /// <para>Слайсы РАЗНЫЕ по осям, потому что рамка не квадратная: 96 по
        /// бокам, 64 сверху и снизу — числа из паспорта самой картинки, а не
        /// подобранные. Масштаб 1/3, потому что текстура нарисована в тройном
        /// разрешении: без него угловая зона встала бы в 96 единиц вместо 32 и
        /// съела бы окно целиком.</para>
        /// </summary>
        public string DialogueFrame;
        public Vector4 DialogueFrameSlice = new Vector4(96f, 96f, 64f, 64f); // Л, П, В, Н
        public float DialogueFrameScale = 1f / 3f;
        /// <summary>
        /// На сколько рамка ВЫСТУПАЕТ за панель с каждой стороны.
        ///
        /// <para>Нужно потому, что линия нарисована не по краю файла: у нашей
        /// текстуры графика начинается на 24 пикселя внутрь (замерено по
        /// альфа-каналу), а при масштабе 1/3 это восемь единиц. Совмести рамку
        /// с панелью край в край — и тёмная заливка вылезет из-под неё ровно на
        /// эти восемь, что и выглядит как «фон больше рамки».</para>
        /// </summary>
        public float DialogueFrameBleed = 13f;

        /// <summary>Сдвиг плашки имени вправо. Ноль ставит её вплотную к левому
        /// краю окна, а рамка там как раз загибается — уголок плашки садится на
        /// угол окна, и оба узора спорят.</summary>
        public float SpeakerBubbleOffsetX = 15f;

        /// <summary>Плашка имени говорящего. Свои слайсы: она куда мельче окна,
        /// и его 96 съели бы её целиком.</summary>
        public string SpeakerBubble;
        public Vector4 SpeakerBubbleSlice = new Vector4(32f, 32f, 16f, 8f);
        /// <summary>Отступы текста внутри плашки: Л, П, В, Н. Из паспорта
        /// картинки — у неё нижняя кромка прямая, поэтому снизу меньше.</summary>
        public Vector4 SpeakerBubblePad = new Vector4(22f, 18f, 13f, 8f);

        // ── атмосфера за содержимым ─────────────────────────────────────────
        public bool Grid = false;
        public bool Scanlines = false;
        public bool Vignette = false;
        public bool Glow = false;

        /// <summary>Заголовок с учётом темы — чтобы капс не размазывался по
        /// экранам вручную.</summary>
        public string Heading(string s) =>
            UpperHeadings && !string.IsNullOrEmpty(s) ? s.ToUpperInvariant() : s;

        /// <summary>Цвет кромки: акцент нужной прозрачности.</summary>
        public Color EdgeColor =>
            new Color(Accent.r, Accent.g, Accent.b, EdgeAlpha);

        // ── готовые темы ────────────────────────────────────────────────────

        /// <summary>Тема по умолчанию: тёплая, скруглённая, без атмосферы.
        /// То, чем оболочка была всегда, — вынесено сюда, чтобы «без темы» и
        /// «тема midnight» означали ровно одно и то же.</summary>
        public static LvnTheme Midnight() => new LvnTheme { Name = "midnight" };

        /// <summary>Киберпанк: холодный, гранёный, с сеткой и свечением.</summary>
        public static LvnTheme Cyber() => new LvnTheme
        {
            Name = "cyber",
            Bg = Hex("#0A0E16"),
            Surface = Hex("#141B2B"),
            SurfaceHi = Hex("#1B2438"),
            Border = new Color(0.18f, 0.90f, 0.84f, 0.35f),
            Text = Hex("#DFF6FF"),
            TextDim = Hex("#7F95AD"),
            // Один яркий акцент на экран. Второй яркий цвет отнимает у первого
            // способность направлять взгляд, поэтому маджента — только тревога.
            Accent = Hex("#2EE6D6"),
            OnAccent = Hex("#06202A"),
            Gold = Hex("#FFC46B"),
            Warn = Hex("#FF2E88"),
            Faint = new Color(0.18f, 0.90f, 0.84f, 0.07f),
            Scrim = new Color(0.02f, 0.04f, 0.07f, 0.82f),
            PanelBg = new Color(0.039f, 0.055f, 0.086f, 0.96f),
            Radius = 14f,
            RadiusSm = 8f,
            EdgeWidth = 3f,
            EdgeAlpha = 0.5f,
            Tracking = 2.5f,
            UpperHeadings = true,
            IconGlow = 1f,
            AccentPlaceholders = false,
            RoundPills = false,
            DialogueFrame = "dialogue_frame_cyan",
            SpeakerBubble = "speaker_bubble_cyan",
            Grid = true,
            Scanlines = true,
            Vignette = true,
            Glow = true,
        };

        /// <summary>
        /// Романтическая, но ГРАНЁНАЯ: палитра Time Romance с той же огранкой,
        /// что у киберпанка.
        ///
        /// <para>Существует потому, что «нравится этот дизайн» почти никогда не
        /// значит «нравится циан». Нравится обычно ЧЁТКОСТЬ: светящаяся кромка
        /// по контуру, техническая типографика, живой фон, отклик на нажатие.
        /// Всё это к цвету отношения не имеет, и здесь оно соединено с розовым
        /// вместо холодного.</para>
        ///
        /// <para>Углы при этом скруглены сильнее, а капса нет: капс с разрядкой
        /// — примета технического интерфейса, и на романтическом он читается
        /// как чужой. Разрядка остаётся, но вдвое меньше.</para>
        /// </summary>
        public static LvnTheme Romance() => new LvnTheme
        {
            Name = "romance",
            Bg = Hex("#17141c"),
            Surface = Hex("#241c2c"),
            SurfaceHi = Hex("#2f2438"),
            Border = new Color(0.88f, 0.35f, 0.54f, 0.30f),
            Text = Hex("#f2ecf4"),
            TextDim = Hex("#9a8fa6"),
            Accent = Hex("#e05a8a"),
            OnAccent = Hex("#1d0f16"),
            Gold = Hex("#f0d9a0"),
            Warn = Hex("#ff5c7a"),
            Faint = new Color(1f, 1f, 1f, 0.07f),
            Scrim = new Color(0.05f, 0.03f, 0.06f, 0.80f),
            PanelBg = new Color(0.09f, 0.07f, 0.11f, 0.96f),
            Radius = 18f,
            RadiusSm = 12f,
            EdgeWidth = 2f,
            EdgeAlpha = 0.40f,
            Tracking = 1.2f,
            UpperHeadings = false,
            IconGlow = 0.7f,
            AccentPlaceholders = false,
            RoundPills = true,
            Grid = false,       // сетка — примета прибора, не романа
            Scanlines = false,
            Vignette = true,
            Glow = true,
        };

        /// <summary>Тема по имени. Неизвестное имя — это тема по умолчанию, а
        /// не пустой экран: опечатка в манифесте не должна ронять оболочку.</summary>
        public static LvnTheme ByName(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "cyber":
                case "cyberpunk": return Cyber();
                case "romance": return Romance();
                default: return Midnight();
            }
        }

        /// <summary>Действующая тема. Экраны читают отсюда.</summary>
        public static LvnTheme Current { get; private set; } = Midnight();

        public static void Use(string name) => Current = ByName(name);
        public static void Use(LvnTheme t) { if (t != null) Current = t; }

        private static Color Hex(string s)
        {
            ColorUtility.TryParseHtmlString(s, out var c);
            return c;
        }
    }
}
