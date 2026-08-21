using System.Collections;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// РЕНТГЕН СОСТАВНОГО ГЕРОЯ — та ошибка, на которую движок наступил дважды.
    ///
    /// <para>Персонаж собран из слоёв: тело, одежда, волосы. Если гасить его
    /// альфой группы, она применяется к КАЖДОМУ слою отдельно — и на середине
    /// перехода одежда начинает пропускать тело. Со стороны это выглядит как
    /// «одежда исчезает быстрее тела», хотя скорость у них одна: так работает
    /// альфа-смешение, и подбором кривых это не лечится.</para>
    ///
    /// <para>Первый раз вывод сделали для приглушения не-говорящего (там яркость
    /// ведут цветом). Второй раз — здесь, когда появился переход. Чтобы третьего
    /// раза не было, правило закреплено проверкой на КАРТИНКЕ, а не на намерении:
    /// чёрная «одежда» поверх белого «тела», гашение наполовину — и в точке
    /// перекрытия не должно посветлеть.</para>
    /// </summary>
    public class CompositeFadeTests
    {
        [UnityTest]
        public IEnumerator ClothesDoNotRevealTheBodyMidFade()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("нет графики — картинку не проверить");
            var shader = Resources.Load<Shader>("LvnSpriteFx");
            if (shader == null || !shader.isSupported)
                Assert.Ignore("шейдер слоёв недоступен на этой машине");

            // Сцена: канвас с камерой в текстуру, серый фон.
            var camGo = new GameObject("fade-cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            var rt = new RenderTexture(96, 96, 16);
            cam.targetTexture = rt;

            var canvasGo = new GameObject("fade-canvas", typeof(Canvas), typeof(CanvasScaler));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 1f;

            var actor = new GameObject("actor", typeof(RectTransform), typeof(CanvasGroup));
            actor.transform.SetParent(canvasGo.transform, false);
            Stretch((RectTransform)actor.transform);

            var body = Layer(actor.transform, Color.white);    // тело — светлое
            var cloth = Layer(actor.transform, Color.black);   // одежда поверх — тёмная
            yield return null;

            // Половина перехода: гаснем ровно наполовину.
            LvnSpriteFxDriver.SetFade(actor, 0.5f);
            yield return null;
            yield return null;
            cam.Render();
            yield return null;

            var shot = Read(rt);
            // СРЕДНЕЕ ПО ОБЛАСТИ, а не один пиксель: боковой фронт проходит
            // через область, поэтому одиночная проба увидит только одну его
            // сторону и не измерит переход целиком.
            float lum = 0f; int n = 0, intermediate = 0, visible = 0, hidden = 0;
            for (int y = rt.height / 2 - 20; y < rt.height / 2 + 20; y++)
                for (int x = rt.width / 2 - 20; x < rt.width / 2 + 20; x++)
                {
                    float px = shot.GetPixel(x, y).grayscale;
                    lum += px; n++;
                    if (px < 0.10f) visible++;
                    else if (px > 0.45f) hidden++;
                    else intermediate++;
                }
            lum /= n;
            Object.Destroy(shot);

            // Форма матта нарочно не делит площадь пополам, поэтому среднее
            // больше не является контрактом. Контракт — почти все пиксели либо
            // чёрная непрозрачная одежда, либо серый фон. Послойная альфа дала
            // бы широкую серую зону (тело через одежду) почти на всей площади.
            Assert.Less((float)intermediate / n, 0.12f,
                $"{intermediate}/{n} полупрозрачных пикселей (среднее {lum:F3}): тело видно сквозь одежду");
            Assert.Greater(visible, n / 50, "матт не оставил видимой стороны героя");
            Assert.Greater(hidden, n / 50, "матт не открыл фон за героем");

            LvnSpriteFxDriver.SetFade(actor, 1f);
            cam.targetTexture = null;
            Object.Destroy(cloth); Object.Destroy(body);
            Object.Destroy(actor); Object.Destroy(canvasGo); Object.Destroy(camGo);
            Object.Destroy(rt);
        }

        [UnityTest]
        public IEnumerator SideFadeIsOneSmoothBandWithoutSpeckle()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("нет графики — картинку не проверить");

            var camGo = new GameObject("clean-fade-cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            var rt = new RenderTexture(96, 96, 16);
            cam.targetTexture = rt;

            var canvasGo = new GameObject("clean-fade-canvas", typeof(Canvas), typeof(CanvasScaler));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 1f;
            var actor = new GameObject("clean-fade-actor", typeof(RectTransform), typeof(CanvasGroup));
            actor.transform.SetParent(canvasGo.transform, false);
            Stretch((RectTransform)actor.transform);
            var body = Layer(actor.transform, Color.white);
            var clothes = Layer(actor.transform, Color.white);
            yield return null;

            LvnSpriteFxDriver.SetFadeDir(actor, 1f);
            LvnSpriteFxDriver.SetFade(actor, 0.5f);
            yield return null;
            yield return null;
            cam.Render();
            yield return null;

            var shot = Read(rt);
            int y = shot.height / 2;
            int upwardJumps = 0;
            float previous = shot.GetPixel(0, y).grayscale;
            for (int x = 1; x < shot.width; x++)
            {
                float current = shot.GetPixel(x, y).grayscale;
                if (current > previous + 0.025f) upwardJumps++;
                previous = current;
            }
            Assert.Greater(shot.GetPixel(4, y).grayscale, 0.9f, "видимая сторона потерялась");
            Assert.Less(shot.GetPixel(shot.width - 5, y).grayscale, 0.1f, "скрытая сторона не погасла");
            Assert.LessOrEqual(upwardJumps, 1,
                $"в боковой маске {upwardJumps} обратных скачков яркости — это экранное зерно/дизеринг");

            Object.Destroy(shot);
            LvnSpriteFxDriver.SetFade(actor, 1f);
            cam.targetTexture = null;
            Object.Destroy(clothes); Object.Destroy(body);
            Object.Destroy(actor); Object.Destroy(canvasGo); Object.Destroy(camGo);
            Object.Destroy(rt);
        }

        [UnityTest]
        public IEnumerator PartOwnedBodyMaterialUsesTheSameActorFade()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("нет графики — картинку не проверить");

            var camGo = new GameObject("part-fade-cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            var rt = new RenderTexture(96, 96, 16);
            cam.targetTexture = rt;

            var canvasGo = new GameObject("part-fade-canvas", typeof(Canvas), typeof(CanvasScaler));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 1f;
            var actor = new GameObject("part-fade-actor", typeof(RectTransform), typeof(CanvasGroup));
            actor.transform.SetParent(canvasGo.transform, false);
            Stretch((RectTransform)actor.transform);

            var body = Layer(actor.transform, Color.white);
            body.name = "layer:body";
            // A part-scoped sfx driver owns this material independently. Before
            // the regression fix the root fade skipped it, while fading clothes.
            body.AddComponent<LvnSpriteFxDriver>();
            var clothes = Layer(actor.transform, Color.black);
            clothes.name = "layer:clothes";
            yield return null;

            LvnSpriteFxDriver.SetFadeDir(actor, 1f);
            LvnSpriteFxDriver.SetFade(actor, 0.5f);
            yield return null;
            yield return null;
            cam.Render();
            yield return null;

            var shot = Read(rt);
            int y = shot.height / 2;
            Assert.Less(shot.GetPixel(12, y).grayscale, 0.08f,
                "visible half must still be opaque clothes, not body");
            Assert.AreEqual(0.5f, shot.GetPixel(84, y).grayscale, 0.08f,
                "hidden half must reveal the background, not the unfaded body/underwear");

            Object.Destroy(shot);
            LvnSpriteFxDriver.SetFade(actor, 1f);
            cam.targetTexture = null;
            Object.Destroy(clothes); Object.Destroy(body);
            Object.Destroy(actor); Object.Destroy(canvasGo); Object.Destroy(camGo);
            Object.Destroy(rt);
        }

        private static GameObject Layer(Transform parent, Color c)
        {
            var go = new GameObject("layer", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            Stretch((RectTransform)go.transform);
            go.GetComponent<Image>().color = c;
            return go;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        private static Texture2D Read(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }
    }
}
