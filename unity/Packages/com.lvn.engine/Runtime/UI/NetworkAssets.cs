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

        /// <summary>ОДИН ПУТЬ ЗА ФАЙЛОМ — сборка запроса и разбор ответа
        /// остаются за видом содержимого, всё остальное общее.
        ///
        /// <para>Звук и картинка тянулись двумя телами по девятнадцать строк,
        /// совпадавшими построчно: адрес, запрос, ожидание, проверка ответа,
        /// глушение сбоя. Признак был виден невооружённым глазом — комментарий
        /// про «ждёт дом» стоял в обоих ДОСЛОВНО. Урок записали дважды; значит
        /// следующий запишут один раз, и вторая копия начнёт отставать.</para>
        ///
        /// <para>Что тут вправе отличаться — ровно две вещи: чем запрос
        /// строится и чем из ответа берут содержимое. Они и остались
        /// доводами.</para>
        ///
        /// <para>Сбой глушится НАМЕРЕННО: отсутствующий файл для показа — не
        /// исключение, а «нечего рисовать». Кто должен об этом узнать, узнаёт
        /// из отметки о непригодном ассете, а не из падения кадра.</para>
        /// </summary>
        private async Task<T> FetchAsync<T>(string url,
                                            System.Func<string, UnityWebRequest> build,
                                            System.Func<UnityWebRequest, T> take,
                                            CancellationToken ct) where T : class
        {
            var fullUrl = FullUrl(url);
            if (fullUrl == null) return null;
            try
            {
                using var request = build(fullUrl);
                var op = request.SendWebRequest();
                // Ждёт дом: он же обрывает запрос по молчанию — здесь висели до
                // срока UnityWebRequest, а он про весь ответ, не про застой.
                if (!await Lvn.LvnNetWait.AwaitAsync(request, op, ct))
                    ct.ThrowIfCancellationRequested();
                if (request.result != UnityWebRequest.Result.Success) return null;
                return take(request);
            }
            catch
            {
                return null;
            }
        }

        private Task<AudioClip> LoadAudioCoreAsync(string url, CancellationToken ct)
            => FetchAsync(url,
                          // Тип декодера — из ДОМА, а не UNKNOWN: дом и заведён
                          // потому, что таблица стояла дважды, а сеть ходила
                          // мимо неё третьей. UNKNOWN на адресе без расширения
                          // (или с хвостом версии) значит «скачано, но не
                          // звучит» — без ошибки и без строки в логе.
                          u => UnityWebRequestMultimedia.GetAudioClip(
                              u, Lvn.Content.DownloadPolicy.AudioTypeOf(u)),
                          DownloadHandlerAudioClip.GetContent,
                          ct);

        private Task<Sprite> LoadSpriteCoreAsync(string url, CancellationToken ct)
            => FetchAsync(url,
                          UnityWebRequestTexture.GetTexture,
                          r =>
                          {
                              var tex = DownloadHandlerTexture.GetContent(r);
                              if (tex == null) return null;
                              // Потолок размера — только на телефоне: контент
                              // везёт атласы 4k–8k, телефон показывает их всё
                              // равно ~1080p, и 2560 там ~без потерь, а памяти
                              // экономит вчетверо-впятнадцатеро. Настольная
                              // машина держит исходник: там качество важнее, и
                              // покадровые атласы не рискуют перевыборкой.
                              return Lvn.Content.AssetMemory.MakeSprite(tex, Application.isMobilePlatform);
                          },
                          ct);

        public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
            => _audio.GetAsync(url, () => LoadAudioCoreAsync(url, ct));


        // Выгрузка — в общем доме (AssetMemory): у поставщиков разные кэши, но
        // одинаковые правила освобождения. Копия здесь и была тем местом, где
        // «почти одинаково» однажды становится «по-разному».
        public void Unload(string url) => Lvn.Content.AssetMemory.Forget(url, _sprites.Done, _audio.Done);

        public void UnloadAll() => Lvn.Content.AssetMemory.ForgetAll(_sprites.Done, _audio.Done);

    }
}
