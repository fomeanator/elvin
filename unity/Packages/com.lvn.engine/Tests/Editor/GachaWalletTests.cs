using System.Collections;
using System.Collections.Generic;
using System.Text;
using Lvn.Content;
using Lvn.Services;
using Lvn.UI;
using Lvn.UI.Screens;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    public class GachaWalletTests
    {
        private string _url;
        private bool _offline;
        private System.Func<float> _clock;

        [SetUp] public void SetUp()
        {
            _url = LvnBackend.BaseUrl;
            _offline = LvnNetworkStatus.ForceOffline;
            _clock = LvnClock.Wall;
            LvnClock.Wall = () => 10f;
            LvnNetworkStatus.ForceOffline = false;
            LvnWallet.ResetLocal();
        }

        [TearDown] public void TearDown()
        {
            LvnWallet.ResetLocal();
            LvnBackend.BaseUrl = _url;
            LvnNetworkStatus.ForceOffline = _offline;
            LvnClock.Wall = _clock;
            LvnWardrobe.Clear("gacha-test");
        }

        [TestCase(409, "{\"error\":\"insufficient_funds\"}", "insufficient_funds")]
        [TestCase(0, "", "offline")]
        [TestCase(200, "{}", "invalid_response")]
        [TestCase(200, "{\"sector\":\"rare\",\"kind\":\"super\"}", "invalid_response")]
        [TestCase(500, "not json", "invalid_response")]
        public void ErrorIsPreservedWithoutInventingAReward(long code, string body, string expected)
            => Assert.AreEqual(expected, LvnGacha.ReadSpin(code, body).Error);

        [UnityTest] public IEnumerator OutfitUpdatesWalletAndMyWardrobeInsideNudgeWindow()
            => SpinAndRefresh("wardrobe:gacha-test:outfit:53");
        [UnityTest] public IEnumerator BackdropUpdatesWalletAndMyWardrobeInsideNudgeWindow()
            => SpinAndRefresh("wardrobe:menu:backdrop:winter");
        [UnityTest] public IEnumerator CurrencyUpdatesWalletInsideNudgeWindow()
            => SpinAndRefresh(null);
        [UnityTest] public IEnumerator EnergyPrizeIsNotClampedToTheFreeRefillCap()
            => SpinAndRefresh(null, energy: true);

        private IEnumerator SpinAndRefresh(string sku, bool energy = false)
        {
            var files = new Dictionary<string, byte[]>
            {
                ["v1/wallet"] = Bytes(Truth(100, null)),
                ["v1/gacha/spin"] = Bytes(sku == null
                    ? new JObject { ["sector"] = "coins", ["kind"] = "currency", ["currency"] = energy ? "energy" : "crystals", ["amount"] = energy ? 150 : 5 }
                    : new JObject { ["sector"] = "rare", ["kind"] = "super", ["prize"] = new JObject { ["sku"] = sku, ["label"] = "Prize" } }),
            };
            using var server = new TestHttpServer(files);
            LvnBackend.BaseUrl = server.Root.TrimEnd('/');
            var initial = LvnWallet.RefreshAsync();
            while (!initial.IsCompleted) yield return null;
            Assert.IsTrue(initial.Result);
            var nudge = LvnWallet.NudgeAsync();
            while (!nudge.IsCompleted) yield return null;
            Assert.AreEqual(1, server.Asked.FindAll(p => p == "v1/wallet").Count, "background refresh is throttled");
            var truth = Truth(sku == null && !energy ? 55 : 50, sku);
            truth["balances"]["energy"] = energy ? 152 : 2;
            truth["regen"] = JObject.Parse(@"{""energy"":{""cap"":5,""balance"":152,""next_refill_unix"":0}}");
            files["v1/wallet"] = Bytes(truth);
            var spin = LvnGacha.SpinAsync();
            while (!spin.IsCompleted) yield return null;
            Assert.IsNull(spin.Result.Error);
            Assert.IsTrue(spin.Result.WalletSynced);
            Assert.AreEqual(sku == null && !energy ? 55 : 50, LvnWallet.Balance("crystals"));
            Assert.AreEqual(2, server.Asked.FindAll(p => p == "v1/wallet").Count);
            Assert.AreEqual(1, server.Asked.FindAll(p => p == "v1/gacha/spin").Count);
            if (energy)
            {
                Assert.AreEqual(152, LvnWallet.Balance("energy"));
                Assert.AreEqual("152", LvnWallet.Display("energy"));
                LvnWallet.ReloadLocal();
                Assert.AreEqual(152, LvnWallet.Balance("energy"));
            }
            if (sku == null) yield break;
            Assert.IsTrue(LvnWallet.Has(sku), "inventory updates before any Take/Close tap");
            LvnWallet.ReloadLocal();
            Assert.IsTrue(LvnWallet.Has(sku), "awarded inventory survives relaunch");
            var sheet = new WardrobeSheet(new WardrobeConfig(), null) { OnlySeen = true };
            sheet.SetContent(Manifest());
            sheet.BuildFor("gacha-test");
            sheet.GoTab(WardrobeSheet.AllTab);
            bool found = false;
            sheet.Query<UnityEngine.UIElements.Label>("card-name").ForEach(label =>
            {
                if (label.text == (sku.Contains(":outfit:") ? "Awarded outfit" : "Awarded backdrop")) found = true;
            });
            Assert.IsTrue(found, "zero-price earned prize must appear in My wardrobe without having been seen in the story");
        }

        private static byte[] Bytes(JObject body) => Encoding.UTF8.GetBytes(body.ToString());
        private static JObject Truth(int balance, string sku) => new JObject
        {
            ["balances"] = new JObject { ["crystals"] = balance },
            ["inventory"] = sku == null ? new JObject() : new JObject { [sku] = 1 },
        };
        private static LvnManifest Manifest() => new LvnManifest
        {
            sprites = new Dictionary<string, LvnSpriteEntity>
            {
                ["gacha-test"] = new LvnSpriteEntity
                {
                    layers = new List<LvnLayer> { new LvnLayer { id = "body", url = "/body.png" } },
                    wardrobe = new Dictionary<string, LvnWardrobeSlot>
                    {
                        ["outfit"] = new LvnWardrobeSlot { items = new List<LvnWardrobeItem>
                        {
                            new LvnWardrobeItem { value = "plain", name = "Free base" },
                            new LvnWardrobeItem { value = "53", name = "Awarded outfit", gacha = true },
                        } },
                        ["backdrop"] = new LvnWardrobeSlot { items = new List<LvnWardrobeItem>
                        {
                            new LvnWardrobeItem { value = "winter", name = "Awarded backdrop", gacha = true },
                        } },
                    },
                },
            },
        };
    }
}
