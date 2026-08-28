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
    }
}
