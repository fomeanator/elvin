using Lvn.UI.World;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// МОРГАНИЕ ОБЯЗАНО ОТКРЫТЬСЯ.
    ///
    /// <para><c>fx blink=1</c> смыкает веки. Всякий другой эффект стека —
    /// состояние: доехал до цели и держит. Для моргания это означало занавес
    /// навсегда — кадр оставался под веками, а следующая команда <c>fx</c> без
    /// поля <c>blink</c> прежнее значение сохраняла. Автор писал слово
    /// «моргнуть» и получал «закрыть глаза до конца главы», причём молча.</para>
    /// </summary>
    public class BlinkGestureTests
    {
        [Test]
        public void ClosedEyesReleaseThemselves()
        {
            Assert.AreEqual(0f, LvnFxStack.ReleaseBlink(1f, 1f), 0.0001f,
                "веки сомкнулись и остались сомкнутыми — это занавес, а не моргание");
            Assert.AreEqual(0f, LvnFxStack.ReleaseBlink(0.6f, 0.6f), 0.0001f,
                "неполное моргание отпускается так же");
        }

        [Test]
        public void EyesStillClosingKeepTheirTarget()
        {
            Assert.AreEqual(1f, LvnFxStack.ReleaseBlink(0.2f, 1f), 0.0001f,
                "веки ещё смыкаются — цель нельзя отпускать, моргания не будет видно");
        }

        [Test]
        public void OpenEyesStayOpen()
        {
            Assert.AreEqual(0f, LvnFxStack.ReleaseBlink(0f, 0f), 0.0001f,
                "покой не должен превращаться в бесконечный жест");
        }
    }
}
