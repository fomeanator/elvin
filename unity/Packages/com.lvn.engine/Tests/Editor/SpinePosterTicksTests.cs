using NUnit.Framework;
using Lvn.UI;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОСТЕР СНИМАЕТ РЕЖЕ, ЧЕМ ИДУТ КАДРЫ. Камера с целевой текстурой рисует
    /// каждый кадр, а таких камер на экране бывает пять — лента, витрина,
    /// столбик магазина. Правило частоты живёт чистой функцией, поэтому его
    /// можно проверить без камеры, скелета и единого кадра.
    /// </summary>
    public class SpinePosterTicksTests
    {
        [Test]
        public void FirstFrameAlwaysRenders()
        {
            Assert.IsTrue(LvnSpinePoster.ShouldRender(-1f, 0f),
                "первый кадр обязан сняться: до него у постера пустая текстура");
        }

        [Test]
        public void TooSoonIsSkipped()
        {
            float step = 1f / LvnSpinePoster.Hz;
            Assert.IsFalse(LvnSpinePoster.ShouldRender(10f, 10f + step * 0.5f),
                "половина такта — рано; иначе ограничение не ограничивает");
        }

        [Test]
        public void TheTickItselfRenders()
        {
            float step = 1f / LvnSpinePoster.Hz;
            Assert.IsTrue(LvnSpinePoster.ShouldRender(10f, 10f + step),
                "ровно такт — снимаем: иначе частота молча уползёт вниз");
        }

        [Test]
        public void RateStaysBelowTheScreen()
        {
            Assert.Less(LvnSpinePoster.Hz, 60f,
                "смысл дома в том, что фигура снимается реже экрана");
            Assert.GreaterOrEqual(LvnSpinePoster.Hz, 24f,
                "ниже кино движение фигуры начнёт дёргаться");
        }
    }
}
