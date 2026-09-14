using System.Collections.Generic;
using Lvn.UI;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class CutsceneCollectionTests
    {
        private const string Title = "test-cutscene-title";
        private const string OtherTitle = "test-cutscene-other";
        private long _now;

        [SetUp]
        public void SetUp()
        {
            LvnCutsceneStore.Clear(Title);
            LvnCutsceneStore.Clear(OtherTitle);
            _now = 10000;
            LvnCutsceneStore.Now = () => _now;
        }

        [TearDown]
        public void Clean()
        {
            LvnCutsceneStore.Clear(Title);
            LvnCutsceneStore.Clear(OtherTitle);
            LvnCutsceneStore.Now = () => System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        [TestCase(0)]
        [TestCase(1801)]
        [TestCase(86400)]
        [TestCase(-5000)]
        public void RevisitingSceneReusesCardAcrossTimeAndReload(int seconds)
        {
            var first = LvnCutsceneStore.Lived(Title, "meet", "Meeting", "ch0", "old.jpg");
            _now += seconds;
            LvnCutsceneStore.Seens(OtherTitle);
            var again = LvnCutsceneStore.Lived(Title, "meet", "Meeting", "ch0", "new.jpg");
            Assert.AreEqual(first, again);
            LvnCutsceneStore.Seens(OtherTitle);
            var cards = LvnCutsceneStore.Seens(Title);
            Assert.AreEqual(1, cards.Count);
            Assert.AreEqual("new.jpg", cards[0].Poster);
        }

        [Test]
        public void DistinctChaptersAndTitlesKeepTheirScenes()
        {
            var first = LvnCutsceneStore.Lived(Title, "meet", "First", "ch0");
            var second = LvnCutsceneStore.Lived(Title, "meet", "Second", "ch1");
            LvnCutsceneStore.Lived(OtherTitle, "meet", "Other", "ch0");
            LvnCutsceneStore.Dress(Title, second, "second.jpg");
            var cards = LvnCutsceneStore.Seens(Title);
            Assert.AreEqual(2, cards.Count);
            Assert.AreEqual("second.jpg", cards.Find(c => c.Key == second).Poster);
            Assert.IsNull(cards.Find(c => c.Key == first).Poster);
            Assert.AreEqual(1, LvnCutsceneStore.Seens(OtherTitle).Count);
        }

        [Test]
        public void UnlockingMoreThanSixtyScenesNeverEvictsMemories()
        {
            for (int i = 0; i < 100; i++)
            {
                LvnCutsceneStore.Lived(Title, "scene-" + i, "Scene " + i, "ch0");
                _now += 86400;
            }
            LvnCutsceneStore.Seens(OtherTitle);
            var cards = LvnCutsceneStore.Seens(Title);
            Assert.AreEqual(100, cards.Count);
            Assert.AreEqual("scene-99", cards[0].Id);
            Assert.IsTrue(cards.Exists(c => c.Id == "scene-0"));
        }

        [Test]
        public void ExistingDuplicateCardsCollapseWithoutDeletingSavedRecords()
        {
            var records = new Dictionary<string, LvnCutsceneStore.Seen>
            {
                ["meet#1"] = new LvnCutsceneStore.Seen { Id = "meet", Chapter = "ch0", At = 1, Poster = "first.jpg" },
                ["meet#2"] = new LvnCutsceneStore.Seen { Id = "meet", Chapter = "ch0", At = 2, Poster = "second.jpg" },
                ["other"] = new LvnCutsceneStore.Seen { Id = "other", Chapter = "ch0", At = 3 },
            };
            var storage = LvnKeep.Scoped("lvn.cutscenes.", Title);
            LvnKeep.Put(storage, JsonConvert.SerializeObject(records));
            var cards = LvnCutsceneStore.Seens(Title);
            Assert.AreEqual(2, cards.Count);
            Assert.AreEqual("meet#2", cards.Find(c => c.Id == "meet").Key);
            Assert.AreEqual("meet#2", LvnCutsceneStore.Lived(Title, "meet", "Meeting", "ch0"));
            var saved = JsonConvert.DeserializeObject<Dictionary<string, LvnCutsceneStore.Seen>>(LvnKeep.Get(storage, ""));
            Assert.AreEqual(3, saved.Count, "Old records are retained; no destructive migration.");
            Assert.AreEqual("first.jpg", saved["meet#1"].Poster);
        }

        [Test]
        public void LegacyCardWithoutKeySurvivesAndIsReused()
        {
            LvnKeep.Put(LvnKeep.Scoped("lvn.cutscenes.", Title),
                "{\"meet\":{\"Id\":\"meet\",\"Name\":\"Meeting\",\"Chapter\":\"ch0\"}}");
            Assert.AreEqual("meet", LvnCutsceneStore.Lived(Title, "meet", "Meeting", "ch0"));
            Assert.AreEqual(1, LvnCutsceneStore.Seens(Title).Count);
        }
    }
}
