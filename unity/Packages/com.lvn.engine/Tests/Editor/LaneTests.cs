using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Lvn.Content;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОЛОСА ПРОПУСКАНИЯ — <see cref="LvnLane"/> и <see cref="LvnRungScope"/>.
    ///
    /// <para>Проверяется не ширина (число можно поменять завтра), а ОБЕЩАНИЕ:
    /// сколько бы фоновой работы ни навалилось, живому есть куда встать.
    /// До этого дома обещания не было вовсе — мест было ровно столько, и кто
    /// первым попросил, того и место; живая картинка ждала за фоновым прогревом
    /// не по невезению, а по устройству.</para>
    /// </summary>
    public class LaneTests
    {
        // Ждать «пока не станет так» вместо фиксированной паузы: тест на
        // таймере краснеет на загруженной машине и ничего этим не сообщает.
        private static async Task Until(Func<bool> cond, string what)
        {
            for (int i = 0; i < 500 && !cond(); i++) await Task.Delay(2);
            Assert.IsTrue(cond(), what);
        }

        [Test]
        public async Task Фон_не_занимает_мест_оставленных_живому()
        {
            var lane = new LvnLane("проба", width: 3, keptForLive: 1);
            var held = new List<IDisposable>();
            int entered = 0;

            // Четыре фоновые работы на полосу из трёх с бронью в одно место.
            for (int i = 0; i < 4; i++)
                _ = Task.Run(async () =>
                {
                    var p = await lane.EnterAsync(LvnRung.Library, CancellationToken.None);
                    lock (held) { held.Add(p); entered++; }
                });

            await Until(() => Volatile.Read(ref entered) >= 2, "фон не занял доступных ему мест");
            await Task.Delay(30);
            Assert.AreEqual(2, Volatile.Read(ref entered),
                "фон занял место, оставленное живому: бронь не работает");

            // Живое проходит СРАЗУ, хотя фон стоит в очереди.
            var live = lane.EnterAsync(LvnRung.Live, CancellationToken.None);
            var won = await Task.WhenAny(live, Task.Delay(1000));
            Assert.AreSame(live, won, "живое не прошло по брони, хотя место было");
            live.Result.Dispose();

            lock (held) foreach (var p in held) p.Dispose();
        }

        [Test]
        public async Task Место_освобождается_выходом_из_using()
        {
            var lane = new LvnLane("проба", width: 1, keptForLive: 0);
            using (await lane.EnterAsync(LvnRung.Live, CancellationToken.None)) { }
            // Если бы Dispose не отпускал, второй вход завис бы навсегда.
            var again = lane.EnterAsync(LvnRung.Live, CancellationToken.None);
            Assert.AreSame(again, await Task.WhenAny(again, Task.Delay(1000)),
                "место не вернулось в полосу после выхода из using");
            (await again).Dispose();
        }

        [Test]
        public async Task Отмена_не_съедает_место()
        {
            var lane = new LvnLane("проба", width: 2, keptForLive: 1);
            using var busy = await lane.EnterAsync(LvnRung.Library, CancellationToken.None);

            // Фону доступно одно место, оно занято — этот вход обязан ждать.
            var cts = new CancellationTokenSource();
            var waiting = lane.EnterAsync(LvnRung.Library, cts.Token);
            await Task.Delay(20);
            Assert.IsFalse(waiting.IsCompleted, "фон прошёл сверх своей доли");
            cts.Cancel();
            try { await waiting; Assert.Fail("отменённый вход завершился успехом"); }
            catch (OperationCanceledException) { }

            // САМОЕ ВАЖНОЕ: отменённый ждун не должен унести с собой место.
            // Утечка тут не видна сразу — полоса просто медленно сужается.
            var after = lane.EnterAsync(LvnRung.Live, CancellationToken.None);
            Assert.AreSame(after, await Task.WhenAny(after, Task.Delay(1000)),
                "отменённый ждун унёс место с собой: полоса молча сужается");
            (await after).Dispose();
        }

        [Test]
        public void Бронь_во_всю_ширину_запрещена()
        {
            // Полоса, целиком отданная живому, останавливает фон навсегда:
            // такую ошибку надо ловить на объявлении, а не по зависшей очереди.
            Assert.Throws<ArgumentOutOfRangeException>(() => new LvnLane("проба", 2, 2));
        }

        [Test]
        public void Молчащий_считается_живым()
        {
            Assert.AreEqual(LvnRung.Live, LvnRungScope.Current,
                "умолчание должно быть «живое»: объявляться обязан ФОН, "
                + "и забыть объявление — потерять бронь, а не показать пустоту");
        }

        [Test]
        public async Task Объявленная_ступень_переживает_ожидание()
        {
            // Ради этого дом и сделан окружением, а не доводом: объявление
            // делается один раз на весь цикл, а читается вглубь — за десятком
            // await и через чужие дома.
            using (LvnRungScope.At(LvnRung.Spare))
            {
                Assert.AreEqual(LvnRung.Spare, LvnRungScope.Current);
                await Task.Yield();
                Assert.AreEqual(LvnRung.Spare, LvnRungScope.Current, "ступень потерялась на await");
                await Deep();
                async Task Deep()
                {
                    await Task.Delay(1);
                    Assert.AreEqual(LvnRung.Spare, LvnRungScope.Current, "ступень не доехала вглубь");
                }
            }
            Assert.AreEqual(LvnRung.Live, LvnRungScope.Current, "ступень не вернулась после using");
        }

        [Test]
        public async Task Запущенное_внутри_наследует_а_объявление_не_протекает_наружу()
        {
            // Две половины одного обещания: работа, ЗАПУЩЕННАЯ внутри
            // объявления, его наследует (в этом смысл дома), а само объявление
            // не остаётся висеть после using — иначе фоновый цикл однажды
            // сделал бы запасом сцену, которая крутится рядом.
            Task<LvnRung> neighbour;
            using (LvnRungScope.At(LvnRung.Spare))
                neighbour = Task.Run(async () => { await Task.Yield(); return LvnRungScope.Current; });
            var seen = await neighbour;
            Assert.AreEqual(LvnRung.Spare, seen,
                "запущенное ВНУТРИ объявления наследует его — это и есть смысл дома");
            Assert.AreEqual(LvnRung.Live, LvnRungScope.Current);
        }
    }
}
