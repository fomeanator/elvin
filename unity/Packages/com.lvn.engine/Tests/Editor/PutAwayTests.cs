using System.Threading;
using NUnit.Framework;
using UnityEngine.UIElements;
using Lvn.UI;

namespace Lvn.Tests
{
    /// <summary>
    /// УХОД С ЭКРАНА ОТМЕНЯЕТ ВЕСЬ ПОКАЗ.
    ///
    /// <para>Проверять тут стоит ровно одно, зато со всех сторон: после ухода
    /// поверхность обязана быть пригодной к возврату ЛЮБЫМ способом — и тем,
    /// что проявляет, и тем, что просто ставит <c>display</c>. Второй способ и
    /// есть ловушка: он ничего не чинит за уходящим, а выглядит как показ.</para>
    /// </summary>
    public class PutAwayTests
    {
        private static VisualElement Shown()
        {
            var el = new VisualElement();
            el.style.display = DisplayStyle.Flex;
            el.style.opacity = 1f;
            return el;
        }

        [Test]
        public void Уход_убирает_из_раскладки()
        {
            var el = Shown();
            ScreenFx.PutAway(el);
            Assert.AreEqual(DisplayStyle.None, el.resolvedStyle.display);
        }

        [Test]
        public void Уход_возвращает_прозрачность_после_гашения()
        {
            var el = Shown();
            el.style.opacity = 0f;      // погасили перед уходом
            ScreenFx.PutAway(el);
            Assert.AreEqual(1f, el.resolvedStyle.opacity, 0.001f,
                "показ, который просто ставит display, дал бы невидимый экран");
        }

        [Test]
        public void Уход_возвращает_смещение_после_отъезда_за_кромку()
        {
            var el = Shown();
            el.style.translate = new Translate(1000f, 0f);   // уехал вкладкой
            ScreenFx.PutAway(el);
            var tr = el.resolvedStyle.translate;
            Assert.AreEqual(0f, tr.x, 0.001f, "раздел открылся бы за кромкой");
            Assert.AreEqual(0f, tr.y, 0.001f);
        }

        [Test]
        public void Уход_дважды_подряд_ничего_не_ломает()
        {
            var el = Shown();
            ScreenFx.PutAway(el);
            ScreenFx.PutAway(el);
            Assert.AreEqual(DisplayStyle.None, el.resolvedStyle.display);
            Assert.AreEqual(1f, el.resolvedStyle.opacity, 0.001f);
        }

        [Test]
        public void Уход_пустоты_не_падает()
        {
            Assert.DoesNotThrow(() => ScreenFx.PutAway(null));
        }

        [Test]
        public void Гашение_с_уходом_кончается_убранным_и_непрозрачным()
        {
            var el = Shown();
            // Нулевая длительность: гашение садится в конечное значение сразу,
            // и весь поступок укладывается в один кадр — ждать нечего.
            var task = ScreenFx.FadeAwayAsync(el, 0f, CancellationToken.None);
            Assert.IsTrue(task.IsCompleted, "нулевое гашение не должно уступать кадр");
            Assert.AreEqual(DisplayStyle.None, el.resolvedStyle.display);
            Assert.AreEqual(1f, el.resolvedStyle.opacity, 0.001f);
        }

        [Test]
        public void Гашение_отменённое_всё_равно_убирает()
        {
            var el = Shown();
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var task = ScreenFx.FadeAwayAsync(el, 5f, cts.Token);
            Assert.IsTrue(task.IsCompleted,
                "отмена не должна оставлять поверхность на полпути");
            Assert.AreEqual(DisplayStyle.None, el.resolvedStyle.display);
            Assert.AreEqual(1f, el.resolvedStyle.opacity, 0.001f);
        }
    }
}
