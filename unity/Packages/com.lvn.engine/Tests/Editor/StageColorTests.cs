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
        public void ЗелёныйОстаётсяЯркимЗелёным()
        {
            // ЭТО ЗАЩИТА УЖЕ НАПИСАННЫХ ГЛАВ, а не придирка к оттенку.
            // «green» в HTML — тёмный #008000, в движке — яркий (0,1,0). Имена
            // движка оставлены в разборе ЯВНО именно поэтому: молча передать их
            // общему разбору значило бы перекрасить каждую вспышку и каждую
            // вуаль, написанную словом, — и ни автор, ни журнал об этом бы не
            // узнали.
            var green = VnStage.ParseColor("green", Color.black);

            Assert.AreEqual(new Color(0f, 1f, 0f, 1f), green, "«green» перестал быть ярким зелёным");
            Assert.AreNotEqual(VnStage.ParseColor("#008000", Color.black), green,
                "движковый «green» съехал на HTML-овский тёмный — главы перекрашены задним числом");
        }

        [Test]
        public void ВосьмизначныйБезРешёткиТожеЦвет()
        {
            // Решётку автор ставит не всегда — правило «без решётки тоже цвет»
            // обязано доходить и до записи с прозрачностью, иначе половина
            // формы работает, а половина молча даёт умолчание.
            Assert.AreEqual(VnStage.ParseColor("#ffffff80", Color.black),
                            VnStage.ParseColor("ffffff80", Color.black));
        }

        [Test]
        public void РегистрНаписанияНеВажен()
        {
            // Автор пишет цвет как ему удобно; «WHITE» и «#3A1C0D» — тот же
            // цвет, а не опечатка с жалобой в журнал.
            Assert.AreEqual(Color.white, VnStage.ParseColor("WHITE", Color.black));
            Assert.AreEqual(VnStage.ParseColor("warm", Color.black),
                            VnStage.ParseColor("Warm", Color.black));
            Assert.AreEqual(VnStage.ParseColor("#3a1c0d", Color.white),
                            VnStage.ParseColor("#3A1C0D", Color.white));
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
