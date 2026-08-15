using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sandbox
{
    /// <summary>
    /// Киберпанк-хаб: не список компонентов, а собранный экран.
    ///
    /// <para>Разница принципиальная. Компоненты с правильными цветами дают
    /// «аккуратно»; «красиво» даёт композиция — слои фона, иерархия, ритм,
    /// свечение в одном месте и техническая мелочь по краям. Поэтому здесь
    /// сначала строится СЦЕНА (сетка, виньетка, сканлайны), и только потом на
    /// неё кладётся содержимое.</para>
    ///
    /// <para>Правила, по которым это собрано:
    /// один яркий акцент на экран (циан), маджента только как предупреждение;
    /// скошенный угол вместо скругления — он и есть подпись жанра;
    /// техническая мелочь (индексы, метки, скобки) создаёт ощущение системы;
    /// пустота работает: тёмное поле между блоками важнее ещё одного блока.</para>
    /// </summary>
    public sealed class CyberHub : MonoBehaviour
    {
        private const string Cy = "#2EE6D6";   // циан — единственный яркий акцент
        private const string Mg = "#FF2E88";   // маджента — только тревога
        private const string Bg = "#0A0E16";
        private const string Sf = "#141B2B";
        private const string Tx = "#DFF6FF";
        private const string Dim = "#7F95AD";

        private static Color C(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var go = new GameObject("CyberHub");
            DontDestroyOnLoad(go);
            go.AddComponent<CyberHub>();
        }

        private void Start()
        {
            var doc = gameObject.AddComponent<UIDocument>();
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1080, 1920);
            ps.match = 1f;
            // ВАЖНО ПРО РАЗМЕРЫ. Холст 1080 пикселей — это примерно 360 точек
            // на телефоне (плотность 3). Поэтому шрифт «17» здесь читается как
            // ШЕСТЬ точек, то есть нечитаемо. Все размеры ниже заданы в
            // пикселях холста и равны точкам, умноженным на три: тело 15×3=45,
            // заголовок 28×3=84, область касания 48×3=144.
            ps.sortingOrder = 900; // поверх всего: это витрина
            var theme = Resources.Load<ThemeStyleSheet>("UI/AppLoading/UnityDefaultRuntimeTheme");
            if (theme != null) ps.themeStyleSheet = theme;
            doc.panelSettings = ps;

            var root = doc.rootVisualElement;
            root.style.flexGrow = 1;
            root.style.backgroundColor = C(Bg);
            BuildScene(root);
            Debug.Log("[cyber-hub] собран");
        }

        // ── слои фона ───────────────────────────────────────────────────────
        private static void BuildScene(VisualElement root)
        {
            // Сетка тайлится: одна текстура 128×128 на весь экран.
            Layer(root, "cyber/grid", 0.5f, true);
            // Пятно света сверху — оно задаёт, куда смотреть.
            var glow = Layer(root, "cyber/glow", 0.55f, false);
            glow.style.top = -260; glow.style.height = 900;
            glow.style.left = -120; glow.style.right = -120;
            // Сканлайны поверх всего фона, но под содержимым.
            Layer(root, "cyber/scanlines", 0.6f, true);
            // Виньетка прижимает края и заставляет центр читаться.
            Layer(root, "cyber/vignette", 1f, false);
            // Свечение у нижней кромки: горизонт города за экраном. Оно же
            // прижимает навигацию и не даёт низу выглядеть обрезанным.
            var floor = Layer(root, "cyber/glow", 0.35f, false);
            floor.style.top = new StyleLength(StyleKeyword.Auto);
            floor.style.bottom = -320; floor.style.height = 620;
            floor.style.left = -160; floor.style.right = -160;

            var page = new VisualElement();
            page.style.flexGrow = 1;
            root.Add(page);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden; // полоса
            // прокрутки в такой витрине — визуальный мусор, жест остаётся
            page.Add(scroll);
            var content = scroll.contentContainer;
            content.style.paddingLeft = 56; content.style.paddingRight = 56;
            content.style.paddingTop = 90; content.style.paddingBottom = 40;

            content.Add(StatusBar());
            content.Add(Space(48));
            content.Add(Hero());
            content.Add(Space(48));
            content.Add(SectionLabel("ДОСТУПНЫЕ ЛИНИИ", "04"));
            content.Add(Space(24));
            content.Add(CardRow());
            content.Add(Space(48));
            content.Add(SectionLabel("СЕГОДНЯ", "07"));
            content.Add(Space(24));
            content.Add(TodayPanel());
            content.Add(Space(40));
            content.Add(HazardStrip("СИГНАЛ НЕСТАБИЛЕН · ПОВТОР ЧЕРЕЗ 00:42"));
            content.Add(Space(40));
            content.Add(LastSession());
            // Навигация ВНЕ прокрутки: она обязана быть на месте всегда.
            var nav = BottomNav();
            nav.style.paddingLeft = 56; nav.style.paddingRight = 56;
            nav.style.paddingBottom = 30;
            nav.style.backgroundColor = new Color(0.039f, 0.055f, 0.086f, 0.96f);
            page.Add(nav);
        }

        private static VisualElement Layer(VisualElement parent, string res, float opacity, bool tile)
        {
            var v = new VisualElement { pickingMode = PickingMode.Ignore };
            v.style.position = Position.Absolute;
            v.style.left = 0; v.style.right = 0; v.style.top = 0; v.style.bottom = 0;
            var tex = Resources.Load<Texture2D>(res);
            if (tex != null)
            {
                if (tile) tex.wrapMode = TextureWrapMode.Repeat;
                v.style.backgroundImage = new StyleBackground(tex);
                v.style.backgroundRepeat = tile
                    ? new BackgroundRepeat(Repeat.Repeat, Repeat.Repeat)
                    : new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                if (!tile)
                    v.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            }
            v.style.opacity = opacity;
            parent.Add(v);
            return v;
        }

        // ── верхняя строка состояния ────────────────────────────────────────
        private static VisualElement StatusBar()
        {
            var row = Row();
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.alignItems = Align.Center;

            var left = Row();
            left.style.alignItems = Align.Center;
            var badge = new Label("V");
            badge.style.width = 108; badge.style.height = 108;
            badge.style.unityTextAlign = TextAnchor.MiddleCenter;
            badge.style.color = C(Bg);
            badge.style.backgroundColor = C(Cy);
            badge.style.fontSize = 52;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            Chamfer(badge, 10);
            left.Add(badge);
            var who = new VisualElement { style = { marginLeft = 12 } };
            who.Add(Text("ВИКТОРИЯ", 54, Tx, 3f, FontStyle.Bold));
            who.Add(Text("ID 0x1F4C · УРОВЕНЬ 7", 36, Dim, 2f));
            left.Add(who);
            row.Add(left);

            var right = Row();
            right.Add(Counter("◈", "1 240"));
            right.Add(Counter("⚡", "3"));
            row.Add(right);
            return row;
        }

        private static VisualElement Counter(string glyph, string value)
        {
            var b = Row();
            b.style.alignItems = Align.Center;
            b.style.marginLeft = 10;
            b.style.paddingLeft = 26; b.style.paddingRight = 26;
            b.style.paddingTop = 18; b.style.paddingBottom = 18;
            b.style.backgroundColor = new Color(0.11f, 0.15f, 0.22f, 0.9f);
            Chamfer(b, 8);
            Edge(b, C(Cy), 0.55f);
            b.Add(Text(glyph, 40, Cy, 0f));
            var v = Text(value, 44, Tx, 1f, FontStyle.Bold);
            v.style.marginLeft = 7;
            b.Add(v);
            return b;
        }

        // ── герой ───────────────────────────────────────────────────────────
        private static VisualElement Hero()
        {
            var card = new VisualElement();
            card.style.height = 700;
            card.style.backgroundColor = C(Sf);
            Chamfer(card, 22);
            Edge(card, C(Cy), 0.9f);
            card.style.overflow = Overflow.Hidden;

            // Диагональная штриховка в углу — техническая деталь, которая
            // делает панель «оборудованием», а не прямоугольником.
            var hz = new VisualElement { pickingMode = PickingMode.Ignore };
            hz.style.position = Position.Absolute;
            hz.style.right = 0; hz.style.top = 0;
            hz.style.width = 190; hz.style.height = 26;
            var ht = Resources.Load<Texture2D>("cyber/hazard");
            if (ht != null)
            {
                ht.wrapMode = TextureWrapMode.Repeat;
                hz.style.backgroundImage = new StyleBackground(ht);
                hz.style.backgroundRepeat = new BackgroundRepeat(Repeat.Repeat, Repeat.Repeat);
            }
            card.Add(hz);


            // Зона под арт: сейчас градиент, но место под кадр героини занято
            // осознанно — пустой верх панели и есть главная причина, по которой
            // экран читался как незаполненный.
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.style.position = Position.Absolute;
            art.style.left = 0; art.style.right = 0; art.style.top = 0;
            art.style.height = 440;
            art.style.backgroundColor = new Color(0.10f, 0.17f, 0.26f, 1f);
            var artGlow = Resources.Load<Texture2D>("cyber/glow");
            if (artGlow != null)
            {
                art.style.backgroundImage = new StyleBackground(artGlow);
                art.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                art.style.opacity = 0.85f;
            }
            card.Add(art);
            // Затемнение под текстом: белая подпись обязана читаться на любом
            // кадре, который сюда однажды ляжет.
            var scrim = new VisualElement { pickingMode = PickingMode.Ignore };
            scrim.style.position = Position.Absolute;
            scrim.style.left = 0; scrim.style.right = 0;
            scrim.style.top = 300; scrim.style.bottom = 0;
            scrim.style.backgroundColor = new Color(0.078f, 0.106f, 0.169f, 0.92f);
            card.Add(scrim);

            var body = new VisualElement();
            body.style.position = Position.Absolute;
            body.style.left = 56; body.style.right = 56; body.style.bottom = 52;
            body.Add(Text("ЭПИЗОД 01 · АКТИВНА", 36, Cy, 4f, FontStyle.Bold));
            var t = Text("НЕМИНУЕМЫЕ\nИЗМЕНЕНИЯ", 84, Tx, 2f, FontStyle.Bold);
            t.style.marginTop = 8; t.style.marginBottom = 14;
            body.Add(t);
            var desc = Text("Ты просыпаешься в чужом городе,\nи всё уже началось без тебя.", 40, Dim, 0f);
            desc.style.whiteSpace = WhiteSpace.Normal;
            body.Add(desc);
            var actions = Row();
            actions.style.marginTop = 20;
            actions.Add(CyberButton("ПРОДОЛЖИТЬ", true));
            actions.Add(CyberButton("ПОДРОБНЕЕ", false));
            body.Add(actions);
            var brackets = new VisualElement { pickingMode = PickingMode.Ignore };
            brackets.style.position = Position.Absolute;
            brackets.style.left = 14; brackets.style.right = 14;
            brackets.style.top = 14; brackets.style.bottom = 14;
            var bt = Resources.Load<Texture2D>("cyber/brackets");
            if (bt != null)
            {
                brackets.style.backgroundImage = new StyleBackground(bt);
                brackets.style.unitySliceLeft = 60;
                brackets.style.unitySliceRight = 60;
                brackets.style.unitySliceTop = 60;
                brackets.style.unitySliceBottom = 60;
                brackets.style.unitySliceScale = 0.5f;
            }
            card.Add(brackets);
            card.Add(body);
            return card;
        }

        // ── карточки ────────────────────────────────────────────────────────
        private static VisualElement CardRow()
        {
            // Горизонтальная карусель, а не три колонки: на 1080 пикселях три
            // карточки дают по 240 на текст, и слово «ЭКСПЕДИЦИЯ» рвётся
            // посередине. Карусель — то, что делают все мобильные хабы, и она
            // же позволяет карточке быть достаточно широкой, чтобы читаться.
            var scroll = new ScrollView(ScrollViewMode.Horizontal);
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.style.flexShrink = 0;
            var row = scroll.contentContainer;
            row.style.flexDirection = FlexDirection.Row;
            string[,] items =
            {
                { "ЭКСПЕДИЦИЯ", "СЕВЕР", "78%" },
                { "ГАРДЕРОБ", "12 ПРЕДМЕТОВ", "НОВОЕ" },
                { "АРХИВ", "5 ЗАПИСЕЙ", "" },
            };
            for (int i = 0; i < items.GetLength(0); i++)
            {
                var c = new VisualElement();
                c.style.width = 430;      // хватает на слово целиком
                c.style.flexShrink = 0;
                c.style.height = 330;
                c.style.marginRight = 24;
                c.style.backgroundColor = C(Sf);
                c.style.paddingLeft = 34; c.style.paddingRight = 34;
                c.style.paddingTop = 30; c.style.paddingBottom = 30;
                Chamfer(c, 14);
                Edge(c, C(Cy), 0.45f);

                var ct = Text(items[i, 0], 30, Cy, 2f, FontStyle.Bold);
                ct.style.whiteSpace = WhiteSpace.Normal;
                c.Add(ct);
                var sub = Text(items[i, 1], 40, Tx, 0f);
                sub.style.whiteSpace = WhiteSpace.Normal;
                sub.style.marginTop = 10;
                c.Add(sub);
                var grow = new VisualElement { style = { flexGrow = 1 } };
                c.Add(grow);
                c.Add(Bar(i == 0 ? 0.78f : i == 1 ? 0.4f : 0.15f));
                if (items[i, 2] != "")
                {
                    var tag = Text(items[i, 2], 32, Bg, 2f, FontStyle.Bold);
                    tag.style.position = Position.Absolute;
                    tag.style.right = 0; tag.style.top = 0;
                    tag.style.paddingLeft = 10; tag.style.paddingRight = 10;
                    tag.style.paddingTop = 4; tag.style.paddingBottom = 4;
                    tag.style.backgroundColor = C(items[i, 2] == "НОВОЕ" ? Mg : Cy);
                    c.Add(tag);
                }
                row.Add(c);
            }
            return scroll;
        }

        private static VisualElement Bar(float fill)
        {
            var wrap = new VisualElement();
            wrap.style.height = 12;
            wrap.style.backgroundColor = new Color(0.18f, 0.24f, 0.34f, 1f);
            var f = new VisualElement();
            f.style.height = 12;
            f.style.width = Length.Percent(fill * 100f);
            f.style.backgroundColor = C(Cy);
            wrap.Add(f);
            return wrap;
        }

        // Панель «сегодня»: заполняет середину экрана и даёт второй уровень
        // иерархии. Без неё содержимое жалось к верху, а низ читался как дыра.
        private static VisualElement TodayPanel()
        {
            var p = new VisualElement();
            p.style.backgroundColor = C(Sf);
            p.style.paddingLeft = 38; p.style.paddingRight = 38;
            p.style.paddingTop = 32; p.style.paddingBottom = 32;
            Chamfer(p, 14);
            Edge(p, C(Cy), 0.4f);

            var head = Row();
            head.style.justifyContent = Justify.SpaceBetween;
            head.style.alignItems = Align.Center;
            head.Add(Text("ЕЖЕДНЕВНАЯ СВЯЗЬ", 38, Cy, 3f, FontStyle.Bold));
            head.Add(Text("3 / 7", 38, Dim, 2f, FontStyle.Bold));
            p.Add(head);

            var days = Row();
            days.style.marginTop = 16;
            days.style.justifyContent = Justify.SpaceBetween;
            for (int i = 0; i < 7; i++)
            {
                var cell = new VisualElement();
                cell.style.flexGrow = 1; cell.style.flexBasis = 0;
                cell.style.height = 118;
                cell.style.marginRight = i < 6 ? 8 : 0;
                bool done = i < 3;
                bool today = i == 3;
                cell.style.backgroundColor = done
                    ? new Color(C(Cy).r, C(Cy).g, C(Cy).b, 0.18f)
                    : new Color(0.10f, 0.14f, 0.21f, 1f);
                Chamfer(cell, 8);
                Edge(cell, C(today ? Mg : Cy), today ? 0.9f : (done ? 0.6f : 0.2f));
                cell.style.alignItems = Align.Center;
                cell.style.justifyContent = Justify.Center;
                cell.Add(Text(done ? "✓" : (i + 1).ToString(), today ? 50 : 44,
                              done ? Cy : (today ? Mg : Dim), 0f, FontStyle.Bold));
                days.Add(cell);
            }
            p.Add(days);
            return p;
        }

        // ── полоса тревоги ──────────────────────────────────────────────────
        private static VisualElement HazardStrip(string text)
        {
            var s = Row();
            s.style.alignItems = Align.Center;
            s.style.paddingLeft = 30; s.style.paddingRight = 30;
            s.style.paddingTop = 22; s.style.paddingBottom = 22;
            s.style.backgroundColor = new Color(0.16f, 0.05f, 0.11f, 0.85f);
            Chamfer(s, 10);
            Edge(s, C(Mg), 0.7f);
            s.Add(Text("▲", 38, Mg, 0f));
            var t = Text(text, 34, Mg, 2f, FontStyle.Bold);
            t.style.marginLeft = 10;
            s.Add(t);
            return s;
        }

        // Последняя сессия: широкий блок внизу. Он не украшение — он закрывает
        // дыру между содержимым и навигацией, из-за которой экран читался
        // недоделанным, и заодно отвечает на первый вопрос вернувшегося
        // игрока: «где я остановилась».
        private static VisualElement LastSession()
        {
            var p = new VisualElement();
            p.style.flexDirection = FlexDirection.Row;
            p.style.alignItems = Align.Center;
            p.style.backgroundColor = C(Sf);
            p.style.paddingLeft = 34; p.style.paddingRight = 34;
            p.style.paddingTop = 32; p.style.paddingBottom = 32;
            Chamfer(p, 14);
            Edge(p, C(Cy), 0.45f);

            var thumb = new VisualElement();
            thumb.style.width = 170; thumb.style.height = 170;
            thumb.style.backgroundColor = new Color(0.10f, 0.17f, 0.26f, 1f);
            Chamfer(thumb, 10);
            Edge(thumb, C(Cy), 0.6f);
            p.Add(thumb);

            var col = new VisualElement { style = { marginLeft = 18, flexGrow = 1 } };
            col.Add(Text("ПОСЛЕДНЯЯ СЕССИЯ", 36, Cy, 3f, FontStyle.Bold));
            var t = Text("Эпизод 01 · сцена 14 из 26", 40, Tx, 0f);
            t.style.whiteSpace = WhiteSpace.Normal;
            t.style.marginTop = 8; t.style.marginBottom = 12;
            col.Add(t);
            col.Add(Bar(0.54f));
            p.Add(col);

            var go = Text("▶", 64, Cy, 0f, FontStyle.Bold);
            go.style.marginLeft = 20;
            p.Add(go);
            return p;
        }

        // ── нижняя навигация ────────────────────────────────────────────────
        private static VisualElement BottomNav()
        {
            var nav = Row();
            nav.style.justifyContent = Justify.SpaceBetween;
            nav.style.paddingTop = 30;
            nav.style.borderTopWidth = 1;
            nav.style.borderTopColor = new Color(C(Cy).r, C(Cy).g, C(Cy).b, 0.35f);
            string[] items = { "ХАБ", "МАГАЗИН", "ГАРДЕРОБ", "АРХИВ", "ПРОФИЛЬ" };
            for (int i = 0; i < items.Length; i++)
            {
                var col = new VisualElement { style = { alignItems = Align.Center } };
                bool active = i == 0;
                // Активный помечается ЧЕРТОЙ СВЕРХУ, а не цветом текста: черта
                // читается мгновенно и работает даже боковым зрением.
                var mark = new VisualElement();
                mark.style.height = 6; mark.style.width = 76;
                mark.style.backgroundColor = active ? C(Cy) : Color.clear;
                mark.style.marginBottom = 18;
                col.Add(mark);
                col.Add(Text(items[i], 32, active ? Cy : Dim, 2f,
                             active ? FontStyle.Bold : FontStyle.Normal));
                nav.Add(col);
            }
            return nav;
        }

        // ── мелочи ──────────────────────────────────────────────────────────
        private static VisualElement CyberButton(string text, bool primary)
        {
            var b = new Button { text = text };
            b.style.marginRight = 12;
            b.style.paddingLeft = 54; b.style.paddingRight = 54;
            b.style.paddingTop = 34; b.style.paddingBottom = 34;
            b.style.fontSize = 44;
            b.style.letterSpacing = 3f;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.style.color = primary ? C(Bg) : C(Cy);
            b.style.backgroundColor = primary ? C(Cy) : new Color(0, 0, 0, 0.25f);
            Chamfer(b, 12);
            Edge(b, C(Cy), primary ? 0f : 0.8f);
            return b;
        }

        private static VisualElement SectionLabel(string text, string index)
        {
            var r = Row();
            r.style.alignItems = Align.Center;
            var line = new VisualElement();
            line.style.width = 46; line.style.height = 4;
            line.style.backgroundColor = C(Cy);
            line.style.marginRight = 12;
            r.Add(line);
            r.Add(Text(text, 40, Tx, 4f, FontStyle.Bold));
            var idx = Text("[" + index + "]", 34, Dim, 2f);
            idx.style.marginLeft = 10;
            r.Add(idx);
            return r;
        }

        private static Label Text(string s, int size, string color, float spacing,
                                  FontStyle weight = FontStyle.Normal)
        {
            var l = new Label(s);
            l.style.fontSize = size;
            l.style.color = C(color);
            l.style.letterSpacing = spacing;
            l.style.unityFontStyleAndWeight = weight;
            return l;
        }

        private static VisualElement Row()
        {
            var r = new VisualElement();
            r.style.flexDirection = FlexDirection.Row;
            return r;
        }

        private static VisualElement Space(int h) =>
            new VisualElement { style = { height = h } };

        /// <summary>Скошенный угол — подпись жанра. UI Toolkit не умеет фаску,
        /// поэтому имитируем малым радиусом: он читается как срез, а не как
        /// мягкое скругление.</summary>
        private static void Chamfer(VisualElement el, int r)
        {
            el.style.borderTopLeftRadius = r;
            el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r;
            el.style.borderBottomRightRadius = r;
        }

        private static void Edge(VisualElement el, Color c, float alpha)
        {
            var col = new Color(c.r, c.g, c.b, alpha);
            el.style.borderLeftWidth = alpha > 0 ? 1 : 0;
            el.style.borderRightWidth = alpha > 0 ? 1 : 0;
            el.style.borderTopWidth = alpha > 0 ? 1 : 0;
            el.style.borderBottomWidth = alpha > 0 ? 1 : 0;
            el.style.borderLeftColor = col;
            el.style.borderRightColor = col;
            el.style.borderTopColor = col;
            el.style.borderBottomColor = col;
        }
    }
}
