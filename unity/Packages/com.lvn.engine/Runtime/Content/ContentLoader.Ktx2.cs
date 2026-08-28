using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if LVN_KTX2
using KtxUnity; // also brings the TextureOrientation extension methods into scope
using Unity.Collections;
#endif

namespace Lvn.Content
{
    /// <summary>
    /// The KTX2 (Basis Universal / UASTC) texture path — the successor to the
    /// raw-.astc experiment in <c>ContentLoader.Astc.cs</c> (kill-switched: raw
    /// block upload broke on non-block-aligned sizes). A sprite load first asks
    /// the server for the "@2k.ktx2" variant (server/ktx2.go encodes it on
    /// demand and caches to disk) and, on success, transcodes it IN A JOB
    /// THREAD to whatever this GPU speaks — ASTC on modern phones, BC7 on
    /// desktop, ETC2 on older Android — via Unity's official
    /// <c>com.unity.cloud.ktx</c> package, which owns all block-alignment
    /// bookkeeping.
    ///
    /// Why: PNG/JPG shrink only wire/disk — decoded, a texture is full RGBA in
    /// VRAM (16 MB per @2k background). A transcoded UASTC texture is GPU-
    /// sampled compressed: ~4–8× less VRAM and a millisecond-scale transcode
    /// instead of a 250 ms image decode.
    ///
    /// Strictly opt-in and additive, three ways:
    ///  • compile-time — everything meaningful sits behind LVN_KTX2, defined
    ///    (asmdef versionDefines) only when com.unity.cloud.ktx is installed;
    ///  • server — no basisu on PATH → 404 → session-latched fallback;
    ///  • per-asset — any decode error falls through to the PNG/JPG path.
    /// Orientation: the server encodes with -y_flip (bottom-up, Unity's
    /// convention) because compressed pixels can't be flipped client-side and
    /// the sprite path has no per-draw UV flip; the KTX orientation metadata is
    /// deliberately ignored here.
    /// </summary>
    public partial class ContentLoader
    {
        // Раньше это была СЕССИОННАЯ защёлка «первый промах гасит весь тракт» —
        // но сервер кодирует .ktx2 лениво, и один холодный файл (404 «ещё
        // кодируется») ронял все ассеты в сырой RGBA до перезапуска (×4 по
        // памяти). Теперь промах помечает ТОЛЬКО свой файл; тракт целиком
        // сдаётся лишь после серии промахов подряд — так сервер без basisu
        // по-прежнему не платит по лишнему запросу на каждый ассет.
        private bool _ktx2Unavailable;
        private readonly HashSet<string> _ktx2Missing = new HashSet<string>();
        private int _ktx2MissStreak;
        private const int Ktx2GiveUpAfterMisses = 8;
        private readonly object _ktx2Lock = new object();

        private bool Ktx2Skipped(string ktx2Url)
        {
            lock (_ktx2Lock) return _ktx2Unavailable || _ktx2Missing.Contains(ktx2Url);
        }

        private void NoteKtx2Miss(string ktx2Url)
        {
            lock (_ktx2Lock)
            {
                _ktx2Missing.Add(ktx2Url);
                if (++_ktx2MissStreak >= Ktx2GiveUpAfterMisses && !_ktx2Unavailable)
                {
                    _ktx2Unavailable = true;
                    Debug.LogWarning($"[content] ktx2: {Ktx2GiveUpAfterMisses} промахов подряд — похоже, сервер без кодов; тракт выключен до перезапуска");
                }
            }
        }

        private void NoteKtx2Hit()
        {
            lock (_ktx2Lock) _ktx2MissStreak = 0;
        }

#if LVN_KTX2
        // GPU honesty probe, once per session: SystemInfo happily CLAIMS
        // ASTC/ETC2 support on GPUs that then sample the texture as black —
        // live-hit on BlueStacks (every ktx2-transcoded texture rendered as a
        // black cutout while raw RGBA art was fine). A tiny solid-red KTX2
        // ships in Resources; transcode it, draw it into a RenderTexture, read
        // the pixel back — not red means the whole path lies on this GPU and
        // the session falls back to PNG/JPG.
        private static bool? _gpuHonest;

        private static async Task<bool> GpuRendersKtx2Async()
        {
            if (_gpuHonest.HasValue) return _gpuHonest.Value;
            try
            {
                var probe = Resources.Load<TextAsset>("LvnKtxProbe");
                if (probe == null) return (_gpuHonest = true).Value; // no probe shipped — trust the GPU
                using var data = new NativeArray<byte>(probe.bytes, Allocator.Persistent);
                var ktx = new KtxTexture();
                var result = await ktx.LoadFromBytes(data.AsReadOnly(), linear: false);
                if (result?.texture == null) return (_gpuHonest = false).Value;

                // Та же пересъёмка, что у переноса под бюджет памяти, — через
                // общий дом: пиксель пробника надо ПРОЧИТАТЬ, поэтому текстура
                // остаётся читаемой, а исходник гасим сами строкой ниже.
                var read = LvnTexCopy.Rescale(result.texture, 4, 4, readable: true);
                var c = read.GetPixel(2, 2);
                UnityEngine.Object.Destroy(read);
                UnityEngine.Object.Destroy(result.texture);
                bool honest = c.r > 0.5f && c.g < 0.3f && c.b < 0.3f; // the probe is solid red
                if (!honest)
                    Debug.LogWarning($"[content] ktx2 disabled for this session: GPU claims support but sampled the probe as {c} (emulator?) — falling back to PNG/JPG");
                _gpuHonest = honest;
                return honest;
            }
            catch
            {
                _gpuHonest = false; // a probe that can't even run is a no
                return false;
            }
        }
#endif

        // Attempts the KTX2 path for `url`. Returns (null, 0) on ANY failure —
        // the caller (DecodeSpriteAsync) then runs the ordinary decode exactly
        // as if this method didn't exist.
        private async Task<(Sprite sprite, long bytes)> TryDecodeKtx2Async(string url, CancellationToken ct)
        {
#if !LVN_KTX2
            return (null, 0);
#else
            var ktx2Url = Ktx2UrlFor(url);
            if (ktx2Url == null || Ktx2Skipped(ktx2Url)) return (null, 0);
            if (!await GpuRendersKtx2Async()) { _ktx2Unavailable = true; return (null, 0); }

            byte[] bytes;
            try
            {
                bytes = await DownloadAssetBytes(ktx2Url, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                NoteKtx2Miss(ktx2Url); // этот файл — мимо; тракт живёт
                return (null, 0);
            }
            if (bytes == null || bytes.Length == 0) { NoteKtx2Miss(ktx2Url); return (null, 0); }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var data = new NativeArray<byte>(bytes, Allocator.Persistent);
                var ktx = new KtxTexture();
                var result = await ktx.LoadFromBytes(data.AsReadOnly(), linear: false);
                if (result?.texture == null)
                {
                    // Байты не декодятся — в кэше обрезок (скачан, пока сервер
                    // ещё кодировал): выбрасываем, следующий заход перекачает
                    // целый. Битое не должно залипать («данные восстановить» —
                    // самолечением, Илья 27.08).
                    DeleteCachedAsset(ktx2Url);
                    NoteKtx2Miss(ktx2Url);
                    return (null, 0);
                }

                var tex = result.texture;
                tex.wrapMode = TextureWrapMode.Clamp;
                // The server bakes a mip chain into every encode (basisu -mipmap);
                // trilinear blends between mips so minified art (shrunk actors,
                // zoom-outs) doesn't shimmer. Bilinear when a chain is absent.
                tex.filterMode = tex.mipmapCount > 1 ? FilterMode.Trilinear : FilterMode.Bilinear;
                // РЕКТ — ПО ОРИГИНАЛУ, НЕ ПО ТЕКСТУРЕ. Блочные GPU-форматы
                // паддятся до кратности 4: слой 1210 px становится текстурой
                // 1212 px, и спрайт «на всю текстуру» менял аспект и смещал
                // контент на пару пикселей против обычного пути («героиня
                // меньше и перескакивает» — живой репорт). Контейнер KTX2
                // помнит исходные размеры; паддинг лежит в хвосте данных
                // (верх текстуры) — рект от низа его отрезает.
                // Размеры — ИЗ ЗАГОЛОВКА КОНТЕЙНЕРА (pixelWidth/pixelHeight,
                // little-endian u32 по смещениям 20/24 спеки KTX2), не из
                // ktx.baseWidth: LoadFromBytes сам делает Dispose() в конце,
                // и обращение к свойству после него кидало NRE — уже
                // транскоженная текстура выбрасывалась, файл стирался из
                // кэша как «битый», и каждый показ платил полный PNG-декод
                // (живой лог 26.08).
                uint origW = bytes.Length >= 28 ? BitConverter.ToUInt32(bytes, 20) : 0;
                uint origH = bytes.Length >= 28 ? BitConverter.ToUInt32(bytes, 24) : 0;
                float w = origW > 0 && origW <= (uint)tex.width ? origW : (uint)tex.width;
                float h = origH > 0 && origH <= (uint)tex.height ? origH : (uint)tex.height;
                var sprite = Sprite.Create(tex, new Rect(0, 0, w, h),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                NoteKtx2Hit(); // «жив» = ДЕКОД прошёл: сброс до декода прятал серию битых файлов от общего счётчика
                if (sw.ElapsedMilliseconds > 30)
                    // БЕЗ orientation в логе: result.orientation бывает null, и
                    // NRE в ЛОГ-СТРОКЕ ронял весь декод уже ПОСЛЕ успешного
                    // транскода — целый файл читался как «битый» (живой стек
                    // 27.08, hair_orchid_red@2k).
                    Debug.Log($"[lvn-perf] ktx2 transcode {ktx2Url}: {sw.ElapsedMilliseconds}ms ({tex.width}x{tex.height}, {tex.format})");
                // Budget the LRU by the COMPRESSED size — that's what actually
                // occupies VRAM; charging width*height*4 would evict 4-8× early.
                return (sprite, bytes.LongLength);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Один битый файл ≠ сломанный транскодер: помечаем файл, тракт
                // живёт; серию подряд добьёт общий счётчик промахов. Битые
                // байты выбрасываются из кэша — перекачаются целыми.
                Debug.LogWarning($"[content] ktx2 decode failed for {ktx2Url}: {ex.Message}");
                DeleteCachedAsset(ktx2Url);
                NoteKtx2Miss(ktx2Url);
                return (null, 0);
            }
#endif
        }

        /// <summary>Байты этого арта уже ДОСТУПНЫ ЛОКАЛЬНО (диск-кэш любого из
        /// файлов показа — ktx2/@2k/оригинал — или сид в APK)? Решает, нужен ли
        /// «силуэт-проявление»: локальные байты декодируются за сотни мс, и
        /// заготовка только мигала бы; силуэт — лекарство от СЕТИ, не от декода.</summary>
        public bool HasLocalSpriteBytes(string url)
        {
            if (string.IsNullOrEmpty(url)) return true;
            var v2k = DownloadPolicy.DownscaleVariant(url);
            var k = Ktx2UrlFor(url);
            if (k != null && IsAssetCached(k)) return true;
            if (v2k != null && IsAssetCached(v2k)) return true;
            if (IsAssetCached(url)) return true;
            // Сид: файл лежит в APK — чтение локальное и мгновенное.
            // Ключ сида считает ОДИН метод (ContentLoader.Seed): здесь стояла
            // его копия, и разойдись они — файл из APK находился бы через раз.
            if (SeedKey(url) != null) return true;
            return false;
        }

        /// <summary>Байтовый прогрев ТОГО ЖЕ файла, который возьмёт показ:
        /// живой KTX2-тракт → .ktx2, иначе обычный вариант. Без декода — это
        /// путь экрана загрузки (см. AssetScheduler.Warm).</summary>
        public async Task PrefetchSpriteBytes(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url)) return;
#if LVN_KTX2
            var ktx2Url = Ktx2UrlFor(url);
            if (ktx2Url != null && !Ktx2Skipped(ktx2Url) && await GpuRendersKtx2Async())
            {
                try
                {
                    var b = await DownloadAssetBytes(ktx2Url, ct);
                    if (b != null && b.Length > 0) { NoteKtx2Hit(); return; }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* провалился в обычный файл ниже */ }
                NoteKtx2Miss(ktx2Url);
            }
#endif
            // Обычный путь: крупный арт показывается из @2k-варианта — греем
            // его; нет варианта (мелкий арт / статический хост) — оригинал.
            var variant = DownloadPolicy.DownscaleVariant(url);
            if (variant != null)
            {
                try { await DownloadAssetBytes(variant, ct); return; }
                catch (OperationCanceledException) { throw; }
                catch { /* вариант не отдался — оригинал ниже */ }
            }
            await DownloadAssetBytes(url, ct);
        }

        // Maps a sprite url onto the KTX2 the server can serve for it. Only
        // large story art plays: the "@2k" display variant maps by extension
        // swap, and an ORIGINAL large-art url maps through its @2k name (the
        // server encodes from the original when the source already fits the 2K
        // box — same errFitsAlready contract as the PNG variant). Pixel art and
        // UI skins return null and never leave the ordinary path.
        internal static string Ktx2UrlFor(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var basis = url.Contains("@2k") ? url : DownloadPolicy.DownscaleVariant(url);
            if (basis == null) return null;
            int dot = basis.LastIndexOf('.');
            if (dot < 0) return null;
            var ext = basis.Substring(dot).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") return null;
            return basis.Substring(0, dot) + ".ktx2";
        }
    }
}
