using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// A reference <see cref="ILvnAssets"/> that loads sprites from a local
    /// folder: a url like <c>/content/bg/room.png</c> maps to
    /// <c>&lt;baseDir&gt;/bg/room.png</c> (the <see cref="ContentPrefix"/> is
    /// stripped). Sprites are cached by url, and the file read happens off the
    /// main thread so showing a character or background doesn't freeze the click
    /// that triggered it. Audio returns null by default.
    /// </summary>
    public sealed class DirectoryAssets : ILvnAssets
    {
        private readonly string _base;
        private readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

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
            if (_cache.TryGetValue(url, out var hit)) return hit; // instant re-show

            var path = PathFor(url);
            if (path == null || !File.Exists(path)) return null;

            // Read off the main thread, so the synchronous click → Advance loop
            // isn't blocked by disk I/O; the await also lets the decode run on a
            // later frame instead of freezing the current one.
            byte[] bytes;
            try { bytes = await Task.Run(() => File.ReadAllBytes(path), ct); }
            catch { return null; }
            if (ct.IsCancellationRequested) return null;

            if (_cache.TryGetValue(url, out hit)) return hit; // another load won the race

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes)) return null;
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            _cache[url] = sprite;
            return sprite;
        }

        public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
            => Task.FromResult<AudioClip>(null);
    }
}
