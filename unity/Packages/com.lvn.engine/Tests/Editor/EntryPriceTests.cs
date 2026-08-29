using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ЦЕНА ВХОДА: показ и списание считают её одинаково.
    ///
    /// <para>Раньше правил было два. Кассир брал цену из экономики манифеста и
    /// уважал список бесплатных глав; карточка новеллы показывала цену новеллы,
    /// а без неё — собственное поле со значением 1, которое никто не задавал. И
    /// «1» из воздуха, и цена на бесплатной главе читаются игроком как обман.</para>
    /// </summary>
    public sealed class EntryPriceTests
    {
        private static LvnEconomyConfig Gate(int cost = 2, params string[] free)
            => new LvnEconomyConfig
            {
                chapter_currency = "energy",
                chapter_cost = cost,
                free_chapters = new List<string>(free),
            };

        // Своей цены у новеллы нет — показываем цену ГЕЙТА, а не выдуманную
        // единицу: спишется именно она.
        [Test]
        public void WithoutOwnCostTheChapterGateIsShown()
        {
            var shown = LvnEntryPrice.Shown(new LvnTitle { id = "t" }, Gate(cost: 2), "ch1");
            Assert.AreEqual("energy", shown.Currency);
            Assert.AreEqual(2, shown.Amount, "показываем то, что спишет кассир");
        }

        // Глава объявлена бесплатной — цены нет вовсе, и плашку показывать
        // нечем: списания не будет.
        [Test]
        public void FreeChapterCostsNothing()
        {
            var price = LvnEntryPrice.ForChapter(Gate(cost: 2, free: "ch1"), "ch1");
            Assert.IsTrue(price.Free, "глава из free_chapters не стоит ничего");
            Assert.IsTrue(LvnEntryPrice.Shown(new LvnTitle { id = "t" }, Gate(2, "ch1"), "ch1").Free);
        }

        // Гейт выключен (валюта не названа) — вход свободный при любой сумме.
        [Test]
        public void NoCurrencyMeansNoGate()
        {
            Assert.IsTrue(LvnEntryPrice.ForChapter(new LvnEconomyConfig { chapter_cost = 5 }, "ch1").Free);
            Assert.IsTrue(LvnEntryPrice.ForChapter(null, "ch1").Free);
        }

        // Своя цена новеллы старше общего гейта — как и при списании.
        [Test]
        public void TitleCostWinsOverTheGate()
        {
            var title = new LvnTitle { id = "t", cost = new LvnCost { currency = "crystals", amount = 30 } };
            var shown = LvnEntryPrice.Shown(title, Gate(cost: 2), "ch1");
            Assert.AreEqual("crystals", shown.Currency);
            Assert.AreEqual(30, shown.Amount);
        }

        // Новелла без цены и без гейта — вход свободный, а не «1».
        [Test]
        public void NothingConfiguredIsFreeNotOne()
        {
            Assert.IsTrue(LvnEntryPrice.Shown(new LvnTitle { id = "t" }, null).Free,
                "движок не придумывает цену за автора — это деньги игрока");
        }

        // Валюта названа, сумма — нет: умолчание объявляет МАНИФЕСТ (одна
        // единица за главу), а не каждый экран по-своему.
        [Test]
        public void NamedCurrencyWithoutAnAmountCostsOne()
        {
            var gate = new LvnEconomyConfig { chapter_currency = "energy" };
            var price = LvnEntryPrice.ForChapter(gate, "ch1");
            Assert.IsFalse(price.Free);
            Assert.AreEqual(1, price.Amount);
        }

        // Ноль и отрицательное — это «гейта нет», а не «спишем ноль»: иначе
        // кассир открывал бы окно покупки ни за чем.
        [Test]
        public void ZeroOrNegativeGateIsNoGate()
        {
            Assert.IsTrue(LvnEntryPrice.ForChapter(Gate(cost: 0), "ch1").Free);
            Assert.IsTrue(LvnEntryPrice.ForChapter(Gate(cost: -5), "ch1").Free);
        }

        // Пустой ценник новеллы — тоже «бесплатно»: объявленное поле со
        // значением 0 не должно рисовать плашку «0».
        [Test]
        public void TitleCostOfZeroIsFree()
        {
            Assert.IsTrue(LvnEntryPrice.ForTitle(null).Free);
            Assert.IsTrue(LvnEntryPrice.ForTitle(new LvnTitle { id = "t" }).Free);
            Assert.IsTrue(LvnEntryPrice.ForTitle(new LvnTitle
                { id = "t", cost = new LvnCost { currency = "crystals", amount = 0 } }).Free);
        }

        // Валюта без имени — не цена, сколько бы там ни стояло.
        [Test]
        public void AnAmountWithoutACurrencyIsNotAPrice()
        {
            Assert.IsTrue(new LvnEntryPrice.Price(null, 30).Free);
            Assert.IsTrue(new LvnEntryPrice.Price("", 30).Free);
            Assert.IsTrue(new LvnEntryPrice.Price("energy", 0).Free);
            Assert.IsTrue(LvnEntryPrice.Price.None.Free);
        }

        // Бесплатен ровно НАЗВАННЫЙ список, а не всё подряд.
        [Test]
        public void OnlyTheListedChaptersAreFree()
        {
            var gate = Gate(cost: 2, free: "ch1");
            Assert.IsFalse(LvnEntryPrice.ForChapter(gate, "ch2").Free);
            Assert.IsFalse(LvnEntryPrice.ForChapter(gate, null).Free,
                "без имени главы поблажку выдать не за что");
        }
    }
}
