using Lvn;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОВОДОК — отпускает всё, на что подписались, и не спотыкается о падение
    /// одной отписки.
    ///
    /// <para>Проверяется то, ради чего он заведён: не «вызвался ли Release», а
    /// что подписка действительно перестала работать, — иначе тест повторял бы
    /// ту же ошибку, что и старый код, где `-=` выглядел отпиской и ею не
    /// был.</para>
    /// </summary>
    public class LeashTests
    {
        private static event System.Action Bell;
        private static void Ring() => Bell?.Invoke();

        [SetUp]
        public void Reset() => Bell = null;

        [Test]
        public void Release_StopsTheSubscription()
        {
            var leash = new LvnLeash();
            int heard = 0;
            System.Action ear = () => heard++;
            leash.Hold(() => Bell += ear, () => Bell -= ear);

            Ring();
            Assert.AreEqual(1, heard, "подписка не сработала");

            leash.Release();
            Ring();
            Assert.AreEqual(1, heard, "после Release событие всё ещё доходит");
            Assert.AreEqual(0, leash.Count);
        }

        [Test]
        public void Release_IsIdempotent()
        {
            var leash = new LvnLeash();
            int heard = 0;
            System.Action ear = () => heard++;
            leash.Hold(() => Bell += ear, () => Bell -= ear);
            leash.Release();
            Assert.DoesNotThrow(() => leash.Release(), "снос бывает двойным");
            Ring();
            Assert.AreEqual(0, heard);
        }

        [Test]
        public void OneBadUnsubscribe_DoesNotStrandTheRest()
        {
            var leash = new LvnLeash();
            int heard = 0;
            System.Action ear = () => heard++;
            leash.Hold(() => { }, () => throw new System.InvalidOperationException("шумная отписка"));
            leash.Hold(() => Bell += ear, () => Bell -= ear);

            LogAssert.ignoreFailingMessages = true;   // упавшая отписка пишет предупреждение
            leash.Release();
            LogAssert.ignoreFailingMessages = false;

            Ring();
            Assert.AreEqual(0, heard, "здоровая подписка осталась висеть из-за соседней");
        }

        [Test]
        public void HalfAPairIsNotASubscription()
        {
            // Подписка без отписки — ровно тот дефект, ради которого поводок и
            // заведён: принять её значило бы завести пятую лямбду, которую
            // нечем отписать.
            var leash = new LvnLeash();
            int heard = 0;
            System.Action ear = () => heard++;

            leash.Hold(() => Bell += ear, null);
            leash.Hold(null, () => Bell -= ear);

            Assert.AreEqual(0, leash.Count);
            Ring();
            Assert.AreEqual(0, heard, "подписались, а отпустить нечем");
        }

        [Test]
        public void CountSaysHowManyAreHeld()
        {
            var leash = new LvnLeash();
            Assert.AreEqual(0, leash.Count);

            leash.Hold(() => { }, () => { });
            leash.Hold(() => { }, () => { });
            Assert.AreEqual(2, leash.Count);

            leash.Release();
            Assert.AreEqual(0, leash.Count, "отпущенное больше не держим");
        }

        [Test]
        public void WhileOnScreenIgnoresAnIncompleteRequest()
        {
            // Экрана может не быть, а половинки пары — не пара.
            Assert.DoesNotThrow(() => LvnLeash.WhileOnScreen(null, () => { }, () => { }));
            Assert.DoesNotThrow(() => LvnLeash.WhileOnScreen(
                new UnityEngine.UIElements.VisualElement(), null, () => { }));
            Assert.DoesNotThrow(() => LvnLeash.WhileOnScreen(
                new UnityEngine.UIElements.VisualElement(), () => { }, null));
        }

        [Test]
        public void WhileOnScreenWaitsForThePanelBeforeSubscribing()
        {
            // Элемент, ещё не вставленный в панель, подписывать не на что:
            // обновление ушло бы в недостроенный экран.
            int subscribed = 0, refreshed = 0;
            LvnLeash.WhileOnScreen(new UnityEngine.UIElements.VisualElement(),
                                   () => subscribed++, () => { }, () => refreshed++);

            Assert.AreEqual(0, subscribed);
            Assert.AreEqual(0, refreshed);
        }
    }
}
