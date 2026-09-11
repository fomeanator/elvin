using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// НАСТРОЕНИЕ ГЕРОИНИ ВИТРИНЫ (TR-66): что прерывает что и куда
    /// возвращаться. Правило выбора — единственное место, где живут ошибки
    /// такой системы: покупка, перебитая переходом в комнату, или скука,
    /// которая не снимается касанием, читаются как «игра сломалась».
    /// </summary>
    public class MenuMoodTests
    {
        private static LvnMenuMood Mood(params string[] labels)
        {
            var m = new LvnMenuMood();
            foreach (var l in labels) m.Known.Add(l);
            return m;
        }

        /// <summary>Пришли в комнату — играет её настроение.</summary>
        [Test]
        public void ARoomPlaysItsOwnMood()
        {
            var m = Mood("on_store", "on_home");
            Assert.AreEqual("on_store", m.EnterRoom("on_store"));
            Assert.AreEqual("on_home", m.EnterRoom("on_home"));
        }

        /// <summary>Метки нет — ничего не играем. Обещать реакцию, которой не
        /// написали, значит погасить комнату ради пустоты.</summary>
        [Test]
        public void AMissingLabelPlaysNothing()
        {
            var m = Mood("on_home");
            Assert.IsNull(m.EnterRoom("on_profile"));
            Assert.IsNull(m.Act("on_purchase"));
        }

        /// <summary>Действие прерывает комнату, а комната действие — НЕТ:
        /// иначе игрок, который купил и тут же перешёл, не увидел бы того,
        /// ради чего платил.</summary>
        [Test]
        public void AnActOutranksARoom()
        {
            var m = Mood("on_store", "on_home", "on_purchase");
            m.EnterRoom("on_store");
            Assert.AreEqual("on_purchase", m.Act("on_purchase"));
            Assert.IsNull(m.EnterRoom("on_home"), "переход перебил покупку");
            // Клип кончился — возвращаемся в комнату, где игрок теперь стоит.
            Assert.AreEqual("on_home", m.Ended());
        }

        /// <summary>Тишина доводит до скуки, касание её снимает, и повтор
        /// придержан кулдауном — иначе героиня вздыхает по кругу, пока игрок
        /// читает.</summary>
        [Test]
        public void SilenceGetsBoringOnceAndTouchResetsIt()
        {
            var m = Mood("on_home", "on_idle");
            m.IdleAfter = 10f; m.IdleCooldown = 25f;
            m.EnterRoom("on_home");

            Assert.IsNull(m.Tick(9f, "on_idle"), "заскучала раньше срока");
            Assert.AreEqual("on_idle", m.Tick(2f, "on_idle"));
            Assert.IsNull(m.Tick(30f, "on_idle"), "скука повторилась без касания");

            m.Touch();
            Assert.IsNull(m.Tick(11f, "on_idle"), "скука вернулась внутри кулдауна");
            Assert.AreEqual("on_idle", m.Tick(30f, "on_idle"), "после кулдауна скука снова возможна");
        }

        /// <summary>Скука не перебивает действие: оно вызвано игроком.</summary>
        [Test]
        public void BoredomWaitsForAnAct()
        {
            var m = Mood("on_home", "on_idle", "on_purchase");
            m.EnterRoom("on_home");
            m.Act("on_purchase");
            Assert.IsNull(m.Tick(30f, "on_idle"), "скука перебила покупку");
        }

        /// <summary>Ушли в главу — витрина забывает всё: вернётся игрок
        /// событием возврата, а не остатком прежнего настроения.</summary>
        [Test]
        public void LeavingForAChapterForgetsTheMood()
        {
            var m = Mood("on_store", "on_idle");
            m.EnterRoom("on_store");
            m.Leave();
            Assert.IsNull(m.Playing);
            Assert.IsNull(m.Ended(), "после главы вернулось настроение прежней комнаты");
        }
    }
}
