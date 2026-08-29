using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>Как язык пишет числа: разделители берутся из слов НОВЕЛЛЫ, а не из
    /// настроек устройства — сумма выглядит одинаково на любом телефоне.</summary>
    public class NumberFormatTests
    {
        private CultureInfo _culture;

        [SetUp]
        public void Setup()
        {
            _culture = Thread.CurrentThread.CurrentCulture;
            LvnWords.Learn(null);
            LvnWords.Translate(null);
        }

        [TearDown]
        public void Clean()
        {
            Thread.CurrentThread.CurrentCulture = _culture;
            LvnWords.Learn(null);
            LvnWords.Translate(null);
        }

        [Test]
        public void УмолчаниеАнглийское()
        {
            Assert.AreEqual("1,200", LvnNumberFormat.Groups(1200));
            Assert.AreEqual("1.5", LvnNumberFormat.Decimals(1.5f));
        }

        [Test]
        public void РусскаяНовеллаСтавитПробелИЗапятую()
        {
            LvnWords.Learn(new Dictionary<string, string> { ["unit.group"] = " ", ["unit.decimal"] = "," });
            Assert.AreEqual("1 200", LvnNumberFormat.Groups(1200));
            Assert.AreEqual("1,5", LvnNumberFormat.Decimals(1.5f));
        }

        [Test]
        public void НастройкиУстройстваНеВлияют()
        {
            // Английская новелла на русском телефоне обязана писать «1,200»,
            // а не «1 200»: язык числа выбирает новелла, а не прошивка.
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
            Assert.AreEqual("1,200", LvnNumberFormat.Groups(1200));
            Assert.AreEqual("1.5", LvnNumberFormat.Decimals(1.5f));
        }

        [Test]
        public void СменаСловМеняетЧислоСразу()
        {
            // Формат кэшируется; если кэш не сбросить по словам, смена языка
            // на лету оставит числа в прежнем виде до перезапуска.
            Assert.AreEqual("1,200", LvnNumberFormat.Groups(1200));
            LvnWords.Learn(new Dictionary<string, string> { ["unit.group"] = " " });
            Assert.AreEqual("1 200", LvnNumberFormat.Groups(1200));
            LvnWords.Learn(null);
            Assert.AreEqual("1,200", LvnNumberFormat.Groups(1200), "снятые слова возвращают умолчание");
        }

        [Test]
        public void ПереводСловТожеМеняетРазделители()
        {
            LvnWords.Learn(new Dictionary<string, string> { ["unit.group"] = " " });
            LvnWords.Translate(new Dictionary<string, string> { ["unit.group"] = "," });
            Assert.AreEqual("1,200", LvnNumberFormat.Groups(1200),
                "выбор игрока сильнее выбора автора — и для слов, и для чисел");
        }

        [Test]
        public void МелкиеЧислаБезРазделителяАКрупныеПоТриЗнака()
        {
            Assert.AreEqual("999", LvnNumberFormat.Groups(999));
            Assert.AreEqual("1,000,000", LvnNumberFormat.Groups(1000000));
            Assert.AreEqual("0", LvnNumberFormat.Groups(0));
        }

        [Test]
        public void ОтрицательноеЧислоОстаётсяОтрицательным()
        {
            Assert.AreEqual("-1,200", LvnNumberFormat.Groups(-1200), "минус у долга не теряется");
        }

        [Test]
        public void ЦелоеДробнымНеПишется()
        {
            Assert.AreEqual("2", LvnNumberFormat.Decimals(2f), "«2.0 МБ» — лишний знак в цене и в размере");
        }

        [Test]
        public void ПустоеСловоРазделителяНеРонаетЧисло()
        {
            // Автор мог оставить поле пустым — число обязано остаться числом.
            LvnWords.Learn(new Dictionary<string, string> { ["unit.group"] = "", ["unit.decimal"] = "" });
            Assert.DoesNotThrow(() => LvnNumberFormat.Groups(1200));
            Assert.DoesNotThrow(() => LvnNumberFormat.Decimals(1.5f));
        }
    }
}
