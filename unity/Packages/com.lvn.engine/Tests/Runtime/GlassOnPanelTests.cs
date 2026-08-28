using System.Collections;
using System.IO;
using Lvn.UI;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// СТЕКЛО ЦЕЛИКОМ: от кадра камеры до фона элемента.
    ///
    /// <para>Что подложка рисуется правильно, проверяет соседний тест. Здесь
    /// проверяется вторая половина пути — попадает ли она в фон окна и НА ТО ЛИ
    /// МЕСТО. Совмещение — вся хитрость приёма: подложка это весь экран, а окно
    /// занимает его часть, и промах даёт «почти правильный» эффект — размытие
    /// не того куска мира. Такое не замечают неделями.</para>
    ///
    /// <para>Проверка прямая: сцена делится по вертикали на красную и синюю
    /// половины, окно ставится в НИЖНЮЮ. Если совмещение верное, в стекле окна
    /// синего заметно больше красного. При промахе на пол-экрана — наоборот.</para>
    /// </summary>
    public class GlassOnPanelTests
    {
        [UnityTest]
        public IEnumerator GlassShowsTheWorldBehindTheBox()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("нет графики — стеклу неоткуда взяться");

            // ── сцена: красный верх, синий низ ──────────────────────────────
            var camGo = new GameObject("glass-panel-cam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 1f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;

            var top = Board(new Color(1f, 0f, 0f), y: 0.5f);
            var bottom = Board(new Color(0f, 0f, 1f), y: -0.5f);

            var frame = new RenderTexture(180, 320, 16);
            cam.targetTexture = frame;
            var glass = LvnGlass.Ensure(cam);
            glass.Retain();
            cam.Render();
            yield return null;
            Assert.IsNotNull(glass.Backdrop, "подложка не появилась");

            // ── панель: окно в нижней трети со стеклом ──────────────────────
            var panelRt = new RenderTexture(180, 320, 24);
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.targetTexture = panelRt;
            settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            settings.clearColor = true;
            settings.colorClearValue = new Color(0f, 0f, 0f, 0f);

            var docGo = new GameObject("glass-panel-doc");
            var doc = docGo.AddComponent<UIDocument>();
            doc.panelSettings = settings;

            var box = new VisualElement();
            box.style.position = Position.Absolute;
            box.style.left = Length.Percent(15);
            box.style.width = Length.Percent(70);
            box.style.top = Length.Percent(70);       // нижняя треть — синяя половина сцены
            box.style.height = Length.Percent(20);
            doc.rootVisualElement.Add(box);

            // КОНТРОЛЬНЫЙ ЗАМЕР. Панель UI Toolkit рисуется в конце обычного
            // кадра, а в headless-прогоне кадров может не быть вовсе — тогда
            // текстура панели останется пустой, и «стекла не видно» скажет не о
            // стекле, а о среде. Сначала проверяем, что панель тут вообще
            // рисует: сплошной цвет обязан появиться.
            box.style.backgroundColor = Color.green;
            for (int i = 0; i < 6; i++) yield return null;
            var probe = TestPixels.Read(panelRt);
            bool panelDraws = false;
            for (int y = 0; y < panelRt.height && !panelDraws; y += 4)
                for (int x = 0; x < panelRt.width; x += 4)
                    if (probe.GetPixel(x, y).g > 0.5f) { panelDraws = true; break; }
            Object.Destroy(probe);
            if (!panelDraws)
            {
                glass.Forget();
                cam.targetTexture = null;
                Object.Destroy(docGo); Object.Destroy(camGo);
                Object.Destroy(top); Object.Destroy(bottom);
                Object.Destroy(frame); Object.Destroy(panelRt);
                Object.Destroy(settings);
                Assert.Ignore("панель UITK в этой среде не рисуется — совмещение проверить нечем");
            }

            box.style.backgroundColor = Color.clear;
            UiGlass.Apply(box, 1f, new Color(0f, 0f, 0f, 0f)); // без тонировки: смотрим чистое стекло
            for (int i = 0; i < 6; i++) yield return null;

            var shot = TestPixels.Read(panelRt);
            Dump(shot, "glass-on-panel.png");

            // Считаем только внутри окна.
            int x0 = Mathf.RoundToInt(panelRt.width * 0.20f), x1 = Mathf.RoundToInt(panelRt.width * 0.80f);
            int y0 = Mathf.RoundToInt(panelRt.height * 0.10f), y1 = Mathf.RoundToInt(panelRt.height * 0.25f);
            float red = 0f, blue = 0f;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var c = shot.GetPixel(x, y);
                    red += c.r; blue += c.b;
                }

            Assert.Greater(red + blue, 1f, "в окне пусто — стекло не дошло до фона элемента");
            Assert.Greater(blue, red * 1.5f,
                $"стекло показывает не тот кусок мира (синего {blue:F0}, красного {red:F0}): " +
                "окно стоит над синей половиной сцены");

            Object.Destroy(shot);
            glass.Forget();
            cam.targetTexture = null;
            Object.Destroy(docGo); Object.Destroy(camGo);
            Object.Destroy(top); Object.Destroy(bottom);
            Object.Destroy(frame); Object.Destroy(panelRt);
            Object.Destroy(settings);
        }

        private static GameObject Board(Color c, float y)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.transform.position = new Vector3(0f, y, 5f);
            q.transform.localScale = new Vector3(10f, 1f, 1f);
            q.GetComponent<Renderer>().material = new Material(Shader.Find("Unlit/Color")) { color = c };
            return q;
        }


        private static void Dump(Texture2D tex, string name)
        {
            var dir = System.Environment.GetEnvironmentVariable("LVN_TEST_SHOTS");
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, name), tex.EncodeToPNG());
            }
            catch { }   // снимок — удобство отладки, а не результат теста
        }
    }
}
