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
    /// stripped). Handy for bundled/offline content and for tests. Audio returns
    /// null by default — wire your own decoding if you ship sound from a folder.
    /// </summary>
    public sealed class DirectoryAssets : ILvnAssets
    {
        private readonly string _base;

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

        public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
        {
            var path = PathFor(url);
            if (path == null || !File.Exists(path)) return Task.FromResult<Sprite>(null);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(path))) return Task.FromResult<Sprite>(null);
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            return Task.FromResult(sprite);
        }

        public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
            => Task.FromResult<AudioClip>(null);
    }
}
