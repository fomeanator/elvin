using System.Collections.Generic;
using UnityEngine;

namespace Lvn.Content
{
    /// <summary>
    /// РАБОТА С ПАМЯТЬЮ ЗАГРУЖЕННОГО АРТА — один дом на всех поставщиков.
    ///
    /// <para>Живёт в слое КОНТЕНТА, а не интерфейса: это про текстуры и кэши.
    /// Стоял в UI — и загрузчик, лежащий ниже по зависимостям, до него не
    /// доставал, из-за чего и завёл третью копию уменьшения.</para>
    ///
    /// <para>Уменьшение великоватой текстуры, сборка спрайта и выгрузка кэшей
    /// были продублированы у <c>DirectoryAssets</c> и <c>NetworkAssets</c>
    /// дословно — в коде даже стоял комментарий «mirrors NetworkAssets».
    /// Копия опасна не сама по себе, а тем, чего в ней не видно: разойдись
    /// потолок размера на одну строку — и на устройстве половина арта грузится
    /// вдвое тяжелее, причём молча.</para>
    /// </summary>
    public static class AssetMemory
    {
        /// <summary>Потолок длинной стороны текстуры. Один на весь движок:
        /// раньше их было два, в соседних файлах, с обещанием совпадать.</summary>
        public const int MaxTextureSize = 2560;

        /// <summary>Уменьшить, если больше потолка, и освободить исходную.
        /// Уже влезающую текстуру возвращает как есть.</summary>
        /// <param name="finalize">Отпустить копию пикселей на процессоре. Ложь
        /// нужна тем, кто после уменьшения ещё дописывает текстуру сам —
        /// финализирует вызывающий.</param>
        public static Texture2D DownscaleIfOversized(Texture2D tex, int cap = MaxTextureSize,
                                                     bool finalize = true)
        {
            if (tex == null) return null;
            int m = Mathf.Max(tex.width, tex.height);
            if (m <= cap) return tex;

            float k = (float)cap / m;
            int w = Mathf.Max(1, Mathf.RoundToInt(tex.width * k));
            int h = Mathf.Max(1, Mathf.RoundToInt(tex.height * k));

            // Пересъёмка через видеопамять — у LvnTexCopy: там возврат активной
            // цели и временной текстуры стоит в finally, а тут стоял по прямой
            // и терялся, если ReadPixels падал (а он падает на части устройств).
            return LvnTexCopy.Rescale(tex, w, h, mipmaps: false,
                                      readable: !finalize, destroySource: true);
        }

        /// <summary>
        /// МИП-УРОВНИ ДЛЯ КРУПНОГО АРТА — иначе у фигуры рвутся края.
        ///
        /// <para>Героиня нарисована в 1600 пикселей по ширине, а на экране
        /// занимает около девятисот. Без мип-уровней видеокарта берёт из
        /// текстуры каждый второй пиксель — по плавным местам это незаметно, а
        /// на границе фигуры с фоном даёт ступеньки. Со стороны выглядит как
        /// «картинку пожали», хотя картинка целая: считали её плохо.</para>
        ///
        /// <para>Уровни строятся через временную RenderTexture, потому что
        /// текстура из сети приходит без копии на процессоре. Стоит это одного
        /// чтения кадра — заметного, поэтому делается ТОЛЬКО для крупного арта
        /// и только на загрузке: мелкие иконки минифицировать нечем, им уровни
        /// лишняя треть памяти.</para>
        /// </summary>
        public static Texture2D WithMipmaps(Texture2D tex, int minSide = 1200, bool finalize = true)
        {
            if (tex == null || tex.mipmapCount > 1) return tex;
            if (Mathf.Max(tex.width, tex.height) < minSide) return tex;

            int w = tex.width, h = tex.height;
            var mipped = LvnTexCopy.Rescale(tex, w, h, mipmaps: true,
                                            readable: !finalize, destroySource: true);
            // Трилинейная: между уровнями тоже надо переходить плавно, иначе на
            // изменении масштаба видна граница переключения.
            mipped.wrapMode = TextureWrapMode.Clamp;
            mipped.filterMode = FilterMode.Trilinear;
            return mipped;
        }

        /// <summary>
        /// Спрайт из текстуры по правилам движка: Clamp и Bilinear, пиксели с
        /// процессора отпущены, меш — FullRect.
        ///
        /// <para>FullRect указан ЯВНО: умолчание Tight обходит альфу всей
        /// текстуры в главном потоке — сотни миллисекунд на 2K, то есть
        /// заметный провал кадра на каждой новой картинке.</para>
        /// </summary>
        public static Sprite MakeSprite(Texture2D tex, bool downscale = true, int cap = MaxTextureSize)
        {
            if (tex == null) return null;
            // Уменьшать или нет решает ВЫЗЫВАЮЩИЙ: на телефоне потолок нужен,
            // на настольной машине исходное качество важнее памяти.
            if (downscale) tex = DownscaleIfOversized(tex, cap);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                 new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        }

        /// <summary>Убрать один спрайт вместе с его текстурой.</summary>
        public static void Release(Sprite sprite)
        {
            if (sprite == null) return;
            if (sprite.texture != null) Object.Destroy(sprite.texture);
            Object.Destroy(sprite);
        }

        /// <summary>Выкинуть запись из обоих кэшей поставщика.</summary>
        public static void Forget(string url,
                                  Dictionary<string, Sprite> sprites,
                                  Dictionary<string, AudioClip> clips)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (sprites != null && sprites.TryGetValue(url, out var sprite))
            {
                Release(sprite);
                sprites.Remove(url);
            }
            if (clips != null && clips.TryGetValue(url, out var clip))
            {
                if (clip != null) Object.Destroy(clip);
                clips.Remove(url);
            }
        }

        /// <summary>Опустошить кэши поставщика целиком.</summary>
        public static void ForgetAll(Dictionary<string, Sprite> sprites,
                                     Dictionary<string, AudioClip> clips)
        {
            if (sprites != null)
            {
                foreach (var kv in sprites) Release(kv.Value);
                sprites.Clear();
            }
            if (clips != null)
            {
                foreach (var kv in clips) if (kv.Value != null) Object.Destroy(kv.Value);
                clips.Clear();
            }
        }
    }
}
