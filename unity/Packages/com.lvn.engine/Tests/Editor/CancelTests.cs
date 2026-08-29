using System;
using System.Threading;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Погасить источник отмены: «остановить дважды» бывает штатно —
    /// снос сцены и смена главы приходят почти одновременно.</summary>
    public class CancelTests
    {
        [Test]
        public void ПустойИсточникБезвреден()
        {
            Assert.DoesNotThrow(() => LvnCancel.Retire(null));
        }

        [Test]
        public void ГаситТокен()
        {
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            LvnCancel.Retire(cts);
            Assert.IsTrue(token.IsCancellationRequested, "погашенный источник обязан отменить свой токен");
        }

        [Test]
        public void ПовторныйВызовНеБросает()
        {
            var cts = new CancellationTokenSource();
            LvnCancel.Retire(cts);
            Assert.DoesNotThrow(() => LvnCancel.Retire(cts),
                "снос сцены и смена главы приходят почти одновременно");
        }

        [Test]
        public void УжеОсвобождённыйИсточникНеБросает()
        {
            var cts = new CancellationTokenSource();
            cts.Dispose();
            Assert.DoesNotThrow(() => LvnCancel.Retire(cts), "Cancel на освобождённом бросает — его и оборачивают");
        }

        [Test]
        public void УжеОтменённыйИсточникГаснетТихо()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.DoesNotThrow(() => LvnCancel.Retire(cts));
        }

        [Test]
        public void УпавшийОбработчикОтменыНеОстанавливаетГашение()
        {
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            cts.Token.Register(() => throw new InvalidOperationException("чей-то обработчик"));

            Assert.DoesNotThrow(() => LvnCancel.Retire(cts),
                "падение чужого обработчика не должно ронять снос сцены");
            Assert.IsTrue(token.IsCancellationRequested);
        }

        [Test]
        public void ИсточникОсвобождёнАНеТолькоОтменён()
        {
            // Без Dispose регистрации отмены переживают того, кто их завёл.
            var cts = new CancellationTokenSource();
            LvnCancel.Retire(cts);
            Assert.Throws<ObjectDisposedException>(() => cts.Token.Register(() => { }),
                "на живом источнике подписка бы прошла — значит, Dispose не вызвали");
        }
    }
}
