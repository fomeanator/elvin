using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Lvn.Tests
{
    /// <summary>Exercise the real menu callbacks and persistent save path.
    /// Replacing the occupancy check with slot?.Snap != null must fail the
    /// newer-save tests: the first tap would write before consent.</summary>
    public class SaveMenuTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;
        private StageMenu _menu;
        private string _title;
        private int _saves;
        private string Key => LvnKeep.Scoped("lvn_slots_", _title);
        private string SlotLabel => _stage.Theme.Word("slot", "Slot") + " 1";

        [UnitySetUp]
        public IEnumerator Boot()
        {
            _title = "test-save-menu-" + Guid.NewGuid().ToString("N");
            _stage = TestStage.Panel("save-menu-stage", out _go, out _panel);
            _stage.SetSaveContext(_title, "chapter", "/content/test-save-menu.lvn");
            yield return null;
            _stage.Play("{\"script\":[{\"op\":\"say\",\"text\":\"current line\"}]}");
            yield return null;
            _saves = 0;
            _stage.Saved += _ => _saves++;
            _menu = (StageMenu)typeof(VnStage).GetField("_menu", Private).GetValue(_stage);
            Assert.IsNotNull(_menu);
            _menu.Open();
            yield return null;
            yield return null; // screen capture completes before opening the sheet
            Assert.IsNotNull(typeof(StageMenu).GetField("_scrim", Private).GetValue(_menu));
        }

        [TearDown]
        public void Cleanup()
        {
            _menu?.Close();
            if (_go != null) Object.DestroyImmediate(_go);
            if (_panel != null) Object.DestroyImmediate(_panel);
            if (_title != null) LvnSaveStore.DeleteAll(_title);
        }

        private void ShowSlots(bool saveMode) => typeof(StageMenu).GetMethod("ShowSlots", Private)
            .Invoke(_menu, new object[] { saveMode });

        private Button SlotButton() => _menu.Query<Button>().ToList().Single(b =>
            b.Query<Label>().ToList().Any(l => l.text.StartsWith(SlotLabel + "   ")));

        private Button ActionButton(string key, string fallback) => _menu.Query<Button>().ToList()
            .SingleOrDefault(b => b.text == _stage.Theme.Word(key, fallback));

        private static void Press(Button button)
        {
            Assert.IsNotNull(button, "the expected menu button is missing");
            var click = TestStage.Press(button);
            Assert.IsNotNull(click, "could not reach the real button callback");
            click();
        }

        private string SeedFutureSave()
        {
            // The future payload deliberately cannot deserialize as today's
            // snapshot. Opening the menu must leave both stored copies intact.
            var json = new JObject
            {
                ["slot1"] = new JObject
                {
                    ["Version"] = LvnSaveSlot.CurrentVersion + 1,
                    ["Snap"] = new JArray("future snapshot"),
                    ["FutureData"] = "must survive until confirmed"
                }
            }.ToString();
            LvnKeep.Put(Key, json);
            LvnKeep.Put(Key + ".bak", json);
            return json;
        }

        private void AssertFutureUnchanged(string json)
        {
            Assert.AreEqual(0, _saves, "the first tap must not save over future data");
            Assert.AreEqual(json, LvnKeep.Get(Key), "the stored save changed before consent");
            Assert.AreEqual(json, LvnKeep.Get(Key + ".bak"), "the backup changed before consent");
        }

        [Test]
        public void EmptySlotSavesOnTheFirstTap()
        {
            ShowSlots(true);
            StringAssert.Contains(_stage.Theme.Word("empty", "— empty —"), SlotButton().Q<Label>().text);
            Press(SlotButton());

            Assert.AreEqual(1, _saves);
            Assert.IsNotNull(LvnSaveStore.Get(_title, "slot1")?.Snap);
            Assert.IsNull(ActionButton("overwrite", "Overwrite"), "empty slots need no confirmation");
        }

        [Test]
        public void ReadableSaveStillRequiresConfirmation()
        {
            Assert.IsTrue(_stage.SaveToSlot("slot1"));
            _saves = 0;
            var before = LvnKeep.Get(Key);
            ShowSlots(true);
            Press(SlotButton());

            Assert.AreEqual(0, _saves);
            Assert.AreEqual(before, LvnKeep.Get(Key));
            Assert.IsNotNull(ActionButton("overwrite", "Overwrite"));
            Assert.That(_menu.Query<Label>().ToList().Select(l => l.text), Does.Contain(
                string.Format(_stage.Theme.Word("overwrite_q", "Overwrite {0}?"), SlotLabel)));
        }

        [Test]
        public void FutureSaveIsLabelledAndOnlyReplacedAfterConfirmation()
        {
            var before = SeedFutureSave();
            ShowSlots(true);
            StringAssert.Contains("Save from a newer app version", SlotButton().Q<Label>().text);
            StringAssert.DoesNotContain(_stage.Theme.Word("empty", "— empty —"), SlotButton().Q<Label>().text);
            Press(SlotButton());

            AssertFutureUnchanged(before);
            Assert.That(_menu.Query<Label>().ToList().Select(l => l.text), Does.Contain(
                SlotLabel + " contains a save from a newer version of the app. Overwrite it?"));
            Press(ActionButton("cancel", "Cancel"));
            AssertFutureUnchanged(before);

            Press(SlotButton());
            Press(ActionButton("overwrite", "Overwrite"));
            Assert.AreEqual(1, _saves);
            var saved = LvnSaveStore.Get(_title, "slot1");
            Assert.IsNotNull(saved?.Snap);
            Assert.AreEqual(LvnSaveSlot.CurrentVersion, saved.Version);
            Assert.IsNull(JObject.Parse(LvnKeep.Get(Key))["slot1"]["FutureData"]);
        }

        [Test]
        public void FutureSaveStaysUnavailableInTheLoadMenu()
        {
            var before = SeedFutureSave();
            ShowSlots(false);

            Assert.IsFalse(SlotButton().enabledSelf);
            Assert.IsFalse(_stage.CanLoadSlot("slot1"));
            Assert.IsFalse(_stage.LoadFromSlot("slot1"));
            Assert.IsFalse(LvnSaveStore.Slots(_title).ContainsKey("slot1"));
            AssertFutureUnchanged(before);
        }

        [Test]
        public void SaveArrivingAfterTheListOpenedStillRequiresConfirmation()
        {
            ShowSlots(true);
            var before = SeedFutureSave();
            Press(SlotButton());

            AssertFutureUnchanged(before);
            Assert.IsNotNull(ActionButton("overwrite", "Overwrite"));
        }

        [Test]
        public void FutureSaveLabelAndQuestionUseTheThemeWords()
        {
            SeedFutureSave();
            _stage.Theme.MenuLabels = new Dictionary<string, string>
            {
                ["save_newer_version"] = "Сохранение из новой версии",
                ["overwrite_newer_q"] = "В {0} сохранение из новой версии приложения. Заменить?"
            };
            ShowSlots(true);
            StringAssert.Contains("Сохранение из новой версии", SlotButton().Q<Label>().text);
            Press(SlotButton());
            Assert.That(_menu.Query<Label>().ToList().Select(l => l.text), Does.Contain(
                "В " + SlotLabel + " сохранение из новой версии приложения. Заменить?"));
            Assert.AreEqual(0, _saves);
        }
    }
}
