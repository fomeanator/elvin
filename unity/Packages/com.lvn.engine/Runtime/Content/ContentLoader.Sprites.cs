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

            // Ширина полосы распаковки и бронь для живого — не здесь:
            // LvnLanes.Decoder. Ожидание меряется ОТДЕЛЬНО от работы: в пачке
            // очередь длиннее самой распаковки, и лог, сложивший их в одно
            // число, читается как регресс декодера.
            var queueSw = System.Diagnostics.Stopwatch.StartNew();
            using var pass = await LvnLanes.Decoder.EnterAsync(ct);
            long queueMs = queueSw.ElapsedMilliseconds;
            using var req = UnityWebRequestTexture.GetTexture(reqUrl, nonReadable: true);
            await AwaitRequest(req, req.SendWebRequest(), ct);
            if (req.result != UnityWebRequest.Result.Success) return (null, queueMs);
            try { return (DownloadHandlerTexture.GetContent(req), queueMs); }
            catch { return (null, queueMs); }
        }

        private async Task<Sprite> DecodeSpriteAsync(string url, CancellationToken ct)
        {
            try
            {
                // ЕДИНСТВЕННЫЙ ФОРМАТ АРТА ИСТОРИИ. Видеокарта читает
                // сжатые блоки как есть: ни распаковки в RGBA, ни полного
                // кадра в видеопамяти (16 МБ на фон @2k превращаются в 4).
                //
                // Форматов тут было два. Сырой ASTC приехал первым, слёг
                // 06.07 на блоках невыровненного размера и с тех пор стоял
                // выключенный — 171 строка клиента и 205 сервера, которые
                // ничего не делали, но исправно объясняли, почему они нужны.
                // Второй, живой, умеет то же самое и на всех платформах.
                // Мёртвый снят 01.09; разбор — в docs/missing-roles.md.
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
                // СТРОГОСТЬ — ТОЛЬКО ТАМ, ГДЕ ЗАВЕДЕНА. Спрашивать код мы
                // теперь можем и за обшивку интерфейса (полотно витрины лежит
                // в /ui/ и весит 2000×1500), но запрещать ей растр нельзя: у
                // неё он объявленный путь.
                if (Ktx2Only && DownloadPolicy.RasterForbidden(url)
                    && Ktx2UrlFor(url) != null && !GpuCannotKtx2)
                {
                    // «КОДА ЕЩЁ НЕТ» — ЭТО ПОДОЖДАТЬ, А НЕ ОТКАЗ.
                    //
                    // Сервер кодирует и на прогреве, и по первому запросу, так
                    // что холодный файл — состояние временное и обычное: на
                    // свежем контенте холодны ВСЕ. Раз растровой подстраховки
                    // больше нет, отказ с первого промаха означает «картинки не
                    // будет никогда» — поймано смоук-тестом 01.09, где обложка
                    // куклы не показалась ни разу за прогон.
                    //
                    // Ждём столько, сколько занимает кодирование одного файла,
                    // и пробуем снова. Забывчивость обязательна: без снятия
                    // отметки повтор уходит в тот же пропуск.
                    for (int wait = 0; wait < Ktx2Waits; wait++)
                    {
                        await Task.Delay(Ktx2WaitMs, ct);
                        ForgetKtx2Cold(url);
                        var (late, lateBytes) = await TryDecodeKtx2Async(url, ct);
                        if (late != null) return CacheSprite(url, late, lateBytes);
                    }
                    // «КОДА НЕТ» И «СЕТИ НЕТ» — РАЗНЫЕ БЕДЫ.
                    //
                    // Показать вторую как первую значит послать разбираться не
                    // туда: человек пойдёт проверять basisu и очередь сервера,
                    // хотя до сервера просто не достучались. Про обрыв связи
                    // кричит сетевой слой, и второй крик тут — шум.
                    //
                    // Поймано прогоном 01.09: два теста, идущих БЕЗ сервера,
                    // покраснели на строке про кодировщик.
                    if (Lvn.LvnNetworkStatus.IsOffline)
                        LvnLog.Warn($"[lvn-content] {url}: кода не спросить — связи нет");
                    else
                        LvnLog.Error($"[lvn-content] {url}: кода нет и через {Ktx2Waits * Ktx2WaitMs / 1000} с, "
                                   + "а растром арт истории мы не показываем. "
                                   + "Соберите коды (tools/warm-ktx2.sh) или проверьте basisu на сервере");
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
                    tex = AssetMemory.Decode(bytes);
                    if (tex == null) return null;
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
