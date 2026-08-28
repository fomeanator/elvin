using System;
using UnityEngine;

namespace Lvn.Content
{
    /// <summary>
    /// ПЕРЕСНЯТЬ ТЕКСТУРУ ЧЕРЕЗ ВИДЕОПАМЯТЬ — единственное место, где движок
    /// подменяет активную цель отрисовки.
    ///
    /// <para>Приём нужен потому, что текстура из сети приходит БЕЗ копии на
    /// процессоре: чтобы её уменьшить или снабдить уровнями детализации, кадр
    /// сначала рисуют во временную цель, а потом читают обратно. Записан он был
    /// трижды — уменьшение под бюджет памяти, построение уровней, эскиз
    /// сохранения, — и каждый раз одной и той же цепочкой из пяти шагов.</para>
    ///
    /// <para>В цепочке две ловушки, и обе молчаливые:</para>
    ///
    /// <list type="bullet">
    /// <item>временная цель берётся из ПУЛА и обязана вернуться в него; забыть
    /// — и пул растёт молча, а память уходит там, где её не ищут;</item>
    /// <item>активная цель отрисовки глобальна: не вернуть прежнюю значит
    /// испортить рисование СЛЕДУЮЩЕМУ, кто ничего об этом не знает.</item>
    /// </list>
    ///
    /// <para>Ни один из трёх экземпляров не был защищён от исключения посреди
    /// чтения — а <c>ReadPixels</c> падает на части устройств. Здесь возврат
    /// обеих вещей стоит в <c>finally</c>, то есть случается всегда.</para>
    /// </summary>
    public static class LvnTexCopy
    {
        /// <summary>
        /// Пересобрать текстуру в новом размере (и, если попросят, с уровнями
        /// детализации).
        /// </summary>
        /// <param name="src">исходник; при <paramref name="destroySource"/> он
        /// уничтожается — вызывающий обычно им больше не владеет</param>
        /// <param name="width">ширина новой текстуры</param>
        /// <param name="height">высота новой текстуры</param>
        /// <param name="mipmaps">строить ли уровни детализации</param>
        /// <param name="readable">оставить ли копию на процессоре; <c>false</c>
        /// отдаёт память, но закрывает повторное чтение</param>
        /// <param name="destroySource">уничтожить исходник после переноса</param>
        public static Texture2D Rescale(Texture2D src, int width, int height,
                                        bool mipmaps = false, bool readable = false,
                                        bool destroySource = false)
        {
            if (src == null) return null;
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);

            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: mipmaps);
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply(updateMipmaps: mipmaps, makeNoLongerReadable: !readable);
                return copy;
            }
            finally
            {
                RenderTexture.active = prev;              // чужое рисование не портим
                RenderTexture.ReleaseTemporary(rt);       // и пул не растим
                if (destroySource) UnityEngine.Object.Destroy(src);
            }
        }
    }
}
