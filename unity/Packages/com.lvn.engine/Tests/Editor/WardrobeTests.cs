using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    // The wardrobe: the equip store, the axis overlay rule (script beats
    // player, player beats nothing) and the screen's card rendering. Purchase
    // paths live in the wallet (server-side Go tests).
    public class WardrobeTests
    {
        private const string Entity = "test_wardrobe_hero";

        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        [TearDown]
        public void Cleanup() => LvnWardrobe.Clear(Entity);

        // ── LvnWardrobe store ──
        [Test]
        public void Wardrobe_EquipPersistsAndUnequips()
        {
            LvnWardrobe.Equip(Entity, "armor", "chain");
            Assert.AreEqual("chain", LvnWardrobe.Equipped(Entity)["armor"]);

            LvnWardrobe.Equip(Entity, "armor", null); // take off
            Assert.IsFalse(LvnWardrobe.Equipped(Entity).ContainsKey("armor"));
        }

        [Test]
        public void Wardrobe_ChangedFiresOncePerActualChange()
        {
            int fired = 0;
            System.Action<string> hook = e => { if (e == Entity) fired++; };
            LvnWardrobe.Changed += hook;
            try
            {
                LvnWardrobe.Equip(Entity, "armor", "chain");
                LvnWardrobe.Equip(Entity, "armor", "chain"); // same value → no event
                LvnWardrobe.Equip(Entity, "armor", null);
                LvnWardrobe.Equip(Entity, "armor", null);    // already off → no event
            }
            finally { LvnWardrobe.Changed -= hook; }
            Assert.AreEqual(2, fired);
        }

        [Test]
        public void Wardrobe_MergeFillsOnlyUnsetAxes()
        {
            LvnWardrobe.Equip(Entity, "armor", "chain");
            LvnWardrobe.Equip(Entity, "weapon", "heavy");

            var axes = new Dictionary<string, string> { ["armor"] = "leather" }; // script's choice
            LvnWardrobe.MergeInto(axes, Entity);

            Assert.AreEqual("leather", axes["armor"], "the writer's explicit value wins");
            Assert.AreEqual("heavy", axes["weapon"], "the player's equip fills the unset axis");
        }

        [Test]
        public void Wardrobe_SkuIsDeterministic()
        {
            Assert.AreEqual("wardrobe:hero:armor:chain", LvnWardrobe.Sku("hero", "armor", "chain"));
        }

        [Test]
        public void Wardrobe_PreviewBeatsEquipped_AndClearSnapsBack()
        {
            LvnWardrobe.Equip(Entity, "armor", "leather");
            LvnWardrobe.Preview(Entity, "armor", "chain"); // trying on in-story

            var axes = new Dictionary<string, string>();
            LvnWardrobe.MergeInto(axes, Entity);
            Assert.AreEqual("chain", axes["armor"], "the live try-on wins over the committed equip");

            LvnWardrobe.ClearPreview(Entity); // sheet collapsed without buying
            axes.Clear();
            LvnWardrobe.MergeInto(axes, Entity);
            Assert.AreEqual("leather", axes["armor"], "cancel snaps back to what's equipped");
        }

        [Test]
        public void Wardrobe_ScriptAxisStillBeatsThePreview()
        {
            LvnWardrobe.Preview(Entity, "armor", "chain");
            try
            {
                var axes = new Dictionary<string, string> { ["armor"] = "leather" };
                LvnWardrobe.MergeInto(axes, Entity);
                Assert.AreEqual("leather", axes["armor"]);
            }
            finally { LvnWardrobe.ClearPreview(Entity); }
        }

        // The other side of the contract: an axis that was VARIABLE-driven (the
        // imported protagonist's outfit={Wardrobe.mainCh_Clothes}) is overridable, so
        // a live try-on updates the on-stage mirror in realtime while she's dressed.
        [Test]
        public void Wardrobe_PreviewOverridesVariableDrivenAxis()
        {
            LvnWardrobe.Preview(Entity, "armor", "chain");
            try
            {
                var axes = new Dictionary<string, string> { ["armor"] = "leather" };
                LvnWardrobe.MergeInto(axes, Entity, new HashSet<string> { "armor" });
                Assert.AreEqual("chain", axes["armor"], "a variable-driven axis yields to the preview");
            }
            finally { LvnWardrobe.ClearPreview(Entity); }
        }

        // ── shared fixture ──
        private static LvnManifest Manifest()
        {
            return new LvnManifest
            {
                sprites = new Dictionary<string, LvnSpriteEntity>
                {
                    [Entity] = new LvnSpriteEntity
                    {
                        name = "Странник",
                        layers = new List<LvnLayer>
                        {
                            new LvnLayer { id = "body", url = "/x/body.png" },
                            new LvnLayer { id = "armor", url = "/x/armor_{armor}.png" },
                        },
                        wardrobe = new Dictionary<string, LvnWardrobeSlot>
                        {
                            ["armor"] = new LvnWardrobeSlot
                            {
                                name = "Броня",
                                items = new List<LvnWardrobeItem>
                                {
                                    new LvnWardrobeItem { value = "leather", name = "Кожаный доспех" },
                                    new LvnWardrobeItem { value = "chain", name = "Кольчуга", currency = "gold", price = 300 },
                                },
                            },
                        },
                    },
                },
            };
        }

        // ── WardrobeSheet (the in-story bottom sheet) ──
        [Test]
        public void Sheet_BrowsingPreviewsOnTheLiveActor()
        {
            var sheet = new WardrobeSheet(new WardrobeConfig { confirm_text = "Выбрать наряд" }, new NoAssets());
            sheet.SetManifest(Manifest());
            try
            {
                sheet.BuildFor(Entity);
                // opening the slot previews its first (or worn) item immediately —
                // the carousel and the actor must agree
                Assert.AreEqual("leather", LvnWardrobe.Previewed(Entity)["armor"]);

                var texts = new List<string>();
                Walk(sheet, el =>
                {
                    if (el is Label l) texts.Add(l.text);
                    if (el is Button b) texts.Add(b.text);
                });
                Assert.IsTrue(texts.Contains("Кожаный доспех"), "the carousel names the previewed item");
                Assert.IsTrue(texts.Contains("Выбрать наряд"), "free preview confirms at no cost");
            }
            finally { LvnWardrobe.ClearPreview(Entity); }
        }

        // BUY and CHOOSE are separate acts (partner's ask): an unowned priced
        // item offers its OWN price; buying keeps the sheet open (so hair and
        // jacket buy back-to-back), and only "choose" commits — never charging.
        [Test]
        public void Sheet_UnownedItemOffersBuy_NotChoose()
        {
            var sheet = new WardrobeSheet(new WardrobeConfig
            { confirm_text = "Выбрать", buy_text = "Купить", currency_label = "◆" }, new NoAssets());
            sheet.SetManifest(Manifest());
            try
            {
                sheet.BuildFor(Entity);
                sheet.Step(+1); // leather (free) → chain (300 gold, unowned)

                string cta = null;
                Walk(sheet, el => { if (el is Button b && (b.text.StartsWith("Купить") || b.text.StartsWith("Выбрать"))) cta = b.text; });
                StringAssert.StartsWith("Купить", cta, "an unowned item offers a purchase, not a choose");
                StringAssert.Contains("300", cta, "the buy button carries THIS item's price");
                StringAssert.Contains("◆", cta, "currency_label replaces the raw currency id");
            }
            finally { LvnWardrobe.ClearPreview(Entity); }
        }

        [Test]
        public async Task Sheet_BuyKeepsShoppingOpen_ChooseCommits()
        {
            var prevUrl = Lvn.Services.LvnBackend.BaseUrl;
            Lvn.Services.LvnBackend.BaseUrl = ""; // offline wallet: pure local mirror
            Lvn.Services.LvnWallet.ResetLocal();
            var sheet = new WardrobeSheet(new WardrobeConfig
            { confirm_text = "Выбрать", buy_text = "Купить" }, new NoAssets());
            sheet.SetManifest(Manifest());
            try
            {
                await Lvn.Services.LvnWallet.EarnAsync("gold", 400, "test");
                sheet.BuildFor(Entity);
                sheet.Step(+1); // chain: 300 gold, unowned

                await sheet.ConfirmAsync(); // = BUY
                Assert.IsTrue(Lvn.Services.LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(Entity, "armor", "chain")),
                    "buying lands the sku");
                Assert.IsFalse(LvnWardrobe.Equipped(Entity).ContainsKey("armor"),
                    "buying must NOT equip — choosing is a separate act");
                Assert.AreEqual("chain", LvnWardrobe.Previewed(Entity)["armor"],
                    "the sheet stays open on the same item after a buy");

                await sheet.ConfirmAsync(); // = CHOOSE (item now owned)
                Assert.AreEqual("chain", LvnWardrobe.Equipped(Entity)["armor"],
                    "choose commits the owned piece");
            }
            finally
            {
                LvnWardrobe.ClearPreview(Entity);
                LvnWardrobe.Clear(Entity);
                Lvn.Services.LvnWallet.ResetLocal();
                Lvn.Services.LvnBackend.BaseUrl = prevUrl;
            }
        }

        private static void Walk(VisualElement root, System.Action<VisualElement> visit)
        {
            visit(root);
            foreach (var c in root.Children()) Walk(c, visit);
        }
    }
}
