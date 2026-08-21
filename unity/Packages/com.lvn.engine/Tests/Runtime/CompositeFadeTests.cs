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
        [Test]
        public void OneImageNeedsNoShaderPath()
        {
            Assert.IsFalse(LvnFade.NeedsCompositeFade(1),
                "одному изображению просвечивать не сквозь что — там дешевле обычная альфа");
            Assert.IsTrue(LvnFade.NeedsCompositeFade(2),
                "два слоя — уже композит: гасить их порознь нельзя");
        }

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
            // СРЕДНЕЕ ПО ОБЛАСТИ, а не один пиксель: гашение решает судьбу
            // каждого пикселя отдельно, и одиночная проба попадёт либо в
            // «выживший», либо в «отброшенный» — то есть измерит случайность.
            float lum = 0f; int n = 0;
            for (int y = rt.height / 2 - 20; y < rt.height / 2 + 20; y++)
                for (int x = rt.width / 2 - 20; x < rt.width / 2 + 20; x++)
                { lum += shot.GetPixel(x, y).grayscale; n++; }
            lum /= n;
            Object.Destroy(shot);

            // Считаем, что должно получиться. Чёрная одежда, погашенная
            // наполовину над серым фоном: половина пикселей — одежда (0),
            // половина — фон (0.5), в среднем ≈0.25.
            //
            // ГРАНИЦЫ С ДВУХ СТОРОН — обязательно. Сверху ловится рентген:
            // послойная альфа пропускает белое тело и поднимает среднее до
            // ≈0.375. Снизу ловится «гашение не применилось вовсе»: сплошная
            // чёрная одежда без прорех даёт ≈0.0 — и первая версия этого теста,
            // с одной верхней границей, приняла ровно такой вакуумный проход
            // (fade при dur=0 молча не доезжал до материала).
            Assert.Less(lum, 0.31f,
                $"средняя яркость в перекрытии {lum:F3} (ожидалось ≈0.25): сквозь одежду " +
                "проступает тело — гашение идёт послойно вместо композитного");
            Assert.Greater(lum, 0.17f,
                $"средняя яркость в перекрытии {lum:F3} (ожидалось ≈0.25): фон сквозь прорехи " +
                "не виден — гашение не применилось, герой всё ещё непрозрачен");

            LvnSpriteFxDriver.SetFade(actor, 1f);
            cam.targetTexture = null;
            Object.Destroy(cloth); Object.Destroy(body);
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
