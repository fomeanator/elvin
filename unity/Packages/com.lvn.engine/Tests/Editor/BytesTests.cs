using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Как игроку показан размер: по одному правилу, и оно про
    /// решение «хватит ли трафика», а не про арифметику.</summary>
    public class BytesTests
    {
        [SetUp]
        [TearDown]
        public void Clean() => LvnWords.Learn(null);

        [Test]
        public void SmallSizeNeverCollapsesToZero()
        {
            // 400 КБ целыми мегабайтами — «0 MB», то есть «качать нечего»,
            // хотя кнопка качает.
            Assert.AreEqual("0.4 MB", LvnBytes.Short(400 * 1024));
            Assert.AreEqual("0.1 MB", LvnBytes.Short(50 * 1024), "меньше сотой доли всё равно не ноль");
            Assert.AreEqual("0 MB", LvnBytes.Short(0), "настоящий ноль — единственный, кто пишется нулём");
        }

        [Test]
        public void BigSizeDropsTheDecimals()
        {
            Assert.AreEqual("117 MB", LvnBytes.Short(117L << 20));
            Assert.AreEqual("100 MB", LvnBytes.Short(100L << 20), "сотня — граница правила");
        }

        [Test]
        public void GigabytesInsteadOfFourDigitMegabytes()
        {
            // «1434 MB» человек переводит в уме и ошибается.
            Assert.AreEqual("1.4 GB", LvnBytes.Short(1434L << 20));
        }

        [Test]
        public void NegativeIsNotAnExcuseForNonsense()
        {
            // Разница размеров бывает отрицательной (сервер пересчитал план);
            // «-3 MB» на экране — сообщение об ошибке, которого игрок не просил.
            Assert.AreEqual("0 MB", LvnBytes.Short(-3L << 20));
        }

        [Test]
        public void UnitsComeFromTheNovella()
        {
            LvnWords.Learn(new System.Collections.Generic.Dictionary<string, string>
            {
                ["unit.mb"] = "МБ",
                ["unit.gb"] = "ГБ",
            });

            Assert.AreEqual("117 МБ", LvnBytes.Short(117L << 20));
            Assert.AreEqual("1.4 ГБ", LvnBytes.Short(1434L << 20));
        }

        [Test]
        public void ApproxMarksTheEstimateOnce()
        {
            Assert.AreEqual("≈117 MB", LvnBytes.Approx(117L << 20),
                "знак приблизительности ставит дом — половина мест раньше его забывала");
        }

        [Test]
        public void DecimalSeparatorComesFromTheNovellaToo()
        {
            // Подпись обязана читаться одинаково на любом телефоне: точку тут
            // когда-то меняли на запятую руками, в любой новелле.
            LvnWords.Learn(new System.Collections.Generic.Dictionary<string, string>
            {
                ["unit.mb"] = "МБ",
                ["unit.decimal"] = ",",
            });
            Assert.AreEqual("0,4 МБ", LvnBytes.Short(400 * 1024));
        }

        [Test]
        public void SpeedInMegabytesKeepsOneDecimal()
        {
            Assert.AreEqual("1.4 MB/s", LvnBytes.Speed(1.4f * (1 << 20)));
        }

        [Test]
        public void SlowNetworkIsShownInKilobytes()
        {
            // «0,1 МБ/с» не сообщает ничего, а 860 КБ/с — реальная скорость на
            // слабой сети.
            Assert.AreEqual("860 KB/s", LvnBytes.Speed(860f * 1024f));
            Assert.AreEqual("0 KB/s", LvnBytes.Speed(0f));
        }

        [Test]
        public void SpeedInKilobytesDoesNotJitterWithDecimals()
        {
            // Дробная часть в килобайтах дрожит на каждом кадре и мешает читать.
            StringAssert.DoesNotContain(".", LvnBytes.Speed(860.7f * 1024f));
        }

        [Test]
        public void NegativeSpeedIsNotShownAtAll()
        {
            Assert.AreEqual("0 KB/s", LvnBytes.Speed(-5f));
        }

        [Test]
        public void SpeedUnitsComeFromTheNovella()
        {
            LvnWords.Learn(new System.Collections.Generic.Dictionary<string, string>
            {
                ["unit.mbs"] = "МБ/с",
                ["unit.kbs"] = "КБ/с",
            });
            Assert.AreEqual("2 МБ/с", LvnBytes.Speed(2f * (1 << 20)));
            Assert.AreEqual("100 КБ/с", LvnBytes.Speed(100f * 1024f));
        }

        [Test]
        public void JustUnderAGigabyteIsStillMegabytes()
        {
            // Граница правила: 1023 МБ — ещё мегабайты, 1 ГБ — уже гигабайты.
            Assert.AreEqual("1023 MB", LvnBytes.Short(1023L << 20));
            Assert.AreEqual("1 GB", LvnBytes.Short(1L << 30));
        }

        [Test]
        public void JustUnderAHundredKeepsTheDecimal()
        {
            Assert.AreEqual("99.9 MB", LvnBytes.Short((long)(99.9f * (1 << 20))));
        }
    }
}
