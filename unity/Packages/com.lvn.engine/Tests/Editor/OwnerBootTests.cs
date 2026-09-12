using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Владелец локальных данных переживает запуск: кто играл последним,
    /// тот и владеет ключами с первого кадра, а не с ответа сервера.
    public class OwnerBootTests
    {
        private const string First = "lvn.local.owner", Last = "lvn.local.owner.last";
        private const string Prefix = "lvn.test.ownerboot.";
        private string _keptFirst, _keptLast;

        [SetUp]
        public void SetUp()
        {
            _keptFirst = LvnKeep.Get(First, ""); _keptLast = LvnKeep.Get(Last, "");
            LvnKeep.Drop(First); LvnKeep.Drop(Last);
            LvnKeep.ForgetOwnerInProcess();
        }

        [TearDown]
        public void TearDown()
        {
            LvnKeep.Drop(Prefix + "u_second.t"); LvnKeep.Drop(Prefix + "t");
            if (string.IsNullOrEmpty(_keptFirst)) LvnKeep.Drop(First); else LvnKeep.Put(First, _keptFirst);
            if (string.IsNullOrEmpty(_keptLast)) LvnKeep.Drop(Last); else LvnKeep.Put(Last, _keptLast);
            LvnKeep.ForgetOwnerInProcess();
        }

        [Test]
        public void ВторойВладелецПолучаетСвоиКлючиСразуПослеЗапуска()
        {
            LvnKeep.NoteOwner("u_first");    // первый забирает ключи без приставки
            LvnKeep.NoteOwner("u_second");   // второй — своё пространство
            var live = LvnKeep.Scoped(Prefix, "t");
            Assert.AreEqual(Prefix + "u_second.t", live, "второй владелец обязан жить за приставкой");

            // «Перезапуск»: память процесса пуста, диск помнит последнего.
            LvnKeep.ForgetOwnerInProcess();
            LvnKeep.RestoreOwnerFromDisk();
            Assert.AreEqual(live, LvnKeep.Scoped(Prefix, "t"),
                "до ответа сервера ключ уезжал к первому владельцу — записанное вчера сегодня не находилось");
        }

        [Test]
        public void ПервыйВладелецПослеЗапускаОстаётсяБезПриставки()
        {
            LvnKeep.NoteOwner("u_first");
            LvnKeep.ForgetOwnerInProcess();
            LvnKeep.RestoreOwnerFromDisk();
            Assert.AreEqual(Prefix + "t", LvnKeep.Scoped(Prefix, "t"));
        }
    }
}
