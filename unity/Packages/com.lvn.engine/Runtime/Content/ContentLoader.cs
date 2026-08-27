using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.Content
{
    /// <summary>
    /// Downloads <c>.lvn</c> scripts and asset bytes, caches them on disk, and
    /// returns local data on subsequent reads. Cache key = <c>sha1(url@version)</c>
    /// — a re-uploaded asset (new hash in the version index) maps to a NEW cache
    /// file and is re-downloaded automatically, while the old file stays as an
    /// offline fallback.
    ///
    /// This is the low-level fetch/decode/cache engine ported from a shipping
    /// visual-novel client: disk cache, an in-memory sprite cache, dedup of
    /// in-flight fetches, a global HTTP/2 download semaphore, resumable retries
    /// with exponential backoff, content-version cache-busting, and byte-level
    /// progress for a loading HUD. <see cref="AssetScheduler"/> sits on top to
    /// prioritize a chapter's release set; <c>NetworkAssets</c> adapts it to the
    /// engine's <c>ILvnAssets</c> seam.
    /// </summary>
    public partial class ContentLoader
    {
        private readonly string _baseUrl;
        // True when the content origin is a local bundle (file:// on desktop, or
        // jar:file:// for Android StreamingAssets). Local reads are always
        // available — they skip the offline gate and the ?v= cache-buster (which
        // would corrupt a file path), so an exported game plays with no server.
        private readonly bool _local;
        private readonly string _cacheRoot;
        private readonly string _scriptCacheDir;
        private readonly string _assetCacheDir;

        // Content-version index: path → sha256, fetched from
        // /content/asset-versions.json. Folded into the disk-cache key so a
        // re-uploaded asset (new hash) maps to a NEW cache file and is
        // re-downloaded automatically, while the old file stays as an offline
        // fallback. Empty until LoadAssetVersionsAsync runs; an unknown asset
        // falls back to the legacy url-only key (still works, just not auto-busted).
        private Dictionary<string, string> _versions = new();
        private readonly object _versionsLock = new();
        private const string VersionsPath = "/content/asset-versions.json";


        // Hard per-request timeout. Deliberately short: a dead/blackhole socket
        // must fail fast so chapter loading degrades to cache instead of hanging
        // (offline a UnityWebRequest can otherwise sit the full timeout). The
        // global LvnNetworkStatus flag is the fast path; this timeout is the LIVE
        // backstop for when the flag is stale (e.g. wifi dropped mid-session).
        private const int RequestTimeoutSeconds = 10;

        // Asset transfers use a STALL deadline instead of a total-time one: a
        // 5 MB background on a slow cell link legitimately takes >10s, and
        // killing it mid-body (then pinning the offline flag) turned one slow
        // file into a session-wide offline flap storm (live BlueStacks case:
        // "Request timeout" every few seconds all session). A transfer stays
        // alive for as long as its byte counter keeps moving; only a counter
        // FROZEN this long is a dead socket.
        private const int StallTimeoutSeconds = 15;

        // Classify a failed request: only a connect-level failure (no bytes,
        // not a timeout/abort) means THE NETWORK is gone and pins the global
        // offline flag. A transfer that died mid-body or timed out is
        // congestion — this fetch failed, the network may be fine; the caller's
        // retry layer handles it without dragging the whole app offline.
        /// <summary>
        /// Ассет не доехал: адрес и код ответа. Движок сам никуда это не шлёт —
        /// он не знает про продуктовую аналитику и не должен, — но и молчать не
        /// вправе: для игрока это пропавшая картинка или тишина вместо музыки, а
        /// снаружи оно выглядит как «игра кривая». Подписывается оболочка.
        /// </summary>
        public static event Action<string, long> AssetFailed;





        // 1 while the background recovery probe is running (guards against starting
        // a second one on every subsequent failed fetch).
        private int _recovering;





        // Dedup tracker for in-flight fetches. Key = url, value = the running
        // task. Lets two callers (a preload + a later regular download) await the
        // same fetch instead of re-issuing it. Tasks self-evict on completion so
        // the dictionary doesn't leak.
        private readonly Dictionary<string, Task> _inflight = new();

        // Batch counters used by a network-progress HUD. A "batch" is the
        // contiguous run of fetches between idle moments — once every queued task
        // finishes, the counters reset to zero so the next batch starts clean.
        public int BatchTotal { get; private set; }
        public int BatchDone { get; private set; }
        public string LastStartedUrl { get; private set; }
        public bool BatchActive => BatchTotal > 0 && BatchDone < BatchTotal;

        // True while VerifyAsync is scanning local cache — the HUD shows a
        // "verifying files" state instead of a filename.
        public bool IsVerifying { get; private set; }

        // Retry count per url (1 = first try, 2+ = previous attempts failed).
        // Surfaced to the HUD so it can show "attempt N" on a flaky network.
        private readonly Dictionary<string, int> _attempts = new();

        // Session-scoped sprite cache. Sprites are keyed by URL so the same
        // background or portrait is decoded once — and BOUNDED: full-res RGBA32
        // decodes are big (a 1080p background ≈ 8 MB), so an unbounded cache is an
        // OOM on a large title. Over budget, the least-recently-requested entries
        // are destroyed — except anything touched within the grace window, which
        // is how "probably still on screen" art is protected without a pin API.
        private sealed class SpriteEntry
        {
            public Sprite Sprite;
            public long Bytes;
            public long Seq;   // request recency (monotonic)
            public float At;   // request time (realtime seconds)
            public int Pins;   // >0 ⇒ a live consumer (e.g. a built Spine skeleton
                               // whose atlas references this texture) forbids eviction
        }

        private long _spriteSeq;
        private long _spriteBytes;

        /// <summary>Decoded-sprite memory budget. Over it, the least-recently-used
        /// sprites are evicted (grace-protected — see <see cref="SpriteEvictionGraceSeconds"/>).
        /// Tune down for low-memory targets.</summary>
        public static long SpriteCacheBudgetBytes = 384L << 20;

        /// <summary>Запись, запрошенную недавно, не вытесняем: только что
        /// затребованный арт почти наверняка ещё на экране.</summary>
        public static float SpriteEvictionGraceSeconds = 60f;

        // Author-supplied display labels for urls, persistent across the session.
        private readonly Dictionary<string, string> _aliases = new();
        public void RegisterAlias(string url, string alias)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(alias)) return;
            lock (_aliases) _aliases[url] = alias;
        }
        public string AliasOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            lock (_aliases)
            {
                if (_aliases.TryGetValue(url, out var a)) return a;
                // Aliases are stored as relative paths (/content/...) but url may
                // be an absolute URL — try matching by path only.
                try
                {
                    var path = new System.Uri(url).AbsolutePath;
                    return _aliases.TryGetValue(path, out var b) ? b : null;
                }
                catch { return null; }
            }
        }

        // Per-url byte progress, updated each frame while a fetch runs. Lets the
        // HUD show byte-level progress instead of file-count progress, which
        // feels stuck when a single file downloads for many seconds.
        private readonly Dictionary<string, long> _bytesExpected = new();
        private readonly Dictionary<string, long> _bytesReceived = new();

        // Label of the file currently being fetched (alias or short name).
        public string CurrentFileLabel { get; private set; }
        public long BatchBytesExpected
        {
            get { lock (_inflight) { long s = 0; foreach (var v in _bytesExpected.Values) s += v; return s; } }
        }
        public long BatchBytesReceived
        {
            get { lock (_inflight) { long s = 0; foreach (var v in _bytesReceived.Values) s += v; return s; } }
        }

        /// <summary>Единый снимок сетевой активности для глобального индикатора
        /// («что сейчас качается» поверх всей оболочки): файлы в полёте,
        /// счётчики батча и суммарные байты — всё под одним замком, чтобы
        /// пилюля не ловила рассинхронные числа. Механика загрузки размазана
        /// по фазам (скачать-всё, прелоад главы, стриминг) — здесь их общее
        /// окно.</summary>
        public (int inflight, int batchTotal, int batchDone, long received, long expected, string label) Transfers()
        {
            lock (_inflight)
            {
                int n = 0;
                string firstUrl = null;
                foreach (var k in _inflight.Keys)
                {
                    if (k == "__preload_batch__") continue;
                    n++;
                    firstUrl ??= k;
                }
                long rec = 0, exp = 0;
                foreach (var v in _bytesReceived.Values) rec += v;
                foreach (var v in _bytesExpected.Values) exp += v;
                // Имя для полной карточки индикатора: алиас текущего файла
                // батча, иначе короткое имя первого файла в полёте.
                string label = CurrentFileLabel;
                if (string.IsNullOrEmpty(label) && firstUrl != null)
                {
                    label = AliasOf(firstUrl);
                    if (string.IsNullOrEmpty(label))
                    {
                        var bare = LvnUrl.Bare(firstUrl);
                        int slash = bare.LastIndexOf('/');
                        label = slash >= 0 ? bare.Substring(slash + 1) : bare;
                    }
                }
                return (n, BatchTotal, BatchDone, rec, exp, label);
            }
        }

        public ContentLoader(string baseUrl, string cacheRoot = null)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _local = _baseUrl.StartsWith("file://") || _baseUrl.StartsWith("jar:");
            cacheRoot ??= Path.Combine(Application.persistentDataPath, "cache");
            _cacheRoot = cacheRoot;
            _scriptCacheDir = Path.Combine(cacheRoot, "scripts");
            _assetCacheDir = Path.Combine(cacheRoot, "assets");
            Directory.CreateDirectory(_scriptCacheDir);
            Directory.CreateDirectory(_assetCacheDir);
            SweepStaleParts();
            TuneBudgetForDevice();
            Application.lowMemory += OnLowMemory;
        }

        // Бюджет по УСТРОЙСТВУ, а не константа: 384 МБ декода на телефоне с
        // 512 МБ RAM — это смертный приговор от системы. Шестая часть RAM в
        // коридоре 96..384 МБ; выполняется один раз (владелец budget-поля —
        // хост, повторные лоадеры не перетирают ручную настройку).
        private static bool _budgetTuned;


        // Resume files (.part) enable interrupted downloads to continue — but one
        // abandoned mid-download (its version has moved on, so its cache key will
        // never be requested again) would sit on disk forever. Sweep any not
        // touched for a week; a live download re-creates its .part instantly.
        private void SweepStaleParts()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (var f in new DirectoryInfo(_assetCacheDir).GetFiles("*.part"))
                    if (f.LastWriteTimeUtc < cutoff)
                        LvnQuiet.Try(f.Delete);
            }
            catch { /* best-effort housekeeping */ }
        }


        /// <summary>Fetches the server's content-version index (path → sha256) and
        /// folds it into the disk-cache key, so changed assets auto-invalidate.
        /// Call once early in boot, before the verify/preload pass. Always fetched
        /// fresh (never disk-cached) and mirrored to disk so a later offline
        /// launch can still resolve versioned cache keys. Network failure is
        /// non-fatal: fall back to the last persisted index, else legacy url-only
        /// keys.</summary>
        public async Task LoadAssetVersionsAsync(CancellationToken ct = default)
        {
            var persistPath = Path.Combine(_cacheRoot, "asset-versions.json");
            try
            {
                // Single attempt (not Fetch's retry-with-backoff): if we're
                // offline this fails fast on host-resolve instead of stalling
                // boot, and we immediately fall back to the disk mirror.
                var bytes = await FetchOnce(VersionsPath, ct);
                var map = ParseVersions(Encoding.UTF8.GetString(bytes));
                if (map.Count > 0)
                {
                    Dictionary<string, string> prev;
                    lock (_versionsLock) { prev = _versions; _versions = map; }
                    EvictStaleSprites(prev, map);
                    try { await WriteAllBytesAsync(persistPath, bytes, ct); } catch { /* mirror is best-effort */ }
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* offline / 404 — fall back to last-known index below */ }

            try
            {
                if (File.Exists(persistPath))
                {
                    var json = Encoding.UTF8.GetString(await ReadAllBytesAsync(persistPath, ct));
                    var map = ParseVersions(json);
                    if (map.Count > 0) lock (_versionsLock) _versions = map;
                }
            }
            catch { /* no usable index — legacy url-only keys */ }
        }

        private static Dictionary<string, string> ParseVersions(string json)
        {
            try
            {
                var map = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                return map ?? new Dictionary<string, string>();
            }
            catch { return new Dictionary<string, string>(); }
        }

        // sha256 for a content url from the version index, or null if unknown.
        // Index keys are content-relative paths ("bg/ch1/x.png"); urls arrive as
        // "/content/bg/ch1/x.png" (or absolute urls). Try both the post-/content/
        // form and the raw-with-"content/" form.
        private string VersionFor(string url)
        {
            Dictionary<string, string> map;
            lock (_versionsLock) map = _versions;
            var v = Lookup(map, url);
            // Транскод наследует версию ИСХОДНИКА — значит перекодировка на
            // сервере (починенный кодировщик, выброшенный битый файл) для
            // клиента невидима: картинка-источник не менялась, ключ прежний, и
            // старый .ktx2 едет из кэша вечно. Живой случай 26.08: слои
            // Виктории лежали закодированными из давно заменённого арта —
            // сжатые по горизонтали и с раскрошенной мелкой деталью
            // («пиксели на украшении»). Поколение в ключе — способ разом
            // объявить все прежние транскоды недействительными; поднимать
            // всякий раз, когда меняется контракт кодирования.
            if (v != null && IsTranscodeUrl(url)) return v + "+k" + Ktx2CacheEpoch;
            return v;
        }

        private const int Ktx2CacheEpoch = 2;

        private static bool IsTranscodeUrl(string url) =>
            !string.IsNullOrEmpty(url) &&
            (url.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase) ||
             url.EndsWith(".astc", StringComparison.OrdinalIgnoreCase));

        // Version for INTEGRITY checks: exact index entries only. A derived
        // variant inherits its source's version (see Lookup) — right for cache
        // keys and ?v= busting, fatally wrong for integrity: an encode's bytes
        // never hash to the source image's sha256, so checking them against it
        // refetches the file forever.
        private string IntegrityVersionFor(string url)
        {
            Dictionary<string, string> map;
            lock (_versionsLock) map = _versions;
            return Lookup(map, url, allowDerived: false);
        }

        internal static string Lookup(Dictionary<string, string> map, string url, bool allowDerived = true)
        {
            if (string.IsNullOrEmpty(url) || map == null || map.Count == 0) return null;
            var path = url;
            if (path.StartsWith("http://") || path.StartsWith("https://"))
            {
                path = LvnQuiet.Try(() => new System.Uri(path).AbsolutePath, path);
            }
            var p = path.TrimStart('/');                                  // content/bg/... or bg/...
            var afterContent = p.StartsWith("content/") ? p.Substring("content/".Length) : p;
            if (map.TryGetValue(afterContent, out var v)) return v;
            if (map.TryGetValue(p, out var v2)) return v2;
            // Derived display variants ("X@2k.png" downscales, "X.ktx2"/"X.astc"
            // transcodes) are deliberately absent from the index: they appear on
            // the server lazily and versioning them made first visits reload
            // chapters mid-play. They must inherit the version OF THE SOURCE
            // IMAGE instead — a versionless variant gets a permanent cache key,
            // and the encode of a photo the author has since replaced would be
            // served from cache forever (live-hit: the heroine stayed a blurry
            // thumbnail through three art replacements).
            if (allowDerived)
                foreach (var candidate in SourceCandidates(afterContent))
                    if (map.TryGetValue(candidate, out var sv)) return sv;
            return null;
        }

        /// <summary>Index paths a derived variant may have been produced from,
        /// in probe order. Empty for a path that isn't a variant. Pure —
        /// internal for tests.</summary>
        internal static IEnumerable<string> SourceCandidates(string path)
        {
            int dot = path.LastIndexOf('.');
            if (dot <= 0) yield break;
            var ext = path.Substring(dot).ToLowerInvariant();
            var stem = path.Substring(0, dot);
            bool transcoded = ext == ".ktx2" || ext == ".astc";
            bool downscaled = stem.EndsWith("@2k", StringComparison.Ordinal);
            if (!transcoded && !downscaled) yield break;
            if (downscaled) stem = stem.Substring(0, stem.Length - "@2k".Length);
            if (!transcoded) { yield return stem + ext; yield break; }
            // A transcode hides the source's extension — try the same set the
            // server's encoder probes (server/astc.go sourceExts).
            yield return stem + ".png";
            yield return stem + ".jpg";
            yield return stem + ".jpeg";
        }


        // Scripts ship from the server and change often — skip the on-disk cache
        // and refetch every time (a few KB, cheap; stale copies cause "why is the
        // old version playing" bugs). `singleAttempt` skips the retry/backoff loop
        // — use it for non-critical boot fetches so an offline launch fails fast.
        public async Task<string> DownloadScriptText(string scriptUrl, CancellationToken ct = default,
            bool singleAttempt = false)
        {
            var bytes = singleAttempt
                ? await FetchOnce(scriptUrl, ct)
                : await Fetch(scriptUrl, ct);
            return Encoding.UTF8.GetString(bytes);
        }

        // Version-pinned script load for chapter playback. Unlike
        // DownloadScriptText (always-fresh, no disk cache) this CACHES the script
        // on disk under a version-folded key, so a chapter opens OFFLINE if ever
        // played online, the version is pinned for the whole session, and an
        // edited script (new hash → new key) is re-downloaded on the next entry.
        // Returns null only if there's no cache AND we can't fetch.
        public async Task<string> DownloadScriptCached(string scriptUrl, CancellationToken ct = default)
        {
            var path = CachePath(_scriptCacheDir, scriptUrl, ".txt");
            if (File.Exists(path))
            {
                try { return await ReadAllTextAsync(path, ct); }
                catch { /* unreadable — fall through to refetch */ }
            }
            try
            {
                var bytes = await FetchOnce(scriptUrl, ct);
                try
                {
                    await WriteAllBytesAsync(path, bytes, ct);
                    await WriteScriptUrlSidecar(path, scriptUrl, ct);
                }
                catch { /* cache write best-effort */ }
                return Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Offline and not cached for this version. Last resort: a previously
                // cached version OF THE SAME url (older but the right chapter).
                var stale = NewestCachedScript(scriptUrl);
                if (stale != null)
                {
                    try { return await ReadAllTextAsync(stale, ct); } catch { }   // старая копия не читается — вернём null, вызывающий сходит в сеть
                }
                return null;
            }
        }

        // Fire-and-forget: pull the latest version of a script to disk so the
        // NEXT chapter entry picks it up. `reloadIndex` re-reads the (no-store)
        // version index first to detect a hash published since boot.
        public void RefreshScriptInBackground(string scriptUrl, bool reloadIndex = true)
        {
            if (string.IsNullOrEmpty(scriptUrl)) return;
            LvnAsync.Fire(RefreshScriptAsync(scriptUrl, reloadIndex), "RefreshScript");
        }

        private async Task RefreshScriptAsync(string scriptUrl, bool reloadIndex)
        {
            try
            {
                if (reloadIndex)
                    await LoadAssetVersionsAsync(CancellationToken.None);
                var path = CachePath(_scriptCacheDir, scriptUrl, ".txt");
                if (File.Exists(path)) return; // newest version already cached
                var bytes = await FetchOnce(scriptUrl, CancellationToken.None);
                await WriteAllBytesAsync(path, bytes, CancellationToken.None);
                await WriteScriptUrlSidecar(path, scriptUrl, CancellationToken.None);
                Debug.Log($"[content] script cache refreshed: {scriptUrl}");
            }
            catch { /* best-effort background refresh */ }
        }

        // Finds the most recently written cached version OF THE SAME script url —
        // the offline fallback. The version-folded filename (sha1(url@version))
        // can't be reversed, so each cached script is written with a `.url` sidecar
        // holding its plain url; we only accept a `.txt` whose sidecar matches the
        // requested url. Without this the fallback returned whatever chapter was
        // cached most recently — silently dropping the player into the wrong
        // chapter and saving the wrong ending. Returns null (→ Unavailable) rather
        // than ever serving a different script.
        private string NewestCachedScript(string scriptUrl)
        {
            if (string.IsNullOrEmpty(scriptUrl)) return null;
            try
            {
                var dir = new DirectoryInfo(_scriptCacheDir);
                if (!dir.Exists) return null;
                FileInfo newest = null;
                foreach (var f in dir.GetFiles("*.txt"))
                {
                    var sidecar = Path.ChangeExtension(f.FullName, ".url");
                    string cachedUrl = null;
                    try { if (File.Exists(sidecar)) cachedUrl = File.ReadAllText(sidecar).Trim(); }
                    catch { }   // сайдкар не прочёлся — считаем, что адреса рядом нет
                    if (cachedUrl != scriptUrl) continue; // different (or legacy, un-tagged) script
                    if (newest == null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;
                }
                return newest?.FullName;
            }
            catch { return null; }
        }

        // Records the plain url of a just-cached script beside its version-folded
        // cache file, so the offline fallback can match cached versions to the
        // requested url (see NewestCachedScript).
        private static async Task WriteScriptUrlSidecar(string scriptPath, string scriptUrl, CancellationToken ct)
        {
            try
            {
                await WriteAllBytesAsync(Path.ChangeExtension(scriptPath, ".url"),
                    Encoding.UTF8.GetBytes(scriptUrl), ct);
            }
            catch { /* sidecar is best-effort; a missing one just disables offline fallback for this file */ }
        }

        public Task<byte[]> DownloadAssetBytes(string assetUrl, CancellationToken ct = default) =>
            DownloadBytes(assetUrl, _assetCacheDir, ct);


        /// <summary>The longest texture side kept on mobile. Art above it is
        /// GPU-resampled once at load; typical phone screens are ≤ ~2400 px, so
        /// visually this is lossless while a 4K background drops 4× in memory.</summary>
        internal const int MobileMaxTextureSize = 2560;

        /// <summary>The longest texture side kept everywhere ELSE (editor,
        /// desktop, WebGL). Looser than mobile — desktop GPUs have the VRAM for
        /// 4K art — but still a hard ceiling: raw Spine page exports run
        /// 7708×8252 (254 MB of RGBA), and before this cap the non-mobile
        /// platforms uploaded that whole thing (the mobile-only check silently
        /// exempted the shipping WebGL build, the worst place to spend it).</summary>
        internal const int DesktopMaxTextureSize = 4096;















        /// <summary>Downloads an audio asset through UnityWebRequestMultimedia so
        /// the engine decodes the format streaming-style on the main thread (the
        /// correct path — never hand-roll a PCM parser). Caches the raw bytes on
        /// disk, then loads the clip from the cached file.</summary>
        public async Task<AudioClip> DownloadAudioClipAsync(string url, CancellationToken ct = default)
        {
            var path = CachePath(_assetCacheDir, url, ".audio");
            if (!File.Exists(path))
            {
                var bytes = await Fetch(url, ct);
                await WriteAllBytesAsync(path, bytes, ct);
            }
            var fileUrl = "file://" + path;
            var type = GuessAudioType(url);
            using var req = UnityWebRequestMultimedia.GetAudioClip(fileUrl, type);

            await AwaitRequest(req, req.SendWebRequest(), ct);
            if (req.result is UnityWebRequest.Result.ConnectionError or UnityWebRequest.Result.DataProcessingError)
                return null;
            return DownloadHandlerAudioClip.GetContent(req);
        }

        private static AudioType GuessAudioType(string url)
        {
            var lower = url.ToLowerInvariant();
            if (lower.EndsWith(".ogg")) return AudioType.OGGVORBIS;
            if (lower.EndsWith(".wav")) return AudioType.WAV;
            if (lower.EndsWith(".mp3")) return AudioType.MPEG;
            return AudioType.UNKNOWN;
        }

        /// <summary>Kicks off a background fetch for <paramref name="url"/> with
        /// the given <paramref name="kind"/> ("sprite"|"audio"|"script"|"bin").
        /// Idempotent — if the same url is already being prefetched (or cached on
        /// disk) this is essentially a no-op. Returns the underlying task so
        /// callers can await it.</summary>
        public Task Prefetch(string url, string kind, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url)) return Task.CompletedTask;
            return kind switch
            {
                "script" => DownloadScriptText(url, ct),
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

            const string batchKey = "__preload_batch__";
            Task<byte[]> batchTask;
            lock (_inflight)
            {
                if (_inflight.ContainsKey(batchKey)) return _inflight[batchKey];
                // Чистый старт: словари байтов копят и одиночные фетчи
                // (фоновый стриминг), и их мусор въезжал в прогресс батча —
                // «Скачано 131 из 135» при пустой очереди (живой скрин).
                _bytesReceived.Clear();
                _bytesExpected.Clear();
                BatchTotal     = pending.Count;
                BatchDone      = 0;
                batchTask      = RunBatchAsync(pending, ct);
                _inflight[batchKey] = batchTask;
                LastStartedUrl = pending[0].Url;
            }
            _ = batchTask.ContinueWith(_ =>
            {
                lock (_inflight)
                {
                    _inflight.Remove(batchKey);
                    BatchTotal     = 0;
                    BatchDone      = 0;
                    LastStartedUrl = null;
                    _attempts.Clear();
                    _bytesExpected.Clear();
                    _bytesReceived.Clear();
                }
            }, TaskScheduler.Default);
            return batchTask;
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
                        // While the global flag says offline a timed retry is
                        // pointless — the fetch fast-fails on the SAME flag without
                        // touching the wire. Sleep on the status change instead
                        // (the recovery probe flips it back), capped at the same
                        // backoff so a dead server still exhausts retries normally.
                        try
                        {
                            if (!_local && LvnNetworkStatus.IsOffline)
                                await DelayOrOnlineAsync(backoff, ct);
                            else
                                await Task.Delay(Mathf.RoundToInt(backoff * 1000f), ct);
                        }
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
        public bool HasCachedAssets()
        {
            try
            {
                return Directory.Exists(_assetCacheDir) &&
                       Directory.EnumerateFiles(_assetCacheDir, "*.bin").Any();
            }
            catch { return false; }
        }

        /// <summary>True when the content origin is a local bundle (StreamingAssets
        /// via file://). For the offline policy this means everything is "cached"
        /// and always reachable, so a bundled build lands on ReadyFromCache.</summary>
        public bool IsLocal => _local;

        /// <summary>True if the version-pinned script for <paramref name="scriptUrl"/>
        /// is on disk. Pure disk check (no network) — used by the offline policy.
        /// A local bundle is authoritative and complete, so it always reports true.</summary>
        public bool IsScriptCached(string scriptUrl)
        {
            if (string.IsNullOrEmpty(scriptUrl)) return false;
            if (_local) return true;
            try { return File.Exists(CachePath(_scriptCacheDir, scriptUrl, ".txt")); }
            catch { return false; }
        }

        /// <summary>True if the asset bytes for <paramref name="url"/> are on disk
        /// under the current version key. Pure disk check (no network). A local
        /// bundle reports true (the asset ships inside the build).</summary>
        public bool IsAssetCached(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (_local) return true;
            try { return File.Exists(CachePath(_assetCacheDir, url, ".bin")); }
            catch { return false; }
        }

        /// <summary>Scans <paramref name="urls"/> against the local asset cache
        /// and returns the subset that are missing. Sets IsVerifying during the
        /// scan so the HUD can show a verifying state instead of filenames.</summary>
        public async Task<IReadOnlyList<string>> VerifyAsync(
            IReadOnlyList<string> urls, CancellationToken ct)
        {
            if (urls == null || urls.Count == 0) return Array.Empty<string>();
            IsVerifying = true;
            lock (_inflight)
            {
                BatchTotal       = urls.Count;
                BatchDone        = 0;
                CurrentFileLabel = null;
                LastStartedUrl   = null;
            }
            var missing = new List<string>();
            foreach (var url in urls)
            {
                try { ct.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException) { IsVerifying = false; throw; }
                if (!File.Exists(CachePath(_assetCacheDir, url, ".bin")))
                    missing.Add(url);
                lock (_inflight) BatchDone++;
                try { await Task.Yield(); }
                catch (OperationCanceledException) { IsVerifying = false; throw; }
            }
            lock (_inflight)
            {
                BatchTotal       = 0;
                BatchDone        = 0;
                CurrentFileLabel = null;
                LastStartedUrl   = null;
            }
            IsVerifying = false;
            return missing;
        }

        // Negative cache С TTL: url, на который сервер ответил 4xx, не
        // передёргивается на каждую перестройку экрана — но и не хоронится на
        // всю сессию. Сервер ГЕНЕРИТ варианты (@2k/@mini/ktx2) лениво: первый
        // запрос честно 404, файл готов через секунды — вечный кэш оставлял
        // витрину на полноразмерах до перезапуска (живой лог «ok via full»
        // при готовых mini). Две минуты — с запасом на самое долгое кодирование.
        private readonly Dictionary<string, float> _notFound = new();
        private const float NotFoundTtlSeconds = 120f;

        // ── сид первого входа (StreamingAssets/lvn-seed) ─────────────────────
        // APK везёт критичные файлы вводной: первый запуск одевает первую
        // сцену БЕЗ сети вообще. Индекс (index.json со списком rel-путей)
        // читается один раз; промах мимо индекса не стоит ни одного запроса.
        // Сид хранит ОРИГИНАЛЫ: запрос @2k-варианта нормализуется к базе, а
        // потолок декода (MobileMaxTextureSize) ужимает картинку сам.
        private string _seedBase;
        private HashSet<string> _seedIndex;
        private Task _seedLoad;

        /// <summary>Включить сид-источник (jar:file://…/lvn-seed или file://…).
        /// Безопасно звать всегда: без index.json сид просто молчит.</summary>
        public void EnableSeed(string seedBase)
        {
            if (string.IsNullOrEmpty(seedBase)) return;
            _seedBase = seedBase.TrimEnd('/');
            _seedLoad = LoadSeedIndexAsync();
        }

        private async Task LoadSeedIndexAsync()
        {
            try
            {
                var raw = await FetchLocalAsync(_seedBase + "/index.json");
                var set = new HashSet<string>();
                if (raw != null)
                {
                    var arr = Newtonsoft.Json.Linq.JArray.Parse(Encoding.UTF8.GetString(raw));
                    foreach (var t in arr)
                    {
                        var s = (string)t;
                        if (!string.IsNullOrEmpty(s)) set.Add(s.TrimStart('/'));
                    }
                }
                _seedIndex = set;
                if (set.Count > 0) Debug.Log($"[content] сид первого входа: {set.Count} файлов в APK");
            }
            catch { _seedIndex = new HashSet<string>(); }
        }


        private async Task<byte[]> TrySeedAsync(string url, string cachePath, CancellationToken ct)
        {
            if (_seedBase == null) return null;
            // Сид мог не прочитаться (нет файла, битый zip) — это не повод
            // валить загрузку: без него просто пойдём в сеть.
            if (_seedLoad != null) { try { await _seedLoad; } catch { } _seedLoad = null; }
            if (_seedIndex == null || _seedIndex.Count == 0) return null;
            int at = url.IndexOf("/content/", StringComparison.Ordinal);
            if (at < 0) return null;
            var rel = url.Substring(at + 1);          // "content/bg/x@2k.jpg"
            var baseRel = DownloadPolicy.StripVariant(rel);
            string hit = _seedIndex.Contains(rel) ? rel
                : _seedIndex.Contains(baseRel) ? baseRel : null;
            if (hit == null) return null;
            var bytes = await FetchLocalAsync(_seedBase + "/" + hit);
            if (bytes == null || bytes.Length == 0) return null;
            // Сид может отстать от живого контента (арт обновили, APK старый).
            // При живой сети протухший сид пропускаем — качается свежее; в
            // офлайне старый арт лучше чёрного экрана. Следующая сборка APK
            // перевозит свежий сид сама (его кладёт серверный экспорт).
            var expect = IntegrityVersionFor(url);
            bool stale = expect != null && !Sha256Matches(bytes, expect);
            if (stale && LvnNetworkStatus.IsOnline) return null;
            if (!stale)
            {
                try { await WriteAllBytesAsync(cachePath, bytes, ct); }
                catch { /* кэш — ускорение, не условие */ }
            }
            return bytes;
        }







        private string ResolveUrl(string url)
        {
            if (url.StartsWith("file://")) return url;
            string full;
            if (url.StartsWith("http://") || url.StartsWith("https://"))
                full = EncodeUrlPath(url);
            else
            {
                if (!url.StartsWith("/")) url = "/" + url;
                // Кодируем ТОЛЬКО сетевой адрес. У офлайн-сборки база — file://
                // или jar:file://, и оттуда путь уходит в File.Exists и в чтение
                // с диска: закодированный «%20» там означал бы файл, которого
                // нет, и офлайн-игра осталась бы без картинок ради починки
                // сетевого случая.
                full = _local ? _baseUrl + url : _baseUrl + EncodeUrlPath(url);
            }
            // A local bundle reads files by path — a ?v= query would corrupt it.
            if (_local) return full;
            // Append the content version as a query param so the device's HTTP
            // cache treats each asset version as a distinct immutable resource.
            var ver = VersionFor(url);
            if (ver != null)
            {
                var sep = full.Contains('?') ? '&' : '?';
                full += sep + "v=" + ver.Substring(0, Math.Min(12, ver.Length));
            }
            return full;
        }

        /// <summary>
        /// Проценты-кодирование пути URL, посегментно.
        ///
        /// <para>Художник приносит файлы с пробелами, скобками и кириллицей в
        /// именах — «Снимок экрана 2025-01-21.png», «cover (1).jpg», — и в
        /// манифест они попадают как есть. UnityWebRequest такой адрес НЕ
        /// экранирует: на одной платформе запрос уходит сырым и сервер отвечает
        /// 400, на другой — падает разбор Uri. Промах при этом выглядит как
        /// «пропала картинка», и ищут его в контенте, а не в транспорте.</para>
        ///
        /// <para>Кодируем именно сегменты: «/» обязан остаться разделителем, а
        /// query (?v=…) мы дописываем сами и трогать его нельзя. Сегмент,
        /// который уже закодирован, пропускаем — иначе %20 превратится в %2520
        /// и сломается ровно то, что работало.</para>
        /// </summary>
        /// <remarks>Публичный, потому что тем же адресом ходит слой UI
        /// (Lvn.Engine.UI — отдельная сборка) и встраивающий хост: две копии
        /// этого кодирования разъехались бы, а промах выглядит как «пропала
        /// картинка».</remarks>
        public static string EncodeUrlPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            // Локальные адреса не кодируем НИКОГДА: за file:// и jar:file://
            // стоит чтение с диска (File.Exists, распаковка из APK), и «%20»
            // там означает файл, которого нет. Проверка живёт здесь, а не у
            // вызывающего: метод публичный, и хост позовёт его как придётся.
            if (url.StartsWith("file://") || url.StartsWith("jar:")) return url;

            // Схема и хост остаются как есть: кодировать надо путь, а не адрес.
            int start = 0;
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                int slash = url.IndexOf('/', scheme + 3);
                if (slash < 0) return url; // адрес без пути
                start = slash;
            }
            // Хвост с query/фрагментом не наш — оставляем нетронутым.
            int cut = url.IndexOfAny(new[] { '?', '#' }, start);
            string head = url.Substring(0, start);
            string path = cut < 0 ? url.Substring(start) : url.Substring(start, cut - start);
            string tail = cut < 0 ? "" : url.Substring(cut);

            var parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0 || AlreadyEncoded(parts[i])) continue;
                parts[i] = Uri.EscapeDataString(parts[i]);
            }
            return head + string.Join("/", parts) + tail;
        }

        /// Сегмент считается закодированным, если в нём есть «%XX» — второй
        /// проход по такому сегменту только испортил бы его.
        private static bool AlreadyEncoded(string segment)
        {
            for (int i = 0; i + 2 < segment.Length; i++)
            {
                if (segment[i] != '%') continue;
                if (Uri.IsHexDigit(segment[i + 1]) && Uri.IsHexDigit(segment[i + 2])) return true;
            }
            return false;
        }

        // On-disk cache file for a content URL: sha1(url@version) hex + ext, where
        // `version` is the asset's sha256 from the version index. Folding the
        // version into the key means a re-uploaded asset gets a fresh cache file
        // (auto-invalidation) without clobbering the old one. Unknown/unversioned
        // assets fall back to sha1(url) — legacy behaviour.
        private string CachePath(string dir, string url, string ext)
        {
            var ver = VersionFor(url);
            return Path.Combine(dir, HashKey(url, ver) + ext);
        }

        /// <summary>Content-integrity check: does the payload hash to the version
        /// index's sha256 hex? Exposed for tests.</summary>
        internal static bool Sha256Matches(byte[] data, string expectedHex)
        {
            if (data == null || string.IsNullOrEmpty(expectedHex)) return false;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(data);
            if (expectedHex.Length != hash.Length * 2) return false;
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return string.Equals(sb.ToString(), expectedHex, StringComparison.OrdinalIgnoreCase);
        }

        // Pure cache-key hash, exposed for tests: sha1(url) or sha1(url@version).
        // ── умная уборка диска ───────────────────────────────────────────────
        // Кэш ключуется по url: общий арт двух глав — ОДИН файл, и он живёт,
        // пока его знает хоть одна глава манифеста («перс есть во второй главе
        // — с первой его не удаляют», правило Ильи). Мёртвые ключи (старые
        // версии после обновления арта, снятый контент) удаляются всегда; над
        // квотой уходят самые давние, защищённые (текущая/следующая глава,
        // вводная) — никогда.

        /// <summary>Ключи кэша, под которыми может лежать этот url: сам файл и
        /// все его варианты (@2k, @mini, .ktx2) — их и держит уборка живыми.</summary>
        public void AddLiveKeysFor(string url, HashSet<string> into)
        {
            if (string.IsNullOrEmpty(url) || into == null) return;
            void Add(string u)
            {
                if (!string.IsNullOrEmpty(u)) into.Add(HashKey(u, VersionFor(u)));
            }
            Add(url);
            var v = DownloadPolicy.DownscaleVariant(url);
            if (v != null)
            {
                // Все ступени живые: игрок может переключать «Качество арта».
                Add(v.Replace(DownloadPolicy.PreferredSuffix, "@2k"));
                Add(v.Replace(DownloadPolicy.PreferredSuffix, "@1440"));
                Add(v.Replace(DownloadPolicy.PreferredSuffix, "@1k"));
                Add(v.Replace(DownloadPolicy.PreferredSuffix, "@mini"));
                Add(Ktx2UrlFor(url));
            }
        }

        /// <summary>Удалить один закэшированный ассет (и его ktx2-транскод) с
        /// диска — чистка противоположного бокса при смене «Качества арта».</summary>
        public bool DeleteCachedAsset(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            bool any = false;
            try
            {
                var path = CachePath(_assetCacheDir, url, ".bin");
                if (File.Exists(path)) { File.Delete(path); any = true; }
                var k = Ktx2UrlFor(url);
                if (k != null)
                {
                    var kp = CachePath(_assetCacheDir, k, ".bin");
                    if (File.Exists(kp)) { File.Delete(kp); any = true; }
                }
            }
            catch { } // файл держит другой процесс или его уже нет — забыть
            return any;
        }

        /// <summary>Занято дисковым кэшем ассетов (байты), на пуле потоков.</summary>
        public Task<long> AssetCacheDiskUsageAsync() => Task.Run(() =>
        {
            long total = 0;
            try
            {
                foreach (var f in new DirectoryInfo(_assetCacheDir).GetFiles("*.bin"))
                    total += f.Length;
            }
            catch { } // папки ещё нет или её читают — покажем ноль, это лишь справка
            return total;
        });

        /// <summary>Стереть скачанное («Удалить загруженное»): весь дисковый кэш
        /// ассетов. Сид в APK цел, RAM-кэш доигрывает своё — дальше стриминг
        /// пересоберёт нужное. На пуле потоков.</summary>
        public Task<long> ClearAssetCacheAsync() => Task.Run(() =>
        {
            long freed = 0;
            try
            {
                foreach (var f in new DirectoryInfo(_assetCacheDir).GetFiles())
                {
                    long sz = f.Length;
                    // Занятый файл пропускаем: чистка — не транзакция, что
                    // не удалилось сейчас, удалится в следующий раз.
                    try { f.Delete(); freed += sz; } catch { }
                }
            }
            catch { } // папки нет — стирать нечего
            return freed;
        });

        /// <summary>Убрать из дискового кэша ассетов мёртвое и, над квотой,
        /// давнее. Файловый IO — на пуле потоков; спрайт-кэш RAM не трогается.</summary>
        public Task<(int removed, long freed)> SweepAssetCacheAsync(
            HashSet<string> liveKeys, HashSet<string> protectedKeys, long quotaBytes)
            => Task.Run(() =>
            {
                int removed = 0; long freed = 0;
                try
                {
                    var files = new DirectoryInfo(_assetCacheDir).GetFiles("*.bin");
                    var list = new List<(string key, long size, double mtime)>(files.Length);
                    var byKey = new Dictionary<string, FileInfo>(files.Length);
                    foreach (var f in files)
                    {
                        var key = Path.GetFileNameWithoutExtension(f.Name);
                        list.Add((key, f.Length, f.LastWriteTimeUtc.Ticks / (double)TimeSpan.TicksPerSecond));
                        byKey[key] = f;
                    }
                    foreach (var key in PickCacheVictims(list, liveKeys, protectedKeys, quotaBytes))
                        if (byKey.TryGetValue(key, out var f))
                        {
                            long sz = f.Length;
                            try { f.Delete(); removed++; freed += sz; } catch { } // занят — оставим до следующей уборки
                        }
                }
                catch { /* уборка — сервис, не условие */ }
                return (removed, freed);
            });

        /// <summary>Чистая политика уборки, exposed for tests: мёртвые ключи —
        /// всегда; над квотой — старейшие из живых незащищённых.</summary>
        internal static List<string> PickCacheVictims(
            List<(string key, long size, double mtime)> files,
            HashSet<string> liveKeys, HashSet<string> protectedKeys, long quotaBytes)
        {
            var victims = new List<string>();
            long total = 0;
            foreach (var f in files) total += f.size;
            foreach (var f in files)
                if (liveKeys == null || !liveKeys.Contains(f.key))
                {
                    victims.Add(f.key);
                    total -= f.size;
                }
            if (total > quotaBytes)
            {
                var live = new List<(string key, long size, double mtime)>();
                foreach (var f in files)
                    if (liveKeys != null && liveKeys.Contains(f.key)
                        && (protectedKeys == null || !protectedKeys.Contains(f.key)))
                        live.Add(f);
                live.Sort((a, b) => a.mtime.CompareTo(b.mtime)); // давние первыми
                foreach (var f in live)
                {
                    if (total <= quotaBytes) break;
                    victims.Add(f.key);
                    total -= f.size;
                }
            }
            return victims;
        }

        internal static string HashKey(string url, string version)
        {
            var key = version == null ? url : url + "@" + version;
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
        {
#if UNITY_2021_2_OR_NEWER
            return await File.ReadAllTextAsync(path, ct);
#else
            return await Task.Run(() => File.ReadAllText(path), ct);
#endif
        }

        private static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
        {
#if UNITY_2021_2_OR_NEWER
            return await File.ReadAllBytesAsync(path, ct);
#else
            return await Task.Run(() => File.ReadAllBytes(path), ct);
#endif
        }

        private static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
        {
            // Always atomic — a half-written cache file is worse than none (File.Exists
            // would treat the truncated file as valid on the next run).
            await Task.Run(() => AtomicWriteAllBytes(path, bytes), ct);
        }

        // Atomic write: stage to a unique temp file in the same directory, then move
        // it into place (mirrors the .part → File.Move pattern DownloadBytes uses).
        // The destination path therefore only ever holds a complete file — never a
        // truncated one from an interrupted write.
        internal static void AtomicWriteAllBytes(string path, byte[] bytes)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                LvnQuiet.Try(() => { if (File.Exists(tmp)) File.Delete(tmp); });
                throw;
            }
        }
    }

    /// <summary>Lightweight descriptor for a single preload batch entry.</summary>
    public sealed class PreloadItem
    {
        public string Url;
        public string Kind;
        public string Alias;
    }

    internal static class TaskExtensions
    {
        /// <summary>Adds cancellation support to any Task. Wraps it in WhenAny with
        /// a CT-driven completion source so awaiting can throw on shutdown even if
        /// the underlying task ignores the token.</summary>
        public static async Task WithCancellation(this Task task, CancellationToken ct)
        {
            if (!ct.CanBeCanceled) { await task; return; }
            var tcs = new TaskCompletionSource<bool>();
            using (ct.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
            {
                if (task != await Task.WhenAny(task, tcs.Task))
                    throw new OperationCanceledException(ct);
            }
            await task; // surface exceptions
        }
    }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
    internal sealed class AcceptAllCertificates : UnityEngine.Networking.CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData) => true;
    }
#endif
}
