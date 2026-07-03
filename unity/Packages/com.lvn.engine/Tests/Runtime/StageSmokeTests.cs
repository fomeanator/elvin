using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lvn;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// Headless PlayMode smoke over a REAL stage — a live UIDocument panel, the
    /// full VnStage/StageMenu chrome, no ILvnStage fakes. Covers the behaviors
    /// that only exist in the assembled UI: the choice a player picks landing in
    /// the History backlog (and surviving/clearing correctly across rollback),
    /// and History rendering the mark as its own accented line.
    ///
    ///   Unity -batchmode -nographics -projectPath unity/TestHost \
    ///         -runTests -testPlatform PlayMode
    /// </summary>
    public class StageSmokeTests
    {
        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;

        private const string Script = @"{
          ""scene"": ""smoke"",
          ""script"": [
            { ""op"": ""say"", ""who"": ""A"", ""text"": ""line one"" },
            { ""op"": ""choice"", ""options"": [
              { ""text"": ""take the left path"", ""goto"": ""L"" },
              { ""text"": ""take the right path"", ""goto"": ""R"" } ] },
            { ""op"": ""label"", ""id"": ""L"" },
            { ""op"": ""say"", ""text"": ""left it is"" },
            { ""op"": ""label"", ""id"": ""R"" },
            { ""op"": ""say"", ""text"": ""right it is"" }
          ]
        }";

        [UnitySetUp]
        public IEnumerator Boot()
        {
            _panel = ScriptableObject.CreateInstance<PanelSettings>();
            _go = new GameObject("smoke-stage");
            var doc = _go.AddComponent<UIDocument>();
            doc.panelSettings = _panel;
            _stage = _go.AddComponent<VnStage>();
            yield return null; // UIDocument brings its panel up on its own OnEnable
            _stage.Play(Script);
            yield return null; // first beat renders
        }

        [TearDown]
        public void Cleanup()
        {
            if (_go != null) Object.Destroy(_go);
            if (_panel != null) Object.Destroy(_panel);
        }

        // The production entry point ChoiceList fires on a button click.
        private void Pick(int optionListPosition)
        {
            var cur = (IReadOnlyList<LvnOption>)typeof(VnStage)
                .GetField("_curChoices", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(_stage);
            Assert.IsNotNull(cur, "a choice must be on screen");
            typeof(VnStage)
                .GetMethod("OnChoiceSelected", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_stage, new object[] { cur[optionListPosition].Index });
        }

        [UnityTest]
        public IEnumerator PickedChoice_IsMarkedInBacklog_AndHistoryRendersIt()
        {
            Assert.AreEqual(1, _stage.Backlog.Count, "the opening line is on screen");

            _stage.Player.Advance();     // tap through the say → options present
            yield return null;
            Pick(0);                     // take the left path
            yield return null;

            var marks = _stage.Backlog.Where(b => b.style == "choice").ToList();
            Assert.AreEqual(1, marks.Count, "exactly one branch mark recorded");
            Assert.AreEqual("take the left path", marks[0].text);
            Assert.AreEqual("left it is", _stage.Backlog.Last().text, "the branch actually played");

            // History renders the mark as its own ▸ line on the real panel —
            // through the production path: open the sheet, then the History view.
            var menu = (StageMenu)typeof(VnStage)
                .GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(_stage);
            typeof(StageMenu)
                .GetMethod("OpenSheet", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(menu, null);
            typeof(StageMenu)
                .GetMethod("ShowHistory", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(menu, null);
            yield return null;
            var arrowed = menu.Query<Label>().ToList()
                .Where(l => l.text != null && l.text.StartsWith("▸")).ToList();
            Assert.AreEqual(1, arrowed.Count, "History shows the picked branch as a ▸ line");
            StringAssert.Contains("take the left path", arrowed[0].text);
        }

        [UnityTest]
        public IEnumerator DisableEnable_RebuildsTheChrome_AndReshowsTheBeat()
        {
            // The historical "black screen": UIDocument brings up a FRESH empty
            // root after a disable/enable cycle, and a _built guard used to skip
            // the rebuild — a permanently blank stage over a live player.
            _go.SetActive(false);
            yield return null;
            _go.SetActive(true);
            yield return null;
            yield return null; // panel may need a frame; Update() retries Build

            var doc = _go.GetComponent<UIDocument>();
            Assert.Greater(doc.rootVisualElement.childCount, 0, "chrome rebuilt on the new panel root");

            var labels = doc.rootVisualElement.Query<Label>().ToList()
                .Where(l => l.text != null &&
                    System.Text.RegularExpressions.Regex.Replace(l.text, "<[^>]+>", "").Contains("line one"))
                .ToList();
            Assert.IsTrue(labels.Count > 0, "the current beat re-rendered after re-enable");
            Assert.AreEqual(1, _stage.Backlog.Count(b => b.text == "line one"),
                "the re-run beat did not duplicate its backlog entry");

            // and the story still advances
            _stage.Player.Advance();
            yield return null;
            Assert.IsNotNull(_stage.Backlog, "player alive after rebuild");
        }

        [UnityTest]
        public IEnumerator SaveSlots_RenderTheThumbnail()
        {
            // Occupy slot1 through the real save path, then give it a thumbnail
            // (headless runs skip the screen capture — the file is the contract).
            _stage.SetSaveContext("smoke-thumb", "ch1", "/content/x.lvn");
            Assert.IsTrue(_stage.SaveToSlot("slot1"));
            var tex = new Texture2D(8, 4, TextureFormat.RGBA32, false);
            LvnSaveStore.WriteThumb("smoke-thumb", "slot1", tex);
            Object.Destroy(tex);

            var menu = (StageMenu)typeof(VnStage)
                .GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_stage);
            typeof(StageMenu).GetMethod("OpenSheet", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(menu, null);
            typeof(StageMenu).GetMethod("ShowSlots", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(menu, new object[] { true });
            yield return null;

            var thumbs = menu.Query<Image>("slot-thumb").ToList();
            Assert.AreEqual(1, thumbs.Count, "exactly the occupied slot with a file shows a thumbnail");

            // cleanup the on-disk artifacts of this test
            LvnSaveStore.WriteThumb("smoke-thumb", "slot1", null);
            PlayerPrefs.DeleteKey("lvn_slots_smoke-thumb");
        }

        [UnityTest]
        public IEnumerator RollbackSteps_JumpsSeveralBeatsInOneHop()
        {
            const string script = @"{""script"":[
                {""op"":""say"",""text"":""one""},
                {""op"":""say"",""text"":""two""},
                {""op"":""say"",""text"":""three""},
                {""op"":""say"",""text"":""four""}
            ]}";
            _stage.Play(script);
            yield return null;
            _stage.Player.Advance(); // two
            _stage.Player.Advance(); // three
            _stage.Player.Advance(); // four
            yield return null;
            Assert.AreEqual("four", _stage.Backlog.Last().text);

            // Two beats back in ONE hop — the History tap-to-return path.
            Assert.IsTrue(_stage.RollbackSteps(2));
            yield return null;
            Assert.AreEqual("two", _stage.Backlog.Last().text, "landed two beats back");
            Assert.AreEqual(2, _stage.Backlog.Count, "undone beats left the history");

            // The story continues forward from there without duplicates.
            _stage.Player.Advance();
            yield return null;
            Assert.AreEqual("three", _stage.Backlog.Last().text);
            Assert.AreEqual(1, _stage.Backlog.Count(b => b.text == "three"));
        }

        [UnityTest]
        public IEnumerator GalleryCg_UnlocksOnMatchingBg_AndGridShowsLockedAsQuestion()
        {
            const string title = "smoke-gallery";
            LvnGalleryStore.Clear(title);
            _stage.SetSaveContext(title, "ch1", "/content/x.lvn");
            _stage.Gallery = new List<Lvn.Content.LvnGalleryItem>
            {
                new Lvn.Content.LvnGalleryItem { id = "cg1", url = "/content/cg/one.png", name = "One" },
                new Lvn.Content.LvnGalleryItem { id = "cg2", url = "/content/cg/two.png" },
            };

            // The script reaches a bg whose url matches an item → unlocked forever,
            // even headless where the sprite itself never loads.
            _stage.ApplyStage(Newtonsoft.Json.Linq.JObject.Parse(
                @"{""op"":""bg"",""sprite_url"":""/content/cg/one.png""}"));
            yield return null;

            Assert.IsTrue(LvnGalleryStore.IsUnlocked(title, "cg1"), "shown CG unlocked");
            Assert.IsFalse(LvnGalleryStore.IsUnlocked(title, "cg2"), "unseen CG stays locked");

            // The grid on the real panel: one open cell, one locked "?" cell.
            var menu = (StageMenu)typeof(VnStage)
                .GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_stage);
            typeof(StageMenu).GetMethod("OpenSheet", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(menu, null);
            typeof(StageMenu).GetMethod("ShowGallery", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(menu, null);
            yield return null;

            var locks = menu.Query<Label>().ToList().Where(l => l.text == "?").ToList();
            Assert.AreEqual(1, locks.Count, "exactly the unseen CG renders locked");
            var captions = menu.Query<Label>().ToList().Where(l => l.text == "One").ToList();
            Assert.AreEqual(1, captions.Count, "the unlocked CG shows its caption");

            LvnGalleryStore.Clear(title);
        }

        [UnityTest]
        public IEnumerator Rollback_StripsTheUndoneMark_AndRepickRecordsFresh()
        {
            _stage.Player.Advance();
            yield return null;
            Pick(0);
            yield return null;
            Assert.AreEqual(1, _stage.Backlog.Count(b => b.style == "choice"));

            Assert.IsTrue(_stage.RollbackStep(), "one beat back from the branch line");
            yield return null;

            Assert.AreEqual(0, _stage.Backlog.Count(b => b.style == "choice"),
                "the undone pick's mark is gone from History");

            Pick(1); // take the other branch this time
            yield return null;
            var marks = _stage.Backlog.Where(b => b.style == "choice").ToList();
            Assert.AreEqual(1, marks.Count, "re-pick records exactly one fresh mark");
            Assert.AreEqual("take the right path", marks[0].text);
            Assert.AreEqual("right it is", _stage.Backlog.Last().text);
        }
    }
}
