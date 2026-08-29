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

        [Test]
        public void CoarseNeverShowsNegativeTime()
        {
            // Тот же перекос часов, что и у Clock: «-1 мин» игрок не заказывал.
            Assert.AreEqual("1 min", LvnTimeWords.Coarse(-90));
        }

        [Test]
        public void ClockPadsMinutesAndSecondsOnceHoursAppear()
        {
            // «1:2:3» — не время; ведущие нули появляются ровно с часами.
            Assert.AreEqual("1:02:03", LvnTimeWords.Clock(3723));
            Assert.AreEqual("2:05", LvnTimeWords.Clock(125), "до часа минуты идут без нуля");
        }

        [Test]
        public void ЦелыйЧасНазываетсяЧасом()
        {
            // Правило «ноль минут не пишем» действовало только ниже часа, и
            // ровный час выходил как «1 h 0 min» — та же поломка, которую это
            // правило и запрещает, просто часом позже.
            Assert.AreEqual("1 h", LvnTimeWords.Coarse(3600));
            Assert.AreEqual("2 h", LvnTimeWords.Coarse(7200));
            Assert.AreEqual("1 h 12 min", LvnTimeWords.Coarse(4320),
                "минуты, когда они есть, остаются на месте");
        }

        [Test]
        public void CoarseWordsComeFromTheNovella()
        {
            LvnWords.Learn(new System.Collections.Generic.Dictionary<string, string>
            {
                ["unit.hours"] = "ч",
                ["unit.minutes"] = "мин",
            });
            try
            {
                Assert.AreEqual("1 ч 12 мин", LvnTimeWords.Coarse(4350));
                Assert.AreEqual("12 мин", LvnTimeWords.Coarse(750));
            }
            finally { LvnWords.Learn(null); }
        }

        [Test]
        public void КраяОтсчётаДержатсяИНаЯзыкеНовеллы()
        {
            // Края правила («целый час», «меньше минуты», «часы разошлись»)
            // проверены выше по-английски, а игрок читает их СЛОВАМИ НОВЕЛЛЫ:
            // подстановка слова и правило про ноль — разные механизмы, и
            // сойтись им ничто не мешает только пока это проверено вместе.
            LvnWords.Learn(new System.Collections.Generic.Dictionary<string, string>
            {
                ["unit.hours"] = "ч",
                ["unit.minutes"] = "мин",
            });
            try
            {
                Assert.AreEqual("1 ч", LvnTimeWords.Coarse(3600), "«1 ч 0 мин» читается как поломка");
                Assert.AreEqual("1 ч 12 мин", LvnTimeWords.Coarse(4350), "минуты, когда они есть, на месте");
                Assert.AreEqual("1 мин", LvnTimeWords.Coarse(40), "«0 мин» — не «вот-вот», а поломка");
                Assert.AreEqual("1 мин", LvnTimeWords.Coarse(-90), "отрицательного ожидания не бывает");
            }
            finally { LvnWords.Learn(null); }
        }

        [Test]
        public void AgoWordsComeFromTheNovellaWithTheNumberInPlace()
        {
            LvnWords.Learn(new System.Collections.Generic.Dictionary<string, string>
            {
                ["time.just_now"] = "только что",
                ["time.minutes_ago"] = "{n} мин назад",
                ["time.days_ago"] = "{n} дн назад",
            });
            try
            {
                long now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Assert.AreEqual("только что", LvnTimeWords.Ago(now - 10_000));
                Assert.AreEqual("5 мин назад", LvnTimeWords.Ago(now - 5 * 60_000));
                Assert.AreEqual("2 дн назад", LvnTimeWords.Ago(now - 2 * 24 * 3600_000L));
            }
            finally { LvnWords.Learn(null); }
        }

        [Test]
        public void AgoDoesNotGoBackwardsWhenClocksDisagree()
        {
            // Сейв, записанный часами, спешащими на минуту, не должен читаться
            // как «-1 мин назад».
            long future = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000;
            Assert.AreEqual("just now", LvnTimeWords.Ago(future));
        }

        [Test]
        public void MissingTimeHasNoStampAtAll()
        {
            Assert.AreEqual("", LvnTimeWords.Ago(-1));
            Assert.AreEqual("", LvnTimeWords.Stamp(-1));
        }

        [Test]
        public void StampFormatIsAWordOfTheNovella()
        {
            // Порядок дня и месяца у языков разный — жёсткая строка в коде
            // читалась бы как ошибка ровно там, где её никто не ищет.
            LvnWords.Learn(new System.Collections.Generic.Dictionary<string, string>
            {
                ["time.stamp_format"] = "yyyy",
            });
            try
            {
                var when = new System.DateTimeOffset(2026, 8, 27, 14, 32, 0, System.TimeSpan.Zero);
                Assert.AreEqual(when.ToLocalTime().Year.ToString(),
                                LvnTimeWords.Stamp(when.ToUnixTimeMilliseconds()));
            }
            finally { LvnWords.Learn(null); }
        }
    }
}
