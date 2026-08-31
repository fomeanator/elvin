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
            Assert.AreEqual(14f, строка.style.paddingTop.value.value, 1e-4f,
                "у строки пропал вертикальный воздух — список читается как сплошная стена");
            Assert.AreEqual(14f, строка.style.paddingBottom.value.value, 1e-4f,
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
