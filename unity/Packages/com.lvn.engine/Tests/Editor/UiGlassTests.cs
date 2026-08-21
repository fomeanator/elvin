using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// Стекло без камеры мира. Размытую подложку рисует камера канвас-сцены, но
    /// половина интерфейса живёт там, где её нет: экраны оболочки, редакторские
    /// тесты, режим без канваса. Требование одно — в этих местах стекло не
    /// должно ронять экран и не должно оставлять окно прозрачной дырой.
    ///
    /// <para>Проверить сам БЛЮР в EditMode нельзя: он рождается в
    /// <c>OnRenderImage</c>, а кадров здесь не бывает. Поэтому тест сторожит
    /// границу — поведение на отсутствующей подложке, — а не картинку.</para>
    /// </summary>
    public class UiGlassTests
    {
        private static VisualElement Host()
        {
            var el = new VisualElement();
            el.style.width = 100; el.style.height = 40;
            return el;
        }

        [Test]
        public void NoCameraStillPaintsTint()
        {
            var host = Host();
            UiGlass.Apply(host, 0.8f, new Color(0.1f, 0.1f, 0.15f, 0.9f));

            Assert.IsTrue(UiGlass.IsOn(host), "слой стекла должен появиться и без камеры");
            var tint = host.Q("lvn-glass-tint");
            Assert.IsNotNull(tint, "тонировка — то, что остаётся вместо размытия");
            Assert.Greater(tint.style.backgroundColor.value.a, 0.5f,
                "без подложки окно держится на одной тонировке: прозрачная дыра хуже плоской заливки");
        }

        [Test]
        public void StrengthScalesTintAlpha()
        {
            var host = Host();
            UiGlass.Apply(host, 0.5f, new Color(0f, 0f, 0f, 0.8f));
            float half = host.Q("lvn-glass-tint").style.backgroundColor.value.a;

            UiGlass.Apply(host, 1f, new Color(0f, 0f, 0f, 0.8f));
            float full = host.Q("lvn-glass-tint").style.backgroundColor.value.a;

            Assert.Less(half, full, "сила стекла должна доходить до тонировки, а не только до размытия");
        }

        [Test]
        public void ZeroStrengthRemovesTheLayer()
        {
            var host = Host();
            UiGlass.Apply(host, 0.8f, Color.black);
            Assert.IsTrue(UiGlass.IsOn(host));

            UiGlass.Apply(host, 0f, Color.black);
            Assert.IsFalse(UiGlass.IsOn(host), "нулевая сила обязана снимать слой, а не прятать его");
        }

        [Test]
        public void GlassClipsToTheHostCorners()
        {
            var host = Host();
            UiGlass.Apply(host, 0.8f, Color.black);
            Assert.AreEqual(Overflow.Hidden, host.style.overflow.value,
                "иначе размытый прямоугольник торчит из скруглённых углов окна");
        }

        // ── совмещение: та самая арифметика, из-за которой стекло может
        //    показывать «правильное размытие не того места» ──────────────────

        [Test]
        public void FitStretchesToTheWholeScreen()
        {
            var (size, _) = UiGlass.Fit(new Rect(0, 0, 1080, 1920), new Rect(100, 1400, 800, 300));
            Assert.AreEqual(1080f, size.x, 0.01f);
            Assert.AreEqual(1920f, size.y, 0.01f, "подложка — весь экран, а не окно: иначе мир в стекле сожмётся");
        }

        [Test]
        public void FitShiftsByMinusBoxPosition()
        {
            var (_, offset) = UiGlass.Fit(new Rect(0, 0, 1080, 1920), new Rect(140, 1500, 800, 300));
            Assert.AreEqual(-140f, offset.x, 0.01f);
            Assert.AreEqual(-1500f, offset.y, 0.01f,
                "знак смещения обратный координатам окна — плюс сдвинул бы мир в стекле в ту же сторону, что и окно");
        }

        [Test]
        public void FitAtOriginDoesNotShift()
        {
            var (_, offset) = UiGlass.Fit(new Rect(0, 0, 1080, 1920), new Rect(0, 0, 1080, 200));
            Assert.AreEqual(Vector2.zero, offset, "окно в углу экрана видит подложку как есть");
        }

        [Test]
        public void GlassIsInvisibleToTaps()
        {
            var host = Host();
            UiGlass.Apply(host, 0.8f, Color.black);
            Assert.AreEqual(PickingMode.Ignore, host.Q("lvn-glass").pickingMode,
                "подложка не должна перехватывать касание у кнопки, под которую её положили");
        }
    }
}
