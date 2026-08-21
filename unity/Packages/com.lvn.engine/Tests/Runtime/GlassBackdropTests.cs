using System.Collections;
using System.IO;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// ОРИЕНТАЦИЯ ПОДЛОЖКИ — единственное в стекле, что нельзя проверить
    /// рассуждением.
    ///
    /// <para>У кадра камеры начало координат внизу, у интерфейса — вверху, и
    /// какая из систем победит, зависит от графического бэкенда. Ошибка тут не
    /// падает и не логируется: стекло просто показывает мир вверх ногами, а так
    /// как оно РАЗМЫТО, глаз соглашается — пока однажды в подложке не всплывёт
    /// узнаваемое пятно не с той стороны.</para>
    ///
    /// <para>Поэтому тест не рассуждает, а сравнивает: рисует кадр с белым
    /// верхом и чёрным низом, потом читает и сам кадр, и подложку ОДНИМ
    /// способом. Совпал знак разницы «верх минус низ» — ориентации сходятся.
    /// Без графики (CI гоняет PlayMode с -nographics) проверять нечего, и тест
    /// честно объявляет себя пропущенным, а не «зелёным».</para>
    /// </summary>
    public class GlassBackdropTests
    {
        [UnityTest]
        public IEnumerator BackdropKeepsTheFrameOrientation()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("нет графики — подложке неоткуда взяться");

            var camGo = new GameObject("glass-test-cam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 1f;
            cam.backgroundColor = Color.black;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.cullingMask = ~0;

            // Белая доска в ВЕРХНЕЙ половине кадра — метка, по которой видно,
            // не перевернулась ли подложка.
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.transform.position = new Vector3(0f, 0.5f, 5f);
            quad.transform.localScale = new Vector3(10f, 1f, 1f);
            quad.GetComponent<Renderer>().material = new Material(Shader.Find("Unlit/Color")) { color = Color.white };

            var frame = new RenderTexture(128, 256, 16) { name = "glass-test-frame" };
            cam.targetTexture = frame;

            var glass = LvnGlass.Ensure(cam);
            glass.Retain();

            cam.Render();
            yield return null;

            var backdrop = glass.Backdrop;
            Assert.IsNotNull(backdrop, "подложка не появилась после кадра");

            float frameSplit = TopMinusBottom(frame);
            float glassSplit = TopMinusBottom(backdrop);

            Dump(backdrop, "glass-backdrop.png");

            Assert.Greater(Mathf.Abs(frameSplit), 8f,
                "тестовый кадр обязан быть контрастным, иначе сравнивать нечего");
            Assert.Greater(frameSplit * glassSplit, 0f,
                $"подложка перевёрнута относительно кадра (кадр {frameSplit:F1}, стекло {glassSplit:F1})");

            glass.Forget();
            cam.targetTexture = null;
            Object.Destroy(frame);
            Object.Destroy(quad);
            Object.Destroy(camGo);
        }

        /// <summary>Средняя яркость верхней половины минус нижней. Знак — и есть
        /// ориентация; величина показывает, что кадр вообще нарисовался.</summary>
        private static float TopMinusBottom(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            var px = tex.GetPixels();
            int half = rt.height / 2;
            float lo = 0f, hi = 0f;
            for (int y = 0; y < rt.height; y++)
                for (int x = 0; x < rt.width; x++)
                {
                    float v = px[y * rt.width + x].grayscale;
                    if (y < half) lo += v; else hi += v;
                }
            int n = half * rt.width;
            Object.Destroy(tex);
            // ReadPixels отдаёт строки СНИЗУ вверх, поэтому «hi» — это верх кадра.
            return (hi - lo) / n * 255f;
        }

        /// <summary>Сохранить подложку рядом с логами — глазами видно за секунду
        /// то, на что числами уходит абзац.</summary>
        private static void Dump(RenderTexture rt, string name)
        {
            var dir = System.Environment.GetEnvironmentVariable("LVN_TEST_SHOTS");
            if (string.IsNullOrEmpty(dir)) return;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, name), tex.EncodeToPNG());
            }
            catch { }   // снимок — удобство отладки, а не результат теста
            Object.Destroy(tex);
        }
    }
}
