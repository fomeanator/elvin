using System;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class StateBackgroundSyncTests
    {
        private string _title, _user, _owner, _testOwner, _firstOwner;
        private bool _offline;
        private HttpStateStore _store;
        private static JObject Vars(int n) => new JObject { ["chapter"] = n };
        private static JObject Remote(int n, long version = 1) => new JObject
            { ["vars"] = Vars(n), ["_version"] = version, ["updatedAt"] = DateTime.UtcNow.ToString("o") };
        private static Task<HttpStateStore.StateReply> Reply(long code, JObject doc = null)
            => Task.FromResult(new HttpStateStore.StateReply(code, doc));

        [SetUp] public void SetUp()
        {
            _title = "sync-test-" + Guid.NewGuid().ToString("N");
            _user = Guid.NewGuid().ToString("N");
            _owner = LvnKeep.Owner;
            _firstOwner = LvnKeep.Get("lvn.local.owner", "");
            _testOwner = "sync-owner-" + _user;
            LvnKeep.NoteOwner(_testOwner);
            _offline = LvnNetworkStatus.ForceOffline;
            LvnNetworkStatus.ForceOffline = false;
            _store = new HttpStateStore("http://state.test", _user, "test-key");
        }

        [TearDown] public void TearDown()
        {
            _store.Dispose();
            LvnKeep.NoteOwner(_testOwner);
            LocalStateStore.Forget(_title);
            LvnKeep.Drop(_store.IndexKey);
            LvnKeep.Drop(_store.IndexKey + ".bak");
            LvnKeep.NoteOwner(_owner);
            if (_firstOwner.Length == 0) LvnKeep.Drop("lvn.local.owner");
            else LvnKeep.Put("lvn.local.owner", _firstOwner);
            LvnNetworkStatus.ForceOffline = _offline;
        }

        [Test] public async Task GameplayReadsAndWritesCompleteWithoutStartingHttp()
        {
            int requests = 0;
            _store.Transport = (m, u, d, ct) => { requests++; return Reply(503); };
            var save = _store.SaveVarsAsync(_title, Vars(7), default);
            Assert.IsTrue(save.IsCompletedSuccessfully);
            var load = _store.LoadVarsAsync(_title, default);
            Assert.IsTrue(load.IsCompletedSuccessfully);
            Assert.AreEqual(7, (int)(await load)["chapter"]);
            Assert.AreEqual(0, requests);
            Assert.AreEqual(1, _store.PendingCount);
            await _store.FlushAsync();
            Assert.AreEqual(1, requests);
            Assert.AreEqual(1, _store.PendingCount);
        }

        [TestCase(0)] [TestCase(429)] [TestCase(503)]
        public async Task FailedDeliverySurvivesRestartWithoutAnotherSave(long failure)
        {
            await _store.SaveVarsAsync(_title, Vars(3), default);
            _store.Transport = (m, u, d, ct) => Reply(m == "GET" ? 404 : failure);
            await _store.FlushAsync();
            Assert.IsNull(LocalStateStore.ReadBase(_title));
            _store.Dispose();
            _store = new HttpStateStore("http://state.test", _user, "test-key");
            int puts = 0;
            _store.Transport = (m, u, d, ct) =>
            {
                if (m == "GET") return Reply(404);
                puts++;
                Assert.AreEqual(3, (int)d["vars"]["chapter"]);
                return Reply(200, new JObject { ["version"] = 1 });
            };
            await _store.FlushAsync();
            Assert.AreEqual(1, puts);
            Assert.AreEqual(0, _store.PendingCount);
        }

        [Test] public async Task LostAcknowledgementDoesNotRepeatAnAlreadyAppliedWrite()
        {
            JObject server = null;
            int puts = 0;
            _store.Transport = (m, u, d, ct) =>
            {
                if (m == "GET") return Reply(server == null ? 404 : 200, server);
                puts++;
                server = (JObject)d.DeepClone(); server["_version"] = 1;
                return Reply(0); // committed remotely, response lost
            };
            await _store.SaveVarsAsync(_title, Vars(4), default);
            await _store.FlushAsync();
            Assert.AreEqual(1, _store.PendingCount);
            await _store.FlushAsync();
            Assert.AreEqual(1, puts);
            Assert.AreEqual(0, _store.PendingCount);
        }

        [Test] public async Task LateAcknowledgementCannotClearANewerPendingSave()
        {
            var entered = new TaskCompletionSource<bool>();
            var ack = new TaskCompletionSource<HttpStateStore.StateReply>();
            _store.Transport = (m, u, d, ct) =>
            {
                if (m == "GET") return Reply(404);
                entered.SetResult(true); return ack.Task;
            };
            await _store.SaveVarsAsync(_title, Vars(1), default);
            var flush = _store.FlushAsync();
            Assert.AreSame(flush, _store.FlushAsync(), "delivery is single-flight");
            await entered.Task;
            await _store.SaveVarsAsync(_title, Vars(2), default);
            ack.SetResult(new HttpStateStore.StateReply(200, new JObject { ["version"] = 1 }));
            await flush;
            Assert.AreEqual(2, (int)(await _store.LoadVarsAsync(_title, default))["chapter"]);
            Assert.AreEqual(1, _store.PendingCount);
            Assert.IsNull(LocalStateStore.ReadBase(_title));
        }

        [TestCase(true)] [TestCase(false)]
        public async Task ForgottenTitleIsNotResurrectedByAReply(bool previouslySaved)
        {
            var entered = new TaskCompletionSource<bool>();
            var reply = new TaskCompletionSource<HttpStateStore.StateReply>();
            _store.Transport = (m, u, d, ct) => { entered.SetResult(true); return reply.Task; };
            if (previouslySaved) await _store.SaveVarsAsync(_title, Vars(1), default);
            else await _store.LoadVarsAsync(_title, default);
            var flush = _store.FlushAsync();
            await entered.Task;
            LocalStateStore.Forget(_title);
            reply.SetResult(new HttpStateStore.StateReply(200, Remote(8)));
            await flush;
            Assert.IsNull(LocalStateStore.ReadDoc(_title));
            Assert.IsNull(LocalStateStore.ReadBase(_title));
        }

        [Test] public async Task AccountSwitchDoesNotApplyAnOldAccountsReply()
        {
            var entered = new TaskCompletionSource<bool>();
            var reply = new TaskCompletionSource<HttpStateStore.StateReply>();
            _store.Transport = (m, u, d, ct) => { entered.SetResult(true); return reply.Task; };
            await _store.SaveVarsAsync(_title, Vars(1), default);
            var flush = _store.FlushAsync();
            await entered.Task;
            LvnKeep.NoteOwner("sync-other-" + _user);
            reply.SetResult(new HttpStateStore.StateReply(200, Remote(8)));
            await flush;
            Assert.IsNull(LocalStateStore.ReadDoc(_title));
            LvnKeep.NoteOwner(_testOwner);
            Assert.AreEqual(1, _store.PendingCount);
        }

        [Test] public async Task ExplicitEmptySaveReplacesServerWithoutABaseline()
        {
            JObject uploaded = null;
            _store.Transport = (m, u, d, ct) => m == "GET" ? Reply(200, Remote(9))
                : Reply(200, uploaded = (JObject)d.DeepClone());
            await _store.SaveVarsAsync(_title, new JObject(), default);
            await _store.FlushAsync();
            Assert.AreEqual(0, ((JObject)uploaded["vars"]).Count);
            Assert.AreEqual(1, (long)uploaded["_version"]);
        }

        [Test] public void MergePreservesLocalDeletionsAndUnrelatedRemoteChanges()
        {
            var baseline = new JObject { ["removed"] = 1, ["remote"] = 2 };
            var local = new JObject { ["remote"] = 2 };
            var server = new JObject { ["removed"] = 1, ["remote"] = 3 };
            Assert.IsTrue(JToken.DeepEquals(new JObject { ["remote"] = 3 },
                HttpStateStore.MergeVars(server, local, baseline)));
        }

        [Test] public async Task ConflictRetryKeepsLocalDeletionAndRemoteUntouchedField()
        {
            var baseline = new JObject { ["removed"] = 1, ["remote"] = 1 };
            LocalStateStore.WriteBase(_title, baseline);
            int puts = 0;
            _store.Transport = (m, u, d, ct) =>
            {
                if (m == "GET") return Reply(200, new JObject { ["vars"] = baseline.DeepClone(), ["_version"] = 1 });
                if (++puts == 1) return Reply(409, new JObject { ["version"] = 2,
                    ["doc"] = new JObject { ["vars"] = new JObject { ["removed"] = 1, ["remote"] = 2 } } });
                Assert.AreEqual(2, (long)d["_version"]);
                Assert.IsNull(d["vars"]["removed"]);
                Assert.AreEqual(2, (int)d["vars"]["remote"]);
                Assert.AreEqual(6, (int)d["vars"]["chapter"]);
                return Reply(200, new JObject { ["version"] = 3 });
            };
            await _store.SaveVarsAsync(_title, new JObject { ["remote"] = 1, ["chapter"] = 6 }, default);
            await _store.FlushAsync();
            Assert.AreEqual(2, puts);
            Assert.AreEqual(0, _store.PendingCount);
        }

        [Test] public async Task DisposeImmediatelyAfterFlushDoesNotFaultOrErasePendingData()
        {
            await _store.SaveVarsAsync(_title, Vars(1), default);
            var flush = _store.FlushAsync();
            _store.Dispose();
            await flush;
            Assert.IsTrue(flush.IsCompletedSuccessfully);
            Assert.AreEqual(1, _store.PendingCount);
        }

        [Test] public async Task ResetWithBaselineAlsoClearsPreviouslyUnseenRemoteFields()
        {
            LocalStateStore.WriteBase(_title, Vars(1));
            JObject uploaded = null;
            var server = Remote(9);
            server["vars"]["new-remote-field"] = true;
            _store.Transport = (m, u, d, ct) => m == "GET" ? Reply(200, server)
                : Reply(200, uploaded = (JObject)d.DeepClone());
            await _store.SaveVarsAsync(_title, new JObject(), default);
            await _store.FlushAsync();
            Assert.AreEqual(0, ((JObject)uploaded["vars"]).Count);
        }

        [Test] public async Task ExplicitAccountCloudAddressIsIndependentOfLocalFirstOwner()
        {
            _store.Dispose();
            LvnKeep.Put("lvn.local.owner", "another-device-owner");
            _store = new HttpStateStore("http://state.test", _testOwner, "shared-key");
            string address = null;
            _store.Transport = (m, u, d, ct) => { address = u; return Reply(404); };
            await _store.LoadVarsAsync(_title, default);
            await _store.FlushAsync();
            Assert.AreEqual("http://state.test/v1/state?user=" + _testOwner + "__" + _title, address);
        }
    }
}
