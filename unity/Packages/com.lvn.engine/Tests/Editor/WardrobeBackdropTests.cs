using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОЛОТНО ОБЩЕЕ, А ЛЕНТА ЖИВАЯ — два дефекта одного экрана, оба с живого
    /// прогона Ильи 08.09.
    ///
    /// <para>Первый: фон покупался заново на каждого героя. Владельцем товара у
    /// фона объявлен «menu» — картина одна на всех, — но покупки до 08.09
    /// писались на персонажа, и такой чек видел только он сам. Кошелёк Ильи
    /// прошёл 1240→1220→1200→1180 за фоны, уже оплаченные Викторией.</para>
    ///
    /// <para>Второй дефект того же прогона — мёртвая лента «Моё» — живёт в
    /// PlayMode (<c>WardrobeMyTabClickTests</c>): тап надо ДОСТАВИТЬ, а
    /// доставка событий UITK требует панели.</para>
    /// </summary>
    public class WardrobeBackdropTests
    {
        private const string Hero = "test_bd_hero";
        private const string Other = "test_bd_other";

        [Test]
        public async Task Backdrop_PaidByOneHero_IsOwnedByEveryHero()
        {
            var prevUrl = Lvn.Services.LvnBackend.BaseUrl;
            Lvn.Services.LvnBackend.BaseUrl = ""; // кошелёк без сети: чистое локальное зеркало
            Lvn.Services.LvnWallet.ResetLocal();
            try
            {
                await Lvn.Services.LvnWallet.EarnAsync("crystals", 100, "test");
                // Чек СТАРОГО образца: фон записан на персонажа.
                await Lvn.Services.LvnWallet.SpendAsync("crystals", 20, "wardrobe",
                    LvnWardrobe.Sku(Hero, "backdrop", "cloister_snow"));

                Assert.IsTrue(WardrobeSheet.OwnsBackdrop(Hero, "cloister_snow"),
                    "свой чек владелец видит");
                Assert.IsTrue(WardrobeSheet.OwnsBackdrop(Other, "cloister_snow"),
                    "полотно общее: второй раз за ту же картину игрок не платит");
                Assert.IsFalse(WardrobeSheet.OwnsBackdrop(Other, "dead_woods"),
                    "неоплаченная картина остаётся неоплаченной");
            }
            finally
            {
                Lvn.Services.LvnWallet.ResetLocal();
                Lvn.Services.LvnBackend.BaseUrl = prevUrl;
            }
        }

        // ── фикстура ──
        private static LvnManifest Manifest() => new LvnManifest
        {
            sprites = new Dictionary<string, LvnSpriteEntity>
            {
                [Hero] = new LvnSpriteEntity
                {
                    name = "Странница",
                    layers = new List<LvnLayer> { new LvnLayer { id = "body", url = "/x/body.png" } },
                    wardrobe = new Dictionary<string, LvnWardrobeSlot>
                    {
                        ["backdrop"] = new LvnWardrobeSlot
                        {
                            name = "Фон",
                            items = new List<LvnWardrobeItem>
                            {
                                new LvnWardrobeItem { value = "cloister_snow", name = "Снежная обитель",
                                                      currency = "crystals", price = 20 },
                                new LvnWardrobeItem { value = "dead_woods", name = "Туманная чаща",
                                                      currency = "crystals", price = 20 },
                            },
                        },
                        ["outfit"] = new LvnWardrobeSlot
                        {
                            name = "Платье",
                            items = new List<LvnWardrobeItem>
                            {
                                new LvnWardrobeItem { value = "plain", name = "Простое" },
                            },
                        },
                    },
                },
            },
        };
    }
}
