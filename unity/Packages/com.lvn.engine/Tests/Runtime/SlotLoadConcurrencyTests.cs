using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Lvn.UI;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    public class SlotLoadConcurrencyTests
    {
        private const string Title = "slot-load-concurrency-test";
        private const string RemoteScript = @"{""scene"":""remote"",""script"":[
            {""op"":""say"",""text"":""Remote first""},
            {""op"":""say"",""text"":""Remote second""},
            {""op"":""say"",""text"":""Remote third""}]}";
        private const string CurrentScript = @"{""scene"":""current"",""script"":[
            {""op"":""say"",""text"":""Current first""},
            {""op"":""say"",""text"":""Current second""},
            {""op"":""say"",""text"":""Current third""}]}";

        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;
        private TaskCompletionSource<bool> _hostReply;
        private Task<bool> _activeLoad;
        private int _hostCalls;
        private int _resumes;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _hostCalls = 0;
            _resumes = 0;
            _activeLoad = null;
            _hostReply = new TaskCompletionSource<bool>();
            LvnSaveStore.DeleteAll(Title);
            _stage = TestStage.Panel("slot-load-concurrency-stage", out _go, out _panel);
            yield return null;

            _stage.SetSaveContext(Title, "remote", "/content/scripts/load-test-remote.lvn");
            _stage.Play(RemoteScript);
            yield return WaitFor(() => HasLine("Remote first"));
            Assert.IsTrue(_stage.SaveToSlot("remote-first"));
            _stage.Player.Advance();
            yield return WaitFor(() => HasLine("Remote second"));
            Assert.IsTrue(_stage.SaveToSlot("remote-second"));

            _stage.SetSaveContext(Title, "current", "/content/scripts/load-test-current.lvn");
            _stage.Play(CurrentScript);
            yield return WaitFor(() => HasLine("Current first"));
            _stage.Player.Vars["checkpoint"] = "saved";
            Assert.IsTrue(_stage.SaveToSlot("current"));
            _stage.Player.Advance();
            yield return WaitFor(() => HasLine("Current second"));
            _stage.Player.Vars["checkpoint"] = "live";

            _stage.Resumed += _ => _resumes++;
            _stage.CrossChapterLoader = slot =>
            {
                _hostCalls++;
                return _hostReply.Task;
            };
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // Release pending work even when an assertion failed, before
            // destroying the panel its continuation could still reference.
            _hostReply?.TrySetResult(false);
            if (_activeLoad != null) yield return WaitFor(() => _activeLoad.IsCompleted);
            _stage?.ClearStage();
            _stage = null;
            yield return null;
            LvnSaveStore.DeleteAll(Title);
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

        private void StartPendingLoad()
        {
            int calls = _hostCalls;
            _activeLoad = _stage.LoadFromSlotAsync("remote-first");
            Assert.AreEqual(calls + 1, _hostCalls, "The stage did not call the host.");
            Assert.IsFalse(_activeLoad.IsCompleted, "The load must wait for the host reply.");
        }

        private IEnumerator RejectConcurrentLoad(string slot)
        {
            StartPendingLoad();
            var player = _stage.Player;
            var snapshot = JsonConvert.SerializeObject(player.Save());
            var backlog = _stage.Backlog.ToArray();

            var second = _stage.LoadFromSlotAsync(slot);
            Assert.AreEqual(TaskStatus.RanToCompletion, second.Status,
                "A busy stage must reject immediately, without queuing the request.");
            Assert.IsFalse(second.Result, "A second concurrent load was accepted.");
            Assert.AreEqual(1, _hostCalls, "The rejected request reached the host.");
            Assert.IsFalse(_activeLoad.IsCompleted, "Rejecting the second load completed the first.");
            Assert.AreSame(player, _stage.Player, "The rejected request replaced the player.");
            Assert.AreEqual(snapshot, JsonConvert.SerializeObject(_stage.Player.Save()),
                "The rejected request changed the cursor or player state.");
            CollectionAssert.AreEqual(backlog, _stage.Backlog,
                "The rejected request changed the visible dialogue history.");
            Assert.AreEqual(0, _resumes, "The rejected request started a snapshot restore.");

            // A rejection must not release the first caller's ownership.
            var third = _stage.LoadFromSlotAsync("remote-second");
            Assert.AreEqual(TaskStatus.RanToCompletion, third.Status);
            Assert.IsFalse(third.Result, "The rejected request cleared the busy state.");
            Assert.AreEqual(1, _hostCalls);

            _hostReply.SetResult(true);
            yield return WaitFor(() => _activeLoad.IsCompleted);
            Assert.IsTrue(_activeLoad.Result);
        }

        private IEnumerator AssertNextLoadAccepted()
        {
            _hostReply = new TaskCompletionSource<bool>();
            int calls = _hostCalls;
            _activeLoad = _stage.LoadFromSlotAsync("remote-second");
            Assert.AreEqual(calls + 1, _hostCalls, "The completed load left the stage busy.");
            Assert.IsFalse(_activeLoad.IsCompleted, "The next load did not wait for its own host reply.");
            _hostReply.SetResult(true);
            yield return WaitFor(() => _activeLoad.IsCompleted);
            Assert.IsTrue(_activeLoad.Result);
        }

        [UnityTest]
        public IEnumerator ConcurrentCrossChapterLoadIsRejectedWithoutChangingStage()
        {
            yield return RejectConcurrentLoad("remote-second");
        }

        [UnityTest]
        public IEnumerator ConcurrentCurrentChapterLoadIsRejectedWithoutChangingStage()
        {
            yield return RejectConcurrentLoad("current");
        }

        [UnityTest]
        public IEnumerator LoadAcceptedAfterHostSuccess()
        {
            StartPendingLoad();
            _hostReply.SetResult(true);
            yield return WaitFor(() => _activeLoad.IsCompleted);
            Assert.IsTrue(_activeLoad.Result);
            yield return AssertNextLoadAccepted();
        }

        [UnityTest]
        public IEnumerator LoadAcceptedAfterHostRefusal()
        {
            StartPendingLoad();
            _hostReply.SetResult(false);
            yield return WaitFor(() => _activeLoad.IsCompleted);
            Assert.IsFalse(_activeLoad.Result);
            yield return AssertNextLoadAccepted();
        }

        [UnityTest]
        public IEnumerator LoadAcceptedAfterHostException()
        {
            StartPendingLoad();
            LogAssert.Expect(LogType.Warning, "[lvn] cross-chapter load failed: test loader failure");
            _hostReply.SetException(new InvalidOperationException("test loader failure"));
            yield return WaitFor(() => _activeLoad.IsCompleted);
            Assert.AreEqual(TaskStatus.RanToCompletion, _activeLoad.Status,
                "A host exception must become a failed load.");
            Assert.IsFalse(_activeLoad.Result);
            yield return AssertNextLoadAccepted();
        }

        [UnityTest]
        public IEnumerator LoadAcceptedAfterMissingSlot()
        {
            var missing = _stage.LoadFromSlotAsync("missing");
            yield return WaitFor(() => missing.IsCompleted);
            Assert.IsFalse(missing.Result);
            Assert.AreEqual(0, _hostCalls);
            yield return AssertNextLoadAccepted();
        }
    }
}
