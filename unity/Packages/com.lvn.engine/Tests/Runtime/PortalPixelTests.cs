using System.Collections;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// СТВОР ВИДНО НА ЭКРАНЕ — проверка по пикселям, а не по полям объекта.
    ///
    /// <para>Всё прочее в портале уже проверено механически: команда доходит,
    /// слой создан, стоит за актёрами, переживает уборку. Но ровно этого и не
    /// хватало, когда портал «не показывался»: объект был на месте и поля были
    /// верные, а на экране — ничего. Слой считал размер один раз, в кадре без
    /// разметки, и оставался точкой.</para>
    ///
    /// <para>Поэтому здесь кадр рисуется в текстуру и читается: в створе должны
    /// быть светящиеся пиксели, вне его — пусто. Это и есть «увидеть глазами»,
    /// только повторяемо.</para>
    /// </summary>
    public class PortalPixelTests
    {
        private Camera _cam;
        private RenderTexture _rt;
        private GameObject _canvasGo;
        private LvnPortalLayer _portal;

        [SetUp]
        public void SetUp()
        {
            _rt = new RenderTexture(256, 256, 0);
            _cam = new GameObject("t-cam", typeof(Camera)).GetComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.black;
            _cam.targetTexture = _rt;

            _canvasGo = new GameObject("t-canvas", typeof(Canvas), typeof(CanvasScaler));
            var canvas = _canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = _cam;
            canvas.planeDistance = 1f;

            _portal = LvnPortalLayer.Create(_canvasGo.transform, siblingIndex: -1);
        }

        [TearDown]
        public void TearDown()
        {
            if (_cam != null) { _cam.targetTexture = null; Object.Destroy(_cam.gameObject); }
            Object.Destroy(_canvasGo);
            Object.Destroy(_rt);
        }

        /// <summary>Средняя яркость квадрата вокруг точки кадра (0..1 по обеим
        /// осям, y вверх — как в текстуре).</summary>
        private float Luminance(float u, float v, int half = 12)
        {
            _cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            int cx = Mathf.RoundToInt(u * _rt.width), cy = Mathf.RoundToInt(v * _rt.height);
            float sum = 0f; int n = 0;
            for (int y = cy - half; y <= cy + half; y++)
                for (int x = cx - half; x <= cx + half; x++)
                {
                    if (x < 0 || y < 0 || x >= _rt.width || y >= _rt.height) continue;
                    sum += tex.GetPixel(x, y).grayscale; n++;
                }
            Object.Destroy(tex);
            return n > 0 ? sum / n : 0f;
        }

        [UnityTest]
        public IEnumerator OpenPortalIsVisibleOnScreen()
        {
            _portal.Place(new Vector2(0.5f, 0.5f), 0.30f, new Color(0.48f, 0.84f, 1f));
            _portal.Set(1f, 0f);
            yield return null;
            yield return null;

            float inside = Luminance(0.5f, 0.5f);
            Assert.Greater(inside, 0.05f,
                "створ раскрыт, а в кадре темно — ровно так выглядел «портал не показывается»");
        }

        [UnityTest]
        public IEnumerator ClosedPortalLeavesTheFrameClean()
        {
            _portal.Place(new Vector2(0.5f, 0.5f), 0.30f, new Color(0.48f, 0.84f, 1f));
            _portal.Set(1f, 0f);
            yield return null;
            _portal.Set(0f, 0f);
            yield return null;
            yield return null;

            Assert.Less(Luminance(0.5f, 0.5f), 0.02f,
                "закрытый створ продолжает светить — он остаётся поверх сцены пятном");
        }

        // Ровно тот отказ, что был живьём: слой считает размер в кадре, где у
        // родителя ещё нет разметки. Объект есть, поля верные — на экране
        // ничего. Поэтому кадр читается СРАЗУ после создания слоя.
        [UnityTest]
        public IEnumerator PortalIsVisibleOnTheVeryFirstFrame()
        {
            _portal.Place(new Vector2(0.5f, 0.5f), 0.30f, new Color(0.48f, 0.84f, 1f));
            _portal.Set(1f, 0f);
            yield return null;   // единственный кадр — как при первом заходе в меню

            Assert.Greater(Luminance(0.5f, 0.5f), 0.05f,
                "в первом же кадре створа нет — он посчитал себя нулевым и таким остался");
        }

        [UnityTest]
        public IEnumerator PortalStandsWhereItWasPlaced()
        {
            // Справа от центра — так он стоит на главной (x = 0.72).
            _portal.Place(new Vector2(0.72f, 0.5f), 0.22f, new Color(0.48f, 0.84f, 1f));
            _portal.Set(1f, 0f);
            yield return null;
            yield return null;

            float right = Luminance(0.72f, 0.5f);
            float left = Luminance(0.20f, 0.5f);
            Assert.Greater(right, 0.05f, "справа, где стоит створ, пусто");
            Assert.Less(left, 0.02f, "створ светит там, где его не ставили");
        }
    }
}
