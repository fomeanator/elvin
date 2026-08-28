using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lvn.Tests
{
    /// <summary>
    /// СНЯТЬ КАРТИНКУ И ПРОВЕРИТЬ ЖЕЛЕЗО — то, что нужно каждому пиксельному
    /// тесту и что до сих пор переписывалось в каждом.
    ///
    /// <para>Чтение с <see cref="RenderTexture"/> — не «три строки», а ПАРА:
    /// подменить активную текстуру и вернуть прежнюю. Забыть возврат значит
    /// уронить СЛЕДУЮЩИЙ тест, причём загадочно: он читает не свою картинку.
    /// Пока копий было три, вероятность забыть держалась на внимательности
    /// пишущего — ровно то, от чего канон предостерегает в парной работе.</para>
    ///
    /// <para>Проверка железа отделена от чтения намеренно: «нет графики» и
    /// «шейдер не поддержан» — разные причины пропустить тест, и сообщение
    /// должно называть настоящую, иначе на чужой машине ищут не то.</para>
    /// </summary>
    public static class TestPixels
    {
        /// <summary>Снимок текстуры. Активная текстура возвращается всегда.</summary>
        public static Texture2D Read(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            try
            {
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                return tex;
            }
            finally { RenderTexture.active = prev; }   // и при исключении тоже
        }

        /// <summary>Пропустить тест, если рисовать нечем.</summary>
        public static void RequireGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("нет графики — картинку не проверить");
        }

        /// <summary>Пропустить тест, если нужный шейдер не собран или не
        /// поддержан этой машиной.</summary>
        public static Shader RequireShader(string name)
        {
            RequireGraphics();
            var shader = Resources.Load<Shader>(name);
            if (shader == null || !shader.isSupported)
                Assert.Ignore($"шейдер «{name}» недоступен на этой машине");
            return shader;
        }
    }
}
