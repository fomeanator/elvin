using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class ManualSlotGlobalStatsTests
    {
        private sealed class MemoryStore : ILvnStateStore
        {
            public Task<JObject> Read = Task.FromResult<JObject>(new JObject { ["rep"] = 9 });

            public Task<JObject> LoadVarsAsync(string titleId, CancellationToken ct)
            {
                Assert.AreEqual(LvnGlobalStats.ScopeId, titleId);
                return Read;
            }

            public Task SaveVarsAsync(string titleId, JObject vars, CancellationToken ct)
                => throw new System.InvalidOperationException("Loading a slot must not write stats.");
        }

        private sealed class NullStage : ILvnStage
        {
            public void ShowSay(string who, string text, string style) { }
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(JObject command, LvnSender sender) { }
            public void ApplyStage(JObject command) { }
            public void OnEnd() { }
        }

        private static LvnPlayer Player()
            => new LvnPlayer(LvnDocument.Parse(@"{""script"":[
                {""op"":""say"",""text"":""first""},
                {""op"":""say"",""text"":""saved""},
                {""op"":""say"",""text"":""later""}]}"), new NullStage());

        private static LvnPlayer.LvnSnapshot OldSlot(LvnPlayer player)
        {
            player.Vars["global"] = new JObject { ["rep"] = 5 };
            player.Vars["local"] = new JObject { ["score"] = 2 };
            player.ContinueFrom(1);
            return player.Save();
        }

        [Test]
        public async Task OldSlotDoesNotRollBackCrossNovelStats()
        {
            var player = Player();
            var snap = OldSlot(player);

            // Removing the production overlay must turn this assertion red: 5 != 9.
            await VnStage.OverlaySlotStatsAsync(new MemoryStore(), snap);
            player.Restore(snap);

            Assert.AreEqual(9, (int)player.Vars["global"]["rep"]);
        }

        [Test]
        public async Task NovelVarsAndPositionStillComeFromTheSlot()
        {
            var player = Player();
            var snap = OldSlot(player);
            player.Vars["local"] = new JObject { ["score"] = 8 };
            player.Vars["laterOnly"] = true;
            player.ContinueFrom(2);

            await VnStage.OverlaySlotStatsAsync(new MemoryStore(), snap);
            player.Restore(snap);

            Assert.AreEqual(snap.Index, player.Index);
            Assert.AreEqual(2, (int)player.Vars["local"]["score"]);
            Assert.IsFalse(player.Vars.ContainsKey("laterOnly"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task EmptyLiveStatsKeepTheSnapshotGlobals(bool missing)
        {
            var player = Player();
            var snap = OldSlot(player);
            var store = new MemoryStore { Read = Task.FromResult(missing ? null : new JObject()) };

            await VnStage.OverlaySlotStatsAsync(store, snap);
            player.Restore(snap);

            Assert.AreEqual(5, (int)player.Vars["global"]["rep"]);
            Assert.AreEqual(2, (int)player.Vars["local"]["score"]);
        }

        [Test]
        public async Task NoStoreKeepsStandaloneSlotLoadingUsable()
        {
            var player = Player();
            var snap = OldSlot(player);

            await VnStage.OverlaySlotStatsAsync(null, snap);
            player.Restore(snap);

            Assert.AreEqual(5, (int)player.Vars["global"]["rep"]);
        }

        [Test]
        public async Task OverlayWaitsForTheLiveRead()
        {
            var snap = OldSlot(Player());
            var reply = new TaskCompletionSource<JObject>();
            var load = VnStage.OverlaySlotStatsAsync(new MemoryStore { Read = reply.Task }, snap);
            try
            {
                Assert.IsFalse(load.IsCompleted);
                Assert.AreEqual(5, (int)snap.Vars["global"]["rep"]);
            }
            finally { reply.TrySetResult(new JObject { ["rep"] = 9 }); }
            await load;
            Assert.AreEqual(9, (int)snap.Vars["global"]["rep"]);
        }
    }
}
