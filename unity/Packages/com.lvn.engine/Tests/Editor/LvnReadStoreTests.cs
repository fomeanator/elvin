using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Read-line tracking: identity hashing, per-title persistence and the
    /// newness signal skip-read-only keys off.
    public class LvnReadStoreTests
    {
        private const string Title = "test-read-title";

        [SetUp]
        [TearDown]
        public void Clean() => LvnReadStore.Clear(Title);

        [Test]
        public void MarkRead_ReportsNewnessOnce_AndPersists()
        {
            Assert.IsFalse(LvnReadStore.IsRead(Title, "A", "line one"));
            Assert.IsTrue(LvnReadStore.MarkRead(Title, "A", "line one"), "first sighting is new");
            Assert.IsFalse(LvnReadStore.MarkRead(Title, "A", "line one"), "re-read is not");
            Assert.IsTrue(LvnReadStore.IsRead(Title, "A", "line one"));
            Assert.AreEqual(1, LvnReadStore.ReadCount(Title));
        }

        [Test]
        public void Identity_IsSpeakerPlusText()
        {
            LvnReadStore.MarkRead(Title, "A", "same words");
            Assert.IsFalse(LvnReadStore.IsRead(Title, "B", "same words"),
                "another speaker saying the same words is a different line");
            Assert.IsFalse(LvnReadStore.IsRead(Title, "A", "same words!"),
                "edited text reads as new — the player hasn't seen the new wording");
            Assert.IsTrue(LvnReadStore.IsRead(Title, "A", "same words"));
        }

        [Test]
        public void NarrationWithNullSpeaker_Roundtrips()
        {
            Assert.IsTrue(LvnReadStore.MarkRead(Title, null, "pure narration"));
            Assert.IsTrue(LvnReadStore.IsRead(Title, null, "pure narration"));
        }

        [Test]
        public void Titles_AreNamespaced()
        {
            LvnReadStore.MarkRead(Title, "A", "line");
            Assert.IsFalse(LvnReadStore.IsRead("other-title", "A", "line"));
        }

        [Test]
        public void Hash_IsStable()
        {
            // The persisted format depends on this value never drifting between
            // releases — players' read history must survive engine updates.
            Assert.AreEqual(LvnReadStore.Hash("A", "line"), LvnReadStore.Hash("A", "line"));
            Assert.AreNotEqual(LvnReadStore.Hash("A", "line"), LvnReadStore.Hash("A", "line "));
        }
    }
}
