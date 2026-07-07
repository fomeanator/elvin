using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.UI
{
    /// <summary>
    /// An <see cref="ILvnAssets"/> that loads sprites and audio from a remote
    /// server via UnityWebRequest. Useful for web games, streaming content,
    /// or as a fallback when local assets are missing.
    ///
    /// Assets are cached by url in memory; call <see cref="Unload"/> or
    /// <see cref="UnloadAll"/> to release GPU/CPU memory.
    /// </summary>
    public sealed class NetworkAssets : ILvnAssets
    {
        private readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
        private readonly Dictionary<string, AudioClip> _audioCache = new Dictionary<string, AudioClip>();
        // In-flight de-dup: a prefetch and a show racing for the same url must
        // share ONE download — the loser of an unguarded race overwrote the
        // cache entry and leaked the winner's Texture2D/AudioClip forever.
        // Main-thread only (Unity awaits resume on the main thread), no locks.
        private readonly Dictionary<string, Task<Sprite>> _spriteInFlight = new Dictionary<string, Task<Sprite>>();
        private readonly Dictionary<string, Task<AudioClip>> _audioInFlight = new Dictionary<string, Task<AudioClip>>();
        private readonly string _baseUrl;

        /// <summary>Optional base url prepended to relative urls.
        /// E.g., "https://cdn.example.com/content".</summary>
        public string BaseUrl
        {
            get => _baseUrl;
            init => _baseUrl = value?.TrimEnd('/');
        }

        public NetworkAssets(string baseUrl = null)
        {
            _baseUrl = baseUrl?.TrimEnd('/');
        }

        private string FullUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (!string.IsNullOrEmpty(_baseUrl) && !url.StartsWith("http"))
                return _baseUrl + "/" + url.TrimStart('/');
            return url;
        }

        public async Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_spriteCache.TryGetValue(url, out var hit)) return hit;
            if (_spriteInFlight.TryGetValue(url, out var pending))
            {
                try { return await pending; }
                catch { return null; } // the initiating call's ct fired — behave like a plain miss
            }
            var task = LoadSpriteCoreAsync(url, ct);
            _spriteInFlight[url] = task;
            try { return await task; }
            finally { _spriteInFlight.Remove(url); }
        }

        private async Task<Sprite> LoadSpriteCoreAsync(string url, CancellationToken ct)
        {
            var fullUrl = FullUrl(url);
            if (fullUrl == null) return null;

            try
            {
                using var request = UnityWebRequestTexture.GetTexture(fullUrl);
                var op = request.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success) return null;

                var tex = DownloadHandlerTexture.GetContent(request);
                if (tex == null) return null;

                // Cap oversized textures ON MOBILE only (content ships 4k–8k Spine
                // atlases; a phone shows them at ~1080p, so 2560 is ~lossless and
                // drops memory 4–15×). Desktop/editor keeps the original so quality
                // is pristine and frame-packed atlases never risk resample skew.
                if (Application.isMobilePlatform)
                    tex = DownscaleIfOversized(tex, MaxTextureSize);
                tex.wrapMode   = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);

                // FullRect, explicitly — the default Tight mesh walks the whole
                // texture's alpha on the main thread (hundreds of ms at 2K+).
                var sprite = Sprite.Create(tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                _spriteCache[url] = sprite;
                return sprite;
            }
            catch
            {
                return null;
            }
        }

        public async Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_audioCache.TryGetValue(url, out var hit)) return hit;
            if (_audioInFlight.TryGetValue(url, out var pending))
            {
                try { return await pending; }
                catch { return null; }
            }
            var task = LoadAudioCoreAsync(url, ct);
            _audioInFlight[url] = task;
            try { return await task; }
            finally { _audioInFlight.Remove(url); }
        }

        private async Task<AudioClip> LoadAudioCoreAsync(string url, CancellationToken ct)
        {
            var fullUrl = FullUrl(url);
            if (fullUrl == null) return null;

            try
            {
                using var request = UnityWebRequestMultimedia.GetAudioClip(fullUrl, AudioType.UNKNOWN);
                var op = request.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success) return null;

                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null) return null;

                _audioCache[url] = clip;
                return clip;
            }
            catch
            {
                return null;
            }
        }

        public async Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
        {
            if (urls == null || urls.Count == 0) return;

            var tasks = new List<Task>();
            foreach (var url in urls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                tasks.Add(kind == "audio"
                    ? LoadAudioAsync(url, ct)
                    : LoadSpriteAsync(url, ct).ContinueWith(_ => { }));
            }
            await Task.WhenAll(tasks);
        }

        // Longest-side cap for loaded textures. A mobile-first VN never needs
        // more; huge atlases otherwise stall on decode/upload and blow memory.
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
