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
        public string ContentPrefix = LvnAssetPath.ContentPrefix;

        public DirectoryAssets(string baseDir) => _base = baseDir;

        private string PathFor(string url)
        {
            var rel = LvnAssetPath.Relative(url, ContentPrefix);
            return rel == null ? null : Path.Combine(_base, rel);
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
            // Потолок размера — только на телефоне: настольная машина держит
            // исходное качество, а телефон показывает всё равно ~1080p.
            var sprite = Lvn.Content.AssetMemory.MakeSprite(tex, Application.isMobilePlatform);
            _spriteCache[url] = sprite;
            return sprite;
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
            using var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, Lvn.Content.DownloadPolicy.AudioTypeOf(path));
            var op = req.SendWebRequest();
            if (!await Lvn.LvnNetWait.AwaitAsync(req, op, ct)) return null;
            if (Lvn.LvnNetWait.Failed(req)) return null;

            if (_audioCache.TryGetValue(url, out hit)) return hit;

            var clip = DownloadHandlerAudioClip.GetContent(req);
            if (clip != null) _audioCache[url] = clip;
            return clip;
        }

        // Выгрузка — в общем доме (AssetMemory): у поставщиков разные кэши, но
        // одинаковые правила освобождения. Копия здесь и была тем местом, где
        // «почти одинаково» однажды становится «по-разному».
        public void Unload(string url) => Lvn.Content.AssetMemory.Forget(url, _spriteCache, _audioCache);

        public void UnloadAll() => Lvn.Content.AssetMemory.ForgetAll(_spriteCache, _audioCache);

    }
}
