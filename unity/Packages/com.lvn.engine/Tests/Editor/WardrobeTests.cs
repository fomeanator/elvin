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
            var sheet = new WardrobeSheet(new WardrobeConfig { confirm_text = "Выбрать наряд" }, new TestAssets());
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
            { confirm_text = "Выбрать", buy_text = "Купить" }, new TestAssets());
            sheet.SetManifest(Manifest());
            try
            {
                sheet.BuildFor(Entity);
                sheet.Step(+1); // leather (free) → chain (300 gold, unowned)

                // Подпись читается свойством, а не обходом дерева: она составная
                // (слово, цена, значок валюты), и у самой Button текста больше нет.
                string cta = sheet.ConfirmCaption;
                StringAssert.StartsWith("Купить", cta, "an unowned item offers a purchase, not a choose");
                StringAssert.Contains("300", cta, "the buy button carries THIS item's price");
                // Валюту показывает ЗНАЧОК рядом с суммой, а не слово в подписи:
                // слово авторское (не переводится) и в узкой кнопке вытесняло цену.
                StringAssert.DoesNotContain("gold", cta, "валюта не пишется служебным id");
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
            { confirm_text = "Выбрать", buy_text = "Купить" }, new TestAssets());
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
    
        // ─────────────────────────────────────────────────────────────────────
        // ЧТО ЗА ОСЬ — ЗНАЕТ ОДИН ДОМ.
        //
        // Правило угадывания по имени («hair», «причёска», «эмоции») жило
        // копиями: витрина, тракт показа актёра, лента листа и камера меню.
        // Копии успели разойтись — дом нормализует «ё» → «е», а копия в камере
        // нет, и ось «Причёска» получала кадр НА КОРПУС, хотя лист показывал её
        // причёской.

        [Test]
        public void ОсьВолос_УзнаётсяВЛюбомНаписании()
        {
            foreach (var axis in new[] { "hair", "hairstyle", "причес", "Причёска", "ВОЛОСЫ" })
                Assert.AreEqual(LvnWardrobeAxisKind.Hair, LvnWardrobeStage.KindOf(axis), axis);
        }

        [Test]
        public void ОсьЛица_НеСчитаетсяПереодеванием()
        {
            foreach (var axis in new[] { "emotion", "эмоция", "mood", "face" })
            {
                Assert.AreEqual(LvnWardrobeAxisKind.Emotion, LvnWardrobeStage.KindOf(axis), axis);
                Assert.IsTrue(LvnWardrobeStage.IsEmotion(axis), axis);
                Assert.IsFalse(LvnWardrobeStage.IsHair(axis), axis);
            }
        }

        [Test]
        public void ОсьУкрашений_ОтличаетсяОтОдежды()
        {
            Assert.AreEqual(LvnWardrobeAxisKind.Decor, LvnWardrobeStage.KindOf("украшения"));
            Assert.AreEqual(LvnWardrobeAxisKind.Decor, LvnWardrobeStage.KindOf("jewelry"));
            Assert.AreEqual(LvnWardrobeAxisKind.Outfit, LvnWardrobeStage.KindOf("outfit"));
            Assert.AreEqual(LvnWardrobeAxisKind.Outfit, LvnWardrobeStage.KindOf("pose"));
        }

        [Test]
        public void НеизвестнаяОсь_ЭтоОдежда()
        {
            // Умолчание намеренное: незнакомую ось показываем как вещь на
            // корпусе — так её хотя бы видно целиком.
            Assert.AreEqual(LvnWardrobeAxisKind.Outfit, LvnWardrobeStage.KindOf("шляпа"));
            Assert.AreEqual(LvnWardrobeAxisKind.Outfit, LvnWardrobeStage.KindOf(""));
            Assert.AreEqual(LvnWardrobeAxisKind.Outfit, LvnWardrobeStage.KindOf(null));
        }
}
}
