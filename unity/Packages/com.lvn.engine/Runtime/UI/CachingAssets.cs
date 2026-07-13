using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

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

        public async Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
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
                catch { }
                if (s != null) return s;
            }
            return await Loader.DownloadSpriteAsync(url, ct);
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

        public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) =>
            Loader.DownloadAudioClipAsync(url, ct);

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

        public void UnloadAll() => Loader.UnloadAll();
    }
}
