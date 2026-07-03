using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// CG-gallery unlocks: per-title persistence (an unlock survives new
    /// playthroughs), idempotence, and the manifest's curated list parsing.
    public class LvnGalleryStoreTests
    {
        private const string Title = "test-gallery-title";

        [SetUp]
        [TearDown]
        public void Clean() => LvnGalleryStore.Clear(Title);

        [Test]
        public void Unlock_PersistsAndReportsNewness()
        {
            Assert.IsFalse(LvnGalleryStore.IsUnlocked(Title, "cg1"));
            Assert.IsTrue(LvnGalleryStore.Unlock(Title, "cg1"), "first unlock is new");
            Assert.IsFalse(LvnGalleryStore.Unlock(Title, "cg1"), "second unlock is a no-op");
            Assert.IsTrue(LvnGalleryStore.IsUnlocked(Title, "cg1"));

            LvnGalleryStore.Unlock(Title, "cg2");
            var set = LvnGalleryStore.Unlocked(Title);
            Assert.AreEqual(2, set.Count);
            Assert.IsTrue(set.Contains("cg2"));
        }

        [Test]
        public void Titles_AreNamespaced()
        {
            LvnGalleryStore.Unlock(Title, "cg1");
            Assert.IsFalse(LvnGalleryStore.IsUnlocked("some-other-title", "cg1"),
                "two novels in one app never share unlocks");
        }

        [Test]
        public void Clear_ForgetsEverything()
        {
            LvnGalleryStore.Unlock(Title, "cg1");
            LvnGalleryStore.Clear(Title);
            Assert.AreEqual(0, LvnGalleryStore.Unlocked(Title).Count);
        }

        [Test]
        public void NullOrEmptyIds_NeverUnlock()
        {
            Assert.IsFalse(LvnGalleryStore.Unlock(Title, null));
            Assert.IsFalse(LvnGalleryStore.Unlock(Title, ""));
            Assert.IsFalse(LvnGalleryStore.IsUnlocked(Title, null));
        }

        [Test]
        public void Manifest_ParsesCuratedGallery()
        {
            const string json = @"{
                ""id"": ""doll"",
                ""gallery"": [
                    { ""id"": ""cg-cover"", ""url"": ""/content/x/cover.png"", ""name"": ""Обложка"" },
                    { ""id"": ""cg-room"", ""url"": ""/content/x/room.png"" }
                ]
            }";
            var title = JsonConvert.DeserializeObject<LvnTitle>(json);
            Assert.AreEqual(2, title.gallery.Count);
            Assert.AreEqual("cg-cover", title.gallery[0].id);
            Assert.AreEqual("/content/x/room.png", title.gallery[1].url);
            Assert.AreEqual("Обложка", title.gallery[0].name);
            Assert.IsNull(title.gallery[1].name, "caption is optional");
        }
    }
}
