using System.Collections.Generic;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>Ряд вариантов: переносится и никогда не сжимается. Третья кнопка
    /// качества арта уехала за край экрана на телефоне партнёра — игрок видел не
    /// обрезанный ряд, а ДВА варианта вместо трёх.</summary>
    public class SegmentTests
    {
        private static readonly string[] Three = { "2K", "1440", "1K" };

        private static VisualElement Row(string current = "2K",
                                         System.Action<string> pick = null,
                                         System.Action<Button, bool> style = null)
            => LvnSegment.Of(Three, o => o, o => o == current, pick, style);

        [Test]
        public void ВариантыПереносятсяАНеСжимаются()
        {
            // НЕВЫКЛЮЧАЕМО: лучше ряд в две строки, чем вариант, которого для
            // игрока не существует.
            var seg = Row();
            Assert.AreEqual(Wrap.Wrap, seg.style.flexWrap.value);
            foreach (var child in seg.Children())
                Assert.AreEqual(0f, child.style.flexShrink.value,
                    "сжатая кнопка теряет подпись раньше, чем ряд — ширину");
        }

        [Test]
        public void РядНеШиреСвоегоМеста()
        {
            var seg = Row();
            Assert.AreEqual(LengthUnit.Percent, seg.style.maxWidth.value.unit);
            Assert.AreEqual(100f, seg.style.maxWidth.value.value,
                "без предела ряд растягивал родителя и уезжал за край экрана");
        }

        [Test]
        public void ОтступСправаАНеСлева()
        {
            // При переносе левый отступ первой кнопки строки сдвигал бы её от
            // края, и ряд выглядел бы кривым.
            var b = (Button)Row()[0];
            Assert.Greater(b.style.marginRight.value.value, 0f);
            Assert.AreEqual(0f, b.style.marginLeft.value.value);
            Assert.Greater(b.style.marginBottom.value.value, 0f,
                "перенос без нижнего отступа слипается со следующей строкой");
        }

        [Test]
        public void ПодписьПереноситсяАНеОбрезается()
        {
            var b = (Button)Row()[0];
            Assert.AreEqual(WhiteSpace.Normal, b.style.whiteSpace.value,
                "кнопка без переноса обрезает текст — игрок видит огрызок");
        }

        [Test]
        public void СоставРядаЭтоАвторскийПорядок()
        {
            var seg = Row();
            Assert.AreEqual(3, seg.childCount);
            for (int i = 0; i < Three.Length; i++)
                Assert.AreEqual(Three[i], ((Button)seg[i]).text);
        }

        [Test]
        public void ПодсветкаСтавитсяВсемКнопкамСразуПриСборке()
        {
            // Раньше это был отдельный список и отдельный цикл в каждом месте.
            var lit = new List<string>();
            var seg = LvnSegment.Of(Three, o => o, o => o == "1440", null,
                                    (b, on) => { if (on) lit.Add(b.text); });
            Assert.AreEqual(1, lit.Count);
            Assert.AreEqual("1440", lit[0]);
            Assert.AreEqual(3, seg.childCount);
        }

        [Test]
        public void РядНеХранитВыборИНеМожетСНимРазойтись()
        {
            // Выбор спрашивается при каждой перерисовке, а не запоминается.
            int asked = 0;
            LvnSegment.Of(Three, o => o, o => { asked++; return false; }, null, (b, on) => { });
            Assert.AreEqual(3, asked, "спросили у каждого варианта");
        }

        [Test]
        public void ПустойСписокДаётПустойРядАНеПадение()
        {
            var seg = LvnSegment.Of<string>(null, o => o, o => false, null, (b, on) => { });
            Assert.AreEqual(0, seg.childCount);

            var empty = LvnSegment.Of(new string[0], o => o, o => false, null, (b, on) => { });
            Assert.AreEqual(0, empty.childCount);
        }

        [Test]
        public void БезПодписиИБезВидаРядВсёРавноСобирается()
        {
            var seg = LvnSegment.Of(new[] { 1, 2 }, null, null, null, null);
            Assert.AreEqual(2, seg.childCount);
            Assert.AreEqual("1", ((Button)seg[0]).text, "без функции подписи берётся сам вариант");
        }

        [Test]
        public void ВыравниваниеРядаВыбираетЭкран()
        {
            var end = LvnSegment.Of(Three, o => o, o => false, null, null, alignEnd: true);
            var start = LvnSegment.Of(Three, o => o, o => false, null, null, alignEnd: false);
            Assert.AreEqual(Justify.FlexEnd, end.style.justifyContent.value);
            Assert.AreEqual(Justify.FlexStart, start.style.justifyContent.value);
        }
    }
}
