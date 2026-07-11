using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn.Content
{
    /// <summary>
    /// The GPU-native texture path: on a device whose GPU supports ASTC-
    /// compressed textures, a sprite load first asks the server for an ".astc"
    /// variant (server/astc.go transcodes it on demand from the source PNG/JPG
    /// and caches the result to disk) and, on success, builds the Texture2D
    /// directly from the compressed block data via
    /// <see cref="Texture2D.LoadRawTextureData(byte[])"/> — no RGBA decode, no
    /// full-size VRAM allocation.
    ///
    /// Why this exists alongside <c>tools/lvnconv optimize</c>'s PNG/JPEG
    /// recompression: that (and WebP, which this project deliberately doesn't
    /// use — see its doc comment) only shrinks the WIRE/DISK footprint. Once
    /// decoded, a texture is full RGBA in VRAM regardless of source format.
    /// ASTC is the one encoding that cuts RUNTIME VRAM too (4–8× at 6x6 blocks),
    /// because the GPU samples the compressed bytes directly, never expanding
    /// them to RGBA in memory.
    ///
    /// Strictly opt-in and additive: every failure mode (GPU doesn't support
    /// ASTC, server has no astcenc installed, the asset has no source image,
    /// corrupt/unexpected data) falls straight through to the existing PNG/JPG
    /// decode in <c>DecodeSpriteAsync</c>, unchanged. A title with no ASTC-
    /// capable client, or a server with no astcenc, behaves exactly as before.
    /// </summary>
    public partial class ContentLoader
    {
        // KILL-SWITCH: live-tested 2026-07-06 and the decoded texture came out
        // sliced into rearranged blocks for non-block-aligned dimensions (e.g.
        // 4000×4048, not a multiple of 6) — a row-stride mismatch between how
        // astcenc lays out the last partial row/column of blocks and how Unity's
        // LoadRawTextureData(byte[]) infers stride for TextureFormat.ASTC_6x6.
        // The .astc file itself is verified byte-correct (header + block count
        // match exactly — see lvn-image-pipeline-2026-07 memory), so the bug is
        // in this decode step, not the server encoder. Disabled until root-
        // caused; flip back to the SystemInfo check once fixed.
        private const bool AstcEnabled = false;

        // Computed once — SystemInfo doesn't change mid-session. Gates every
        // attempt so a GPU without ASTC support never even tries the extra
        // request. Standardized on 6x6 (see server/astc.go's astcBlockDim);
        // ARM's own guidance is that 5x5–6x6 is the sane default for game art.
        private static readonly bool AstcSupported =
            AstcEnabled && SystemInfo.SupportsTextureFormat(TextureFormat.ASTC_6x6);

        // Flips true the first time an ".astc" request fails (404, network
        // error, bad data) — after that, every later sprite in this session
        // skips straight to the normal PNG/JPG path instead of paying a failed
        // request (and a console warning — DownloadBytes logs permanent 4xxs)
        // per asset for a server that simply doesn't have astcenc installed. A
        // fresh ContentLoader (next app launch) tries again.
        private bool _astcUnavailable;

        private const uint AstcMagic = 0x5CA1AB13;
        private const int AstcHeaderSize = 16;

        // Attempts the GPU-native path for `url`. Returns (null, 0) on ANY
        // failure — the caller (DecodeSpriteAsync) then runs the ordinary
        // decode exactly as if this method didn't exist.
        private async Task<(Sprite sprite, long bytes)> TryDecodeAstcAsync(string url, CancellationToken ct)
        {
            if (_astcUnavailable || !AstcSupported) return (null, 0);
            var astcUrl = AstcUrlFor(url);
            if (astcUrl == null) return (null, 0);

            byte[] bytes;
            try
            {
                bytes = await DownloadAssetBytes(astcUrl, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // A 404 (no server-side astcenc, or genuinely no source image) is
                // the expected steady state until a server adds astcenc — stop
                // asking for the rest of this session instead of retrying (and
                // logging a warning) per asset.
                _astcUnavailable = true;
                return (null, 0);
            }
            if (bytes == null || bytes.Length == 0) { _astcUnavailable = true; return (null, 0); }

            if (!TryParseAstcHeader(bytes, out int width, out int height, out var format, out int offset))
                return (null, 0); // corrupt/unexpected data — never treat this as fatal

            Texture2D tex = null;
            try
            {
                var raw = new byte[bytes.Length - offset];
                Buffer.BlockCopy(bytes, offset, raw, 0, raw.Length);

                // linear:false → sRGB, matching the encoder's "-cs" (sRGB) profile
                // (server/astc.go) — game art is authored gamma-encoded, so the GPU
                // must degamma on sample or colours come out too dark.
                tex = new Texture2D(width, height, format, mipChain: false, linear: false);
                tex.LoadRawTextureData(raw);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                var sprite = Sprite.Create(tex, new Rect(0, 0, width, height),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                return (sprite, raw.Length);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[content] astc decode failed for {astcUrl}: {ex.Message}");
                if (tex != null) UnityEngine.Object.Destroy(tex);
                return (null, 0);
            }
        }

        // "/content/bg/room.png" -> "/content/bg/room.astc". Null for a url with
        // no recognised image extension (nothing sensible to transcode).
        private static string AstcUrlFor(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            int dot = url.LastIndexOf('.');
            if (dot < 0) return null;
            var ext = url.Substring(dot).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") return null;
            return url.Substring(0, dot) + ".astc";
        }

        // Parses the standard ARM .astc container: a 16-byte header (4-byte
        // magic, 3 block-dim bytes, width/height/depth as 3-byte little-endian
        // fields) followed by raw block data. Rejects anything this project's
        // decoder can't use — 3D textures, and block sizes Unity has no
        // matching TextureFormat for.
        private static bool TryParseAstcHeader(byte[] bytes, out int width, out int height,
            out TextureFormat format, out int dataOffset)
        {
            width = height = dataOffset = 0;
            format = default;
            if (bytes == null || bytes.Length <= AstcHeaderSize) return false;

            uint magic = (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24);
            if (magic != AstcMagic) return false;

            int blockX = bytes[4], blockY = bytes[5], blockZ = bytes[6];
            if (blockZ != 1) return false; // 2D only — this project never emits 3D ASTC

            width = bytes[7] | bytes[8] << 8 | bytes[9] << 16;
            height = bytes[10] | bytes[11] << 8 | bytes[12] << 16;
            int depth = bytes[13] | bytes[14] << 8 | bytes[15] << 16;
            if (depth != 1 || width <= 0 || height <= 0) return false;

            if (!TryBlockDimsToFormat(blockX, blockY, out format)) return false;
            dataOffset = AstcHeaderSize;
            return true;
        }

        // Every 2D square ASTC block size Unity exposes a TextureFormat for.
        // This project's encoder (server/astc.go) always emits 6x6; the rest
        // are accepted too in case that default ever changes.
        private static bool TryBlockDimsToFormat(int x, int y, out TextureFormat format)
        {
            format = default;
            if (x != y) return false; // this project only ever emits square blocks
            switch (x)
            {
                case 4: format = TextureFormat.ASTC_4x4; return true;
                case 5: format = TextureFormat.ASTC_5x5; return true;
                case 6: format = TextureFormat.ASTC_6x6; return true;
                case 8: format = TextureFormat.ASTC_8x8; return true;
                case 10: format = TextureFormat.ASTC_10x10; return true;
                case 12: format = TextureFormat.ASTC_12x12; return true;
                default: return false;
            }
        }
    }
}
