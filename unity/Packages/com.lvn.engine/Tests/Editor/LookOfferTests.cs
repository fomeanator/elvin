using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ВИТРИНА ПРОХОЖДЕНИЯ (TR-18): чужой образ ПОКАЗЫВАЮТ, но не выдают.
    /// Ошибка в этом счёте — это либо подарок вместо продажи (один купил,
    /// десять получили даром), либо второй счёт за уже купленное.
    /// </summary>
    public class LookOfferTests
    {
        private const string Hero = "victoria";

        private static LvnManifest Catalog()
        {
            LvnWardrobeSlot Slot(params LvnWardrobeItem[] items)
                => new LvnWardrobeSlot { items = new List<LvnWardrobeItem>(items) };
            return new LvnManifest
            {
                sprites = new Dictionary<string, LvnSpriteEntity>
                {
                    [Hero] = new LvnSpriteEntity
                    {
                        wardrobe = new Dictionary<string, LvnWardrobeSlot>
                        {
                            ["outfit"] = Slot(
                                new LvnWardrobeItem { value = "rose", name = "Розовое", price = 0 },
                                new LvnWardrobeItem { value = "orchid", name = "Орхидея", currency = "crystals", price = 150 },
                                new LvnWardrobeItem { value = "winter", name = "Зимний", currency = "crystals", price = 300, gacha = true }),
                            ["hair"] = Slot(
                                new LvnWardrobeItem { value = "black", name = "Чёрные", currency = "crystals", price = 90 }),
                            ["emotion"] = Slot(
                                new LvnWardrobeItem { value = "happy", name = "Радость" }),
                        },
                    },
                },
            };
        }

        private static Dictionary<string, string> Worn(params (string axis, string value)[] pairs)
        {
            var d = new Dictionary<string, string>();
            foreach (var (a, v) in pairs) d[a] = v;
            return d;
        }

        /// <summary>Цена набора — только за то, чего у смотрящего НЕТ.</summary>
        [Test]
        public void ThePriceCountsOnlyWhatIsMissing()
        {
            var offer = LvnLookOffer.Build(Hero,
                Worn(("outfit", "orchid"), ("hair", "black")), Catalog(),
                sku => sku.EndsWith(":hair:black"));   // причёска уже куплена

            Assert.AreEqual(1, offer.Missing, "не хватает ровно одной вещи");
            Assert.AreEqual(150, offer.Price, "в цену попало уже купленное");
            Assert.AreEqual("crystals", offer.Currency);
        }

        /// <summary>Бесплатная вещь считается своей: за неё не просят денег и
        /// не показывают ценник.</summary>
        [Test]
        public void FreePiecesAreAlreadyYours()
        {
            var offer = LvnLookOffer.Build(Hero, Worn(("outfit", "rose")), Catalog(), _ => false);
            Assert.AreEqual(0, offer.Missing);
            Assert.AreEqual(0, offer.Price);
            Assert.AreEqual(1, offer.Pieces.Count);
            Assert.IsTrue(offer.Pieces[0].Owned);
        }

        /// <summary>Приз круток показываем, но в счёт НЕ берём: кнопка «купить
        /// набор» не должна обещать того, что за деньги не продаётся.</summary>
        [Test]
        public void AWheelOnlyPieceIsShownButNotSold()
        {
            var offer = LvnLookOffer.Build(Hero, Worn(("outfit", "winter")), Catalog(), _ => false);
            Assert.IsTrue(offer.HasGachaOnly, "экран не узнает, что вещь только из круток");
            Assert.AreEqual(0, offer.Price, "приз круток попал в цену набора");
            Assert.AreEqual(1, offer.Missing, "вещи нет у игрока — это правда, и её показывают");
        }

        /// <summary>Лицо — не вещь: эмоция в образ не входит и не продаётся.</summary>
        [Test]
        public void TheFaceIsNotForSale()
        {
            var offer = LvnLookOffer.Build(Hero,
                Worn(("outfit", "orchid"), ("emotion", "happy")), Catalog(), _ => false);
            foreach (var piece in offer.Pieces)
                Assert.AreNotEqual("emotion", piece.Axis, "эмоция попала в набор на продажу");
        }

        /// <summary>Чужого героя и пустого образа не бывает: молчим, а не
        /// выдумываем набор.</summary>
        [Test]
        public void NothingToShowIsNotAnOffer()
        {
            Assert.AreEqual(0, LvnLookOffer.Build(null, Worn(("outfit", "rose")), Catalog(), _ => false).Pieces.Count);
            Assert.AreEqual(0, LvnLookOffer.Build(Hero, null, Catalog(), _ => false).Pieces.Count);
            Assert.AreEqual(0, LvnLookOffer.Build("stranger", Worn(("outfit", "rose")), Catalog(), _ => false).Pieces.Count);
        }
    }
}
