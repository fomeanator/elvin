using NUnit.Framework;
using Lvn.Services;

namespace Lvn.Tests
{
    /// <summary>
    /// ШТОРМ ОТКЛОНЕНИЙ (TR-145): хвост чёрного ящика к каждому медленному
    /// кадру разгонял сам себя — запись потяжелевшей очереди логов делала
    /// следующий кадр медленным. Хвост выдаётся не чаще раза в полминуты и
    /// никогда к кадру, который тормозит сама запись логов.
    /// </summary>
    public class SlowFrameTailStormTests
    {
        private const string Slow = "[lvn-perf] S f=20410 ms=633.3 b=16.7 ctx=WardrobeTabScreen,chapter=,label=,step=0 m=591.7 pw=0.0 fw=0.0 gcm=43.1 mem=280.3 tri=6113.0 top=ActorBuild:300.5/343.8/1;ActorComposite:43.3/43.3/1";
        private const string SelfMade = "[lvn-perf] S f=32231 ms=10083.3 b=16.7 ctx=WardrobeTabScreen,chapter=,label=,step=0 m=9764.8 pw=0.0 fw=5.8 gcm=46.9 mem=284.0 tri=6113.0 top=LogSerialize:1699.9/1699.9/1;OutboxPersist:1031.4/1034.6/1;Diagnostics:806.5/1104.4/4";
        private const string Fast = "[lvn-perf] S f=100 ms=120.0 b=16.7 ctx=home m=100.0 top=UiRedress:10.4/10.6/5";

        [Test] public void FirstSlowFrameAfterGraceGetsATail()
        {
            float last = float.NegativeInfinity;
            Assert.IsTrue(LvnLogShip.SlowFrameWantsTail(Slow, 60f, ref last));
            Assert.AreEqual(60f, last);
        }

        [Test] public void SlowFramesInsideHalfAMinuteShipWithoutATail()
        {
            float last = float.NegativeInfinity;
            Assert.IsTrue(LvnLogShip.SlowFrameWantsTail(Slow, 60f, ref last));
            Assert.IsFalse(LvnLogShip.SlowFrameWantsTail(Slow, 62f, ref last), "второй кадр через 2 с — без хвоста");
            Assert.IsFalse(LvnLogShip.SlowFrameWantsTail(Slow, 89f, ref last), "через 29 с — ещё без хвоста");
            Assert.AreEqual(60f, last, "отказ не сдвигает отметку");
            Assert.IsTrue(LvnLogShip.SlowFrameWantsTail(Slow, 91f, ref last), "через 31 с — снова с хвостом");
        }

        [Test] public void AFrameSlowedByTheLogWriteItselfNeverGetsATail()
        {
            float last = float.NegativeInfinity;
            Assert.IsFalse(LvnLogShip.SlowFrameWantsTail(SelfMade, 60f, ref last));
            Assert.AreEqual(float.NegativeInfinity, last, "самонаведённый кадр не тратит окно");
            Assert.IsTrue(LvnLogShip.SelfInflicted(SelfMade));
            Assert.IsFalse(LvnLogShip.SelfInflicted(Slow));
            Assert.IsFalse(LvnLogShip.SelfInflicted("[lvn-perf] S f=1 ms=900 top=DiagnosticsX:1/1/1"), "сравнение по целому имени, не по приставке");
        }

        [Test] public void StartupAndFastFramesAreNotDeviations()
        {
            float last = float.NegativeInfinity;
            Assert.IsFalse(LvnLogShip.SlowFrameWantsTail(Slow, 10f, ref last), "первые 15 с — льгота старта");
            Assert.IsFalse(LvnLogShip.SlowFrameWantsTail(Fast, 60f, ref last));
            Assert.IsFalse(LvnLogShip.SlowFrameWantsTail("[lvn-perf] W n=464 f0=2 f1=465 s=9.3 fps=50.0", 60f, ref last), "окно — не кадр");
            Assert.IsFalse(LvnLogShip.SlowFrameWantsTail(null, 60f, ref last));
        }
    }
}
