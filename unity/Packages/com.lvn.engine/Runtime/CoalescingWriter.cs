using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lvn
{
    /// <summary>
    /// A coalescing single-flight writer: many fast <see cref="Request"/> calls
    /// collapse into a serialized stream of writes where at most ONE write runs at
    /// a time and only the NEWEST pending value is ever written. The standard
    /// "latest-wins debounce without a timer" / "conflated queue" pattern — the
    /// right shape for autosaves, where a burst of progress updates must persist
    /// without overlapping PUTs racing each other.
    ///
    /// <para>Guarantees:</para>
    /// <list type="bullet">
    ///   <item>Request() is cheap, synchronous and never blocks the caller.</item>
    ///   <item>At most one write is in flight; writes never overlap.</item>
    ///   <item>Bursts coalesce: N values arriving while a write runs cause exactly
    ///   one follow-up write carrying the LAST value (older ones superseded).</item>
    ///   <item>FlushAsync() is an awaitable barrier that completes once everything
    ///   queued up to that point has been written.</item>
    ///   <item>The write delegate's exceptions route to <c>onError</c> and never
    ///   break the loop or escape Request/FlushAsync.</item>
    /// </list>
    /// </summary>
    public sealed class CoalescingWriter<T>
    {
        private readonly Func<T, CancellationToken, Task> _write;
        private readonly Action<Exception> _onError;

        private readonly object _gate = new object();
        private T _pending;
        private bool _hasPending;
        private bool _running;
        private Task _loop = Task.CompletedTask;

        public CoalescingWriter(Func<T, CancellationToken, Task> write, Action<Exception> onError = null)
        {
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _onError = onError;
        }

        /// <summary>True while a write is in flight or one is queued. Mostly for tests.</summary>
        public bool IsBusy { get { lock (_gate) return _running || _hasPending; } }

        /// <summary>Record the newest value to write and ensure the drain loop runs.
        /// Returns immediately; the write happens asynchronously.</summary>
        public void Request(T value)
        {
            lock (_gate)
            {
                _pending = value;
                _hasPending = true;
                if (_running) return;     // existing loop will pick it up
                _running = true;
                _loop = DrainLoop();
            }
        }

        private async Task DrainLoop()
        {
            while (true)
            {
                T value;
                lock (_gate)
                {
                    if (!_hasPending) { _running = false; return; }
                    value = _pending;
                    _hasPending = false;
                }
                // Unlinked CancellationToken on purpose: a write started here should
                // land even if some outer scope is cancelling.
                try { await _write(value, CancellationToken.None); }
                catch (Exception ex) { _onError?.Invoke(ex); }
            }
        }

        /// <summary>Completes once everything queued up to now has been written.</summary>
        public async Task FlushAsync()
        {
            Task loop;
            lock (_gate)
            {
                if (!_running && _hasPending)
                {
                    _running = true;
                    _loop = DrainLoop();
                }
                loop = _loop;
            }
            try { await loop; }
            catch (Exception ex) { _onError?.Invoke(ex); }
        }
    }
}
