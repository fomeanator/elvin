using System.Collections.Generic;
using Lvn;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    public class SaveStoreTests
    {
        private const string TitleA = "test-title-a";
        private const string TitleB = "test-title-b";

        [SetUp]
        [TearDown]
        public void Clean()
        {
            PlayerPrefs.DeleteKey("lvn_slots_" + TitleA);
            PlayerPrefs.DeleteKey("lvn_slots_" + TitleB);
        }

        private static LvnSaveSlot Slot(int index, string preview = "линия")
            => new LvnSaveSlot
            {
                Snap = new LvnPlayer.LvnSnapshot
                {
                    Index = index,
                    Vars = new Dictionary<string, JToken> { ["gold"] = 5 },
                    CallStack = new int[0],
                    ScriptUrl = "/content/scripts/a-ch01.lvn",
                    AnchorLabel = "scene2",
                    AnchorSteps = 3,
                },
                ChapterId = "a-ch01",
                Preview = preview,
            };

        [Test]
        public void RoundtripKeepsSnapshotAndMetadata()
        {
            LvnSaveStore.Put(TitleA, "slot1", Slot(42, "Привет, мир"));

            var got = LvnSaveStore.Get(TitleA, "slot1");
            Assert.IsNotNull(got);
            Assert.AreEqual(42, got.Snap.Index);
            Assert.AreEqual("scene2", got.Snap.AnchorLabel, "the position anchor survives serialization");
            Assert.AreEqual(3, got.Snap.AnchorSteps);
            Assert.AreEqual(5d, (double)got.Snap.Vars["gold"], 0.001);
            Assert.AreEqual("/content/scripts/a-ch01.lvn", got.Snap.ScriptUrl);
            Assert.AreEqual("Привет, мир", got.Preview);
            Assert.Greater(got.SavedAtUnixMs, 0, "Put stamps the save time");
        }

        [Test]
        public void TitlesAreNamespaced()
        {
            LvnSaveStore.Put(TitleA, "slot1", Slot(1));
            LvnSaveStore.Put(TitleB, "slot1", Slot(99));

            Assert.AreEqual(1, LvnSaveStore.Get(TitleA, "slot1").Snap.Index);
            Assert.AreEqual(99, LvnSaveStore.Get(TitleB, "slot1").Snap.Index,
                "two novels on one device never see each other's saves");
        }

        [Test]
        public void DeleteRemovesOnlyThatSlot()
        {
            LvnSaveStore.Put(TitleA, "slot1", Slot(1));
            LvnSaveStore.Put(TitleA, LvnSaveStore.AutoSlot, Slot(7));

            LvnSaveStore.Delete(TitleA, LvnSaveStore.AutoSlot);

            Assert.IsNull(LvnSaveStore.Get(TitleA, LvnSaveStore.AutoSlot));
            Assert.IsNotNull(LvnSaveStore.Get(TitleA, "slot1"), "other slots untouched");
        }

        [Test]
        public void NewerSchemaSlotIsHiddenNotMisread()
        {
            // Simulate a save written by a future build (schema v99): this build
            // must not load it into corrupt state — and must not destroy it.
            LvnSaveStore.Put(TitleA, "future", Slot(1));
            var json = PlayerPrefs.GetString("lvn_slots_" + TitleA);
            PlayerPrefs.SetString("lvn_slots_" + TitleA, json.Replace("\"Version\":1", "\"Version\":99"));

            Assert.IsNull(LvnSaveStore.Get(TitleA, "future"), "a newer-schema slot is invisible");
            Assert.AreEqual(0, LvnSaveStore.Slots(TitleA).Count);

            // An unrelated write must not garbage-collect the hidden slot.
            LvnSaveStore.Put(TitleA, "slot1", Slot(2));
            LvnSaveStore.Delete(TitleA, "slot1");
            StringAssert.Contains("\"Version\":99",
                PlayerPrefs.GetString("lvn_slots_" + TitleA),
                "the future save survives Put/Delete round-trips for when the app updates");
        }

        [Test]
        public void PutStampsTheCurrentSchemaVersion()
        {
            var s = Slot(5);
            s.Version = 0; // pretend it came from an ancient in-memory path
            LvnSaveStore.Put(TitleA, "slot1", s);
            Assert.AreEqual(LvnSaveSlot.CurrentVersion, LvnSaveStore.Get(TitleA, "slot1").Version,
                "every write re-persists at the schema this build speaks");
        }

        [Test]
        public void ThumbWriteLoadRoundtrip_AndNullWipes()
        {
            var tex = new Texture2D(4, 2, TextureFormat.RGBA32, false);
            LvnSaveStore.WriteThumb(TitleA, "slot1", tex);
            var back = LvnSaveStore.LoadThumb(TitleA, "slot1");
            Assert.IsNotNull(back, "thumbnail file round-trips");
            Assert.AreEqual(4, back.width);
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(back);

            // a save with no fresh capture must not keep the stale scene
            LvnSaveStore.WriteThumb(TitleA, "slot1", null);
            Assert.IsNull(LvnSaveStore.LoadThumb(TitleA, "slot1"), "null wipes the file");
            Assert.IsNull(LvnSaveStore.LoadThumb(TitleA, "never-existed"), "absent file → null, no throw");
        }

        [Test]
        public void MissingAndCorruptDataDegradeToEmpty()
        {
            Assert.IsNull(LvnSaveStore.Get(TitleA, "nope"));
            Assert.AreEqual(0, LvnSaveStore.Slots(TitleA).Count);

            PlayerPrefs.SetString("lvn_slots_" + TitleA, "{не json вовсе");
            Assert.AreEqual(0, LvnSaveStore.Slots(TitleA).Count, "corrupt store reads as empty, never throws");

            // And a write recovers it.
            LvnSaveStore.Put(TitleA, "slot1", Slot(3));
            Assert.AreEqual(3, LvnSaveStore.Get(TitleA, "slot1").Snap.Index);
        }
    }
}
