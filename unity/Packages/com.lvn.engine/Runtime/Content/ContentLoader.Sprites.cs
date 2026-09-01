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
    /// ОТ БАЙТОВ К КАРТИНКЕ — декод в текстуру и сборка спрайта.
    ///
    /// <para>Самое дорогое место загрузчика по времени: JPEG в две тысячи
    /// пикселей декодится дольше секунды, поэтому декод уходит на рабочие
    /// потоки, ограничен по числу одновременных и делится между теми, кто
    /// попросил один и тот же адрес, — иначе гонка оставляет за собой лишние
    /// текстуры.</para>
    ///
    /// <para>Сколько всего этого держать в памяти и что вытеснять — другая
    /// тема и другой дом: <c>ContentLoader.SpriteCache.cs</c>.</para>
    /// </summary>
    public sealed partial class ContentLoader
    {
        private readonly Dictionary<string, SpriteEntry> _spriteCache = new();

        private readonly Dictionary<string, Task<Sprite>> _decoding = new();

        // When the version index changes (a live content update), any in-memory sprite
        // whose content hash moved is stale — the memory cache is url-keyed, so it would
        // otherwise keep handing back the OLD art forever. Evict exactly those, so the
        // next load (e.g. a live ReplayVisuals) decodes the replaced file.
        private void EvictStaleSprites(Dictionary<string, string> oldMap, Dictionary<string, string> newMap)
        {
            List<string> stale = null;
            lock (_spriteCache)
            {
                foreach (var url in _spriteCache.Keys)
                    if (Lookup(oldMap, url) != Lookup(newMap, url))
                        (stale ??= new List<string>()).Add(url);
            }
            if (stale != null) foreach (var u in stale) Unload(u);
        }

        /// <summary>Loads (or fetches and caches) the URL, decodes the bytes into
        /// a texture, and wraps it as a Sprite. Returns null on missing data.
        /// Concurrent requests for the same url share ONE decode (no leaked
        /// Texture2D from a lost race), and the cache is LRU-bounded by
        /// <see cref="SpriteCacheBudgetBytes"/>.</summary>
        public Task<Sprite> DownloadSpriteAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url)) return Task.FromResult<Sprite>(null);
            lock (_spriteCache)
            {
                if (_spriteCache.TryGetValue(url, out var hit) && hit.Sprite != null)
                {
                    Touch(hit);
                    return Task.FromResult(hit.Sprite);
                }
                // Someone is already decoding this url — share their result instead
                // of decoding a second texture and leaking the loser.
                if (_decoding.TryGetValue(url, out var inflight)) return inflight;
                var task = DecodeSpriteAsync(url, ct);
                _decoding[url] = task;
                // Self-clean via a continuation, NOT a finally inside the async
                // body: a decode that throws BEFORE its first await (e.g. an
                // offline guard) runs its finally synchronously — i.e. before the
                // `_decoding[url] = task` above — so a finally-based remove would
                // delete nothing and then leave the faulted task wedged in the map
                // forever (every later request returns the dead task). The
                // continuation runs strictly after this insert. Guard on identity
                // so we never evict a newer in-flight decode of the same url.
                task.ContinueWith(t =>
                {
                    lock (_spriteCache)
                        if (_decoding.TryGetValue(url, out var cur) && ReferenceEquals(cur, t))
                            _decoding.Remove(url);
                }, System.Threading.Tasks.TaskScheduler.Default);
                return task;
            }
        }

        /// <summary>Fit (w, h) within <paramref name="cap"/> on the longest side,
        /// preserving aspect. Identity when already within. Pure — unit-tested.</summary>
        internal static Vector2Int FitWithin(int w, int h, int cap)
        {
            int m = Mathf.Max(w, h);
            if (m <= cap) return new Vector2Int(w, h);
            float k = (float)cap / m;
            return new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(w * k)),
                                  Mathf.Max(1, Mathf.RoundToInt(h * k)));
        }

        // PNG/JPG decode OFF the main thread. UnityWebRequestTexture's native
        // DownloadHandlerTexture buffers, decompresses and creates the texture
        // on a worker thread — unlike Texture2D.LoadImage, which blocks the
        // main thread 75-400 ms per 2K Spine page (the "prefetch still
        // hitches" stutter). Bytes land in the same disk cache first (offline
        // policy unchanged); the request then reads them back via file://.
        // Returns null wherever the trick can't work — WebGL has no file://,
        // and any request failure just falls back to the synchronous decode.
        private async Task<(Texture2D tex, long queueMs)> DecodeTextureOffThreadAsync(string url, CancellationToken ct)
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer) return (null, 0);
            string reqUrl;
            try
            {
                if (_local) reqUrl = ResolveUrl(url); // file:// or jar:file:// already
                else
                {
                    var path = CachePath(_assetCacheDir, url, ".bin");
                    if (!File.Exists(path))
                    {
                        var bytes = await DownloadBytes(url, _assetCacheDir, ct); // writes the cache file
                        if (bytes == null || bytes.Length == 0) return (null, 0);
                    }
                    if (!File.Exists(path)) return (null, 0);
                    reqUrl = "file://" + path;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return (null, 0); }

            // Bound concurrent native decodes: a burst (boot warm, chapter warm)
            // otherwise completes many textures in the same frame and their GPU
            // uploads stack into one visible hitch. Three in flight keeps the
            // pipeline busy while spreading uploads across frames.
            // The wait is timed separately: in a burst the queue dominates, and a
            // perf log that folds it into "decode" reads as a decoder regression.
            var queueSw = System.Diagnostics.Stopwatch.StartNew();
            await _textureDecodes.WaitAsync(ct);
            long queueMs = queueSw.ElapsedMilliseconds;
            try
            {
                using var req = UnityWebRequestTexture.GetTexture(reqUrl, nonReadable: true);
                await AwaitRequest(req, req.SendWebRequest(), ct);
                if (req.result != UnityWebRequest.Result.Success) return (null, queueMs);
                try { return (DownloadHandlerTexture.GetContent(req), queueMs); }
                catch { return (null, queueMs); }
            }
            finally { _textureDecodes.Release(); }
        }

        // See DecodeTextureOffThreadAsync: bounds concurrent UWR texture decodes
        // so completion (and the GPU upload inside GetContent) spreads over
        // frames instead of landing as one burst.
        private static readonly SemaphoreSlim _textureDecodes = new(3, 3);

        private async Task<Sprite> DecodeSpriteAsync(string url, CancellationToken ct)
        {
            try
            {
                // GPU-native compressed texture, when the device supports it and the
                // server has a transcoded variant: the ONE encoding that actually cuts
                // runtime VRAM (the GPU samples the compressed bytes directly), not
                // just download size. Never a hard dependency — any miss (unsupported
                // GPU, no server-side astcenc, corrupt data) falls through to the
                // normal PNG/JPG decode below untouched. See ContentLoader.Astc.cs.
                var (astcSprite, astcBytes) = await TryDecodeAstcAsync(url, ct);
                if (astcSprite != null)
                {
                    // ASTC's whole point is using far fewer bytes than raw RGBA — charge
                    // the cache budget the texture's ACTUAL compressed size, not
                    // width*height*4, or the LRU would evict as if every hit here were
                    // still full-size and erase most of the memory win.
                    return CacheSprite(url, astcSprite, astcBytes);
                }

                // KTX2/BasisU (see ContentLoader.Ktx2.cs) — the raw-ASTC path's
                // successor: same VRAM win, official transcoder, every platform.
                var (ktx2Sprite, ktx2Bytes) = await TryDecodeKtx2Async(url, ct);
                if (ktx2Sprite != null)
                    return CacheSprite(url, ktx2Sprite, ktx2Bytes);

                // РАСТР — НЕ ЗАПАСНОЙ ПУТЬ ДЛЯ АРТА ИСТОРИИ.
                //
                // Пока PNG «спасал», никто не замечал, что быстрый формат не
                // работает вовсе: 62 закодированных файла в каталоге и почти ни
                // одного показа через них, героиня по 1,2–3,7 с на слой вместо
                // 110 мс. Костыль был удобнее беды — он делал поломку
                // незаметной.
                //
                // Теперь у арта истории (то, для чего вообще положен код —
                // фоны, спрайты, Spine) растрового пути НЕТ. Не собрался код —
                // это отказ, громкий и видимый, а не тихая замена медленным.
                // Пиксель-арт и обшивка интерфейса сюда не попадают: у них кода
                // не бывает по природе (блочное сжатие размажет пиксельную
                // сетку), и растр для них — объявленный путь, а не запасной.
                if (Ktx2Only && Ktx2UrlFor(url) != null && !GpuCannotKtx2)
                {
                    LvnLog.Error($"[content] {url}: кода нет, а растром арт истории мы не показываем. "
                               + "Соберите коды (tools/warm-ktx2.sh) или дождитесь очереди сервера");
                    return null;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (tex, decodeQueueMs) = await DecodeTextureOffThreadAsync(url, ct);
                bool offThread = tex != null;
                if (!offThread)
                {
                    var bytes = await DownloadAssetBytes(url, ct);
                    if (bytes == null || bytes.Length == 0) return null;
                    sw.Restart();
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                    if (!tex.LoadImage(bytes))
                    {
                        UnityEngine.Object.Destroy(tex);
                        return null;
                    }
                }
                long decodeMs = sw.ElapsedMilliseconds;
                // No platform pays full price for oversized art: phones must not
                // hold 33 MB of RGBA for a 4K background shown at ~1080p, and
                // even desktop/WebGL must not upload a raw 8K Spine page. Cap
                // the longest side and let the GPU resample once at load.
                tex = AssetMemory.DownscaleIfOversized(tex,
                    Application.isMobilePlatform ? MobileMaxTextureSize : DesktopMaxTextureSize,
                    finalize: false);   // финализирует вызывающий, ниже
                // Крупный арт получает мип-уровни: фигуру в 1600 пикселей рисуют
                // примерно в 900, и без них край фигуры идёт ступеньками.
                tex = AssetMemory.WithMipmaps(tex, finalize: false);
                tex.wrapMode   = TextureWrapMode.Clamp;
                if (tex.mipmapCount <= 1) tex.filterMode = FilterMode.Bilinear;
                // Nothing reads pixels back — free the CPU copy (halves the
                // memory of every loaded sprite). The off-thread texture is born
                // non-readable (no CPU copy to free — Apply would throw).
                if (tex.isReadable)
                    tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                long resizeMs = sw.ElapsedMilliseconds - decodeMs;
                // FullRect, explicitly: Sprite.Create's DEFAULT mesh type is
                // Tight — it walks the whole texture's alpha on the main thread
                // to trace an outline (hundreds of ms for a 2K Spine page), and
                // full-frame VN art gains nothing from a tight mesh anyway.
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                // [lvn-perf] main-thread hitch map. Off-thread decodes log wall
                // time (mostly worker-thread, not a hitch); the LoadImage
                // fallback is a true main-thread stall. Only meaningful ones —
                // the console stays quiet for icons and thumbnails.
                if (sw.ElapsedMilliseconds > 30)
                {
                    long queueMs = offThread ? decodeQueueMs : 0;
                    // v= — sha исходника из индекса версий (8 знаков): сразу
                    // видно, КАКАЯ ревизия картинки играет в кадре.
                    // НАРОЧНО по единицам ниже: версия — шестнадцатеричная,
                    // и режется она для журнала, а не для глаза игрока.
                    var v = VersionFor(url);
                    LvnLog.Trace($"[lvn-perf] sprite decode {url}: queue={queueMs}ms decode={decodeMs - queueMs}ms{(offThread ? " (worker thread)" : "")} resize+upload={resizeMs}ms sprite={sw.ElapsedMilliseconds - decodeMs - resizeMs}ms ({tex.width}x{tex.height}) v={(string.IsNullOrEmpty(v) ? "-" : v.Substring(0, 8))}");
                }
                return CacheSprite(url, sprite, (long)tex.width * tex.height * 4);
            }
            finally
            {
                lock (_spriteCache) _decoding.Remove(url);
            }
        }

    }
}
