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
            // Пробелы, скобки и кириллица в имени файла — обычное дело для арта
            // от художника, а UnityWebRequest их не экранирует: адрес уходит
            // сырым и промахивается. Кодируем тем же способом, что и основной
            // загрузчик — иначе одна и та же картинка грузилась бы по-разному
            // в зависимости от того, кто её запросил.
            //
            // «Свой ли это адрес» спрашиваем у ДОМА АДРЕСА. Здесь стояло
            // «начинается на http», и локальный file:// считался
            // относительным: к нему приписывалась база И его кодировали — а за
            // file:// стоит чтение с диска, где «%20» означает файл, которого
            // нет. Тот же загрузчик, на который ссылается комментарий выше,
            // локальные адреса не кодирует НИКОГДА.
            if (Lvn.Content.LvnUrl.Local(url)) return url;
            if (!string.IsNullOrEmpty(_baseUrl) && !Lvn.Content.LvnUrl.Remote(url))
                return _baseUrl + "/" + Lvn.Content.ContentLoader.EncodeUrlPath(url.TrimStart('/'));
            return Lvn.Content.ContentLoader.EncodeUrlPath(url);
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
                // Ждёт дом: он же обрывает запрос по молчанию — здесь висели до
                // срока UnityWebRequest, а он про весь ответ, не про застой.
                if (!await Lvn.Content.LvnNetWait.AwaitAsync(request, op, ct))
                    ct.ThrowIfCancellationRequested();

                if (request.result != UnityWebRequest.Result.Success) return null;

                var tex = DownloadHandlerTexture.GetContent(request);
                if (tex == null) return null;

                // Cap oversized textures ON MOBILE only (content ships 4k–8k Spine
                // atlases; a phone shows them at ~1080p, so 2560 is ~lossless and
                // drops memory 4–15×). Desktop/editor keeps the original so quality
                // is pristine and frame-packed atlases never risk resample skew.
                // Потолок размера — только на телефоне: настольная машина держит
                // исходное качество, а телефон показывает всё равно ~1080p.
                var sprite = Lvn.Content.AssetMemory.MakeSprite(tex, Application.isMobilePlatform);
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
                // Ждёт дом: он же обрывает запрос по молчанию — здесь висели до
                // срока UnityWebRequest, а он про весь ответ, не про застой.
                if (!await Lvn.Content.LvnNetWait.AwaitAsync(request, op, ct))
                    ct.ThrowIfCancellationRequested();

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

        // more; huge atlases otherwise stall on decode/upload and blow memory.
        // Выгрузка — в общем доме (AssetMemory): у поставщиков разные кэши, но
        // одинаковые правила освобождения. Копия здесь и была тем местом, где
        // «почти одинаково» однажды становится «по-разному».
        public void Unload(string url) => Lvn.Content.AssetMemory.Forget(url, _spriteCache, _audioCache);

        public void UnloadAll() => Lvn.Content.AssetMemory.ForgetAll(_spriteCache, _audioCache);

    }
}
