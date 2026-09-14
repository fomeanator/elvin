using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Lvn.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    public class AdRewardTests
    {
        private string _url, _owner;
        private bool _offline;
        private Func<string, Task<bool>> _show;
        private Func<float> _clock;

        [SetUp] public void SetUp()
        {
            _url = LvnBackend.BaseUrl; _owner = LvnKeep.Owner;
            _offline = LvnNetworkStatus.ForceOffline; _show = LvnAds.ShowRewarded;
            _clock = LvnClock.Wall;
            LvnNetworkStatus.ForceOffline = false;
            LvnKeep.NoteOwner("ad-test-" + Guid.NewGuid());
            LvnWallet.ResetLocal();
        }

        [TearDown] public void TearDown()
        {
            LvnWallet.ResetLocal(); LvnKeep.NoteOwner(_owner);
            LvnAds.ShowRewarded = _show; LvnBackend.BaseUrl = _url;
            LvnNetworkStatus.ForceOffline = _offline; LvnClock.Wall = _clock;
        }

        private static byte[] Bytes(string body) => Encoding.UTF8.GetBytes(body);
        private static Dictionary<string, byte[]> Responses() => new Dictionary<string, byte[]>
        {
            ["v1/ads/catalog"] = Bytes("{\"placements\":[{\"placement\":\"test\",\"currency\":\"crystals\",\"amount\":5,\"charges\":3,\"left\":3,\"ready_at\":0}]}"),
            ["v1/ads/reward"] = Bytes("{\"granted\":true,\"currency\":\"crystals\",\"amount\":5,\"left\":2,\"ready_at\":0}"),
            ["v1/wallet"] = Bytes("{\"balances\":{\"crystals\":105},\"inventory\":{}}"),
        };

        [UnityTest] public IEnumerator OneVideoHasOneRewardAndUpdatesWalletImmediately()
        {
            using var server = new TestHttpServer(Responses());
            LvnBackend.BaseUrl = server.Root.TrimEnd('/');
            LvnWallet.Apply("{\"balances\":{\"crystals\":100},\"inventory\":{}}");
            var video = new TaskCompletionSource<bool>(); int shows = 0;
            LvnAds.ShowRewarded = _ => { shows++; return video.Task; };
            var first = LvnAds.WatchAndRewardAsync("test");
            yield return Until(() => shows == 1);
            var duplicate = LvnAds.WatchAndRewardAsync("test");
            yield return Until(() => duplicate.IsCompleted);
            Assert.IsFalse(duplicate.Result);
            video.SetResult(true);
            yield return Until(() => first.IsCompleted);
            Assert.IsTrue(first.Result);
            Assert.AreEqual(1, shows);
            Assert.AreEqual(1, server.Asked.FindAll(p => p == "v1/ads/reward").Count);
            Assert.AreEqual(105, LvnWallet.Balance("crystals"));
            Assert.AreEqual(2, LvnAds.StateOf("test").Left);
            LvnWallet.ReloadLocal();
            Assert.AreEqual(105, LvnWallet.Balance("crystals"));
        }

        [UnityTest] public IEnumerator CancelledVideoNeverRequestsReward()
        {
            using var server = new TestHttpServer(Responses());
            LvnBackend.BaseUrl = server.Root.TrimEnd('/');
            LvnAds.ShowRewarded = _ => Task.FromResult(false);
            var watch = LvnAds.WatchAndRewardAsync("test");
            yield return Until(() => watch.IsCompleted);
            Assert.IsFalse(watch.Result);
            Assert.IsFalse(server.Asked.Contains("v1/ads/reward"));
        }

        [UnityTest] public IEnumerator ExpiredRechargeRefreshesWithoutReopening()
        {
            using var server = new TestHttpServer(Responses());
            LvnBackend.BaseUrl = server.Root.TrimEnd('/');
            LvnAds.NoteCatalog(new List<LvnAds.Placement> { new LvnAds.Placement
                { Id = "test", Left = 0, Charges = 3, ReadyAtUnix = 1 } });
            var refresh = LvnAds.RefreshDueAsync();
            yield return Until(() => refresh.IsCompleted);
            Assert.IsTrue(LvnAds.StateOf("test").Ready);
            Assert.AreEqual(3, LvnAds.StateOf("test").Left);
        }

        [UnityTest] public IEnumerator AccountChangeWhileVideoPlaysCannotRewardAnotherAccount()
        {
            using var server = new TestHttpServer(Responses());
            LvnBackend.BaseUrl = server.Root.TrimEnd('/');
            var video = new TaskCompletionSource<bool>(); bool started = false;
            LvnAds.ShowRewarded = _ => { started = true; return video.Task; };
            var watch = LvnAds.WatchAndRewardAsync("test");
            yield return Until(() => started);
            LvnKeep.NoteOwner("ad-other-" + Guid.NewGuid());
            video.SetResult(true);
            yield return Until(() => watch.IsCompleted);
            Assert.IsFalse(watch.Result);
            Assert.IsFalse(server.Asked.Contains("v1/ads/reward"));
            Assert.IsNull(LvnAds.StateOf("test"));
        }

        [UnityTest] public IEnumerator MalformedSuccessIsNotDisplayedAsAReward()
        {
            var responses = Responses(); responses["v1/ads/reward"] = Bytes("{}");
            using var server = new TestHttpServer(responses);
            LvnBackend.BaseUrl = server.Root.TrimEnd('/');
            LvnAds.ShowRewarded = _ => Task.FromResult(true);
            var watch = LvnAds.WatchAndRewardAsync("test");
            yield return Until(() => watch.IsCompleted);
            Assert.IsFalse(watch.Result);
        }

        private static IEnumerator Until(Func<bool> done)
        {
            float deadline = Time.realtimeSinceStartup + 8;
            while (!done() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(done(), "Timed out waiting for ad operation");
        }
    }
}
