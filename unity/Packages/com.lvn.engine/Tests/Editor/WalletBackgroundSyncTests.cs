using System.Threading.Tasks;
using Lvn.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class WalletBackgroundSyncTests
    {
        private bool _offline;
        [SetUp] public void SetUp()
        {
            _offline = LvnNetworkStatus.ForceOffline;
            LvnNetworkStatus.ForceOffline = false;
            LvnWallet.ResetLocal();
        }
        [TearDown] public void TearDown()
        {
            LvnWallet.SyncPost = null;
            LvnWallet.SyncGet = null;
            LvnWallet.ResetLocal();
            LvnNetworkStatus.ForceOffline = _offline;
        }
        private static string Truth(long gold) => new JObject
            { ["balances"] = new JObject { ["gold"] = gold }, ["inventory"] = new JObject() }.ToString();

        [TestCase(true)] [TestCase(false)]
        public async Task RewardRefreshWinsAgainstAnOlderBackgroundRead(bool olderFinishesFirst)
        {
            var oldReply = new TaskCompletionSource<(long, string)>();
            var rewardReply = new TaskCompletionSource<(long, string)>();
            int reads = 0;
            LvnWallet.SyncGet = _ => ++reads == 1 ? oldReply.Task : rewardReply.Task;
            var oldRead = LvnWallet.RefreshAsync();
            var rewardRead = LvnWallet.RefreshAsync();
            Assert.AreEqual(2, reads);
            if (olderFinishesFirst)
            {
                oldReply.SetResult((200, Truth(5)));
                Assert.IsFalse(await oldRead, "a read started before the reward must not apply after the reward refresh starts");
                rewardReply.SetResult((200, Truth(155)));
            }
            else
            {
                rewardReply.SetResult((200, Truth(155)));
                Assert.IsTrue(await rewardRead);
                oldReply.SetResult((200, Truth(5)));
            }
            Assert.IsTrue(await rewardRead);
            Assert.IsFalse(await oldRead);
            Assert.AreEqual(155, LvnWallet.Balance("gold"));
            LvnWallet.ReloadLocal();
            Assert.AreEqual(155, LvnWallet.Balance("gold"));
        }

        [Test] public void FailingObserverDoesNotBlockTheBalanceDisplay()
        {
            System.Action broken = () => throw new System.InvalidOperationException("test observer");
            long shown = -1;
            System.Action display = () => shown = LvnWallet.Balance("gold");
            LvnWallet.Changed += broken;
            LvnWallet.Changed += display;
            try
            {
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, "[lvn-wallet] observer: test observer");
                Assert.IsTrue(LvnWallet.Apply(Truth(155)));
                Assert.AreEqual(155, shown);
            }
            finally { LvnWallet.Changed -= broken; LvnWallet.Changed -= display; }
        }

        [TestCase(0)] [TestCase(408)] [TestCase(429)] [TestCase(500)] [TestCase(503)]
        [TestCase(401)] [TestCase(403)]
        public async Task RetryKeepsTheSameOperationAndJournalAcrossRestart(long failure)
        {
            string id = null;
            LvnWallet.SyncPost = (path, body) =>
            {
                id = (string)JObject.Parse(body)["op_id"];
                return Task.FromResult((failure, ""));
            };
            var earn = LvnWallet.EarnAsync("gold", 10, "chapter");
            Assert.IsTrue(earn.IsCompletedSuccessfully, "story operations never wait for HTTP");
            Assert.IsNull(id, "HTTP runs only in the background delivery pass");
            await LvnWallet.FlushAsync();
            Assert.IsNotEmpty(id);
            LvnWallet.ReloadLocal();
            Assert.AreEqual(10, LvnWallet.Balance("gold"));
            Assert.AreEqual(1, LvnWallet.PendingCount);
            LvnWallet.SyncPost = (path, body) =>
            {
                Assert.AreEqual(id, (string)JObject.Parse(body)["op_id"]);
                return Task.FromResult((200L, Truth(10)));
            };
            await LvnWallet.FlushAsync();
            Assert.AreEqual(0, LvnWallet.PendingCount);
            Assert.AreEqual(10, LvnWallet.Balance("gold"));
        }

        [Test] public async Task AckRebasesPendingTailWithoutOverwritingIt()
        {
            await LvnWallet.EarnAsync("gold", 100, "chapter");
            await LvnWallet.SpendAsync("gold", 30, "choice", "hat");
            var tailEntered = new TaskCompletionSource<bool>();
            var tailReply = new TaskCompletionSource<(long, string)>();
            int calls = 0;
            LvnWallet.SyncPost = (path, body) =>
            {
                if (++calls == 1) return Task.FromResult((200L, Truth(100)));
                tailEntered.SetResult(true); return tailReply.Task;
            };
            var flush = LvnWallet.FlushAsync();
            Assert.AreSame(flush, LvnWallet.FlushAsync());
            await tailEntered.Task;
            Assert.AreEqual(70, LvnWallet.Balance("gold"));
            Assert.IsTrue(LvnWallet.Has("hat"));
            tailReply.SetResult((503, ""));
            await flush;
            LvnWallet.ReloadLocal();
            Assert.AreEqual(70, LvnWallet.Balance("gold"));
            Assert.AreEqual(1, LvnWallet.PendingCount);
        }

        [Test] public async Task OldReplyCannotOverwriteAResetWalletOrRemoveItsQueueHead()
        {
            var entered = new TaskCompletionSource<bool>();
            var reply = new TaskCompletionSource<(long, string)>();
            LvnWallet.SyncPost = (p, b) => { entered.SetResult(true); return reply.Task; };
            await LvnWallet.EarnAsync("gold", 100, "old-player");
            var flush = LvnWallet.FlushAsync();
            await entered.Task;
            LvnWallet.ResetLocal();
            await LvnWallet.EarnAsync("gold", 7, "new-player");
            reply.SetResult((200, Truth(100)));
            await flush;
            Assert.AreEqual(7, LvnWallet.Balance("gold"));
            Assert.AreEqual(1, LvnWallet.PendingCount);
        }

        [Test] public async Task MalformedSuccessIsNotAnAcknowledgement()
        {
            LvnWallet.SyncPost = (p, b) => Task.FromResult((200L, "{}"));
            await LvnWallet.EarnAsync("gold", 8, "chapter");
            await LvnWallet.FlushAsync();
            Assert.AreEqual(1, LvnWallet.PendingCount);
            Assert.AreEqual(8, LvnWallet.Balance("gold"));
        }

        [Test] public async Task InvalidAmountsCannotMintFundsOrOverflowOffline()
        {
            Assert.IsFalse(await LvnWallet.SpendAsync("gold", -10, "bad"));
            Assert.IsFalse(await LvnWallet.EarnAsync("gold", 0, "bad"));
            Assert.IsFalse(await LvnWallet.EarnAsync("", 10, "bad"));
            Assert.IsTrue(await LvnWallet.EarnAsync("gold", long.MaxValue, "test"));
            Assert.IsFalse(await LvnWallet.EarnAsync("gold", 1, "overflow"));
            Assert.AreEqual(long.MaxValue, LvnWallet.Balance("gold"));
            Assert.AreEqual(1, LvnWallet.PendingCount);
        }
    }
}
