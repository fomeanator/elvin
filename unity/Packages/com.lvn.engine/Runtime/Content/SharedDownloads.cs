using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lvn.Content
{
    /// <summary>
    /// One operation per cache key, with cancellation owned by ALL its readers.
    /// Leaving one screen cancels its wait, not another screen's download.
    /// The last reader leaving aborts the operation; a new reader waits for that
    /// abort to drain before restarting, so two writers never share a .part file.
    /// Factories start on the caller's context (Unity APIs need the main thread).
    /// </summary>
    internal sealed class SharedDownloads<T>
    {
        private sealed class Flight
        {
            internal CancellationTokenSource Stop = new();
            internal readonly CancellationToken Token;
            internal readonly TaskCompletionSource<T> Result =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int Readers;
            internal bool Abandoned;
            internal Flight() { Token = Stop.Token; }
        }

        private readonly Dictionary<string, Flight> _flights = new();

        internal Task<T> Run(string key, Func<CancellationToken, Task<T>> work, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
            Flight flight;
            bool start = false, draining;
            lock (_flights)
            {
                if (!_flights.TryGetValue(key, out flight) || flight.Result.Task.IsCompleted)
                {
                    flight = new Flight();
                    _flights[key] = flight;
                    start = true;
                }
                draining = flight.Abandoned;
                if (!draining) flight.Readers++;
            }
            if (draining) return RestartAfterAsync(key, flight, work, ct);
            if (start) LvnAsync.Fire(CompleteAsync(key, flight, work), "SharedDownload");
            return ReadAsync(flight, ct);
        }

        private async Task CompleteAsync(string key, Flight flight, Func<CancellationToken, Task<T>> work)
        {
            try { flight.Result.TrySetResult(await work(flight.Token)); }
            catch (OperationCanceledException) { flight.Result.TrySetCanceled(); }
            catch (Exception error)
            {
                flight.Result.TrySetException(error);
                _ = flight.Result.Task.Exception; // all readers may already have left
            }
            finally
            {
                CancellationTokenSource stop;
                lock (_flights)
                {
                    if (_flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
                        _flights.Remove(key);
                    stop = flight.Stop;
                    flight.Stop = null;
                }
                stop?.Dispose();
            }
        }

        private async Task<T> ReadAsync(Flight flight, CancellationToken ct)
        {
            try
            {
                await flight.Result.Task.WithCancellation(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return await flight.Result.Task.ConfigureAwait(false);
            }
            finally
            {
                CancellationTokenSource stop = null;
                lock (_flights)
                {
                    if (--flight.Readers == 0 && !flight.Result.Task.IsCompleted)
                    {
                        flight.Abandoned = true;
                        stop = flight.Stop;
                        flight.Stop = null;
                    }
                }
                // Claim the source before retiring it: completion must not
                // dispose the same source while its cancellation callbacks run.
                LvnCancel.Retire(stop);
            }
        }

        private async Task<T> RestartAfterAsync(string key, Flight old,
            Func<CancellationToken, Task<T>> work, CancellationToken ct)
        {
            // Keep the caller's context here: the next factory may touch Unity.
            try { await old.Result.Task.WithCancellation(ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* old operation's failure is not ours */ }
            ct.ThrowIfCancellationRequested();
            return await Run(key, work, ct);
        }
    }
}
