using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    public sealed class DownloadCenterTests
    {
        [Test]
        public void MissingFilesAreNotSuccessfulCompletion()
        {
            var center = new DownloadCenter((_, ct) => Task.CompletedTask, _ => false);
            center.Enqueue("Chapter", 100, Items());
            Assert.IsFalse(center.LastRunCompleted);
            Assert.AreEqual(1, center.Failed.Count);
            Assert.AreEqual(2, center.Failed[0].MissingFiles);
            Assert.AreEqual(0, center.Progress.doneBytes);
            Assert.IsFalse(center.Running);
        }

        [Test]
        public void RetryFetchesOnlyTheMissingPart()
        {
            var cached = new HashSet<string>();
            int calls = 0;
            var center = new DownloadCenter((items, ct) =>
            {
                calls++;
                if (calls == 1) cached.Add(items[0].Url);
                else
                {
                    Assert.AreEqual(1, items.Count);
                    Assert.AreEqual("/b.bin", items[0].Url);
                    cached.Add(items[0].Url);
                }
                return Task.CompletedTask;
            }, cached.Contains);
            center.Enqueue("Chapter", 200, Items());
            center.Retry(center.Failed[0]);
            Assert.AreEqual(2, calls);
            Assert.IsEmpty(center.Failed);
            Assert.IsTrue(center.LastRunCompleted);
        }

        [Test]
        public void FinalNotificationIncludesTheFinalState()
        {
            var center = new DownloadCenter((_, ct) => Task.CompletedTask, _ => true);
            bool sawFinished = false;
            center.Changed += () => sawFinished |= !center.Running && center.LastRunCompleted;
            center.Enqueue("Chapter", 200, Items());
            Assert.IsTrue(sawFinished);
        }

        [Test]
        public void DismissingFailureDoesNotTurnItIntoSuccess()
        {
            var center = new DownloadCenter((_, ct) => Task.CompletedTask, _ => false);
            center.Enqueue("Chapter", 200, Items());
            center.Remove(center.Failed[0]);
            Assert.IsEmpty(center.Failed);
            Assert.IsFalse(center.LastRunCompleted);
        }

        private static List<PreloadItem> Items() => new List<PreloadItem>
        {
            new PreloadItem { Url = "/a.bin", Size = 100 },
            new PreloadItem { Url = "/b.bin", Size = 100 }
        };
    }
}
