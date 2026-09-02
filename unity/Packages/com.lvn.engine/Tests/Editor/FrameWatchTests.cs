using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// СЧЁТ ЗАПИНОК. Порог и вопрос «это уже рывок?» жили строкой внутри цикла
    /// сцены, а ответ был только в логе: видно, ЧТО случилось, но не видно,
    /// стало ли лучше после правки.
    /// </summary>
    public class FrameWatchTests
    {
        [SetUp]
        public void Убрать() => LvnFrameWatch.Take();

        [Test]
        public void РовныйКадрНеСчитаетсяЗапинкой()
        {
            LvnFrameWatch.Frame(0.016f, 1000);
            LvnFrameWatch.Frame(LvnFrameWatch.HitchSeconds, 1000);   // ровно порог — ещё не рывок
            Assert.AreEqual(0, LvnFrameWatch.Hitches);
        }

        [Test]
        public void ДолгийКадрСчитаетсяИЗапоминаетсяХудший()
        {
            LvnFrameWatch.Frame(0.2f, 1000);
            LvnFrameWatch.Frame(0.4f, 1001);
            LvnFrameWatch.Frame(0.25f, 1002);
            Assert.AreEqual(3, LvnFrameWatch.Hitches);
            Assert.AreEqual(400, LvnFrameWatch.WorstMs, "худшая запинка — та, что заметнее всех");
        }

        [Test]
        public void ПервыеКадрыПослеЗагрузкиНеВСчёт()
        {
            // Они тяжелы всегда и о плавности ничего не говорят.
            LvnFrameWatch.Frame(0.9f, LvnFrameWatch.WarmupFrames);
            Assert.AreEqual(0, LvnFrameWatch.Hitches);
        }

        [Test]
        public void СнятиеОбнуляетСчёт()
        {
            LvnFrameWatch.Frame(0.3f, 1000);
            var (hitches, worst) = LvnFrameWatch.Take();
            Assert.AreEqual(1, hitches);
            Assert.AreEqual(300, worst);
            Assert.AreEqual(0, LvnFrameWatch.Hitches, "следующая глава считается заново");
            Assert.AreEqual(0, LvnFrameWatch.WorstMs);
        }

        [Test]
        public void ЗанятостьСпрашиваютУСчётчикаТолькоПриЗапинке()
        {
            // Сцена отдаёт пояснение счётчику, а кадры считает не она: значит
            // без явного note счётчик обязан спросить Busy — и только у запинки.
            int asked = 0;
            LvnFrameWatch.Busy = () => { asked++; return " (busy)"; };
            try
            {
                LvnFrameWatch.Frame(0.016f, 1000);
                Assert.AreEqual(0, asked, "обычный кадр не платит за диагностику");
                LvnFrameWatch.Frame(0.3f, 1000);
                Assert.AreEqual(1, asked, "у запинки спрашивают, чем занят движок");
            }
            finally { LvnFrameWatch.Busy = null; }
        }

        [Test]
        public void ПояснениеСпрашиваютТолькоУЗапинки()
        {
            int asked = 0;
            LvnFrameWatch.Frame(0.016f, 1000, () => { asked++; return ""; });
            Assert.AreEqual(0, asked, "обычный кадр не платит за диагностику");
            LvnFrameWatch.Frame(0.3f, 1000, () => { asked++; return ""; });
            Assert.AreEqual(1, asked);
        }
    }
}
