using System;
using System.Threading;
using System.Threading.Tasks;
using Lvn.UI.Screens;
using NUnit.Framework;

namespace Lvn.Tests
{
    public sealed class CoalescingWorkTests
    {
        [Test]
        public async Task RequestsNeverOverlapWork()
        {
            var work = new HeldWork();
            var coalescer = new CoalescingWork(work.RunAsync);
            try
            {
                var cycle = coalescer.RequestAsync();
                _ = coalescer.RequestAsync();
                Assert.AreEqual(1, work.Calls);
                Assert.AreEqual(1, work.Active);

                work.Finish(0);
                await WaitForAsync(work.Started(1));
                Assert.AreEqual(1, work.Active);
                Assert.IsFalse(work.Overlapped);

                work.Finish(1);
                await WaitForAsync(cycle);
                Assert.IsFalse(work.Overlapped);
                Assert.AreEqual(0, work.Active);
            }
            finally { work.ReleaseAll(); }
        }

        [Test]
        public async Task FiveRequestsDuringWorkProduceExactlyOneRepeat()
        {
            var work = new HeldWork();
            var coalescer = new CoalescingWork(work.RunAsync);
            try
            {
                var cycle = coalescer.RequestAsync();
                for (int i = 0; i < 5; i++) _ = coalescer.RequestAsync();
                Assert.AreEqual(1, work.Calls);

                work.Finish(0);
                await WaitForAsync(work.Started(1));
                Assert.AreEqual(2, work.Calls);

                work.Finish(1);
                await WaitForAsync(cycle);
                Assert.AreEqual(2, work.Calls);
                Assert.AreEqual(0, work.Active);
            }
            finally { work.ReleaseAll(); }
        }

        [Test]
        public async Task CompletedRepeatAllowsNewWork()
        {
            var work = new HeldWork();
            var coalescer = new CoalescingWork(work.RunAsync);
            try
            {
                var cycle = coalescer.RequestAsync();
                _ = coalescer.RequestAsync();
                work.Finish(0);
                await WaitForAsync(work.Started(1));
                work.Finish(1);
                await WaitForAsync(cycle);

                var next = coalescer.RequestAsync();
                Assert.AreEqual(3, work.Calls);
                Assert.AreEqual(1, work.Active);
                Assert.IsFalse(next.IsCompleted);
                work.Finish(2);
                await WaitForAsync(next);
                Assert.AreEqual(3, work.Calls);
                Assert.AreEqual(0, work.Active);
                Assert.IsFalse(work.Overlapped);
            }
            finally { work.ReleaseAll(); }
        }

        [Test]
        public async Task FailurePreservesPendingRequestAndAllowsNewWork()
        {
            var work = new HeldWork();
            var coalescer = new CoalescingWork(work.RunAsync);
            var failure = new InvalidOperationException("test failure");
            try
            {
                var cycle = coalescer.RequestAsync();
                _ = coalescer.RequestAsync();
                work.Fail(0, failure);
                await WaitForAsync(work.Started(1));
                work.Finish(1);
                Exception observed = null;
                try { await WaitForAsync(cycle); }
                catch (InvalidOperationException ex) { observed = ex; }
                Assert.AreSame(failure, observed);

                var next = coalescer.RequestAsync();
                Assert.AreEqual(3, work.Calls);
                work.Finish(2);
                await WaitForAsync(next);
                Assert.AreEqual(0, work.Active);
                Assert.IsFalse(work.Overlapped);
            }
            finally { work.ReleaseAll(); }
        }

        private static async Task WaitForAsync(Task task)
        {
            // Пропавший повтор должен дать красный тест, а не вечное ожидание.
            Assert.AreSame(task, await Task.WhenAny(task, Task.Delay(5000)),
                "Работа не дошла до ожидаемой границы");
            await task;
        }

        private sealed class HeldWork
        {
            private readonly TaskCompletionSource<bool>[] _started = Gates();
            private readonly TaskCompletionSource<bool>[] _finished = Gates();
            private int _calls, _active, _overlapped;
            public int Calls => Volatile.Read(ref _calls);
            public int Active => Volatile.Read(ref _active);
            public bool Overlapped => Volatile.Read(ref _overlapped) != 0;

            public async Task RunAsync()
            {
                int call = Interlocked.Increment(ref _calls) - 1;
                if (Interlocked.Increment(ref _active) > 1)
                    Interlocked.Exchange(ref _overlapped, 1);
                try
                {
                    Assert.Less(call, _finished.Length, "Лишний запуск работы");
                    _started[call].TrySetResult(true);
                    await _finished[call].Task;
                }
                finally { Interlocked.Decrement(ref _active); }
            }

            public Task Started(int call) => _started[call].Task;
            public void Finish(int call) => _finished[call].TrySetResult(true);
            public void Fail(int call, Exception error) => _finished[call].TrySetException(error);
            public void ReleaseAll()
            {
                foreach (var gate in _finished) gate.TrySetResult(true);
            }

            private static TaskCompletionSource<bool>[] Gates() => new[]
            {
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
        }
    }
}
