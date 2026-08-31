using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.Content
{
    /// <summary>
    /// ДОСТАВКА БАЙТОВ — часть <see cref="ContentLoader"/>: сеть, докачка с
    /// обрыва, счёт попыток и жизнь в офлайне.
    ///
    /// <para>Всё, что знает про UnityWebRequest, живёт здесь; остальной
    /// загрузчик работает с байтами и не думает о том, откуда они приехали —
    /// из сети, из докачанного куска или из локального сида.</para>
    /// </summary>
    public sealed partial class ContentLoader
    {
        // Caps simultaneous in-flight downloads. HTTP/2 MULTIPLEXES many
        // concurrent requests over a SINGLE TLS connection — so a wider cap
        // doesn't open more sockets, it fills more h2 streams. 12 lets a burst of
        // small files (UI/script/actors) all fly at once without the
        // request-per-file round-trip tax a 6-cap (the HTTP/1.1 socket limit)
        // imposed.
        private static readonly SemaphoreSlim _downloadSlots = new(12, 12);

        private void NoteFetchFailure(UnityWebRequest req)
        {
            try { AssetFailed?.Invoke(req.url, req.responseCode); }
            catch { /* диагностика не смеет ронять загрузку */ }
            var err = req.error ?? "";
            bool transient = req.downloadedBytes > 0
                || err.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                || err.IndexOf("abort", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!transient) MarkOfflineUnlessLocal("content fetch network error");
        }

        // Await a UnityWebRequest via its `completed` callback instead of polling
        // isDone once per frame. Polling quantizes every await to frame
        // boundaries — and on a busy main thread it inflated the PERCEIVED cost
        // of every concurrent decode at once (each "finished" only when the next
        // frame ran the poll). `completed` fires the same frame the native op
        // ends. Cancellation aborts the request, which completes the op; the
        // OperationCanceledException follows. NOT used by the download loops
        // that publish per-frame byte progress — those need the poll.
        // Ожидание живёт в доме (LvnNetWait): и опросом — для тех, кто считает
        // байты и следит за молчанием, — и событием, как здесь.
        private static Task AwaitRequest(UnityWebRequest req, UnityWebRequestAsyncOperation op, CancellationToken ct)
            => LvnNetWait.CompletedAsync(req, op, ct);

        // Fast-fail when we already know we're offline: skip the wire entirely so
        // callers fall straight back to the on-disk cache. Code "network" →
        // callers/retry-loops treat it as a connectivity miss.
        private void ThrowIfOffline()
        {
            if (_local) return; // local bundle is always available
            if (LvnNetworkStatus.IsOffline)
            {
                // Whoever pinned the flag may not have started the recovery probe
                // (the host's boot healthz calls MarkOffline directly). Without
                // this re-arm the app is wedged: every fetch fast-fails HERE,
                // before the wire, so the fetch-failure path that normally starts
                // the probe never runs — offline becomes permanent for the session.
                EnsureRecoveryLoop();
                throw new LvnFetchException(0, "network", "offline (global status)");
            }
        }

        // MarkOffline only when reading from a real network origin; a missing
        // local file must not poison the global offline status. Going offline also
        // starts the recovery probe so the app self-heals when the wire returns.
        private void MarkOfflineUnlessLocal(string reason)
        {
            if (_local) return;
            LvnNetworkStatus.MarkOffline(reason);
            EnsureRecoveryLoop();
        }

        // Once we've gone offline, nothing else re-probes connectivity — every
        // fetch just fast-fails on the global flag — so a single network blip would
        // wedge the app offline for the whole session (dead live-sync, no new
        // chapters, dropped saves). This loop probes /healthz with backoff while
        // offline and flips the flag back on the moment the server answers, which
        // unblocks the next fetch/sync automatically. HealthzAsync MarkOnlines on a
        // 2xx (and never MarkOffline), so a failed probe just waits and retries.
        private void EnsureRecoveryLoop()
        {
            if (_local || LvnNetworkStatus.ForceOffline) return; // never probe a local bundle / a test kill-switch
            if (Interlocked.Exchange(ref _recovering, 1) == 1) return; // already probing
            LvnAsync.Fire(RecoveryLoopAsync(), "RecoveryLoop");
        }

        private async Task RecoveryLoopAsync()
        {
            try
            {
                int attempt = 2; // start at the first non-zero backoff step
                while (LvnNetworkStatus.IsOffline && !LvnNetworkStatus.ForceOffline)
                {
                    var delay = LvnBackoff.DelaySeconds(attempt++);
                    // Wake the sleep early on ANY status change (recovered via another
                    // path, or ForceOffline set) so the loop reacts at once instead of
                    // idling out the full backoff. A fresh token per iteration avoids a
                    // stale-cancelled-token hot spin.
                    using (var wake = new CancellationTokenSource())
                    {
                        Action<bool> onChange = _ => { LvnQuiet.Try(wake.Cancel); };
                        LvnNetworkStatus.Changed += onChange;
                        try { await Task.Delay((int)(delay * 1000f) + 500, wake.Token); }
                        catch (OperationCanceledException) { /* status changed — re-check now */ }
                        finally { LvnNetworkStatus.Changed -= onChange; }
                    }
                    if (LvnNetworkStatus.IsOnline || LvnNetworkStatus.ForceOffline) break;
                    try { if (await HealthzAsync()) break; } // MarkOnlines on success
                    catch { /* probe failed — keep waiting */ }
                }
            }
            finally { Interlocked.Exchange(ref _recovering, 0); }
        }

        // A backoff sleep that wakes EARLY the moment the global status flips
        // back online — so a retry loop parked on the offline flag resumes the
        // instant the recovery probe finds the server, instead of idling out
        // its full delay. Cancellation of `ct` still propagates as usual.
        private static async Task DelayOrOnlineAsync(float seconds, CancellationToken ct)
        {
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Action<bool> onChange = online => { if (online) { LvnQuiet.Try(wake.Cancel); } };
            LvnNetworkStatus.Changed += onChange;
            try { await Task.Delay(Math.Max(1, (int)(seconds * 1000f)), wake.Token); }
            catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); }
            finally { LvnNetworkStatus.Changed -= onChange; }
        }

        /// <summary>Ensure the url's bytes exist as a plain local FILE and return
        /// its path — for consumers that need a real file rather than decoded
        /// content (runtime fonts: <c>new Font(path)</c> has no bytes overload).
        /// Server origin → the versioned disk cache; local file:// bundle → the
        /// file itself; Android jar bundle → copied out to the cache once.</summary>
        public async Task<string> EnsureCachedFile(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_local)
            {
                var resolved = ResolveUrl(url);
                if (resolved.StartsWith("file://"))
                {
                    var direct = resolved.Substring("file://".Length);
                    return File.Exists(direct) ? direct : null;
                }
                // jar:file:// (StreamingAssets inside the APK) has no plain path —
                // read through UnityWebRequest and stage a cache copy once.
                var staged = CachePath(_assetCacheDir, url, ".bin");
                if (!File.Exists(staged))
                {
                    var data = await DownloadAssetBytes(url, ct);
                    if (data == null || data.Length == 0) return null;
                    AtomicWriteAllBytes(staged, data);
                }
                return staged;
            }
            var path = CachePath(_assetCacheDir, url, ".bin");
            if (!File.Exists(path))
            {
                var bytes = await DownloadBytes(url, _assetCacheDir, ct); // writes the cache file
                if (bytes == null || bytes.Length == 0) return null;
            }
            return File.Exists(path) ? path : null;
        }

        /// <summary>Lightweight connectivity probe: GET <c>&lt;baseUrl&gt;/healthz</c>.
        /// Returns true and marks the process online on a 2xx; returns false on any
        /// error, non-2xx or cancellation WITHOUT flipping the global flag (the
        /// caller decides whether to <see cref="LvnNetworkStatus.MarkOffline"/>), so
        /// a cancelled probe never poisons a still-good connection. A local
        /// (<c>file://</c>) origin is always reachable → true.
        ///
        /// <para>Pass a token with a hard deadline (e.g. <c>CancelAfter(3s)</c>):
        /// <c>UnityWebRequest.timeout</c> alone doesn't reliably interrupt a stall at
        /// DNS/TLS setup (a dead VPN), so the loop aborts on the token instead — the
        /// difference between an instant offline fallback and a ~30s boot hang.</para></summary>
        public async Task<bool> HealthzAsync(string path = "/healthz", CancellationToken ct = default)
        {
            if (_local) return true;
            try
            {
                using var req = UnityWebRequest.Get(ResolveUrl(path));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = RequestTimeoutSeconds;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                req.certificateHandler = new AcceptAllCertificates();
#endif
                try { await AwaitRequest(req, req.SendWebRequest(), ct); }
                catch (OperationCanceledException) { return false; }
                bool ok = !LvnNetWait.Failed(req) && req.responseCode is >= 200 and < 300;
                if (ok) LvnNetworkStatus.MarkOnline("healthz ok");
                return ok;
            }
            catch { return false; }
        }

        // Silent prefetch variant: does NOT update the byte counters so the
        // progress bar doesn't see the parallel warm-start and jump backward.
        /// <summary>
        /// ОДИН ЗАХОД ЗА БАЙТАМИ — правила движка про сеть, записанные однажды.
        ///
        /// <para>Их пять, и все пять — про то, чем эта загрузка отличается от
        /// обычного веб-запроса: место в очереди загрузок (и обязательное его
        /// освобождение), срок держит СТОРОЖ ЗАСТОЯ, а не таймаут запроса
        /// (медленная сеть — не отказ, молчащая — отказ), самоподписанный
        /// сертификат принимается только в отладочной сборке, сетевой сбой
        /// ЗАСЧИТЫВАЕТСЯ (по этому счёту оболочка решает, что связи нет), и
        /// не-двухсотый код — это отказ, а не пустой файл.</para>
        ///
        /// <para>Записаны они были ТРИЖДЫ: у тихой предзагрузки, у одиночного
        /// захода и у пакетного. Отличались только тем, что считать по дороге,
        /// — а расхождение в любом из пяти читалось бы как «иногда офлайн не
        /// определяется» или «иногда качает вечно».</para>
        /// </summary>
        /// <summary>Что вернулось: тело и КОД. Код нужен докачке — сервер,
        /// не умеющий Range, отвечает на просьбу «с байта N» двумястами и
        /// присылает файл сначала.</summary>
        private readonly struct Fetched
        {
            public readonly byte[] Body;
            public readonly long Code;
            public Fetched(byte[] body, long code) { Body = body; Code = code; }
        }

        private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct,
                                                 Action<UnityWebRequest> onProgress)
            => (await GetAsync(url, ct, onProgress)).Body;

        /// <param name="prepare">что добавить к запросу до отправки (докачка
        /// ставит заголовок Range)</param>
        private async Task<Fetched> GetAsync(string url, CancellationToken ct,
                                             Action<UnityWebRequest> onProgress,
                                             Action<UnityWebRequest> prepare = null)
        {
            ThrowIfOffline();
            await _downloadSlots.WaitAsync(ct);
            try
            {
                var full = ResolveUrl(url);
                using var req = UnityWebRequest.Get(full);
                prepare?.Invoke(req);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 0; // the stall guard below owns the deadline
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                req.certificateHandler = new AcceptAllCertificates();
#endif
                var op = req.SendWebRequest();
                if (!await LvnNetWait.AwaitAsync(req, op, ct, StallTimeoutSeconds, onProgress))
                    throw new OperationCanceledException(ct);
                if (LvnNetWait.Failed(req))
                {
                    NoteFetchFailure(req);
                    throw new LvnFetchException((int)req.responseCode, "network", req.error ?? "network error");
                }
                if (req.responseCode is < 200 or >= 300)
                    throw new LvnFetchException((int)req.responseCode, "http_" + req.responseCode, $"GET {full}");
                return new Fetched(req.downloadHandler.data ?? Array.Empty<byte>(), req.responseCode);
            }
            finally { _downloadSlots.Release(); }
        }

        private Task<byte[]> FetchToMemoryPrefetch(string url, CancellationToken ct)
            => GetBytesAsync(url, ct, null);   // счётчиков не трогаем — в том и смысл

        // Downloads url into memory, updating byte-progress counters. No disk I/O
        // — used by RunBatchAsync so disk writes can be pipelined.
        private async Task<byte[]> FetchToMemory(string url, CancellationToken ct)
        {
            // Ждёт ДОМ; здесь остаётся только то, что принадлежит очереди
            // загрузок: сколько байт пришло и сколько их всего.
            lock (_inflight) { _bytesReceived[url] = 0; _bytesExpected[url] = 0; }
            return await GetBytesAsync(url, ct, r =>
            {
                lock (_inflight) _bytesReceived[url] = (long)r.downloadedBytes;
                if (_bytesExpected.GetValueOrDefault(url) == 0)
                {
                    var cl = r.GetResponseHeader("Content-Length");
                    if (cl != null && long.TryParse(cl, out var sz) && sz > 0)
                        lock (_inflight) _bytesExpected[url] = sz;
                }
            });
        }

        // Локальное чтение (jar:/file:) через UnityWebRequest — File.IO не
        // умеет внутрь APK.
        private static async Task<byte[]> FetchLocalAsync(string url)
        {
            using var req = UnityEngine.Networking.UnityWebRequest.Get(url);
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            return req.result == UnityEngine.Networking.UnityWebRequest.Result.Success
                ? req.downloadHandler.data : null;
        }

        private async Task<byte[]> DownloadBytes(string url, string dir, CancellationToken ct)
        {
            var path     = CachePath(dir, url, ".bin");
            var partPath = path + ".part";

            if (File.Exists(path))
                return await ReadAllBytesAsync(path, ct);

            // Сид из APK — раньше сети: первый вход не качает критичное вовсе.
            var seeded = await TrySeedAsync(url, path, ct);
            if (seeded != null) return seeded;

            lock (_notFound)
                if (_notFound.TryGetValue(url, out var at))
                {
                    if (Lvn.LvnClock.Wall() - at < NotFoundTtlSeconds)
                        throw new LvnFetchException(404, "http_404", url + " (cached 404)");
                    _notFound.Remove(url); // TTL вышел — пробуем сеть снова
                }

            return await TrackedFetch(url, async () =>
            {
                const int MaxAttempts = 10;
                lock (_inflight) _attempts[url] = 1;

                while (true)
                {
                    try
                    {
                        // Each retry reads the current .part size → resumes from there.
                        long resumeFrom = 0;
                        if (File.Exists(partPath))
                            resumeFrom = LvnQuiet.Try(() => new FileInfo(partPath).Length, 0L);

                        var bytes = await FetchResumable(url, partPath, resumeFrom, ct);

                        // Integrity: the version index carries each asset's sha256.
                        // A torn resume (server changed the file between two Range
                        // requests) would otherwise cache spliced bytes as valid
                        // forever. Mismatch → drop the .part and refetch clean.
                        // Exact entries only — a derived variant's inherited
                        // version describes its SOURCE, not these bytes.
                        var expect = IntegrityVersionFor(url);
                        if (expect != null && !Sha256Matches(bytes, expect))
                        {
                            LvnQuiet.Try(() => File.Delete(partPath));
                            throw new LvnFetchException(0, "integrity",
                                "sha256 mismatch for " + url + " — refetching");
                        }

                        lock (_inflight) _attempts.Remove(url);

                        if (File.Exists(path)) File.Delete(path);
                        File.Move(partPath, path);
                        return bytes;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (LvnFetchException ex) when (ex.Code == "network" && LvnNetworkStatus.IsOffline)
                    {
                        throw; // offline — retrying is pointless; caller falls back to cache
                    }
                    catch (LvnFetchException ex) when (ex.Status is >= 400 and < 500)
                    {
                        bool first;
                        lock (_notFound)
                        {
                            first = !_notFound.ContainsKey(url);
                            // Тоже реальное: срок «этого файла нет» обязан
                            // истекать и в фоне, иначе свёрнутая на час игра
                            // вернётся с той же протухшей памятью о 404.
                            _notFound[url] = Lvn.LvnClock.Wall();
                        }
                        // Info, not warning: a 4xx here is usually the EXPECTED
                        // steady state of an optional probe (.ktx2/.astc/@2k
                        // variants, demo-stub art) — a yellow triangle per asset
                        // per session reads like breakage and drowns real ones.
                        if (first) Debug.Log($"[content] {url} permanent {ex.Status} (silenced for this session)");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        int attempt;
                        lock (_inflight) attempt = _attempts[url] = _attempts.GetValueOrDefault(url, 1) + 1;
                        if (attempt > MaxAttempts)
                        {
                            Debug.LogWarning($"[content] {url} gave up after {MaxAttempts} attempts");
                            throw;
                        }
                        var backoff = LvnBackoff.DelaySeconds(attempt);
                        Debug.LogWarning($"[content] {url} attempt {attempt} failed, resume in {backoff:F1}s: {ex.Message}");
                        try { await Task.Delay(Mathf.RoundToInt(backoff * 1000f), ct); }
                        catch (OperationCanceledException) { throw; }
                    }
                }
            });
        }

        // Single streaming GET for the whole file. If resumeFrom > 0 sends
        // Range: bytes=N- so the server picks up from that offset. One HTTP
        // request per file — no chunk loop, no extra round-trips.
        private async Task<byte[]> FetchResumable(string url, string partPath, long resumeFrom, CancellationToken ct)
        {
            lock (_inflight) { _bytesReceived[url] = resumeFrom; }
            var got = await GetAsync(url, ct,
                r =>
                {
                    lock (_inflight) _bytesReceived[url] = resumeFrom + (long)r.downloadedBytes;
                    if (_bytesExpected.GetValueOrDefault(url) <= resumeFrom)
                    {
                        var cl = r.GetResponseHeader("Content-Length");
                        if (cl != null && long.TryParse(cl, out var sz) && sz > 0)
                            lock (_inflight) _bytesExpected[url] = resumeFrom + sz;
                    }
                },
                r => { if (resumeFrom > 0) r.SetRequestHeader("Range", $"bytes={resumeFrom}-"); });

            // Server returned 200 when we asked for 206 → no resume support,
            // overwrite .part with the full fresh response.
            bool overwrite = resumeFrom == 0 || (int)got.Code == 200;
            await AppendBytesAsync(partPath, got.Body, overwrite, ct);

            lock (_inflight)
            {
                var total = resumeFrom + got.Body.Length;
                _bytesReceived[url] = total;
                _bytesExpected[url] = total;
            }
            return await ReadAllBytesAsync(partPath, ct);
        }

        private static async Task AppendBytesAsync(string path, byte[] data, bool overwrite, CancellationToken ct)
        {
            await Task.Run(() =>
            {
                var mode = overwrite ? FileMode.Create : FileMode.Append;
                using var fs = new FileStream(path, mode, FileAccess.Write, FileShare.None);
                fs.Write(data, 0, data.Length);
            }, ct);
        }

        // Wraps the actual network work in the in-flight tracker so any cache-miss
        // shows up in the BatchTotal/BatchDone counters. Dedups duplicate calls to
        // the same url — second caller awaits the first one's task.
        private Task<T> TrackedFetch<T>(string url, Func<Task<T>> work)
        {
            lock (_inflight)
            {
                if (_inflight.TryGetValue(url, out var existing) && existing is Task<T> typed)
                    return typed;
            }
            var task = work();
            lock (_inflight)
            {
                _inflight[url] = task;
                BatchTotal++;
                LastStartedUrl = url;
            }
            _ = task.ContinueWith(_ =>
            {
                lock (_inflight)
                {
                    _inflight.Remove(url);
                    BatchDone++;
                    if (BatchDone >= BatchTotal) ClearBatchTally();
                }
            }, TaskScheduler.Default);
            return task;
        }

        // Retries with exponential backoff until the asset arrives or the token
        // fires. FetchOnce (private) does the single request with a short timeout
        // so a stuck connection can't hang the whole batch.
        private async Task<byte[]> Fetch(string url, CancellationToken ct)
        {
            lock (_inflight) _attempts[url] = 1;
            const int MaxAttempts = 5;
            while (true)
            {
                try
                {
                    var bytes = await FetchOnce(url, ct);
                    lock (_inflight) _attempts.Remove(url);
                    return bytes;
                }
                catch (OperationCanceledException) { throw; }
                catch (LvnFetchException ex) when (ex.Code == "network" && LvnNetworkStatus.IsOffline)
                {
                    throw; // offline — retrying is pointless; caller falls back to cache
                }
                catch (LvnFetchException ex) when (ex.Status is >= 400 and < 500)
                {
                    Debug.LogWarning($"[content] {url} permanent {ex.Status}: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    int attempt;
                    lock (_inflight) attempt = _attempts[url] = _attempts.GetValueOrDefault(url, 1) + 1;
                    if (attempt > MaxAttempts)
                    {
                        Debug.LogWarning($"[content] {url} gave up after {MaxAttempts} attempts: {ex.Message}");
                        throw;
                    }
                    var backoff = LvnBackoff.DelaySeconds(attempt);
                    Debug.LogWarning($"[content] {url} failed (was attempt {attempt - 1}): {ex.Message}; retry #{attempt} in {backoff:F1}s");
                    try { await Task.Delay(Mathf.RoundToInt(backoff * 1000f), ct); }
                    catch (OperationCanceledException) { throw; }
                }
            }
        }

        // Single attempt — downloads url into memory, no disk writes. Used for
        // text (scripts, version index) and on-demand bytes not worth persisting.
        private Task<byte[]> FetchOnce(string url, CancellationToken ct)
        {
            lock (_inflight) { _bytesReceived[url] = 0; }
            return GetBytesAsync(url, ct,
                r => { lock (_inflight) _bytesReceived[url] = (long)r.downloadedBytes; });
        }
    }
}
