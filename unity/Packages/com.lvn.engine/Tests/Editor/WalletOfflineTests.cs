using System.Threading.Tasks;
using Lvn.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    // The wallet's offline half (the Liminal model): a persisted local mirror
    // gates offline spends, honest earnings queue for replay. The network
    // paths are covered by the server's Go tests; these drive the pure local
    // seam directly. BaseUrl is cleared so every call takes the offline branch.
    public class WalletOfflineTests
    {
        private string _prevUrl;

        [SetUp]
        public void Setup()
        {
            _prevUrl = LvnBackend.BaseUrl;
            LvnBackend.BaseUrl = ""; // hard offline: transport code 0 everywhere
            LvnWallet.ResetLocal();
        }

        [TearDown]
        public void Teardown()
        {
            LvnWallet.ResetLocal();
            LvnBackend.BaseUrl = _prevUrl;
        }

        [Test]
        public async Task OfflineEarn_LandsInTheMirrorAndQueues()
        {
            Assert.IsTrue(await LvnWallet.EarnAsync("gold", 50, "quest"), "an offline earn is still true");
            Assert.AreEqual(50, LvnWallet.Balances["gold"]);
            Assert.AreEqual(1, LvnWallet.PendingCount, "queued for replay");
        }

        [Test]
        public async Task OfflineSpend_GatedByTheLocalBalance()
        {
            await LvnWallet.EarnAsync("gold", 100, "quest");

            Assert.IsFalse(await LvnWallet.SpendAsync("gold", 999, "greed"),
                "offline overdraft must be an honest no");
            Assert.AreEqual(100, LvnWallet.Balances["gold"], "refused spend touches nothing");

            Assert.IsTrue(await LvnWallet.SpendAsync("gold", 30, "shop", "wardrobe:hill:decor:rose"));
            Assert.AreEqual(70, LvnWallet.Balances["gold"]);
            Assert.AreEqual(1, LvnWallet.Inventory["wardrobe:hill:decor:rose"],
                "the sku lands in the local inventory — the wardrobe works on a plane");
            Assert.AreEqual(2, LvnWallet.PendingCount);
        }

        [Test]
        public void Apply_ParsesRegenBlock_ForTheHudCountdown()
        {
            Assert.IsTrue(LvnWallet.Apply(
                @"{""balances"":{""energy"":2},""inventory"":{},
                   ""regen"":{""energy"":{""balance"":2,""cap"":3,""interval_seconds"":7200,""next_refill_unix"":1783440000}}}"));
            Assert.IsTrue(LvnWallet.Regen.TryGetValue("energy", out var r), "regen currency is exposed");
            Assert.AreEqual(3, r.Cap);
            Assert.AreEqual(7200, r.IntervalSeconds);
            Assert.AreEqual(1783440000, r.NextRefillUnix);
            Assert.IsFalse(r.Full, "below cap with a pending refill → not full");

            // A wallet with no regen block leaves the map empty (plain currencies).
            Assert.IsTrue(LvnWallet.Apply(@"{""balances"":{""gold"":10},""inventory"":{}}"));
            Assert.AreEqual(0, LvnWallet.Regen.Count);
        }

        [Test]
        public void LocalOps_ArePureAndExact()
        {
            LvnWallet.ApplyLocal(new JObject { ["op"] = "earn", ["currency"] = "gold", ["amount"] = 10L });
            LvnWallet.ApplyLocal(new JObject { ["op"] = "spend", ["currency"] = "gold", ["amount"] = 4L, ["sku"] = "hat" });
            Assert.AreEqual(6, LvnWallet.Balances["gold"]);
            Assert.AreEqual(1, LvnWallet.Inventory["hat"]);
            Assert.IsFalse(LvnWallet.CanApplyLocal(
                new JObject { ["op"] = "spend", ["currency"] = "gold", ["amount"] = 7L }));
            Assert.IsTrue(LvnWallet.CanApplyLocal(
                new JObject { ["op"] = "earn", ["currency"] = "gold", ["amount"] = 1L }), "earns always fit");
        }

        [Test]
        public async Task Flush_KeepsTheQueueWhileStillOffline()
        {
            await LvnWallet.EarnAsync("gold", 5, "x");
            await LvnWallet.FlushAsync(); // no network — nothing must be lost
            Assert.AreEqual(1, LvnWallet.PendingCount);
        }
    }
}
