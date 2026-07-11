using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.UI
{
    /// <summary>
    /// A reference <see cref="ILvnAssets"/> that loads sprites from a local
    /// folder: a url like <c>/content/bg/room.png</c> maps to
    /// <c>&lt;baseDir&gt;/bg/room.png</c> (the <see cref="ContentPrefix"/> is
    /// stripped). Sprites are cached by url, and the file read happens off the
    /// main thread so showing a character or background doesn't freeze the click
    /// that triggered it. Audio clips are loaded from .wav/.ogg files in the
    /// same base directory.
    /// </summary>
    public sealed class DirectoryAssets : ILvnAssets
    {
        private readonly string _base;
        private readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
        private readonly Dictionary<string, AudioClip> _audioCache = new Dictionary<string, AudioClip>();

        /// <summary>Url prefix stripped before mapping to a file (default "/content").</summary>
        public string ContentPrefix = "/content";

        public DirectoryAssets(string baseDir) => _base = baseDir;

        private string PathFor(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var rel = url;
            if (!string.IsNullOrEmpty(ContentPrefix) && rel.StartsWith(ContentPrefix))
                rel = rel.Substring(ContentPrefix.Length);
            return Path.Combine(_base, rel.TrimStart('/'));
        }

        public async Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_spriteCache.TryGetValue(url, out var hit)) return hit;

            var path = PathFor(url);
            if (path == null || !File.Exists(path)) return null;

            byte[] bytes;
            try { bytes = await Task.Run(() => File.ReadAllBytes(path), ct); }
            catch { return null; }
            if (ct.IsCancellationRequested) return null;

            if (_spriteCache.TryGetValue(url, out hit)) return hit;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes)) return null;
            // Cap oversized textures ON MOBILE only (bundled content can still
            // ship 4k–8k Spine atlases; a phone shows them at ~1080p, so 2560 is
            // ~lossless and drops memory 4–15×). Desktop/editor keeps the
            // original — see NetworkAssets, which mirrors this exact policy.
            if (Application.isMobilePlatform)
                tex = DownscaleIfOversized(tex, MaxTextureSize);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            // Nothing reads pixels back — free the CPU copy (halves per-sprite memory).
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            // FullRect, explicitly — the default Tight mesh walks the whole
            // texture's alpha on the main thread (hundreds of ms at 2K+).
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            _spriteCache[url] = sprite;
            return sprite;
        }

        // Longest-side cap for loaded textures — mirrors NetworkAssets.
        private const int MaxTextureSize = 2560;

        // GPU-resample an oversized texture down to the cap and destroy the
        // original. Returns the input unchanged when it already fits.
        private static Texture2D DownscaleIfOversized(Texture2D tex, int cap)
        {
            int m = Mathf.Max(tex.width, tex.height);
            if (m <= cap) return tex;
            float k = (float)cap / m;
            int w = Mathf.Max(1, Mathf.RoundToInt(tex.width * k));
            int h = Mathf.Max(1, Mathf.RoundToInt(tex.height * k));
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var small = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
            small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Object.Destroy(tex);
            return small;
        }

        public Task<string> LoadTextAsync(string url, CancellationToken ct)
        {
            var path = PathFor(url);
            if (path == null || !File.Exists(path)) return Task.FromResult<string>(null);
            try { return Task.FromResult(File.ReadAllText(path)); }
            catch { return Task.FromResult<string>(null); }
        }

        public async Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_audioCache.TryGetValue(url, out var hit)) return hit;

            var path = PathFor(url);
            if (path == null || !File.Exists(path)) return null;

            // Decode through UnityWebRequestMultimedia from a file:// url — Unity's
            // own decoder, run on the main thread (the only place AudioClip can be
            // built). This handles wav/ogg/mp3 correctly; never hand-roll PCM.
            using var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, GuessAudioType(path));
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (ct.IsCancellationRequested) { req.Abort(); return null; }
                await Task.Yield();
            }
            if (req.result is UnityWebRequest.Result.ConnectionError
                           or UnityWebRequest.Result.DataProcessingError)
                return null;

            if (_audioCache.TryGetValue(url, out hit)) return hit;

            var clip = DownloadHandlerAudioClip.GetContent(req);
            if (clip != null) _audioCache[url] = clip;
            return clip;
        }

        private static AudioType GuessAudioType(string path)
        {
            var lower = path.ToLowerInvariant();
            if (lower.EndsWith(".ogg")) return AudioType.OGGVORBIS;
            if (lower.EndsWith(".wav")) return AudioType.WAV;
            if (lower.EndsWith(".mp3")) return AudioType.MPEG;
            return AudioType.UNKNOWN;
        }

        public void Unload(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (_spriteCache.TryGetValue(url, out var sprite))
            {
                if (sprite != null)
                {
                    if (sprite.texture != null) Object.Destroy(sprite.texture);
                    Object.Destroy(sprite);
                }
                _spriteCache.Remove(url);
            }
            if (_audioCache.TryGetValue(url, out var clip))
            {
                if (clip != null) Object.Destroy(clip);
                _audioCache.Remove(url);
            }
        }

        public void UnloadAll()
        {
            foreach (var kv in _spriteCache)
            {
                if (kv.Value != null)
                {
                    if (kv.Value.texture != null) Object.Destroy(kv.Value.texture);
                    Object.Destroy(kv.Value);
                }
            }
            foreach (var kv in _audioCache)
            {
                if (kv.Value != null) Object.Destroy(kv.Value);
            }
            _spriteCache.Clear();
            _audioCache.Clear();
        }
    }
}
