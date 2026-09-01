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
        // «Готовое отдаём, начатое разделяем» — у дома (LvnOnce). ЗДЕСЬ БЫЛА
        // ТОЛЬКО ПЕРВАЯ ПОЛОВИНА: кэш был, а защиты от гонки не было. Стояла
        // перепроверка кэша после ожидания — она сужает окно, но не закрывает:
        // оба захода проходят обе проверки, оба строят текстуру, один
        // результат теряется навсегда вместе с видеопамятью. Сетевой поставщик
        // эту гонку закрыл ещё летом; сюда правило не доехало.
        private readonly Lvn.Content.LvnOnce<Sprite> _sprites = new Lvn.Content.LvnOnce<Sprite>();
        private readonly Lvn.Content.LvnOnce<AudioClip> _audio = new Lvn.Content.LvnOnce<AudioClip>();

        /// <summary>Url prefix stripped before mapping to a file (default "/content").</summary>
        public string ContentPrefix = LvnAssetPath.ContentPrefix;

        public DirectoryAssets(string baseDir) => _base = baseDir;

        private string PathFor(string url)
        {
            var rel = LvnAssetPath.Relative(url, ContentPrefix);
            return rel == null ? null : Path.Combine(_base, rel);
        }

        public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
            => _sprites.GetAsync(url, () => LoadSpriteCoreAsync(url, ct));

        private async Task<Sprite> LoadSpriteCoreAsync(string url, CancellationToken ct)
        {
            var path = PathFor(url);
            if (path == null || !File.Exists(path)) return null;

            byte[] bytes;
            try { bytes = await Task.Run(() => File.ReadAllBytes(path), ct); }
            catch { return null; }
            if (ct.IsCancellationRequested) return null;

            // Через дом: здесь текстуру на неудаче НЕ уничтожали, и битый файл
            // тёк пустой текстурой при каждой попытке.
            var tex = Lvn.Content.AssetMemory.Decode(bytes);
            if (tex == null) return null;
            // Cap oversized textures ON MOBILE only (bundled content can still
            // ship 4k–8k Spine atlases; a phone shows them at ~1080p, so 2560 is
            // ~lossless and drops memory 4–15×). Desktop/editor keeps the
            // original — see NetworkAssets, which mirrors this exact policy.
            // Потолок размера — только на телефоне: настольная машина держит
            // исходное качество, а телефон показывает всё равно ~1080p.
            return Lvn.Content.AssetMemory.MakeSprite(tex, Application.isMobilePlatform);
        }


        public Task<string> LoadTextAsync(string url, CancellationToken ct)
        {
            var path = PathFor(url);
            if (path == null || !File.Exists(path)) return Task.FromResult<string>(null);
            try { return Task.FromResult(File.ReadAllText(path)); }
            catch { return Task.FromResult<string>(null); }
        }

        public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
            => _audio.GetAsync(url, () => LoadAudioCoreAsync(url, ct));

        private async Task<AudioClip> LoadAudioCoreAsync(string url, CancellationToken ct)
        {
            var path = PathFor(url);
            if (path == null || !File.Exists(path)) return null;

            // Decode through UnityWebRequestMultimedia from a file:// url — Unity's
            // own decoder, run on the main thread (the only place AudioClip can be
            // built). This handles wav/ogg/mp3 correctly; never hand-roll PCM.
            using var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, Lvn.Content.DownloadPolicy.AudioTypeOf(path));
            var op = req.SendWebRequest();
            if (!await Lvn.LvnNetWait.AwaitAsync(req, op, ct)) return null;
            if (Lvn.LvnNetWait.Failed(req)) return null;

            return DownloadHandlerAudioClip.GetContent(req);
        }

        // Выгрузка — в общем доме (AssetMemory): у поставщиков разные кэши, но
        // одинаковые правила освобождения. Копия здесь и была тем местом, где
        // «почти одинаково» однажды становится «по-разному».
        public void Unload(string url) => Lvn.Content.AssetMemory.Forget(url, _sprites.Done, _audio.Done);

        public void UnloadAll() => Lvn.Content.AssetMemory.ForgetAll(_sprites.Done, _audio.Done);

    }
}
