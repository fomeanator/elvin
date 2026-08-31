using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// РЯД, ПОЛОСА И ЗНАЧЕНИЕ — три приёма, которые экраны повторяли руками:
    /// ряд по центру тридцать четыре раза, ширину полосы присваиванием, а
    /// сменившееся число ставили молча.
    /// </summary>
    public class ScreenUiRowTests
    {
        [Test]
        public void РядИдётСтрокойПоЦентру()
        {
            var row = ScreenUi.Row();
            Assert.AreEqual(FlexDirection.Row, row.style.flexDirection.value);
            Assert.AreEqual(Align.Center, row.style.alignItems.value);
            Assert.AreEqual(StyleKeyword.Null, row.style.justifyContent.keyword,
                "обычный ряд не разгоняет содержимое — это отдельная просьба");
        }

        [Test]
        public void РазгонПоКраямПоПросьбе()
        {
            Assert.AreEqual(Justify.SpaceBetween, ScreenUi.Row(spread: true).style.justifyContent.value);
        }

        [Test]
        public void РядНеТрогаетВнешниеПоля()
        {
            // Поля — про компоновку места, а не про сам ряд.
            var row = ScreenUi.Row();
            Assert.AreEqual(StyleKeyword.Null, row.style.marginBottom.keyword);
            Assert.AreEqual(StyleKeyword.Null, row.style.marginTop.keyword);
        }

        [Test]
        public void ГотовыйЭлементТожеСтановитсяРядом()
        {
            var b = ScreenUi.Row(new Button(), spread: true);
            Assert.AreEqual(FlexDirection.Row, b.style.flexDirection.value);
            Assert.AreEqual(Justify.SpaceBetween, b.style.justifyContent.value);
        }

        [Test]
        public void ПолосаНазадЕдетСразу()
        {
            var fill = new VisualElement();
            ScreenUi.SetFill(fill, 0.8f);
            // Вперёд — ходом (значение доедет само), назад — мгновенно: откат
            // знаменателя не событие для глаза.
            fill.style.width = new Length(80f, LengthUnit.Percent);
            ScreenUi.SetFill(fill, 0.3f);
            Assert.AreEqual(30f, fill.style.width.value.value, 0.001f);
        }

        [Test]
        public void ПолосаНеВыходитЗаКрая()
        {
            var fill = new VisualElement();
            ScreenUi.SetFill(fill, 5f);
            fill.style.width = new Length(500f, LengthUnit.Percent);
            ScreenUi.SetFill(fill, -1f);
            Assert.AreEqual(0f, fill.style.width.value.value, 0.001f);
        }

        [Test]
        public void ЗначениеСтавитсяИНеМигаетНаТомЖе()
        {
            var l = new Label();
            ScreenUi.SetValue(l, "12 МБ");
            Assert.AreEqual("12 МБ", l.text);

            l.style.opacity = 1f;
            ScreenUi.SetValue(l, "12 МБ");   // та же строка — без вдоха
            Assert.AreEqual(1f, l.style.opacity.value, 0.001f);
        }

        [Test]
        public void НичегоНеПадаетНаПустом()
        {
            Assert.IsNull(ScreenUi.Row<VisualElement>(null));
            ScreenUi.SetFill(null, 0.5f);
            ScreenUi.SetValue(null, "x");
        }
    }
}
