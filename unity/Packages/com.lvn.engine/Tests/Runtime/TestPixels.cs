using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

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

        /// <summary>
        /// ЭТАЛОННЫЙ КАДР — сверка картинки с той, что признали правильной.
        ///
        /// <para>Раскладку меряют числа, а «стало некрасиво» — только глаз:
        /// шрифт съехал, цвет плашки поменяла тема, рамка сплющилась. Эталон
        /// лежит рядом с тестами (<c>Tests/Runtime/Golden/&lt;имя&gt;.png</c>),
        /// снимается тем же тестом с <c>LVN_GOLDEN_WRITE=1</c> и меняется
        /// отдельным коммитом — осознанно, с картинкой в PR. Нет эталона —
        /// тест пропускается и говорит, как его записать; есть — считается
        /// доля пикселей, ушедших дальше порога, и при провале рядом с кадрами
        /// (<c>LVN_TEST_SHOTS</c>) кладётся разница, чтобы искать не вслепую.</para>
        /// </summary>
        /// <param name="tolerance">Допустимая доля изменившихся пикселей:
        /// сглаживание шрифта и мерцание градиента укладываются в один процент.</param>
        public static void AssertGolden(Texture2D shot, string name, float tolerance = 0.01f)
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                Application.dataPath, "..", "..", "Packages", "com.lvn.engine", "Tests", "Runtime", "Golden"));
            string path = System.IO.Path.Combine(dir, name + ".png");
            // ПУСТОЙ КАДР — НЕ ЭТАЛОН. Первый эталон главной записался чёрным:
            // вид ждал входа и стоял невидимым, а сверка с пустотой проходила бы
            // вечно. Прежде чем писать или сверять, кадр обязан что-то показывать.
            var pixels = shot.GetPixels32();
            int painted = 0;
            for (int i = 0; i < pixels.Length; i++) if (pixels[i].a > 0) painted++;
            Assert.Greater(painted, pixels.Length / 100,
                $"кадр «{name}» пуст ({painted} закрашенных пикселей из {pixels.Length}) — сверять нечего: вид не показан или снят до раскладки");
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("LVN_GOLDEN_WRITE")))
            {
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllBytes(path, shot.EncodeToPNG());
                TestContext.WriteLine($"эталон записан: {path}");
                return;
            }
            if (!System.IO.File.Exists(path))
                Assert.Ignore($"эталона «{name}» нет — запишите его: LVN_GOLDEN_WRITE=1 qa/run-all.sh --playmode --filter …");

            var golden = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Assert.IsTrue(golden.LoadImage(System.IO.File.ReadAllBytes(path)), $"эталон «{name}» не читается");
                Assert.AreEqual((golden.width, golden.height), (shot.width, shot.height),
                    $"эталон «{name}» другого размера — панель теста изменилась, перепишите эталон");
                var a = golden.GetPixels32();
                var b = shot.GetPixels32();
                int changed = 0;
                var diff = new Color32[a.Length];
                for (int i = 0; i < a.Length; i++)
                {
                    int d = Mathf.Max(Mathf.Abs(a[i].r - b[i].r), Mathf.Abs(a[i].g - b[i].g), Mathf.Abs(a[i].b - b[i].b));
                    bool off = d > 40;
                    if (off) changed++;
                    diff[i] = off ? new Color32(255, 40, 40, 255) : new Color32((byte)(b[i].r / 3), (byte)(b[i].g / 3), (byte)(b[i].b / 3), 255);
                }
                float share = (float)changed / a.Length;
                TestContext.WriteLine($"эталон «{name}»: ушло {share:P2} пикселей (порог {tolerance:P0})");
                if (share <= tolerance) return;
                string shots = System.Environment.GetEnvironmentVariable("LVN_TEST_SHOTS");
                string where = string.IsNullOrEmpty(shots) ? System.IO.Path.GetTempPath() : shots;
                System.IO.Directory.CreateDirectory(where);
                var diffTex = new Texture2D(shot.width, shot.height, TextureFormat.RGBA32, false);
                try
                {
                    diffTex.SetPixels32(diff); diffTex.Apply();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(where, name + "-diff.png"), diffTex.EncodeToPNG());
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(where, name + "-now.png"), shot.EncodeToPNG());
                }
                finally { Object.Destroy(diffTex); }
                Assert.Fail($"кадр «{name}» ушёл от эталона: {share:P2} пикселей при пороге {tolerance:P0}; "
                          + $"разница — {where}/{name}-diff.png. Если так и задумано, перепишите эталон (LVN_GOLDEN_WRITE=1).");
            }
            finally { Object.Destroy(golden); }
        }

        /// <summary>Пропустить тест, если панель UITK в этой среде не считает
        /// раскладку: без размеров мерить нечего, а молчаливый ноль выдавать
        /// за «не влезло» нельзя. Одно место на все геометрические тесты.</summary>
        public static void RequireLayout(UnityEngine.UIElements.VisualElement el, string what)
        {
            if (el == null || float.IsNaN(el.worldBound.width) || el.worldBound.width <= 0f
                || float.IsNaN(el.worldBound.height) || el.worldBound.height <= 0f)
                Assert.Ignore($"панель UITK в этой среде не считает раскладку ({what}) — размеры проверить нечем");
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
