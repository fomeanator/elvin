using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ЛИСТ, ЗАТЕМНЕНИЕ И КАРТОЧКА — три приёма, которые оболочка повторяла
    /// руками: геометрию листа пятью экранами, правило закрытия семью (и двумя
    /// разными событиями), карточку восемью.
    /// </summary>
    public class ChromeSheetTests
    {
        [Test]
        public void ЛистОтступаетОтБоковИЛежитАбсолютно()
        {
            var el = LvnChrome.Sheet(new VisualElement());
            Assert.AreEqual(Position.Absolute, el.style.position.value);
            Assert.AreEqual(LvnChrome.SheetSidePercent, el.style.left.value.value, 0.001f);
            Assert.AreEqual(LvnChrome.SheetSidePercent, el.style.right.value.value, 0.001f);
            Assert.AreEqual(LengthUnit.Percent, el.style.left.value.unit, "отступ — доля ширины, а не пиксели");
        }

        [Test]
        public void ЛистНеТребуетВысоты()
        {
            // Верх, низ и потолок — дело вызывающего: у списка сохранений и
            // формы входа они разные по смыслу.
            var el = LvnChrome.Sheet(new VisualElement());
            Assert.AreEqual(StyleKeyword.Null, el.style.top.keyword);
            Assert.AreEqual(StyleKeyword.Null, el.style.bottom.keyword);
        }

        [Test]
        public void ЗатемнениеБерётЦветТемыИлиАвторский()
        {
            var byTheme = LvnChrome.Scrim(new VisualElement(), null);
            Assert.AreEqual(LvnTokens.Scrim, byTheme.style.backgroundColor.value);

            var own = new Color(0.1f, 0.2f, 0.3f, 0.5f);
            Assert.AreEqual(own, LvnChrome.Scrim(new VisualElement(), null, own).style.backgroundColor.value);
        }

        [Test]
        public void КарточкаБерётПоверхностьИРадиусТемы()
        {
            var el = LvnChrome.Card(new VisualElement());
            Assert.AreEqual(LvnTokens.Surface, el.style.backgroundColor.value);
            Assert.AreEqual(LvnTokens.RadiusSm, el.style.borderTopLeftRadius.value.value, 0.001f);
        }

        [Test]
        public void КарточкаПринимаетСвойФонИРадиус()
        {
            var el = LvnChrome.Card(new VisualElement(), LvnTokens.SurfaceHi, 42f);
            Assert.AreEqual(LvnTokens.SurfaceHi, el.style.backgroundColor.value);
            Assert.AreEqual(42f, el.style.borderTopLeftRadius.value.value, 0.001f);
        }

        [Test]
        public void ПолупрозрачнаяПоверхностьЭтоТаЖеПоверхность()
        {
            var soft = LvnTokens.SurfaceSoft;
            var solid = LvnTokens.Surface;
            Assert.AreEqual(solid.r, soft.r, 0.001f);
            Assert.AreEqual(solid.g, soft.g, 0.001f);
            Assert.AreEqual(solid.b, soft.b, 0.001f);
            Assert.Less(soft.a, 1f, "сквозь неё видно сцену");
        }

        [Test]
        public void НичегоНеПадаетНаПустомЭлементе()
        {
            Assert.IsNull(LvnChrome.Sheet<VisualElement>(null));
            Assert.IsNull(LvnChrome.Scrim<VisualElement>(null, null));
            Assert.IsNull(LvnChrome.Card<VisualElement>(null));
        }
    }
}
