using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// Pins the coalescing single-flight writer contract: serialized writes,
    /// latest-wins burst collapse, awaitable flush, error isolation.
    public class CoalescingWriterTests
    {
        [Test]
        public async Task SingleRequest_WritesOnce()
        {
            var written = new List<int>();
            var w = new CoalescingWriter<int>((v, ct) => { written.Add(v); return Task.CompletedTask; });
            w.Request(7);
            await w.FlushAsync();
            CollectionAssert.AreEqual(new[] { 7 }, written);
        }

        [Test]
        public async Task Burst_DuringSlowWrite_CoalescesToLatest()
        {
            var written = new List<int>();
            var gate = new TaskCompletionSource<bool>();
            var firstStarted = new TaskCompletionSource<bool>();
            var w = new CoalescingWriter<int>(async (v, ct) =>
            {
                if (written.Count == 0) { firstStarted.TrySetResult(true); await gate.Task; }
                written.Add(v);
            });

            w.Request(1);                 // starts the (blocked) first write
            await firstStarted.Task;      // ensure write #1 is in flight
            w.Request(2);                 // these three queue behind it…
            w.Request(3);
            w.Request(4);                 // …and only the LAST should survive
            gate.SetResult(true);         // release write #1
            await w.FlushAsync();

            CollectionAssert.AreEqual(new[] { 1, 4 }, written);
        }

        [Test]
        public async Task Writes_NeverOverlap()
        {
            int concurrent = 0, maxConcurrent = 0;
            var w = new CoalescingWriter<int>(async (v, ct) =>
            {
                var now = Interlocked.Increment(ref concurrent);
                maxConcurrent = System.Math.Max(maxConcurrent, now);
                await Task.Yield();
                Interlocked.Decrement(ref concurrent);
            });
            for (int i = 0; i < 20; i++) w.Request(i);
            await w.FlushAsync();
            Assert.AreEqual(1, maxConcurrent, "writes must be serialized");
        }

        [Test]
        public async Task Flush_WithNothingQueued_CompletesImmediately()
        {
            var w = new CoalescingWriter<int>((v, ct) => Task.CompletedTask);
            await w.FlushAsync();   // must not hang
            Assert.Pass();
        }

        [Test]
        public async Task WriteException_IsRoutedToOnError_AndLoopSurvives()
        {
            var errors = new List<string>();
            var written = new List<int>();
            var w = new CoalescingWriter<int>(
                (v, ct) =>
                {
                    if (v == 1) throw new System.InvalidOperationException("boom");
                    written.Add(v);
                    return Task.CompletedTask;
                },
                ex => errors.Add(ex.Message));

            w.Request(1);
            await w.FlushAsync();
            w.Request(2);            // loop must still work after the error
            await w.FlushAsync();

            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual("boom", errors[0]);
            CollectionAssert.AreEqual(new[] { 2 }, written);
        }

        [Test]
        public async Task SequentialRequests_AllWriteInOrder()
        {
            var written = new List<int>();
            var w = new CoalescingWriter<int>(async (v, ct) => { await Task.Yield(); written.Add(v); });
            for (int i = 1; i <= 5; i++) { w.Request(i); await w.FlushAsync(); }
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, written);
        }

        [Test]
        public void Ctor_NullWrite_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => new CoalescingWriter<int>(null));
        }
    }
}
