using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    // Uses the real command dispatch, snapshots and PlayerPrefs, without a panel.
    // Mutation checks for acceptance:
    // - unscoped SaveKey must fail SecondOwnerCannotReadOrOverwriteFirstOwner;
    // - unscoped LoadSlot fallback must fail LegacyFallbackStaysWithItsOwner.
    public class ScriptSaveOwnerTests
    {
        private const string Title = "test-script-save-owner";
        private const string First = "test-script-owner-a";
        private const string Second = "test-script-owner-b";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo KnownKeys = typeof(LvnKeep).GetField(
            "_known", BindingFlags.Static | BindingFlags.NonPublic);
        private const string OldSave =
            "{\"Index\":1,\"CommandCount\":4,\"Vars\":{\"marker\":\"old\"},\"CallStack\":[]}";

        private readonly Dictionary<string, string> _prefs = new Dictionary<string, string>();
        private GameObject _go;
        private VnStage _stage;
        private LvnPlayer _player;
        private string _previousOwner;
        private object _previousKnownKeys;
        private LvnRandom _previousRandom;
        private float _previousCps;
        private string _slot;
        private int _resumes;

        [SetUp]
        public void SetUp()
        {
            _prefs.Clear();
            _previousOwner = LvnKeep.Owner;
            _previousKnownKeys = KnownKeys.GetValue(null);
            _previousRandom = LvnExpression.Random;
            _previousCps = TypewriterClock.GlobalCps;
            LvnExpression.Random = new LvnRandom();
            KeepAndClear("lvn.local.owner");
            KeepAndClear("lvn.local.keys");
            KnownKeys.SetValue(null, null);
            LvnKeep.NoteOwner(First);

            _go = new GameObject("script-save-owner-test");
            _go.SetActive(false); // OnEnable must not build a UI panel in EditMode.
            _stage = _go.AddComponent<VnStage>();
            _player = new LvnPlayer(LvnDocument.Parse(@"{""script"":[
                {""op"":""say"",""text"":""saved line""},
                {""op"":""say"",""text"":""next line""},
                {""op"":""say"",""text"":""no save""},
                {""op"":""say"",""text"":""last line""}]}"), _stage);
            typeof(VnStage).GetField("_player", Private).SetValue(_stage, _player);
            _resumes = 0;
            _stage.Resumed += _ => _resumes++;
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            LvnExpression.Random = _previousRandom;
            TypewriterClock.GlobalCps = _previousCps;
            // Restore both persisted ownership and the in-memory key registry;
            // quick saves and ownership may already belong to another test.
            LvnKeep.NoteOwner(_previousOwner);
            using (LvnKeep.Batch())
                foreach (var pref in _prefs)
                    if (pref.Value == null) LvnKeep.Drop(pref.Key);
                    else LvnKeep.Put(pref.Key, pref.Value);
            KnownKeys.SetValue(null, _previousKnownKeys);
        }

        private void KeepAndClear(string key)
        {
            if (_prefs.ContainsKey(key)) return;
            _prefs.Add(key, LvnKeep.Has(key) ? LvnKeep.Get(key) : null);
            LvnKeep.Drop(key);
        }

        private void Context(string title, string slot)
        {
            _slot = slot;
            _stage.SetSaveContext(title, "chapter", null);
            var name = string.IsNullOrEmpty(slot) ? "quick" : slot;
            var titleName = string.IsNullOrEmpty(title) ? name : title + "_" + name;
            // Cleanup includes the old fallback and both owners, independently
            // of SaveKey, so a broken implementation cannot leave test saves.
            foreach (var id in new[] { name, titleName })
            {
                KeepAndClear("lvn_save_" + id);
                KeepAndClear("lvn_save_" + Second + "." + id);
            }
        }

        private JObject Command(string op)
        {
            var cmd = new JObject { ["op"] = op };
            if (_slot != null) cmd["slot"] = _slot; // null tests an omitted slot.
            return cmd;
        }

        private string SaveKey() => (string)typeof(VnStage).GetMethod("SaveKey", Private)
            .Invoke(_stage, new object[] { Command("save") });

        private void Save(string marker)
        {
            _player.ContinueFrom(0);
            _player.Vars["marker"] = marker;
            _stage.ApplyStage(Command("save"));
        }

        private void AssertLoad(string expected)
        {
            _player.ContinueFrom(0);
            _player.Vars["marker"] = "unsaved";
            var before = _resumes;
            _stage.ApplyStage(Command("load"));
            // There are no assets, renderer or pending builds: restoration
            // completes synchronously. Check completion as well as variables.
            Assert.AreEqual(expected ?? "unsaved", (string)_player.Vars["marker"]);
            Assert.AreEqual(before + (expected == null ? 0 : 1), _resumes);
            Assert.AreEqual(expected == null ? 3 : 1, _player.Index,
                "missing saves skip load; existing saves resume the saved line");
        }

        [TestCase(Title, "checkpoint", "lvn_save_test-script-save-owner_checkpoint")]
        [TestCase(Title, "", "lvn_save_test-script-save-owner_quick")]
        [TestCase(Title, null, "lvn_save_test-script-save-owner_quick")]
        [TestCase("", "checkpoint", "lvn_save_checkpoint")]
        [TestCase(null, "", "lvn_save_quick")]
        [TestCase(null, null, "lvn_save_quick")]
        public void FirstOwnerLoadsExistingKey(string title, string slot, string oldKey)
        {
            Context(title, slot);
            // Seed the literal pre-fix key, without calling SaveSlot or Scoped.
            LvnKeep.Put(oldKey, OldSave);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(oldKey), Encoding.UTF8.GetBytes(SaveKey()),
                "the first owner's key must remain byte-for-byte identical");
            AssertLoad("old");
            Assert.AreEqual(OldSave, LvnKeep.Get(oldKey), "loading must not migrate or rewrite the save");
            Save("updated");
            Assert.AreEqual("updated", (string)JObject.Parse(LvnKeep.Get(oldKey))["Vars"]["marker"],
                "new saves must still write to the existing key");
        }

        [TestCase(Title, "checkpoint")]
        [TestCase(Title, "")]
        [TestCase(Title, null)]
        [TestCase(null, "checkpoint")]
        [TestCase(null, "")]
        [TestCase(null, null)]
        public void SecondOwnerCannotReadOrOverwriteFirstOwner(string title, string slot)
        {
            Context(title, slot);
            Save("first");
            var firstKey = SaveKey();
            var firstSave = LvnKeep.Get(firstKey);
            Assert.IsNotEmpty(firstSave, "the first owner's save must actually exist");

            LvnKeep.NoteOwner(Second);
            AssertLoad(null);
            Save("second");
            Assert.AreEqual(firstSave, LvnKeep.Get(firstKey), "the second owner overwrote the first save");
            AssertLoad("second");

            LvnKeep.NoteOwner(First);
            AssertLoad("first");
            Assert.AreEqual(firstSave, LvnKeep.Get(firstKey));
        }

        [TestCase("checkpoint")]
        [TestCase("")]
        [TestCase(null)]
        public void LegacyFallbackStaysWithItsOwner(string slot)
        {
            Context(Title, slot);
            var legacyKey = string.IsNullOrEmpty(slot) ? "lvn_save_quick" : "lvn_save_checkpoint";
            LvnKeep.Put(legacyKey, OldSave);
            AssertLoad("old");

            LvnKeep.NoteOwner(Second);
            AssertLoad(null);
            Save("second");
            AssertLoad("second");
            Assert.AreEqual(OldSave, LvnKeep.Get(legacyKey));

            LvnKeep.NoteOwner(First);
            AssertLoad("old");
        }
    }
}
