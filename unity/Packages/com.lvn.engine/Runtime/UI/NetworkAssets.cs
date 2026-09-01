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
        // «Готовое отдаём, начатое разделяем» — у дома (LvnOnce). Здесь была
        // половина правила, написанная дважды: свой кэш и свой словарь полёта
        // отдельно картинкам и отдельно звуку.
        private readonly Lvn.Content.LvnOnce<Sprite> _sprites = new Lvn.Content.LvnOnce<Sprite>();
        private readonly Lvn.Content.LvnOnce<AudioClip> _audio = new Lvn.Content.LvnOnce<AudioClip>();
        private readonly string _baseUrl;

        /// <summary>Optional base url prepended to relative urls.
        /// E.g., "https://cdn.example.com/content".</summary>
        public string BaseUrl
        {
            get => _baseUrl;
            init => _baseUrl = Lvn.LvnUrl.Base(value);
        }

        public NetworkAssets(string baseUrl = null)
        {
            _baseUrl = Lvn.LvnUrl.Base(baseUrl);
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
            if (Lvn.LvnUrl.Local(url)) return url;
            if (!string.IsNullOrEmpty(_baseUrl) && !Lvn.LvnUrl.Remote(url))
                return _baseUrl + "/" + Lvn.Content.ContentLoader.EncodeUrlPath(url.TrimStart('/'));
            return Lvn.Content.ContentLoader.EncodeUrlPath(url);
        }

        public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
            => _sprites.GetAsync(url, () => LoadSpriteCoreAsync(url, ct));

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
                if (!await Lvn.LvnNetWait.AwaitAsync(request, op, ct))
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
                return Lvn.Content.AssetMemory.MakeSprite(tex, Application.isMobilePlatform);
            }
            catch
            {
                return null;
            }
        }

        public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
            => _audio.GetAsync(url, () => LoadAudioCoreAsync(url, ct));

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
                if (!await Lvn.LvnNetWait.AwaitAsync(request, op, ct))
                    ct.ThrowIfCancellationRequested();

                if (request.result != UnityWebRequest.Result.Success) return null;

                return DownloadHandlerAudioClip.GetContent(request);
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
        public void Unload(string url) => Lvn.Content.AssetMemory.Forget(url, _sprites.Done, _audio.Done);

        public void UnloadAll() => Lvn.Content.AssetMemory.ForgetAll(_sprites.Done, _audio.Done);

    }
}
