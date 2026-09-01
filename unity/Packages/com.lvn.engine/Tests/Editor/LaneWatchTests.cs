using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ЗАМЕР ПОЛОС — <see cref="LvnLaneWatch"/>.
    ///
    /// <para>Проверяется не точность миллисекунд, а СПОСОБНОСТЬ ОТЛИЧИТЬ: живой
    /// вход от фонового и ожидание от его отсутствия. Ради этого дом и заведён —
    /// 01.09 фоновая работа ходила по сети как живая, и ни один страж этого не
    /// увидел: строка объявления была на месте, не совпадал адресат. Забытое
    /// видно по отсутствию, объявленное не тому — только по числу.</para>
    /// </summary>
    public class LaneWatchTests
    {
        [SetUp]
        public void Setup() => LvnLaneWatch.Take();   // чужие заходы не наши

        [Test]
        public async Task Живые_и_фоновые_входы_считаются_врозь()
        {
            var lane = new LvnLane("проба", width: 4, keptForLive: 1);
            using (await lane.EnterAsync(LvnRung.Live, CancellationToken.None))
            using (await lane.EnterAsync(LvnRung.Live, CancellationToken.None))
            using (await lane.EnterAsync(LvnRung.Library, CancellationToken.None)) { }

            var (live, _, background, _) = LvnLaneWatch.Take();
            Assert.AreEqual(2, live, "живые входы посчитаны неверно — а именно по ним "
                                   + "и видно, что фон ходит по сети как живое");
            Assert.AreEqual(1, background, "фоновые входы посчитаны неверно");
        }

        [Test]
        public async Task Ожидание_попадает_в_худшее()
        {
            var lane = new LvnLane("проба", width: 1, keptForLive: 0);
            var держим = await lane.EnterAsync(LvnRung.Live, CancellationToken.None);
            var ждун = lane.EnterAsync(LvnRung.Live, CancellationToken.None);
            await Task.Delay(60);
            держим.Dispose();
            (await ждун).Dispose();

            var (_, worst, _, _) = LvnLaneWatch.Take();
            Assert.GreaterOrEqual(worst, 40,
                "ожидание живого не попало в замер — «бронь не работает» останется незаметным");
        }

        [Test]
        public async Task Снятие_обнуляет_счёт()
        {
            var lane = new LvnLane("проба", width: 2, keptForLive: 0);
            using (await lane.EnterAsync(LvnRung.Live, CancellationToken.None)) { }
            LvnLaneWatch.Take();

            var (live, _, background, yields) = LvnLaneWatch.Take();
            Assert.AreEqual(0, live + background + yields,
                "счёт не обнулился — числа следующей главы приедут с хвостом прошлой");
        }

        [Test]
        public async Task Разбор_называет_полосу_и_ступень()
        {
            var lane = new LvnLane("сеть", width: 2, keptForLive: 0);
            using (await lane.EnterAsync(LvnRung.Spare, CancellationToken.None)) { }
            var отчёт = LvnLaneWatch.Report();
            StringAssert.Contains("сеть", отчёт);
            StringAssert.Contains("Spare", отчёт);
            LvnLaneWatch.Take();
        }
    }
}
