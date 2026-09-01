using System.Collections;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Lvn.Tests.Runtime
{
    /// <summary>Переход целиком, в движении: составной герой не должен показать
    /// тело сквозь одежду и не должен подключать служебный материал. Явный
    /// dissolve остаётся отдельным сюжетным эффектом.</summary>
    public class FadeTransitionTests
    {
        // ── общая сцена: серый фон, белое «тело», чёрная «одежда» ────────────

        private Camera _cam;
        private RenderTexture _rt;
        private GameObject _canvasGo, _actor, _body, _cloth;

        private void BuildScene(bool clothSmaller)
        {
            _cam = new GameObject("t-cam").AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            _rt = new RenderTexture(96, 96, 16);
            _cam.targetTexture = _rt;

            _canvasGo = TestStage.Canvas(_cam);

            _actor = new GameObject("t-actor", typeof(RectTransform), typeof(CanvasGroup));
            _actor.transform.SetParent(_canvasGo.transform, false);
            TestStage.Stretch((RectTransform)_actor.transform);

            _body = TestStage.Layer(_actor.transform, Color.white);
            _cloth = TestStage.Layer(_actor.transform, Color.black);
            if (clothSmaller)
            {
                // Одежда меньше тела и сдвинута — у слоёв РАЗНЫЕ UV-сетки.
                // Именно на такой паре шум, привязанный к UV, прогрызает
                // несовпадающие дыры.
                var rt = (RectTransform)_cloth.transform;
                rt.anchorMin = new Vector2(0.25f, 0.25f);
                rt.anchorMax = new Vector2(0.75f, 0.75f);
            }
        }

        private void TearDown()
        {
            if (_cam != null) _cam.targetTexture = null;
            Object.Destroy(_cloth); Object.Destroy(_body);
            Object.Destroy(_actor); Object.Destroy(_canvasGo);
            if (_cam != null) Object.Destroy(_cam.gameObject);
            Object.Destroy(_rt);
        }

        /// <summary>Средняя яркость центральной области (там, где одежда
        /// перекрывает тело в обеих сборках сцены).</summary>
        private float CentreLuminance()
        {
            _cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            float lum = 0f; int n = 0;
            for (int y = _rt.height / 2 - 15; y < _rt.height / 2 + 15; y++)
                for (int x = _rt.width / 2 - 15; x < _rt.width / 2 + 15; x++)
                { lum += tex.GetPixel(x, y).grayscale; n++; }
            Object.Destroy(tex);
            return lum / n;
        }

        private float CentreIntermediateFraction()
        {
            _cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            int intermediate = 0, n = 0;
            for (int y = _rt.height / 2 - 15; y < _rt.height / 2 + 15; y++)
                for (int x = _rt.width / 2 - 15; x < _rt.width / 2 + 15; x++)
                {
                    float px = tex.GetPixel(x, y).grayscale;
                    if (px > 0.10f && px < 0.45f) intermediate++;
                    n++;
                }
            Object.Destroy(tex);
            return n > 0 ? (float)intermediate / n : 1f;
        }

        // ── 1. живой ход LvnFade ────────────────────────────────────────────

        /// <remarks>ПРОБЕЛ ЗАКРЫТ НЕ ЗДЕСЬ. Рентген составного героя (сквозь
        /// одежду видно тело) лечится сведением слоёв в одну картинку ДО
        /// гашения — так делают Ren'Py и Naninovel. Снимок в текстуру на живой
        /// сцене провалился (канвас рисует только своя камера), и вместо него
        /// сведение делает <c>LvnActorComposite</c>: до восьми слоёв в одном
        /// фрагменте шейдера, включается из <c>WorldActor.BeginTransitionVisual</c>
        /// на время перехода.
        ///
        /// <para>Этот тест остаётся выключенным, потому что он дёргает
        /// <c>LvnFade</c> НАПРЯМУЮ, минуя сцену, которая и ставит композит —
        /// то есть проверяет путь, которым продукт не ходит. Пересобрать его
        /// надо от <c>WorldStage</c>; и помнить, что пиксельные проверки всё
        /// равно пропускаются на безголовой машине (CI гоняет -nographics),
        /// поэтому настоящая проверка композита — снимок живой сцены.</para></remarks>
        [UnityTest, Ignore("проверяет путь мимо сцены — пересобрать от WorldStage, см. remarks")]
        public IEnumerator MidTransitionHasNoXrayAndUsesNoMaterial()
        {
            TestPixels.RequireGraphics();
            BuildScene(clothSmaller: false);
            yield return null;

            bool done = false;
            LvnFade.Play(_actor.GetComponent<CanvasGroup>(), 1f, 0f, 1.0f, () => done = true);
            // Середина настоящего секундного перехода: smoothstep(0.5) = 0.5,
            // кадровая погрешность ±0.05 — окно теста заметно шире.
            yield return new WaitForSecondsRealtime(0.5f);

            // ГРАНИЦЫ С ДВУХ СТОРОН. Верхняя ловит рентген; нижняя — «герой
            // на середине перехода вообще исчез». Без неё проверка проходит
            // вхолостую: сведение в картинку, снявшее пустой кадр, даёт ноль
            // полупрозрачной площади и выглядит как идеальный результат.
            Assert.Greater(CentreLuminance(), 0.02f,
                "на середине перехода героя не видно совсем — гашение съело его целиком");
            float intermediate = CentreIntermediateFraction();
            Assert.Less(intermediate, 0.12f,
                $"середина перехода: {intermediate:P1} полупрозрачной площади — тело проступает сквозь одежду");
            Assert.IsNull(_actor.GetComponent<LvnSpriteFxDriver>(),
                "обычный вход/уход не должен подключать шейдерный драйвер");
            // ЦВЕТ СЛОЁВ НЕ ТРОГАЕТСЯ. Проявление яркостью (герой темнел из
            // чёрного силуэта) владелец отверг: на уходе это читалось как
            // чёрное затемнение, а переписывание цвета КАЖДОГО слоя каждый кадр
            // перестраивало канвас и давало микрозадержки. Многослойный герой
            // теперь сводится в одну картинку, и гаснет она — слои остаются
            // ровно такими, какими их нарисовали.
            float bodyLight = _body.GetComponent<Image>().color.grayscale;
            Assert.That(bodyLight, Is.GreaterThan(0.95f),
                "слой потемнел — вернулось гашение яркостью вместо сведения в картинку");

            yield return new WaitForSecondsRealtime(0.7f);
            Assert.IsTrue(done, "хвост перехода не вызван — уходящего некому спрятать");
            yield return null;
            Assert.AreEqual(Color.white, _body.GetComponent<Image>().color,
                "после fade исходный цвет слоя должен восстановиться");
            foreach (var image in _actor.GetComponentsInChildren<Image>(true))
                Assert.AreNotEqual("Hidden/LvnSpriteFx", image.materialForRendering.shader.name,
                    "после fade слой должен вернуться на UI/Default");
            TearDown();
        }

        // ── 2. растворение: дыры всех слоёв на одной сетке ──────────────────

        [UnityTest]
        public IEnumerator DissolveEatsAllLayersOnOneGrid()
        {
            TestPixels.RequireGraphics();
            var shader = Resources.Load<Shader>("LvnSpriteFx");
            if (shader == null || !shader.isSupported)
                Assert.Ignore("шейдер слоёв недоступен");
            BuildScene(clothSmaller: true);
            yield return null;

            LvnSpriteFxDriver.Apply(_actor,
                new Newtonsoft.Json.Linq.JObject { ["dissolve"] = 0.5f });
            yield return null;
            yield return null;

            // Центр: одежда (чёрная, полурастворённая) поверх тела (белого,
            // полурастворённого) на сером фоне. Дыры на одной сетке: где дыра —
            // там дыра у ОБОИХ, виден фон (0.5); где нет — чёрная одежда (0).
            // Среднее ≈0.25 плюс светящаяся кромка. Несовпадающие сетки
            // подмешивают белое тело в дыры одежды и тянут к 0.4+.
            float lum = CentreLuminance();
            Assert.Less(lum, 0.34f,
                $"растворение: {lum:F3} — дыры слоёв не совпадают, сквозь одежду видно тело");
            Assert.Greater(lum, 0.15f,
                $"растворение: {lum:F3} — эффект не применился вовсе");

            LvnSpriteFxDriver.Apply(_actor,
                new Newtonsoft.Json.Linq.JObject { ["off"] = 1 });
            yield return null;
            TearDown();
        }

    }
}
