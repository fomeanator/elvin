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
    }
}
