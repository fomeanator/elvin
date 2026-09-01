using System.Threading;
using System.Threading.Tasks;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Ждать, пока экран закроют — и не остаться ждать навсегда. Ни одна
    /// из ошибок этой связки не видна на глаз: «повисло» случается через раз и на
    /// чужой машине.</summary>
    public class CloseGateTests
    {
        [Test]
        public async Task ПодтверждениеВозвращаетДа()
        {
            var gate = new LvnCloseGate();
            var wait = gate.WaitAsync(CancellationToken.None);
            gate.Release(true);
            Assert.IsTrue(await wait);
        }

        [Test]
        public async Task ОтменаВозвращаетНет()
        {
            var gate = new LvnCloseGate();
            var wait = gate.WaitAsync(CancellationToken.None);
            gate.Release(false);
            Assert.IsFalse(await wait);
        }

        [Test]
        public async Task СносСценыВозвращаетУправление()
        {
            // Без регистрации отмены экран, закрытый сносом, не вернул бы
            // управление НИКОГДА — и виток игры остался бы стоять.
            var gate = new LvnCloseGate();
            var cts = new CancellationTokenSource();
            var wait = gate.WaitAsync(cts.Token);
            cts.Cancel();

            Assert.IsFalse(await wait, "снос — это «не подтверждено», а не исключение");
            cts.Dispose();
        }

        [Test]
        public async Task УжеОтменённыйТокенНеЗависает()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var gate = new LvnCloseGate();

            Assert.IsFalse(await gate.WaitAsync(cts.Token),
                "отмена, случившаяся ДО ожидания, тоже обязана его отпустить");
            cts.Dispose();
        }

        [Test]
        public async Task ПовторноеЗакрытиеБезвредно()
        {
            // Экран закрывают и кнопкой, и тапом мимо, и сносом сцены — иногда
            // почти одновременно.
            var gate = new LvnCloseGate();
            var wait = gate.WaitAsync(CancellationToken.None);
            gate.Release(true);
            Assert.DoesNotThrow(() => gate.Release(false));

            Assert.IsTrue(await wait, "первый ответ и есть ответ");
        }

        [Test]
        public async Task ПовторныйПоказОтпускаетПервогоЖдущего()
        {
            // Экран открывают второй раз, не закрыв первый: тап по двум
            // карточкам подряд, ссылка поверх открытого листа, возврат из
            // соседнего экрана. Ожидание заменялось молча — и первый ждущий не
            // получал ответа НИКОГДА, а вместе с ним висел его цикл: глава,
            // покупка, выбор наряда.
            //
            // Правило это стояло не здесь, а у экрана конца главы, который дом
            // обошёл и завёл своё ожидание. Тот, кто написал связку заново,
            // знал о ней больше дома.
            var gate = new LvnCloseGate();
            var первый = gate.WaitAsync(CancellationToken.None);

            var второй = gate.ReopenAsync(CancellationToken.None);

            var успел = await Task.WhenAny(первый, Task.Delay(3000));
            Assert.AreSame(первый, успел, "первого ждущего бросили: он не получит ответа никогда");
            Assert.IsFalse(await первый, "брошенное ожидание отвечает «не подтверждено»");
            Assert.IsFalse(второй.IsCompleted, "второй показ ещё ждёт игрока");

            gate.Release(true);
            Assert.IsTrue(await второй);
        }

        [Test]
        public void ЗакрытьДоОжиданияБезвредно()
        {
            var gate = new LvnCloseGate();
            Assert.DoesNotThrow(() => gate.Release());
            Assert.IsFalse(gate.Waiting);
        }

        [Test]
        public async Task ЖдётЛиКтоТоПрямоСейчас()
        {
            var gate = new LvnCloseGate();
            Assert.IsFalse(gate.Waiting, "до открытия никто не ждёт");

            var wait = gate.WaitAsync(CancellationToken.None);
            Assert.IsTrue(gate.Waiting);

            gate.Release();
            await wait;
            Assert.IsFalse(gate.Waiting, "закрытый экран не считается ожидающим");
        }
    }
}
