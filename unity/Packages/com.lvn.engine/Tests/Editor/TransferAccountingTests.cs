using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    public sealed class TransferAccountingTests
    {
        private ContentLoader _loader;
        private string _cache;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp]
        public void SetUp()
        {
            _cache = Path.Combine(Path.GetTempPath(), "elvin-transfer-test-" + Guid.NewGuid().ToString("N"));
            _loader = new ContentLoader("https://unused.invalid", _cache);
        }

        [TearDown]
        public void TearDown()
        {
            _loader.Dispose();
            if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
        }

        private void Field(string name, object value)
            => typeof(ContentLoader).GetField(name, Private).SetValue(_loader, value);

        private void Counter(string name, object value)
            => typeof(ContentLoader).GetProperty(name).SetValue(_loader, value);

        private object Record(string url)
            => typeof(ContentLoader).GetMethod("Progress", Private).Invoke(_loader, new object[] { url });

        private void Received(string url, long bytes)
        {
            var record = Record(url);
            record.GetType().GetField("Received").SetValue(record, bytes);
        }

        private void BeginBatch()
        {
            Field("_batchUrls", new HashSet<string> { "/a", "/b" });
            Field("_tallyEpoch", 1);
            Counter("BatchTotal", 2);
            Counter("BatchPlannedBytes", 200L);
        }

        private Task<byte[]> Track(string url, Task<byte[]> work)
            => (Task<byte[]>)typeof(ContentLoader).GetMethod("TrackedFetch", Private)
                .MakeGenericMethod(typeof(byte[])).Invoke(_loader,
                    new object[] { url, new Func<Task<byte[]>>(() => work) });

        private async Task AwaitTracker(string url)
        {
            for (int i = 0; i < 1000; i++)
            {
                var records = (IDictionary)typeof(ContentLoader).GetField("_underway", Private).GetValue(_loader);
                lock (records)
                {
                    var record = records[url];
                    if (record == null || record.GetType().GetField("Work").GetValue(record) == null) return;
                }
                await Task.Delay(1);
            }
            Assert.Fail("The fetch continuation did not finish");
        }

        [Test]
        public async Task BatchFilesAreNotRegisteredTwice()
        {
            BeginBatch();
            var a = new TaskCompletionSource<byte[]>();
            var b = new TaskCompletionSource<byte[]>();
            _ = Track("/a", a.Task);
            _ = Track("/b", b.Task);
            Assert.AreEqual(2, _loader.Transfers().BatchTotal);
            a.SetResult(new byte[100]);
            b.SetResult(new byte[100]);
            await AwaitTracker("/a");
            await AwaitTracker("/b");
            Assert.AreEqual(0, _loader.Transfers().BatchDone,
                "Only the batch worker closes its planned files");
        }

        [Test]
        public void ClosedBytesAndUnrelatedStreamingAreNotAddedTwice()
        {
            BeginBatch();
            Received("/a", 100);
            Received("/b", 20);
            Received("/unrelated", 999);
            ((HashSet<string>)typeof(ContentLoader).GetField("_batchClosed", Private).GetValue(_loader)).Add("/a");
            Counter("BatchClosedBytes", 100L);
            Counter("BatchDone", 1);
            var snapshot = _loader.Transfers();
            Assert.AreEqual(120, snapshot.Received);
            Assert.AreEqual(200, snapshot.PlannedBytes);
        }

        [Test]
        public async Task OldStandaloneCompletionDoesNotCloseTheNewBatch()
        {
            var previous = new TaskCompletionSource<byte[]>();
            _ = Track("/old", previous.Task);
            BeginBatch();
            previous.SetResult(new byte[100]);
            await AwaitTracker("/old");
            Assert.AreEqual(2, _loader.Transfers().BatchTotal);
            Assert.AreEqual(0, _loader.Transfers().BatchDone);
        }

        [Test]
        public async Task UnrelatedFetchDoesNotRenameTheCurrentBatch()
        {
            BeginBatch();
            Counter("LastStartedUrl", "/a");
            var other = new TaskCompletionSource<byte[]>();
            _ = Track("/other", other.Task);
            Assert.AreEqual("/a", _loader.LastStartedUrl);
            other.SetResult(Array.Empty<byte>());
            await AwaitTracker("/other");
        }
    }
}
