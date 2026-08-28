using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Как язык пишет время: форма подписи одна на все экраны.</summary>
    public class TimeWordsTests
    {
        [Test]
        public void ClockDropsHoursUntilThereAreAny()
        {
            Assert.AreEqual("3:07", LvnTimeWords.Clock(187));
            Assert.AreEqual("1:12:30", LvnTimeWords.Clock(4350));
            Assert.AreEqual("0:00", LvnTimeWords.Clock(0));
        }

        [Test]
        public void ClockNeverShowsNegativeTime()
        {
            // Часы устройства и сервера расходятся; «-1:-3» на экране —
            // сообщение об ошибке, которого игрок не заказывал.
            Assert.AreEqual("0:00", LvnTimeWords.Clock(-5));
        }

        [Test]
        public void CoarseRoundsDownButNeverToZero()
        {
            Assert.AreEqual("1 h 12 min", LvnTimeWords.Coarse(4350));
            Assert.AreEqual("12 min", LvnTimeWords.Coarse(750));
            // 40 секунд — не «0 мин»: ноль читается как поломка, а не как «вот-вот».
            Assert.AreEqual("1 min", LvnTimeWords.Coarse(40));
        }

        [Test]
        public void AgoHasNoStampForMissingTime()
        {
            Assert.AreEqual("", LvnTimeWords.Ago(0), "подпись «01.01.1970» хуже отсутствующей");
            Assert.AreEqual("", LvnTimeWords.Stamp(0));
        }

        [Test]
        public void AgoWalksTheScale()
        {
            long now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.AreEqual("just now", LvnTimeWords.Ago(now - 30_000));
            Assert.AreEqual("5 min ago", LvnTimeWords.Ago(now - 5 * 60_000));
            Assert.AreEqual("3 h ago", LvnTimeWords.Ago(now - 3 * 3600_000L));
            Assert.AreEqual("2 d ago", LvnTimeWords.Ago(now - 2 * 24 * 3600_000L));
        }

        [Test]
        public void StampSurvivesABrokenFormatWord()
        {
            // Кривой формат из манифеста не имеет права оставить слот без времени.
            LvnWords.Learn(new System.Collections.Generic.Dictionary<string, string>
            {
                ["time.stamp_format"] = "dd.MM \"незакрытая",
            });
            try
            {
                var s = LvnTimeWords.Stamp(System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Assert.IsNotEmpty(s);
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning,
                    new System.Text.RegularExpressions.Regex("stamp_format"));
            }
            finally { LvnWords.Learn(null); }
        }
    }
}
