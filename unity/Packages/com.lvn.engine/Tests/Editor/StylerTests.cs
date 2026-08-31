using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// Стилизатор: роль решает вид, вид следует за темой, а компоновку
    /// (размеры, отступы, шрифт) роль не трогает.
    public class StylerTests
    {
        private string _themeWas;

        [SetUp]
        public void Setup() => _themeWas = LvnTheme.Current.Name;

        [TearDown]
        public void Restore() => LvnTheme.Use(_themeWas);

        // Ради этого роль и заводилась: цвет берётся у ДЕЙСТВУЮЩЕЙ темы, а не
        // прописан в экране. Пока каждый экран красил себя сам, смена темы
        // перекрашивала половину приложения — хуже, чем ничего.
        [Test]
        public void Role_FollowsTheTheme_NotALiteral()
        {
            LvnTheme.Use("midnight");
            var midnight = LvnStyler.Primary(new Button()).style.backgroundColor.value;

            LvnTheme.Use("cyber");
            var cyber = LvnStyler.Primary(new Button()).style.backgroundColor.value;

            Assert.AreNotEqual(midnight, cyber, "тема сменилась — роль обязана выглядеть иначе");
            Assert.AreEqual(LvnTokens.Accent, cyber);
        }

        [Test]
        public void Primary_IsAccentWithInkOverIt()
        {
            var b = LvnStyler.Primary(new Button());
            Assert.AreEqual(LvnTokens.Accent, b.style.backgroundColor.value);
            Assert.AreEqual(LvnTokens.OnAccent, b.style.color.value);
        }

        [Test]
        public void Choice_ChosenLooksLikePrimary_OthersAreQuiet()
        {
            var chosen = LvnStyler.Choice(new Button(), true);
            var other = LvnStyler.Choice(new Button(), false);

            Assert.AreEqual(LvnTokens.Accent, chosen.style.backgroundColor.value);
            Assert.AreEqual(LvnTokens.Faint, other.style.backgroundColor.value);
            Assert.AreNotEqual(chosen.style.color.value, other.style.color.value,
                "выбранный вариант должен читаться с одного взгляда");
        }

        [Test]
        public void ВкладкаМеняетИЦвет_ИВес_ИВозвращаетИхОбратно()
        {
            // Правил на одну вкладку было ТРИ, и каждое сломалось по-своему.
            // Магазин звал общий выбор и добавлял жирность отдельной строкой;
            // витрина скинов жирность только ВКЛЮЧАЛА и никогда не снимала —
            // походив по вкладкам, игрок получал жирными все сразу; таблица
            // лидеров красила всё сама, и невыбранная вкладка выходила
            // ПРОЗРАЧНОЙ вместо приглушённой, то есть переставала выглядеть
            // кнопкой. Ряды на вид одинаковые, ведут себя по-разному.
            //
            // Вес здесь не украшение: на солнце и на дешёвом экране одного
            // цвета мало, чтобы понять, где ты стоишь.
            var вкладка = new Button();

            LvnStyler.Tab(вкладка, active: true);
            Assert.AreEqual(LvnTokens.Accent, вкладка.style.backgroundColor.value,
                "активная вкладка не горит акцентом — раздел, в котором стоит игрок, ничем не отмечен");
            Assert.AreEqual(FontStyle.Bold, вкладка.style.unityFontStyleAndWeight.value,
                "активная вкладка держится на одном цвете — на солнце его не видно");

            LvnStyler.Tab(вкладка, active: false);
            Assert.AreEqual(FontStyle.Normal, вкладка.style.unityFontStyleAndWeight.value,
                "вкладка не похудела обратно — походив по разделам, игрок получит жирными все сразу");
            Assert.AreEqual(LvnTokens.Faint, вкладка.style.backgroundColor.value,
                "невыбранная вкладка стала прозрачной — она перестала выглядеть кнопкой");
        }

        [Test]
        public void ГнездоПодЗнакДержитСимволВЦентреКвадрата()
        {
            // Собиралось вручную в пяти местах по десять строк, и ни одна не
            // выглядит лишней — потому их и переписывали заново, а не искали
            // общее. Обнулить ВСЕ ЧЕТЫРЕ отступа обязательно: у кнопки они свои
            // по умолчанию, и символ уезжает из центра гнезда — «назад» стоит
            // криво, а увидеть это можно только глазами.
            var гнездо = LvnStyler.IconSlot(new Button(), 48f);

            Assert.AreEqual(48f, гнездо.style.width.value.value, 1e-4f, "гнездо не квадратное");
            Assert.AreEqual(48f, гнездо.style.height.value.value, 1e-4f, "гнездо не квадратное");
            Assert.AreEqual(0f, гнездо.style.paddingLeft.value.value, 1e-4f, "символ уехал из центра гнезда");
            Assert.AreEqual(0f, гнездо.style.paddingRight.value.value, 1e-4f, "символ уехал из центра гнезда");
            Assert.AreEqual(0f, гнездо.style.paddingTop.value.value, 1e-4f, "символ уехал из центра гнезда");
            Assert.AreEqual(0f, гнездо.style.paddingBottom.value.value, 1e-4f, "символ уехал из центра гнезда");
            Assert.AreEqual(Align.Center, гнездо.style.alignItems.value, "символ не выровнен поперёк гнезда");
            Assert.AreEqual(Justify.Center, гнездо.style.justifyContent.value, "символ не выровнен вдоль гнезда");
            Assert.AreEqual(LvnTokens.Faint, гнездо.style.backgroundColor.value,
                "плашка гнезда взята мимо палитры — «назад» в галерее и в таблице лидеров станут разными кнопками");
            Assert.AreEqual(LvnTokens.RadiusSm, гнездо.style.borderTopLeftRadius.value.value, 1e-4f,
                "скругление гнезда взято не у темы");
        }

        [Test]
        public void РазмерГнездаОстаётсяЭкрану()
        {
            // Разнобой был не в размере: над постером во весь экран уместна
            // кнопка крупнее, чем в плотной шапке списка. Разъезжалось ВСЁ
            // ОСТАЛЬНОЕ, что каждый решал заново. Забери роль ещё и размер — и
            // экранам придётся чинить её поверх.
            Assert.AreEqual(60f, LvnStyler.IconSlot(new Button(), 60f).style.height.value.value, 1e-4f);
            Assert.AreEqual(4f, LvnStyler.IconSlot(new Button(), 44f, 4f).style.borderTopLeftRadius.value.value, 1e-4f,
                "экран настоял на своём скруглении, и его не услышали");
        }

        [Test]
        public void ПилюляСкругляетсяПодСобственнуюВысоту()
        {
            // Пилюля читается ярлыком именно из-за полукруглых торцов. Задай ей
            // скругление числом — при другой высоте она превратится в
            // прямоугольник со срезанными углами, и в одном ряду с настоящей
            // пилюлей (счётчик валюты рядом с меткой) разнобой виден сразу.
            var высокая = LvnStyler.Pill(new VisualElement(), 28f);
            var низкая = LvnStyler.Pill(new VisualElement(), 18f);

            Assert.AreEqual(14f, высокая.style.borderTopLeftRadius.value.value, 1e-4f,
                "торцы пилюли не полукруглые — ярлык стал прямоугольником");
            Assert.AreEqual(9f, низкая.style.borderTopLeftRadius.value.value, 1e-4f,
                "скругление не поехало за высотой — пилюли в одном ряду выглядят разными");
            Assert.AreEqual(LvnTokens.Faint, высокая.style.backgroundColor.value,
                "плашка пилюли взята не у темы");
        }

        [Test]
        public void КарточкаНоситКромкуТемы_АКнопкаНет()
        {
            // Кромка — примета ГРАНЁНЫХ тем: ею карточка отделяется от живого
            // фона, на котором иначе тонет. Но она свойство ТЕМЫ, а не роли:
            // в теме без кромки карточка обязана остаться гладкой, иначе
            // «Полночь» получит обводку, которой в ней нет по замыслу.
            // Кнопке кромка не полагается ни в какой теме: её край задаёт
            // плашка, а рамка от умолчаний UITK — мусор.
            LvnTheme.Use("cyber");
            Assert.Greater(LvnStyler.Card(new VisualElement()).style.borderTopWidth.value, 0f,
                "карточка осталась без кромки в гранёной теме — она утонет в живом фоне");
            Assert.AreEqual(0f, LvnStyler.Primary(new Button()).style.borderTopWidth.value, 1e-4f,
                "кнопке досталась кромка карточки");

            LvnTheme.Use("midnight");
            Assert.AreEqual(0f, LvnStyler.Card(new VisualElement()).style.borderTopWidth.value, 1e-4f,
                "кромка появилась в теме, которая её не носит");
            Assert.AreEqual(LvnTokens.Surface, LvnStyler.Card(new VisualElement()).style.backgroundColor.value,
                "поверхность карточки взята не у темы — смена темы её не перекрасит");
        }

        [Test]
        public void Ghost_HasNoPlateAtAll()
        {
            var b = LvnStyler.Ghost(new Button());
            Assert.AreEqual(Color.clear, b.style.backgroundColor.value);
            Assert.AreEqual(LvnTokens.Text, b.style.color.value);
        }

        [Test]
        public void Ghost_TakesTheSceneInkWhenGiven()
        {
            var ink = new Color(0.2f, 0.9f, 0.4f);
            Assert.AreEqual(ink, LvnStyler.Ghost(new Button(), ink).style.color.value,
                "у сцены своя палитра из манифеста — роль обязана её принять");
        }

        // Новелла вправе переопределить цвета в манифесте. Роль от этого не
        // исчезает: вид всё равно собирает стилизатор, а не копия четырёх строк.
        [Test]
        public void Plate_TakesAForeignPalette()
        {
            var plate = new Color(0.1f, 0.2f, 0.3f);
            var ink = new Color(0.9f, 0.9f, 0.9f);
            var b = LvnStyler.Plate(new Button(), plate, ink, 7f);

            Assert.AreEqual(plate, b.style.backgroundColor.value);
            Assert.AreEqual(ink, b.style.color.value);
            Assert.AreEqual(7f, b.style.borderTopLeftRadius.value.value, 1e-4f);
        }

        [Test]
        public void Plate_ClearsTheBorder_SkinnedKeepsIt()
        {
            var plain = new Button();
            plain.style.borderTopWidth = 3f;
            LvnStyler.Plate(plain, Color.red, Color.white);
            Assert.AreEqual(0f, plain.style.borderTopWidth.value, 1e-4f,
                "рамка от умолчаний — мусор, роль её снимает");

            var art = new Button();
            art.style.borderTopWidth = 3f;
            LvnStyler.Skinned(art, Color.red, Color.white, 4f);
            Assert.AreEqual(3f, art.style.borderTopWidth.value, 1e-4f,
                "под кнопкой новеллы лежит 9-slice арт — рамка там часть оформления");
        }

        [Test]
        public void Radius_DefaultsToTheTheme_ButTheScreenMayInsist()
        {
            Assert.AreEqual(LvnTokens.Radius,
                LvnStyler.Primary(new Button()).style.borderTopLeftRadius.value.value, 1e-4f);
            Assert.AreEqual(3f,
                LvnStyler.Primary(new Button(), 3f).style.borderTopLeftRadius.value.value, 1e-4f);
        }

        [Test]
        public void Track_ClipsItsFill()
        {
            var t = LvnStyler.Track(new VisualElement(), 12f);
            Assert.AreEqual(Overflow.Hidden, t.style.overflow.value,
                "иначе заливка вылезет за скруглённые углы дорожки");
            Assert.AreEqual(6f, t.style.borderTopLeftRadius.value.value, 1e-4f,
                "скругление дорожки — половина её высоты");
        }

        [Test]
        public void Fill_IsAccentUnlessTheBarMeansSomethingElse()
        {
            Assert.AreEqual(LvnTokens.Accent,
                LvnStyler.Fill(new VisualElement()).style.backgroundColor.value);
            Assert.AreEqual(LvnTokens.Gold,
                LvnStyler.Fill(new VisualElement(), tint: LvnTokens.Gold).style.backgroundColor.value);
        }

        // ── строка списка ───────────────────────────────────────────────────

        [Test]
        public void ПлиткаСтрокиНеСжимаетсяВДлинномСписке()
        {
            // Ею набраны списки глав, сейвов, наград и достижений — а список
            // тем и отличается, что длиннее экрана. Флекс сжимает детей,
            // которые не помещаются: без запрета двадцатая глава превращается
            // в полоску в пару пикселей, и вместе со строкой сплющивается всё
            // её содержимое. Ловится это только на списке, который перерос
            // экран, — то есть у игрока, а не на стенде.
            var строка = LvnStyler.ListRow(new VisualElement());

            Assert.AreEqual(0f, строка.style.flexShrink.value, 1e-4f,
                "строку разрешено сжимать — длинный список схлопнет её в полоску");
            // Воздух берётся СТУПЕНЬЮ ТЕМЫ, а не числом: до 01.09 здесь стояло
            // 14 — на два пикселя мимо ступени, как ещё в семидесяти местах.
            // Проверяем именно ступень, чтобы смена шага в теме не роняла тест
            // ложно, но исчезновение воздуха роняло по-прежнему.
            Assert.AreEqual(LvnTokens.Space2, строка.style.paddingTop.value.value, 1e-4f,
                "у строки пропал вертикальный воздух — список читается как сплошная стена");
            Assert.AreEqual(LvnTokens.Space2, строка.style.paddingBottom.value.value, 1e-4f,
                "воздух снизу разошёлся с воздухом сверху — строка стала кривой");
        }

        [Test]
        public void СодержимоеСтрокиСтоитВРядПоЦентру()
        {
            // Строка списка — это всегда «значок, название, значение справа».
            // Оставь колонку по умолчанию, и они встанут ДРУГ ПОД ДРУГОМ; без
            // выравнивания по центру значок и текст разной высоты разъезжаются
            // по верхнему краю.
            var строка = LvnStyler.ListRow(new VisualElement());

            Assert.AreEqual(FlexDirection.Row, строка.style.flexDirection.value,
                "содержимое строки встало столбцом — так список не выглядит списком");
            Assert.AreEqual(Align.Center, строка.style.alignItems.value,
                "значок и подпись разной высоты разъедутся по верхнему краю");
        }

        [Test]
        public void СтрокаСпискаНеКарточка_УНеёСвоёСкругление()
        {
            // Строка и карточка — разные вещи и обязаны читаться разными. Дай
            // строке скругление карточки, и список глав станет столбиком
            // карточек: иерархия экрана исчезает.
            var строка = LvnStyler.ListRow(new VisualElement());

            Assert.AreEqual(LvnTokens.Surface, строка.style.backgroundColor.value,
                "поверхность строки взята не у темы — смена темы её не перекрасит");
            Assert.AreEqual(LvnTokens.RadiusSm, строка.style.borderTopLeftRadius.value.value, 1e-4f,
                "у строки скругление карточки — список превратился в столбик карточек");
        }

        [Test]
        public void ПоляВокругСтрокиОстаютсяЭкрану()
        {
            // Отступ до соседней строки и поля по горизонтали — про поля
            // ЭКРАНА, а не про саму строку, и у разных списков они честно
            // разные (награды шире глав). Забери их роль — и каждый список
            // пришлось бы чинить обратно поверх роли.
            var строка = new VisualElement();
            строка.style.marginBottom = 9;
            строка.style.paddingLeft = 22;
            строка.style.paddingRight = 22;

            LvnStyler.ListRow(строка);

            Assert.AreEqual(9f, строка.style.marginBottom.value.value, 1e-4f,
                "роль съела отступ до следующей строки — список слипся");
            Assert.AreEqual(22f, строка.style.paddingLeft.value.value, 1e-4f,
                "роль съела горизонтальные поля экрана");
            Assert.AreEqual(22f, строка.style.paddingRight.value.value, 1e-4f,
                "роль съела горизонтальные поля экрана");
        }

        // Роль отвечает за вид, а не за место. Забери она размеры — и экран
        // потеряет право компоновать себя.
        [Test]
        public void Roles_DoNotTouchLayout()
        {
            var b = new Button();
            b.style.fontSize = 33;
            b.style.paddingLeft = 21;
            b.style.width = 120;

            LvnStyler.Primary(b);

            Assert.AreEqual(33f, b.style.fontSize.value.value, 1e-4f);
            Assert.AreEqual(21f, b.style.paddingLeft.value.value, 1e-4f);
            Assert.AreEqual(120f, b.style.width.value.value, 1e-4f);
        }

        [Test]
        public void NothingIsNotAnError()
        {
            Assert.IsNull(LvnStyler.Primary<Button>(null));
            Assert.IsNull(LvnStyler.Ghost<Button>(null));
            Assert.IsNull(LvnStyler.Card<VisualElement>(null));
            Assert.IsNull(LvnStyler.Track<VisualElement>(null, 10f));
            Assert.IsNull(LvnStyler.ListRow<VisualElement>(null));
        }
    }
}
