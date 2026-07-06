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

        // ── WardrobeScreen ──
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

        [Test]
        public void Screen_RendersTabsAndCards_FreeOwnedPricedNot()
        {
            var screen = new WardrobeScreen(new WardrobeConfig { equip_text = "Надеть" }, new NoAssets());
            screen.SetManifest(Manifest());
            screen.BuildFor(Entity);

            var labels = new List<string>();
            var buttons = new List<string>();
            Walk(screen, el =>
            {
                if (el is Label l) labels.Add(l.text);
                if (el is Button b) buttons.Add(b.text);
            });

            Assert.IsTrue(buttons.Contains("Броня"), "slot tab uses its display name");
            Assert.IsTrue(labels.Contains("Кожаный доспех"));
            Assert.IsTrue(labels.Contains("Кольчуга"));
            Assert.IsTrue(buttons.Contains("Надеть"), "free item is owned → equip button");
            Assert.IsTrue(buttons.Contains("300 gold"), "priced unowned item shows its price");
        }

        [Test]
        public void Screen_EquippedItemShowsItsState()
        {
            LvnWardrobe.Equip(Entity, "armor", "leather");
            var screen = new WardrobeScreen(new WardrobeConfig
            {
                equipped_text = "Надето",
                remove_text = "Снять",
            }, new NoAssets());
            screen.SetManifest(Manifest());
            screen.BuildFor(Entity);

            var texts = new List<string>();
            Walk(screen, el =>
            {
                if (el is Label l) texts.Add(l.text);
                if (el is Button b) texts.Add(b.text);
            });
            Assert.IsTrue(texts.Contains("Надето"), "worn card carries the equipped state");
            Assert.IsTrue(texts.Contains("Снять"), "worn card's action is take-off");
        }

        [Test]
        public void Screen_ListsOnlyEntitiesWithAWardrobe()
        {
            var m = Manifest();
            m.sprites["plain"] = new LvnSpriteEntity { name = "Без гардероба" };
            var screen = new WardrobeScreen(null, new NoAssets());
            screen.SetManifest(m);
            CollectionAssert.AreEqual(new[] { Entity }, screen.Entities());
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

        [Test]
        public void Sheet_ConfirmPriceSumsUnownedPreviews()
        {
            var sheet = new WardrobeSheet(new WardrobeConfig { confirm_text = "Выбрать" }, new NoAssets());
            sheet.SetManifest(Manifest());
            try
            {
                sheet.BuildFor(Entity);
                LvnWardrobe.Preview(Entity, "armor", "chain"); // priced, unowned
                // rebuild the button text through the same path the arrows use
                sheet.BuildFor(Entity);

                string confirm = null;
                Walk(sheet, el => { if (el is Button b && b.text.StartsWith("Выбрать")) confirm = b.text; });
                StringAssert.Contains("300 gold", confirm, "the confirm button carries the unowned total");
            }
            finally { LvnWardrobe.ClearPreview(Entity); }
        }

        private static void Walk(VisualElement root, System.Action<VisualElement> visit)
        {
            visit(root);
            foreach (var c in root.Children()) Walk(c, visit);
        }
    }
}
