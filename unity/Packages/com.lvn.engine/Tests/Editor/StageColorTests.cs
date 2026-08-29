using Lvn.UI;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// ЦВЕТ ВСПЫШКИ И ВУАЛИ ПИШЕТСЯ ТАК ЖЕ, КАК ВЕЗДЕ.
    ///
    /// <para>Автор задаёт цвет шестнадцатеричным числом во всех остальных
    /// командах грамматики — в тексте, в эффектах, у портала. У <c>tint</c> и
    /// <c>flash</c> разбор был свой, знал одиннадцать английских слов и на всё
    /// прочее молча отвечал белым. Написанный автором тёплый полумрак
    /// оборачивался белой заливкой во весь экран — без единой строчки в
    /// журнале.</para>
    ///
    /// <para>Мнемоники настроения («холодно», «тепло», «сепия») — своя история:
    /// это готовые оттенки, которые зовут словом, и они обязаны пережить
    /// переход на общий разбор.</para>
    /// </summary>
    public sealed class StageColorTests
    {
        [Test]
        public void ШестнадцатеричныйЦветДоходитДоСцены()
        {
            var c = VnStage.ParseColor("#3a1c0d", Color.white);

            Assert.AreNotEqual(Color.white, c, "автор написал цвет — получил белую заливку");
            Assert.AreEqual(0x3a / 255f, c.r, 0.01f);
            Assert.AreEqual(0x1c / 255f, c.g, 0.01f);
            Assert.AreEqual(0x0d / 255f, c.b, 0.01f);
        }

        [Test]
        public void ЦветБезРешёткиТожеЦвет()
        {
            Assert.AreEqual(VnStage.ParseColor("#ff0000", Color.white),
                            VnStage.ParseColor("ff0000", Color.white));
        }

        [Test]
        public void ПрозрачностьВЦветеСохраняется()
        {
            var c = VnStage.ParseColor("#ffffff80", Color.black);

            Assert.AreEqual(0.5f, c.a, 0.02f, "восьмизначный цвет несёт и прозрачность");
        }

        [Test]
        public void ИменаОстаютсяПрежними()
        {
            Assert.AreEqual(Color.white, VnStage.ParseColor("white", Color.black));
            Assert.AreEqual(Color.red, VnStage.ParseColor("red", Color.black));
            Assert.AreEqual(Color.magenta, VnStage.ParseColor("magenta", Color.black));
        }

        [Test]
        public void МнемоникиНастроенияПережилиПереход()
        {
            var warm = VnStage.ParseColor("warm", Color.black);
            var cold = VnStage.ParseColor("cold", Color.black);
            var sepia = VnStage.ParseColor("sepia", Color.black);

            Assert.Greater(warm.r, warm.b, "«тепло» — тёплый оттенок");
            Assert.Greater(cold.b, cold.r, "«холодно» — холодный оттенок");
            Assert.Greater(sepia.r, sepia.b, "«сепия» — коричневатый");
        }

        [Test]
        public void ОпечаткаЖалуетсяИНеМолчит()
        {
            // Опечатку автор обязан УВИДЕТЬ: без жалобы «вспышка не того цвета»
            // ищется глазами по всему скрипту.
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("chartreuse"));

            Assert.AreEqual(Color.magenta, VnStage.ParseColor("chartreuse", Color.magenta),
                "непонятое имя — умолчание вызывающего, а не белый");
        }
    }
}
