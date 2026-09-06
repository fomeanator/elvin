using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    // Uses real PlayerPrefs, LocalStateStore and the shell's rollback method.
    // Run in Unity; compiling this fixture alone does not prove persistence.
    public class CheckpointRecoveryTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _host;
        private NovelApp _app;
        private LvnTitle _title;
        private LvnChapter _chapter;
        private LocalStateStore _state;
        private string _entryKey, _backupKey, _slotsKey, _statsKey, _globalKey;
        private string _oldGlobal;
        private PopupScreen _popup;
        private Func<float> _oldClock;

        [SetUp]
        public void SetUp()
        {
            _title = new LvnTitle { id = "test-checkpoints-" + Guid.NewGuid().ToString("N") };
            _chapter = new LvnChapter { id = "ch2", script_url = "/test/ch2.lvn" };
            _entryKey = LvnKeep.Scoped("lvn_entry_", _title.id);
            _backupKey = _entryKey + ".bak";
            _slotsKey = LvnKeep.Scoped("lvn_slots_", _title.id);
            _statsKey = LvnKeep.Scoped("lvn_state_", _title.id);
            _globalKey = LvnKeep.Scoped("lvn_state_", LvnGlobalStats.ScopeId);
            _oldGlobal = LvnKeep.Has(_globalKey) ? LvnKeep.Get(_globalKey) : null;
            _state = new LocalStateStore();

            // Keep the objects inactive: rollback needs SeedVars, not stage UI or boot.
            _host = new GameObject("CheckpointRecoveryTests");
            _host.SetActive(false);
            _app = _host.AddComponent<NovelApp>();
            _app.Stage = _host.AddComponent<VnStage>();
            typeof(NovelApp).GetField("_state", PrivateInstance).SetValue(_app, _state);
        }

        [TearDown]
        public void TearDown()
        {
            if (_popup != null)
            {
                _popup.Hide();
                LvnClock.Now = _oldClock;
                LvnWords.Translate(null);
                LvnWords.Learn(null, null);
                LvnScreenDirector.Current.Reset();
                _popup = null;
            }
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
            foreach (var key in new[] { _entryKey, _backupKey, _slotsKey, _slotsKey + ".bak", _statsKey })
                LvnKeep.Drop(key);
            LvnProgress.ClearRestart(_title.id);
            if (_oldGlobal == null) LvnKeep.Drop(_globalKey);
            else LvnKeep.Put(_globalKey, _oldGlobal);
        }

        private Task<bool> RollBack() => (Task<bool>)typeof(NovelApp)
            .GetMethod("RollBackToEntryAsync", PrivateInstance)
            .Invoke(_app, new object[] { _title, _chapter });

        private Task<bool?> TryEntry() => (Task<bool?>)typeof(NovelApp)
            .GetMethod("TryRollBackToEntryAsync", PrivateInstance)
            .Invoke(_app, new object[] { _title, _chapter });

        private void CreateEntryPopup()
        {
            // Real shell and popup, with instant fades so EditMode needs no frames.
            _oldClock = LvnClock.Now;
            float tick = 0;
            LvnClock.Now = () => tick += 10f;
            LvnWords.Translate(null);
            LvnWords.Learn(null, null);
            LvnScreenDirector.Current.Reset();
            _popup = new PopupScreen(null);
            var shell = _host.AddComponent<NovelShell>();
            typeof(NovelShell).GetProperty("Popup").SetValue(shell, _popup);
            typeof(NovelApp).GetField("_shell", PrivateInstance).SetValue(_app, shell);
        }

        private async Task HoldProgress()
        {
            await _state.SaveVarsAsync(_title.id, new JObject { ["score"] = 91, ["route"] = "held" }, default);
            await _state.SaveVarsAsync(LvnGlobalStats.ScopeId, new JObject { ["score"] = 37 }, default);
            _app.Stage.SeedVars = new JObject { ["score"] = 91, ["route"] = "held" };
            Assert.IsTrue(LvnSaveStore.Put(_title.id, LvnSaveStore.AutoSlot, new LvnSaveSlot
            {
                ChapterId = _chapter.id,
                Snap = new LvnPlayer.LvnSnapshot
                {
                    Index = 42, ScriptUrl = _chapter.script_url, CallStack = new int[0],
                    Vars = new Dictionary<string, JToken> { ["score"] = 91, ["route"] = "held" },
                },
            }), "fixture must hold an actual autosave");
            LvnProgress.RequestRestart(_title.id, _chapter.id);
        }

        [Test]
        public void MissingCheckpointIsDifferentFromUnreadableBlock()
        {
            Assert.IsNull(LvnProgress.Checkpoint(_title.id, _chapter.id));
            LvnKeep.Put(_entryKey, "{}");
            Assert.IsNull(LvnProgress.Checkpoint(_title.id, _chapter.id));
            LvnKeep.Put(_entryKey, "{");
            Assert.Throws<InvalidDataException>(() => LvnProgress.Checkpoint(_title.id, _chapter.id));
        }

        [TestCase("{")]
        [TestCase("")]
        [TestCase("[]")]
        [TestCase("null")]
        [TestCase("{\"ch2\":null}")]
        [TestCase("{\"ch2\":7}")]
        public void MalformedBlockDoesNotMasqueradeAsAMissingEntry(string damaged)
        {
            LvnKeep.Put(_entryKey, damaged);
            Assert.Throws<InvalidDataException>(() => LvnProgress.Checkpoint(_title.id, _chapter.id));
            Assert.AreEqual(damaged, LvnKeep.Get(_entryKey), "retain the raw block for recovery");
            Assert.IsFalse(LvnKeep.Has(_backupKey));
        }

        [Test]
        public void BackupIncludesTheLatestChapterAndSurvivesTheNextWrite()
        {
            LvnProgress.SaveCheckpoint(_title.id, "ch1", new JObject { ["score"] = 3 });
            LvnProgress.SaveCheckpoint(_title.id, _chapter.id, new JObject { ["score"] = 12 });
            Assert.AreEqual(LvnKeep.Get(_entryKey), LvnKeep.Get(_backupKey), "backup must not lag a write behind");
            LvnKeep.Put(_entryKey, "{");

            Assert.AreEqual(3, (int)LvnProgress.Checkpoint(_title.id, "ch1")["score"]);
            Assert.AreEqual(12, (int)LvnProgress.Checkpoint(_title.id, _chapter.id)["score"]);
            LvnProgress.SaveCheckpoint(_title.id, "ch3", new JObject { ["score"] = 20 });
            LvnKeep.Drop(_backupKey); // read the newly written primary, not the fallback
            Assert.AreEqual(3, (int)LvnProgress.Checkpoint(_title.id, "ch1")["score"]);
            Assert.AreEqual(12, (int)LvnProgress.Checkpoint(_title.id, _chapter.id)["score"]);
            Assert.AreEqual(20, (int)LvnProgress.Checkpoint(_title.id, "ch3")["score"]);
        }

        [TestCase(null)]
        [TestCase("{")]
        public async Task FailedRecoveryPreservesAutosaveStatsAndRestartOnEveryAttempt(string backup)
        {
            await HoldProgress();
            LvnKeep.Put(_entryKey, "{");
            if (backup != null) LvnKeep.Put(_backupKey, backup);
            var slots = LvnKeep.Get(_slotsKey);
            var slotBackup = LvnKeep.Get(_slotsKey + ".bak");
            var stats = LvnKeep.Get(_statsKey);
            var global = LvnKeep.Get(_globalKey);
            var seed = _app.Stage.SeedVars;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                Exception failure = null;
                try { await RollBack(); }
                catch (Exception e) { failure = e; }
                // Assert the player's data before the error: the old bug must
                // fail on lost progress, not merely on a different return value.
                Assert.AreEqual(slots, LvnKeep.Get(_slotsKey), "restart destroyed the held autosave");
                Assert.AreEqual(slotBackup, LvnKeep.Get(_slotsKey + ".bak"));
                Assert.AreEqual(stats, LvnKeep.Get(_statsKey), "restart overwrote saved stats");
                Assert.AreEqual(global, LvnKeep.Get(_globalKey));
                Assert.AreSame(seed, _app.Stage.SeedVars, "failed rollback changed the live seed");
                Assert.AreEqual(_chapter.id, LvnProgress.PendingRestart(_title.id), "retry must not bypass recovery");
                Assert.IsInstanceOf<InvalidDataException>(failure, "failure must be explicit");
            }
        }

        [TestCase(null, false)]
        [TestCase("{", false)]
        [TestCase(null, true)]
        [TestCase("{", true)]
        public async Task CancelledEntryExplainsRecoveryBeforeReturningToMenu(string backup, bool translated)
        {
            CreateEntryPopup();
            const string recoveryTitle = "Chapter can't open yet";
            const string recoveryMessage = "Your progress is safe. A supporting save record is damaged, so this chapter can't open yet. Please contact support for help recovering it.";
            const string translatedTitle = "Saved progress needs attention";
            const string translatedMessage = "Saved progress is retained. Contact support about the damaged record.";
            if (translated)
                LvnWords.Translate(new Dictionary<string, string>
                {
                    ["chapter.checkpoint_recovery_title"] = translatedTitle,
                    ["chapter.checkpoint_recovery"] = translatedMessage,
                });

            await HoldProgress();
            LvnKeep.Put(_entryKey, "{");
            if (backup != null) LvnKeep.Put(_backupKey, backup);
            var slots = LvnKeep.Get(_slotsKey);
            var stats = LvnKeep.Get(_statsKey);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                var entry = TryEntry();
                try
                {
                    Assert.IsTrue(_popup.IsOpen, "cancelled entry must explain the failure to the player");
                    Assert.AreEqual(DisplayStyle.Flex, _popup.style.display.value);
                    Assert.AreEqual(translated ? translatedTitle : recoveryTitle,
                        _popup.Q<Label>("popup-title").text);
                    Assert.AreEqual(translated ? translatedMessage : recoveryMessage,
                        _popup.Q<Label>("popup-message").text);
                    Assert.IsFalse(entry.IsCompleted, "the menu must wait for acknowledgement");
                }
                finally { _popup.Hide(); }

                Assert.IsNull(await entry, "acknowledging the explanation must still cancel entry");
                Assert.AreEqual(slots, LvnKeep.Get(_slotsKey), "the explanation must preserve the autosave");
                Assert.AreEqual(stats, LvnKeep.Get(_statsKey));
                Assert.AreEqual(_chapter.id, LvnProgress.PendingRestart(_title.id));
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task HealthyOrRecoveredEntryShowsNoRecoveryExplanation(bool restart, bool corruptPrimary)
        {
            CreateEntryPopup();
            if (restart)
            {
                await HoldProgress();
                LvnProgress.SaveCheckpoint(_title.id, _chapter.id, new JObject { ["score"] = 12 });
                if (corruptPrimary) LvnKeep.Put(_entryKey, "{");
            }

            var entry = TryEntry();
            try
            {
                Assert.IsFalse(_popup.IsOpen, "normal entry and successful backup recovery need no alert");
                Assert.AreEqual(DisplayStyle.None, _popup.style.display.value);
                Assert.AreEqual(restart, await entry);
            }
            finally { _popup.Hide(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HealthyOrRecoveredRestartRestoresEntryStatsAndKeepsLiveGlobals(bool corruptPrimary)
        {
            await HoldProgress();
            LvnProgress.SaveCheckpoint(_title.id, "ch1", new JObject { ["score"] = 3 });
            LvnProgress.SaveCheckpoint(_title.id, _chapter.id, new JObject
            {
                ["score"] = 12, ["route"] = "entry", ["global"] = new JObject { ["score"] = 1 },
            });
            if (corruptPrimary) LvnKeep.Put(_entryKey, "{");
            else LvnKeep.Drop(_backupKey); // healthy legacy blocks have no backup yet

            Assert.IsTrue(await RollBack());
            var saved = await _state.LoadVarsAsync(_title.id, default);
            Assert.AreEqual(12, (int)saved["score"], "entry stats must survive a corrupt primary");
            Assert.AreEqual("entry", (string)saved["route"]);
            Assert.IsNull(saved["global"], "global stats must stay in their own scope");
            Assert.AreEqual(37, (int)_app.Stage.SeedVars["global"]["score"]);
            Assert.AreEqual(37, (int)(await _state.LoadVarsAsync(LvnGlobalStats.ScopeId, default))["score"]);
            Assert.IsNull(LvnSaveStore.Get(_title.id, LvnSaveStore.AutoSlot));
            Assert.IsEmpty(LvnProgress.PendingRestart(_title.id));
            Assert.AreEqual(12, (int)LvnProgress.Checkpoint(_title.id, _chapter.id)["score"]);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task TrulyMissingEntryStillRestartsWithEmptyTitleStats(bool hasOtherChapter)
        {
            await HoldProgress();
            if (hasOtherChapter) LvnProgress.SaveCheckpoint(_title.id, "ch1", new JObject { ["score"] = 3 });
            Assert.IsTrue(await RollBack());
            Assert.IsEmpty(await _state.LoadVarsAsync(_title.id, default));
            Assert.AreEqual(37, (int)_app.Stage.SeedVars["global"]["score"]);
            Assert.IsNull(LvnSaveStore.Get(_title.id, LvnSaveStore.AutoSlot));
        }

        [TestCase(null)]
        [TestCase("{")]
        public void NewCheckpointCannotOverwriteUnrecoverableBlock(string backup)
        {
            LvnKeep.Put(_entryKey, "{");
            if (backup != null) LvnKeep.Put(_backupKey, backup);
            LvnProgress.SaveCheckpoint(_title.id, "ch3", new JObject { ["score"] = 20 });
            Assert.AreEqual("{", LvnKeep.Get(_entryKey));
            Assert.AreEqual(backup != null, LvnKeep.Has(_backupKey));
            if (backup != null) Assert.AreEqual(backup, LvnKeep.Get(_backupKey));
        }

        [Test]
        public void FullResetDropsTheBackupAsWell()
        {
            LvnProgress.SaveCheckpoint(_title.id, _chapter.id, new JObject { ["score"] = 12 });
            Assert.IsTrue(LvnKeep.Has(_backupKey));
            LvnProgress.ResetTitle(_title.id);
            Assert.IsFalse(LvnKeep.Has(_entryKey));
            Assert.IsFalse(LvnKeep.Has(_backupKey));
            Assert.IsNull(LvnProgress.Checkpoint(_title.id, _chapter.id));
        }
    }
}
