using System;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    public class ManualSlotGlobalStatsStageTests
    {
        private const string ScriptUrl = "/content/scripts/manual-slot-stats.lvn";
        private const string Script = @"{""script"":[
            {""op"":""say"",""text"":""first""},
            {""op"":""say"",""text"":""saved""},
            {""op"":""say"",""text"":""later""}]}";

        private sealed class MemoryStore : ILvnStateStore
        {
            public Task<JObject> Read;
            public int Reads;
            public Task<JObject> LoadVarsAsync(string titleId, CancellationToken ct)
            {
                Assert.AreEqual(LvnGlobalStats.ScopeId, titleId);
                Reads++;
                return Read;
            }
            public Task SaveVarsAsync(string titleId, JObject vars, CancellationToken ct)
                => throw new InvalidOperationException("Loading a slot must not write stats.");
        }

        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;
        private string _title;
        private MemoryStore _store;
        private TaskCompletionSource<JObject> _reply;
        private Task<bool> _load;
        private int _resumes, _repAtResume, _savedIndex;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _title = "manual-slot-stats-test-" + Guid.NewGuid().ToString("N");
            _reply = new TaskCompletionSource<JObject>();
            _store = new MemoryStore { Read = _reply.Task };
            _load = null;
            _resumes = 0;
            _repAtResume = -1;
            _stage = TestStage.Panel("manual-slot-stats-stage", out _go, out _panel);
            _stage.SlotStateStore = _store;
            yield return null;
            _stage.SetSaveContext(_title, "chapter", ScriptUrl);
            _stage.Play(Script);
            yield return WaitFor(() => HasLine("first"));
            _stage.Player.Vars["global"] = new JObject { ["rep"] = 5 };
            _stage.Player.Vars["local"] = new JObject { ["score"] = 2 };
            _stage.Player.Advance();
            yield return WaitFor(() => HasLine("saved"));
            _savedIndex = _stage.Player.Index;
            Assert.IsTrue(_stage.SaveToSlot("old"));
            _stage.Player.Vars["global"] = new JObject { ["rep"] = 6 };
            _stage.Player.Vars["local"] = new JObject { ["score"] = 8 };
            _stage.Player.Advance();
            yield return WaitFor(() => HasLine("later"));
            _stage.Resumed += _ =>
            {
                _resumes++;
                _repAtResume = (int)_stage.Player.Vars["global"]["rep"];
            };
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _reply?.TrySetResult(new JObject());
            if (_load != null) yield return WaitFor(() => _load.IsCompleted);
            _stage?.ClearStage();
            yield return null;
            LvnSaveStore.DeleteAll(_title);
            LvnScreenDirector.Current.ShowChromeAll();
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_panel != null) UnityEngine.Object.Destroy(_panel);
            yield return null;
        }

        private static IEnumerator WaitFor(Func<bool> ready)
        {
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!ready() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(ready(), "Timed out waiting for the stage or slot load.");
        }

        private bool HasLine(string text) => _stage.Backlog.Any(line => line.text == text);
        private string State() => JsonConvert.SerializeObject(_stage.Player.Save());

        private void AssertRestored(int rep)
        {
            Assert.AreEqual(1, _resumes);
            Assert.AreEqual(rep, _repAtResume, "Restore observers saw stale globals.");
            Assert.AreEqual(rep, (int)_stage.Player.Vars["global"]["rep"]);
            Assert.AreEqual(2, (int)_stage.Player.Vars["local"]["score"]);
            Assert.AreEqual(_savedIndex, _stage.Player.Index);
            Assert.AreEqual(5, (int)LvnSaveStore.Get(_title, "old").Snap.Vars["global"]["rep"],
                "Loading must not rewrite the stored slot.");
        }

        [UnityTest]
        public IEnumerator AsyncLoadWaitsForLiveStatsBeforeRestoring()
        {
            var before = State();
            _load = _stage.LoadFromSlotAsync("old");
            Assert.IsFalse(_load.IsCompleted);
            Assert.AreEqual(before, State(), "Waiting for stats installed the old snapshot.");
            Assert.AreEqual(0, _resumes);
            var concurrent = _stage.LoadFromSlotAsync("old");
            Assert.IsTrue(concurrent.IsCompleted);
            Assert.IsFalse(concurrent.Result);
            Assert.AreEqual(1, _store.Reads);

            _reply.SetResult(new JObject { ["rep"] = 9 });
            yield return WaitFor(() => _load.IsCompleted);
            Assert.IsTrue(_load.Result);
            yield return WaitFor(() => HasLine("saved"));
            AssertRestored(9);
        }

        [UnityTest]
        public IEnumerator SynchronousEntryAlsoWaitsForLiveStats()
        {
            var before = State();
            Assert.IsTrue(_stage.LoadFromSlot("old"));
            Assert.AreEqual(before, State());
            Assert.AreEqual(0, _resumes);
            Assert.AreEqual(1, _store.Reads);

            _reply.SetResult(new JObject { ["rep"] = 9 });
            yield return WaitFor(() => _resumes == 1 && HasLine("saved"));
            AssertRestored(9);
        }

        [UnityTest]
        public IEnumerator EmptyLiveStatsPreserveSnapshotGlobals()
        {
            _reply.SetResult(new JObject());
            _load = _stage.LoadFromSlotAsync("old");
            yield return WaitFor(() => _load.IsCompleted);
            Assert.IsTrue(_load.Result);
            yield return WaitFor(() => HasLine("saved"));
            AssertRestored(5);
        }

        [UnityTest]
        public IEnumerator ScriptMismatchIsRejectedWithoutTouchingState()
        {
            _stage.SetSaveContext(_title, "other", "/content/scripts/other-slot-stats.lvn");
            var before = State();
            Assert.IsFalse(_stage.LoadFromSlot("old"));
            _load = _stage.LoadFromSlotAsync("old");
            yield return WaitFor(() => _load.IsCompleted);
            Assert.IsFalse(_load.Result);
            Assert.AreEqual(before, State());
            Assert.AreEqual(0, _resumes);
            Assert.AreEqual(0, _store.Reads);
        }

        [UnityTest]
        public IEnumerator CrossChapterLoaderReceivesLiveStatsAndSavedNovelState()
        {
            _stage.SetSaveContext(_title, "other", "/content/scripts/other-slot-stats.lvn");
            _stage.Play(@"{""script"":[{""op"":""say"",""text"":""other chapter""}]}");
            yield return WaitFor(() => HasLine("other chapter"));
            int calls = 0;
            _stage.CrossChapterLoader = slot =>
            {
                calls++;
                Assert.AreEqual(ScriptUrl, slot.Snap.ScriptUrl);
                Assert.AreEqual("chapter", slot.ChapterId);
                Assert.AreEqual(9, (int)slot.Snap.Vars["global"]["rep"]);
                Assert.AreEqual(2, (int)slot.Snap.Vars["local"]["score"]);
                _stage.SetSaveContext(_title, slot.ChapterId, slot.Snap.ScriptUrl);
                _stage.Play(Script, warmIntroSpine: false);
                _stage.RestoreSnapshot(slot.Snap);
                return Task.FromResult(true);
            };
            _load = _stage.LoadFromSlotAsync("old");
            Assert.IsFalse(_load.IsCompleted);
            Assert.AreEqual(0, calls);
            _reply.SetResult(new JObject { ["rep"] = 9 });
            yield return WaitFor(() => _load.IsCompleted);
            Assert.IsTrue(_load.Result);
            yield return WaitFor(() => HasLine("saved"));
            Assert.AreEqual(1, calls);
            AssertRestored(9);
        }

        [UnityTest]
        public IEnumerator FailedStatsReadDoesNotRestoreOrFallThroughToAnotherChapter()
        {
            int calls = 0;
            _stage.CrossChapterLoader = slot => { calls++; return Task.FromResult(true); };
            var before = State();
            _load = _stage.LoadFromSlotAsync("old");
            LogAssert.Expect(LogType.Warning, "[lvn] slot load failed: stats read failed");
            _reply.SetException(new InvalidOperationException("stats read failed"));
            yield return WaitFor(() => _load.IsCompleted);
            Assert.IsFalse(_load.Result);
            Assert.AreEqual(before, State());
            Assert.AreEqual(0, _resumes);
            Assert.AreEqual(0, calls);

            _store.Read = Task.FromResult(new JObject { ["rep"] = 9 });
            _load = _stage.LoadFromSlotAsync("old");
            yield return WaitFor(() => _load.IsCompleted);
            Assert.IsTrue(_load.Result, "Failed loading left the stage busy.");
            yield return WaitFor(() => HasLine("saved"));
            AssertRestored(9);
        }

        [UnityTest]
        public IEnumerator ChapterChangeDuringStatsReadRetiresTheLoad()
        {
            _load = _stage.LoadFromSlotAsync("old");
            _stage.SetSaveContext(_title, "other", "/content/scripts/other-slot-stats.lvn");
            _stage.Play(@"{""script"":[{""op"":""say"",""text"":""other chapter""}]}");
            yield return WaitFor(() => HasLine("other chapter"));
            var before = State();
            _reply.SetResult(new JObject { ["rep"] = 9 });
            yield return WaitFor(() => _load.IsCompleted);
            Assert.IsFalse(_load.Result);
            Assert.AreEqual(before, State());
            Assert.AreEqual(0, _resumes);
        }
    }
}
