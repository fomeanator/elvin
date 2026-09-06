using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    public sealed class SharedDownloadsTests
    {
        private static async Task Cancelled(Task task)
        {
            try { await task; Assert.Fail("Expected cancellation"); }
            catch (OperationCanceledException) { }
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task OneReaderCancellingDoesNotCancelTheOther(bool cancelFirst)
        {
            var shared = new SharedDownloads<int>();
            var result = new TaskCompletionSource<int>();
            using var firstStop = new CancellationTokenSource();
            using var secondStop = new CancellationTokenSource();
            CancellationToken wire = default;
            int calls = 0;
            Task<int> Work(CancellationToken token) { wire = token; calls++; return result.Task; }
            var first = shared.Run("file", Work, firstStop.Token);
            var second = shared.Run("file", Work, secondStop.Token);
            (cancelFirst ? firstStop : secondStop).Cancel();
            await Cancelled(cancelFirst ? first : second);
            Assert.IsFalse(wire.IsCancellationRequested, "A live reader still needs these bytes");
            result.SetResult(42);
            Assert.AreEqual(42, await (cancelFirst ? second : first));
            Assert.AreEqual(1, calls);
        }

        [Test]
        public async Task LastReaderCancellingStopsTheWire()
        {
            var shared = new SharedDownloads<int>();
            var result = new TaskCompletionSource<int>();
            using var stop = new CancellationTokenSource();
            CancellationToken wire = default;
            var task = shared.Run("file", token => { wire = token; return result.Task; }, stop.Token);
            stop.Cancel();
            await Cancelled(task);
            Assert.IsTrue(wire.IsCancellationRequested);
            result.SetCanceled();
        }

        [Test]
        public async Task NewReaderWaitsForAbortedWriterBeforeRestarting()
        {
            var shared = new SharedDownloads<int>();
            var draining = new TaskCompletionSource<int>();
            using var stop = new CancellationTokenSource();
            var first = shared.Run("file", _ => draining.Task, stop.Token);
            stop.Cancel();
            await Cancelled(first);
            int restarted = 0;
            var next = shared.Run("file", _ => { restarted++; return Task.FromResult(7); }, default);
            Assert.AreEqual(0, restarted, "Two writers would race over the same partial file");
            draining.SetCanceled();
            Assert.AreEqual(7, await next);
            Assert.AreEqual(1, restarted);
        }

        [Test]
        public async Task CancelledRestartDoesNotStartAnotherWriter()
        {
            var shared = new SharedDownloads<int>();
            var draining = new TaskCompletionSource<int>();
            using var stop = new CancellationTokenSource();
            var old = shared.Run("file", _ => draining.Task, stop.Token);
            stop.Cancel();
            await Cancelled(old);
            using var nextStop = new CancellationTokenSource();
            int calls = 0;
            var next = shared.Run("file", _ => { calls++; return Task.FromResult(7); }, nextStop.Token);
            nextStop.Cancel();
            await Cancelled(next);
            draining.SetCanceled();
            Assert.AreEqual(0, calls);
        }

        [Test]
        public async Task PreCancelledReaderNeverStartsWork()
        {
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            int calls = 0;
            await Cancelled(new SharedDownloads<int>().Run("file",
                _ => { calls++; return Task.FromResult(7); }, stop.Token));
            Assert.AreEqual(0, calls);
        }

        [Test]
        public async Task SynchronousFailureDoesNotPoisonTheNextRequest()
        {
            var shared = new SharedDownloads<int>();
            try
            {
                await shared.Run("file", _ => throw new InvalidOperationException("first"), default);
                Assert.Fail("Expected failure");
            }
            catch (InvalidOperationException) { }
            Assert.AreEqual(9, await shared.Run("file", _ => Task.FromResult(9), default));
        }

        [Test]
        public async Task ConcurrentReadersStartOnlyOneFactory()
        {
            var shared = new SharedDownloads<int>();
            var result = new TaskCompletionSource<int>();
            int calls = 0, joined = 0;
            var allJoined = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readers = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            {
                var task = shared.Run("file", token => { Interlocked.Increment(ref calls); return result.Task; }, default);
                if (Interlocked.Increment(ref joined) == 20) allJoined.SetResult(true);
                return await task;
            })).ToArray();
            await allJoined.Task;
            Assert.AreEqual(1, calls);
            result.SetResult(3);
            CollectionAssert.AreEqual(Enumerable.Repeat(3, 20).ToArray(), await Task.WhenAll(readers));
        }

        [Test]
        public async Task DifferentVersionsNeverShareTheirBytes()
        {
            var shared = new SharedDownloads<int>();
            var old = new TaskCompletionSource<int>();
            var first = shared.Run("file@v1", _ => old.Task, default);
            Assert.AreEqual(2, await shared.Run("file@v2", _ => Task.FromResult(2), default));
            old.SetResult(1);
            Assert.AreEqual(1, await first);
        }
    }
}
