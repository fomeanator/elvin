using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lvn.UI
{
    /// <summary>
    /// The production-grade, disk-cached <see cref="ILvnAssets"/>. Where
    /// <see cref="DirectoryAssets"/> reads a local folder and the lightweight
    /// <see cref="NetworkAssets"/> streams over the wire with an in-memory cache,
    /// this wraps a full <see cref="ContentLoader"/> pipeline: a sha1(url@version)
    /// <b>disk</b> cache (content survives restarts and plays offline), an
    /// in-memory sprite cache, dedup of parallel loads, content-version
    /// cache-busting (a re-uploaded asset auto-invalidates), resumable retries
    /// with backoff, and byte-level progress for a loading HUD.
    ///
    /// <para>Point it at a base URL (your CDN or the bundled Go server), call
    /// <see cref="WarmVersionsAsync"/> once at boot, then assign it to
    /// <c>VnStage.Assets</c>. For a chapter's prioritized release set (required
    /// gates Play, deferred streams in during play) drive <see cref="Scheduler"/>
    /// with a map of path → <see cref="LvnAssetMeta"/> from your manifest.</para>
    /// </summary>
    public sealed class CachingAssets : ILvnAssets
    {
        /// <summary>The underlying loader — exposed for HUD progress
        /// (<c>BatchActive</c>, <c>BatchBytesReceived</c>, <c>CurrentFileLabel</c>),
        /// version refresh, version-pinned script loads, and warmed-sprite
        /// lookups (<c>TryGetSprite</c>).</summary>
        public ContentLoader Loader { get; }

        private AssetScheduler _scheduler;
        /// <summary>The prioritized chapter download planner (lazily created).
        /// Feed it a release set via <c>Scheduler.Start(assets, ct)</c>; poll
        /// <c>RequiredReady</c>/<c>Progress</c> on the loading screen.</summary>
        public AssetScheduler Scheduler => _scheduler ??= new AssetScheduler(Loader);

        /// <param name="baseUrl">Content origin, e.g. "https://cdn.example.com" or
        /// "http://localhost:8000". Relative urls ("/content/bg/x.png") resolve
        /// against it; absolute urls pass through.</param>
        /// <param name="cacheRoot">Disk cache root. Defaults to
        /// <c>Application.persistentDataPath/cache</c>.</param>
        public CachingAssets(string baseUrl, string cacheRoot = null)
            : this(new ContentLoader(baseUrl, cacheRoot)) { }

        public CachingAssets(ContentLoader loader)
        {
            Loader = loader;
        }

        /// <summary>Fetch the content-version index once at boot so changed assets
        /// auto-invalidate their cache. Non-fatal if offline (falls back to the
        /// last persisted index, then to url-only cache keys).</summary>
        public Task WarmVersionsAsync(CancellationToken ct = default) =>
            Loader.LoadAssetVersionsAsync(ct);

        /// <summary>Interactive loads in flight right now (sprites/audio a LIVE
        /// surface is waiting to draw). Background warmers poll this and yield
        /// the pipe — a viewer staring at a missing actor outranks a prefetch
        /// of next week's chapters. Batch preloads don't count: the chapter
        /// gate has its own bandwidth contract.</summary>
        public int LivePressure => _livePressure;
        private int _livePressure;

        private IReadOnlyDictionary<string, Lvn3DSet> _sets3d;
        private readonly Dictionary<string, Task<SetBundle>> _setLoads
            = new Dictionary<string, Task<SetBundle>>();
        private readonly List<SetBundle> _warmSets = new List<SetBundle>();
        private const int MaxWarmSets = 2;
        private int _setLoadEpoch;

        private sealed class SetBundle
        {
            public string Key;
            public AssetBundle Bundle;
            public GameObject Prefab;
            public int Leases;
            public bool Warm;
        }

        /// <summary>Apply the live manifest's 3D catalog. Existing sets keep their
        /// lease until the stage replaces them; the next load immediately uses
        /// the new descriptor/hash.</summary>
        public void Set3DSetCatalog(IReadOnlyDictionary<string, Lvn3DSet> sets) =>
            _sets3d = sets;

        public async Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
        {
            System.Threading.Interlocked.Increment(ref _livePressure);
            try
            {
                // Large story art prefers the server's @2k variant (same trick the
                // Spine pages use): the Go server resizes on demand to fit 2048² —
                // a fraction of the bytes and decode time of a 4K original, and the
                // industry ceiling for runtime textures anyway. Pixel art and UI
                // skins are exempt (resampling would wreck them). A miss (already
                // ≤2048 → server 404s; or a plain static host) falls back to the
                // original — and the loader's session 404-cache makes every repeat
                // miss free, so there is no global kill-switch to mis-trip.
                var variant = DownscaleVariant(url);
                if (variant != null)
                {
                    Sprite s = null;
                    try { s = await Loader.DownloadSpriteAsync(variant, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { }   // кэш не отдал — пойдём в сеть
                    if (s != null) return s;
                }
                return await Loader.DownloadSpriteAsync(url, ct);
            }
            finally { System.Threading.Interlocked.Decrement(ref _livePressure); }
        }

        /// <summary>The "@2k" downscale-variant url for large story art (see
        /// <see cref="DownloadPolicy.DownscaleVariant"/> — shared with the chapter
        /// scheduler so every phase warms the SAME file).</summary>
        internal static string DownscaleVariant(string url) =>
            DownloadPolicy.DownscaleVariant(url);

        // Disk-cached, version-folded (unlike scripts, which are always-fresh via
        // DownloadScriptText): today's only LoadTextAsync callers are the Spine
        // skeleton .json / .atlas.txt loads, and those are immutable content —
        // refetching a 1 MB skeleton JSON on every cold show blocked the first
        // render on the wire, and offline play lost Spine scenes entirely.
        public Task<string> LoadTextAsync(string url, System.Threading.CancellationToken ct)
            => Loader.DownloadScriptCached(url, ct);

        /// <summary>Compatibility path for custom code written before leased
        /// remote sets. It deliberately resolves only the bundled fallback;
        /// <see cref="Load3DSetAsync"/> is the lifecycle-safe remote API.</summary>
        public Task<GameObject> LoadPrefabAsync(string id, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(id)) return Task.FromResult<GameObject>(null);
            return Task.FromResult(LoadSetFallback(id,
                _sets3d != null && _sets3d.TryGetValue(id, out var set) ? set : null));
        }

        /// <summary>A 3D set is content, just like a sprite: the manifest picks a
        /// platform bundle, ContentLoader downloads it into the same
        /// version-folded disk cache, and this method opens it from FILE (without
        /// a second full-size byte[] copy). Resources is the offline fallback.</summary>
        public async Task<Lvn3DSetAsset> Load3DSetAsync(string id, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Lvn3DSet set = null;
            if (_sets3d != null) _sets3d.TryGetValue(id, out set);
            var descriptor = Select3DBundle(set, PlatformKey(Application.platform));
            if (descriptor != null && !string.IsNullOrEmpty(descriptor.url))
            {
                try
                {
                    var remote = await AcquireSetBundleAsync(id, descriptor, ct);
                    if (remote != null) return remote;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    Debug.LogWarning($"[assets] 3D set '{id}' remote bundle failed: {e.Message}; using fallback");
                }
            }

            var fallback = LoadSetFallback(id, set);
            return fallback != null ? new Lvn3DSetAsset(id, fallback) : null;
        }

        /// <summary>Download, open and resolve the prefab ahead of <c>bg3d</c>,
        /// but do not instantiate it. Two unused sets are retained as an LRU;
        /// acquiring one consumes its warm pin and normal lease lifetime takes
        /// over.</summary>
        public async Task Preload3DSetAsync(string id, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(id)) return;
            Lvn3DSet set = null;
            if (_sets3d != null) _sets3d.TryGetValue(id, out set);
            var descriptor = Select3DBundle(set, PlatformKey(Application.platform));
            if (descriptor == null || string.IsNullOrEmpty(descriptor.url)) return;

            var loaded = await GetSetBundleAsync(id, descriptor, ct);
            if (ct.IsCancellationRequested)
            {
                if (loaded != null && loaded.Leases == 0 && !loaded.Warm)
                    _ = UnloadSetBundleNextFrame(loaded);
                ct.ThrowIfCancellationRequested();
            }
            if (loaded?.Prefab == null || loaded.Leases > 0 || loaded.Warm) return;
            loaded.Warm = true;
            _warmSets.Remove(loaded);
            _warmSets.Add(loaded);
            while (_warmSets.Count > MaxWarmSets)
            {
                var evicted = _warmSets[0];
                _warmSets.RemoveAt(0);
                evicted.Warm = false;
                if (evicted.Leases == 0) _ = UnloadSetBundleNextFrame(evicted);
            }
        }

        internal static string PlatformKey(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.Android: return "android";
                case RuntimePlatform.IPhonePlayer: return "ios";
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor: return "windows";
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor: return "macos";
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor: return "linux";
                case RuntimePlatform.WebGLPlayer: return "webgl";
                default: return "default";
            }
        }

        internal static Lvn3DBundle Select3DBundle(Lvn3DSet set, string platform)
        {
            if (set?.platforms == null) return null;
            if (!string.IsNullOrEmpty(platform) &&
                set.platforms.TryGetValue(platform, out var exact)) return exact;
            return set.platforms.TryGetValue("default", out var fallback) ? fallback : null;
        }

        private static GameObject LoadSetFallback(string id, Lvn3DSet set)
        {
            var path = set?.fallback_resource;
            if (!string.IsNullOrEmpty(path))
            {
                var configured = Resources.Load<GameObject>(path);
                if (configured != null) return configured;
            }
            return Resources.Load<GameObject>("Sets/" + id) ?? Resources.Load<GameObject>(id);
        }

        private async Task<Lvn3DSetAsset> AcquireSetBundleAsync(
            string id, Lvn3DBundle descriptor, CancellationToken ct)
        {
            var loaded = await GetSetBundleAsync(id, descriptor, ct);
            if (ct.IsCancellationRequested)
            {
                if (loaded != null && loaded.Leases == 0 && !loaded.Warm)
                    _ = UnloadSetBundleNextFrame(loaded);
                ct.ThrowIfCancellationRequested();
            }
            if (loaded?.Prefab == null) return null;
            if (loaded.Warm)
            {
                loaded.Warm = false;
                _warmSets.Remove(loaded);
            }
            loaded.Leases++;
            return new Lvn3DSetAsset(id, loaded.Prefab, remote: true,
                release: () => ReleaseSetBundle(loaded));
        }

        private async Task<SetBundle> GetSetBundleAsync(
            string id, Lvn3DBundle descriptor, CancellationToken ct)
        {
            var address = string.IsNullOrEmpty(descriptor.asset) ? id : descriptor.asset;
            var key = descriptor.url + "|" + (descriptor.hash ?? "") + "|" + address;
            if (!_setLoads.TryGetValue(key, out var load))
            {
                load = LoadSetBundleFileAsync(
                    key, descriptor.url, address, _setLoadEpoch, descriptor.scene);
                _setLoads[key] = load;
            }

            SetBundle loaded;
            try { loaded = await load; }
            catch
            {
                if (_setLoads.TryGetValue(key, out var failed) && ReferenceEquals(failed, load))
                    _setLoads.Remove(key);
                throw;
            }

            return loaded;
        }

        private async Task<SetBundle> LoadSetBundleFileAsync(
            string key, string url, string address, int epoch, bool isScene = false)
        {
            var path = await Loader.EnsureCachedFile(url);
            if (epoch != _setLoadEpoch) throw new OperationCanceledException();
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("bundle is unavailable and has no cached copy");

            // ПО СОБЫТИЮ, А НЕ ОПРОСОМ. Прогресса загрузки набора никто не
            // показывает, значит каждый оборот «пока не готово — уступи кадр»
            // это потраченный кадр на ровном месте.
            var create = AssetBundle.LoadFromFileAsync(path);
            await Lvn.LvnNetWait.DoneAsync(create);
            var bundle = create.assetBundle;
            if (bundle == null) throw new InvalidOperationException("Unity rejected the AssetBundle");
            if (epoch != _setLoadEpoch)
            {
                bundle.Unload(true);
                throw new OperationCanceledException();
            }

            var request = bundle.LoadAssetAsync<GameObject>(address);
            await Lvn.LvnNetWait.DoneAsync(request);
            var prefab = request.asset as GameObject;
            if (prefab == null || epoch != _setLoadEpoch)
            {
                bundle.Unload(true);
                if (epoch != _setLoadEpoch) throw new OperationCanceledException();
                throw new InvalidOperationException($"set address '{address}' is absent");
            }
            return new SetBundle { Key = key, Bundle = bundle, Prefab = prefab };
        }


        private void ReleaseSetBundle(SetBundle loaded)
        {
            if (loaded == null || loaded.Leases <= 0) return;
            loaded.Leases--;
            if (loaded.Leases == 0 && !loaded.Warm) _ = UnloadSetBundleNextFrame(loaded);
        }

        private async Task UnloadSetBundleNextFrame(SetBundle loaded)
        {
            // WorldStage destroys the old instantiated set at end-of-frame.
            // Wait one continuation before Unload(true), otherwise Unity can
            // invalidate materials still referenced by that dying instance.
            await Task.Yield();
            if (loaded == null || loaded.Leases != 0 || loaded.Warm || loaded.Bundle == null) return;
            if (_setLoads.TryGetValue(loaded.Key, out var task) &&
                task.IsCompletedSuccessfully && ReferenceEquals(task.Result, loaded))
                _setLoads.Remove(loaded.Key);
            loaded.Bundle.Unload(true);
            loaded.Bundle = null;
            loaded.Prefab = null;
        }

        public async Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
        {
            System.Threading.Interlocked.Increment(ref _livePressure);
            try { return await Loader.DownloadAudioClipAsync(url, ct); }
            finally { System.Threading.Interlocked.Decrement(ref _livePressure); }
        }

        /// <summary>Batch-warm a set of urls. Sprite-kind urls go through the
        /// pipelined preload batch (overlapping each disk write with the next
        /// file's network setup); audio-kind urls load individually into the
        /// audio cache.</summary>
        public async Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
        {
            if (urls == null || urls.Count == 0) return;

            if (kind == "audio")
            {
                var tasks = new List<Task>(urls.Count);
                foreach (var url in urls)
                    if (!string.IsNullOrEmpty(url))
                        tasks.Add(Loader.DownloadAudioClipAsync(url, ct));
                await Task.WhenAll(tasks);
                return;
            }

            var items = new List<PreloadItem>(urls.Count);
            foreach (var url in urls)
                if (!string.IsNullOrEmpty(url))
                {
                    // Warm the SAME file the display path will fetch — the @2k
                    // variant for large story art (see LoadSpriteAsync).
                    var warmUrl = DownscaleVariant(url) ?? url;
                    items.Add(new PreloadItem { Url = warmUrl, Kind = DownloadPolicy.Kind(url) });
                }
            await Loader.StartPreloadBatch(items, ct);
            await Loader.WaitForAll(null, ct);
        }

        /// <summary>The url's bytes as a plain local FILE (downloaded/copied into
        /// the cache when needed) — for consumers that need a real path, e.g.
        /// runtime fonts. Null when unavailable (offline and not cached).</summary>
        public Task<string> EnsureCachedFileAsync(string url, CancellationToken ct = default)
            => Loader.EnsureCachedFile(url, ct);

        public void Unload(string url) => Loader.Unload(url);

        public void UnloadAll()
        {
            _setLoadEpoch++; // in-flight bundle opens self-cancel and unload
            Loader.UnloadAll();
            foreach (var task in _setLoads.Values)
                if (task.IsCompletedSuccessfully && task.Result?.Bundle != null)
                {
                    task.Result.Bundle.Unload(true);
                    task.Result.Bundle = null;
                    task.Result.Prefab = null;
                }
            _setLoads.Clear();
            _warmSets.Clear();
        }
    }
}
