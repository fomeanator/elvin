using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using System.Linq;

namespace Lvn.Content
{
    /// <summary>
    /// ОБОЗ — пакетная загрузка вперёд: главы, обложки, набор для входа.
    ///
    /// <para>Разница с одиночной загрузкой не в объёме, а в обещании: обоз
    /// говорит, СКОЛЬКО осталось, и потому за ним можно показывать полосу и
    /// им можно управлять — приостановить, отменить, дождаться именно этих
    /// адресов. Одиночная загрузка обещает только себя.</para>
    /// </summary>
    public sealed partial class ContentLoader
    {
        public Task Prefetch(string url, string kind, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url)) return Task.CompletedTask;
            return kind switch
            {
                LvnParts.Script => DownloadScriptText(url, ct),
                _ => DownloadAssetBytes(url, ct),
            };
        }

        /// <summary>Downloads a list of assets, pipelining disk writes with the
        /// next file's network setup so the progress bar shows smooth overall
        /// progress and there's no idle gap between files. Files already on disk
        /// are skipped (they don't inflate the total). Returns a Task the caller
        /// can await; <see cref="WaitForAll"/>(null) also works.</summary>
        public Task StartPreloadBatch(IReadOnlyList<PreloadItem> assets, CancellationToken ct)
        {
            if (assets == null || assets.Count == 0) return Task.CompletedTask;

            // Register all aliases up-front so the HUD label is ready the moment a
            // fetch starts (no one-frame flash of raw URL).
            foreach (var a in assets)
                if (!string.IsNullOrEmpty(a.Alias) && !string.IsNullOrEmpty(a.Url))
                    lock (_aliases) _aliases[a.Url] = a.Alias;

            // Count how many files are actually missing from disk cache.
            var pending = new List<PreloadItem>(assets.Count);
            foreach (var a in assets)
            {
                if (string.IsNullOrEmpty(a.Url)) continue;
                var path = CachePath(_assetCacheDir, a.Url, ".bin");
                if (!File.Exists(path)) pending.Add(a);
            }
            if (pending.Count == 0) return Task.CompletedTask;

            // ОЧЕРЕДЬ, А НЕ ПОДМЕНА.
            //
            // Ключ был ОДИН на все пакеты («__preload_batch__»), и второй
            // вызывающий получал ЧУЖУЮ задачу вместо своей. А вызывающих
            // четверо: бут греет обложки, менеджер тянет следующую главу,
            // центр загрузок ведёт очередь глав, кнопка «Докачать» в
            // настройках просит остаток. Стоило нажать «Докачать», пока идёт
            // очередь, — и её глава «скачивалась» чужой задачей: центр снимал
            // запись с очереди, файлов никто не качал, а счётчики показывали
            // «глав 1 · файлов 0» при скорости «—» и остаток, который не
            // уменьшался (живой скрин 01.09).
            //
            // Тот же СПИСОК по-прежнему схлопывается в одну задачу (повторный
            // запрос той же главы не качает её дважды), а РАЗНЫЕ списки честно
            // встают в очередь: полоса пропускания одна, и параллелить их
            // нечем.
            string batchKey = "__preload_batch__" + BatchKey(pending);
            Task<byte[]> batchTask;
            lock (_inflight)
            {
                if (_inflight.TryGetValue(batchKey, out var same)) return same;
                batchTask = RunBatchQueuedAsync(pending, ct);
                _inflight[batchKey] = batchTask;
            }
            _ = batchTask.ContinueWith(_ =>
            {
                lock (_inflight) _inflight.Remove(batchKey);
            }, TaskScheduler.Default);
            return batchTask;
        }

        /// <summary>
        /// ОЧЕРЕДЬ ОПУСТЕЛА — счёт обнуляется ВЕСЬ.
        ///
        /// <para>Счёт складывают шесть полей: сколько всего, сколько сделано,
        /// что скачивалось последним, попытки и два словаря байтов. Забыть одно
        /// из них — значит показать игроку «Скачано 131 из 135» при пустой
        /// очереди (живой скрин): мусор одиночных фетчей въезжает в проценты
        /// следующего пакета.</para>
        ///
        /// <para>Обнуление стояло дважды — в конце пакета и в конце одиночной
        /// загрузки, — и это ровно то место, где поле теряют.</para>
        ///
        /// <para>Звать ТОЛЬКО под <c>lock (_inflight)</c>: те же поля читает
        /// полоса загрузки из другого потока.</para>
        /// </summary>
        private void ClearBatchTally()
        {
            BatchTotal     = 0;
            BatchDone      = 0;
            LastStartedUrl = null;
            _attempts.Clear();
            _bytesExpected.Clear();
            _bytesReceived.Clear();
        }

        // Пакеты идут ПО ОЧЕРЕДИ: канал один, и два пакета, тянущие его
        // одновременно, только делят его пополам — зато оба показывают половину
        // скорости и вдвое больше ждут.
        private readonly System.Threading.SemaphoreSlim _batchGate = new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>Устойчивое имя пакета — по списку адресов. Тот же список
        /// (повторный запрос главы) находит свою задачу, чужой не находит.</summary>
        private static string BatchKey(List<PreloadItem> pending)
        {
            unchecked
            {
                int h = 17;
                foreach (var a in pending) h = h * 31 + (a.Url?.GetHashCode() ?? 0);
                return pending.Count + ":" + h;
            }
        }

        private async Task<byte[]> RunBatchQueuedAsync(List<PreloadItem> pending, CancellationToken ct)
        {
            await _batchGate.WaitAsync(ct);
            try
            {
                lock (_inflight)
                {
                    // Чистый старт: словари байтов копят и одиночные фетчи
                    // (фоновый стриминг), и их мусор въезжал в прогресс батча —
                    // «Скачано 131 из 135» при пустой очереди (живой скрин).
                    _bytesReceived.Clear();
                    _bytesExpected.Clear();
                    BatchTotal     = pending.Count;
                    BatchDone      = 0;
                    LastStartedUrl = pending[0].Url;
                }
                return await RunBatchAsync(pending, ct);
            }
            finally
            {
                lock (_inflight) ClearBatchTally();
                _batchGate.Release();
            }
        }

        private async Task<byte[]> RunBatchAsync(List<PreloadItem> pending, CancellationToken ct)
        {
            // Pipeline: at 90% of file N, warm-start file N+1 via a silent
            // prefetch (no progress counters — avoids the bar jumping backward).
            // By the time N finishes, N+1's TCP/TLS is already up and data is
            // flowing, so there's no idle gap between files.
            Task diskTask = Task.CompletedTask;
            Task<byte[]> prefetchTask = null;
            string       prefetchUrl  = null; // URL the prefetch is downloading

            for (int i = 0; i < pending.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var asset = pending[i];
                var path  = CachePath(_assetCacheDir, asset.Url, ".bin");

                if (File.Exists(path)) { lock (_inflight) BatchDone++; continue; }

                CurrentFileLabel = AliasOf(asset.Url);
                LastStartedUrl   = asset.Url;

                const int MaxRetries = 10;
                byte[] body = null;
                int attempt = 1;
                while (true)
                {
                    try
                    {
                        lock (_inflight) _attempts[asset.Url] = attempt;

                        // Reuse warm prefetch if it was for this URL and didn't fault.
                        Task<byte[]> fetchTask;
                        if (prefetchUrl == asset.Url &&
                            prefetchTask is { IsFaulted: false, IsCanceled: false })
                        {
                            fetchTask    = prefetchTask;
                            prefetchTask = null;
                            prefetchUrl  = null;
                        }
                        else
                        {
                            if (prefetchUrl == asset.Url) { prefetchTask = null; prefetchUrl = null; }
                            fetchTask = FetchToMemory(asset.Url, ct);
                        }

                        // Drive the download; fire a silent warm-start for the next
                        // file once this one crosses 90%.
                        while (!fetchTask.IsCompleted)
                        {
                            ct.ThrowIfCancellationRequested();

                            if (prefetchUrl == null) // not yet decided for next file
                            {
                                long exp, rec;
                                lock (_inflight)
                                {
                                    exp = _bytesExpected.GetValueOrDefault(asset.Url);
                                    rec = _bytesReceived.GetValueOrDefault(asset.Url);
                                }
                                if (exp > 0 && (float)rec / exp >= 0.9f)
                                {
                                    var nextUrl = FindNextUncachedUrl(pending, i + 1);
                                    prefetchUrl  = nextUrl ?? ""; // "" = nothing to prefetch
                                    if (nextUrl != null)
                                        prefetchTask = FetchToMemoryPrefetch(nextUrl, ct);
                                }
                            }

                            await Task.Yield();
                        }

                        body = await fetchTask;
                        // Same integrity rule as DownloadBytes: never cache bytes
                        // that don't match the version index's sha256. Exact
                        // entries only — a derived variant's inherited version
                        // describes its SOURCE, not these bytes.
                        var expect = IntegrityVersionFor(asset.Url);
                        if (body != null && expect != null && !Sha256Matches(body, expect))
                            throw new LvnFetchException(0, "integrity",
                                "sha256 mismatch for " + asset.Url + " — refetching");
                        if (prefetchUrl == "") prefetchUrl = null;
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (LvnFetchException ex) when (ex.Status is >= 400 and < 500)
                    {
                        Debug.LogWarning($"[content] preload {asset.Url} permanent {ex.Status}");
                        if (prefetchUrl == "") prefetchUrl = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        attempt++;
                        if (attempt > MaxRetries)
                        {
                            Debug.LogWarning($"[content] preload {asset.Url} gave up after {MaxRetries} attempts");
                            if (prefetchUrl == "") prefetchUrl = null;
                            break;
                        }
                        var backoff = LvnBackoff.DelaySeconds(attempt);
                        Debug.LogWarning($"[content] preload {asset.Url} attempt {attempt}, retry in {backoff:F1}s: {ex.Message}");
                        // Правило паузы — у RetryPauseAsync: пока флаг говорит
                        // «офлайн», ждём смены состояния, а не часов.
                        try { await RetryPauseAsync(backoff, ct); }
                        catch (OperationCanceledException) { throw; }
                    }
                }

                var capPath = path;
                var capBody = body;
                await diskTask;
                diskTask = capBody != null
                    // Write atomically (staged temp + move): a crash mid-write must
                    // not leave a truncated .bin, which File.Exists would then treat
                    // as a valid cache entry forever (permanent boot-art corruption).
                    ? Task.Run(() => AtomicWriteAllBytes(capPath, capBody), CancellationToken.None)
                    : Task.CompletedTask;

                lock (_inflight) BatchDone++;
            }

            await diskTask;
            CurrentFileLabel = null;
            return null;
        }

        // Returns the URL of the first file in pending[fromIdx..] not yet on disk.
        private string FindNextUncachedUrl(List<PreloadItem> pending, int fromIdx)
        {
            for (int j = fromIdx; j < pending.Count; j++)
                if (!File.Exists(CachePath(_assetCacheDir, pending[j].Url, ".bin")))
                    return pending[j].Url;
            return null;
        }



        /// <summary>Waits until either the listed urls finish prefetching, or (if
        /// <paramref name="urls"/> is null) until the whole batch settles — no
        /// task in flight and the counters reset. Polling BatchActive catches
        /// tasks that join the queue mid-wait.</summary>
        public async Task WaitForAll(IEnumerable<string> urls, CancellationToken ct = default)
        {
            if (urls == null)
            {
                while (BatchActive)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await Task.Delay(50, ct); }
                    catch (OperationCanceledException) { throw; }
                }
                return;
            }
            List<Task> tasks;
            lock (_inflight)
            {
                tasks = urls.Where(u => _inflight.ContainsKey(u))
                            .Select(u => _inflight[u]).ToList();
            }
            if (tasks.Count == 0) return;
            try { await Task.WhenAll(tasks).WithCancellation(ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* individual asset failures don't block the wait */ }
        }

        /// <summary>True if at least one asset has been downloaded and cached
        /// locally. Used to decide whether to show the verify phase on startup.</summary>
    }
}
