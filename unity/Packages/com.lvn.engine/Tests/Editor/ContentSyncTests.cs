using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    public class ContentSyncTests
    {
        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        [Test]
        public void ParseVersion_ReadsVersionField()
        {
            Assert.AreEqual("abc123", ContentSync.ParseVersion("{\"version\":\"abc123\"}"));
        }

        [Test]
        public void ParseVersion_NullForGarbageOrMissing()
        {
            Assert.IsNull(ContentSync.ParseVersion(""));
            Assert.IsNull(ContentSync.ParseVersion(null));
            Assert.IsNull(ContentSync.ParseVersion("not json"));
            Assert.IsNull(ContentSync.ParseVersion("{\"other\":1}"));
        }

        [Test]
        public void FirstPoll_NotifiesWhenBootReconciliationIsEnabled()
        {
            string last = null;
            Assert.IsTrue(ContentSync.AdvanceVersion(ref last, "after-save", notifyOnFirst: true));
            Assert.AreEqual("after-save", last);
        }

        [Test]
        public void FirstPoll_DefaultOnlyEstablishesBaseline()
        {
            string last = null;
            Assert.IsFalse(ContentSync.AdvanceVersion(ref last, "baseline", notifyOnFirst: false));
            Assert.AreEqual("baseline", last);
            Assert.IsFalse(ContentSync.AdvanceVersion(ref last, "baseline", notifyOnFirst: true));
            Assert.IsTrue(ContentSync.AdvanceVersion(ref last, "changed", notifyOnFirst: true));
        }

        [Test]
        public void Carousel_SetTitles_RebuildsAndClampsIndex()
        {
            var c = new TitleCarousel(
                new List<LvnTitle> { new LvnTitle { id = "a", name = "A" } },
                new CarouselConfig(), new NoAssets());
            Assert.AreEqual("a", c.Current.id);

            c.SetTitles(new List<LvnTitle>
            {
                new LvnTitle { id = "x", name = "X" },
                new LvnTitle { id = "y", name = "Y" },
            });
            Assert.AreEqual("x", c.Current.id);   // index 0 preserved, now points at the new first title
            Assert.AreEqual(0, c.Index);

            c.SetTitles(new List<LvnTitle>());     // empty set must not throw or break
            Assert.IsNull(c.Current);
            Assert.AreEqual(0, c.Index);
        }
    }
}
