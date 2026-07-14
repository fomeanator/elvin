using System;
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
        // Flips true the first time a ".ktx2" request fails — a server without
        // basisu (or an old deploy) answers 404 for every asset; stop asking
        // for the rest of the session. A fresh ContentLoader tries again.
        private bool _ktx2Unavailable;

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

                var rt = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGB32);
                var prev = RenderTexture.active;
                Graphics.Blit(result.texture, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                read.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
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
            if (_ktx2Unavailable) return (null, 0);
            var ktx2Url = Ktx2UrlFor(url);
            if (ktx2Url == null) return (null, 0);
            if (!await GpuRendersKtx2Async()) { _ktx2Unavailable = true; return (null, 0); }

            byte[] bytes;
            try
            {
                bytes = await DownloadAssetBytes(ktx2Url, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                _ktx2Unavailable = true; // steady state for a server without basisu
                return (null, 0);
            }
            if (bytes == null || bytes.Length == 0) { _ktx2Unavailable = true; return (null, 0); }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var data = new NativeArray<byte>(bytes, Allocator.Persistent);
                var ktx = new KtxTexture();
                var result = await ktx.LoadFromBytes(data.AsReadOnly(), linear: false);
                if (result?.texture == null) return (null, 0);

                var tex = result.texture;
                tex.wrapMode = TextureWrapMode.Clamp;
                // The server bakes a mip chain into every encode (basisu -mipmap);
                // trilinear blends between mips so minified art (shrunk actors,
                // zoom-outs) doesn't shimmer. Bilinear when a chain is absent.
                tex.filterMode = tex.mipmapCount > 1 ? FilterMode.Trilinear : FilterMode.Bilinear;
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                if (sw.ElapsedMilliseconds > 30)
                    Debug.Log($"[lvn-perf] ktx2 transcode {ktx2Url}: {sw.ElapsedMilliseconds}ms ({tex.width}x{tex.height}, {tex.format}, yflip={result.orientation.IsYFlipped()})");
                // Budget the LRU by the COMPRESSED size — that's what actually
                // occupies VRAM; charging width*height*4 would evict 4-8× early.
                return (sprite, bytes.LongLength);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[content] ktx2 decode failed for {ktx2Url}: {ex.Message}");
                _ktx2Unavailable = true; // a broken transcoder would fail every asset — stop early
                return (null, 0);
            }
#endif
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
