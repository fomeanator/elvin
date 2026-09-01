using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// СЦЕНА ДЛЯ ТЕСТА — камера с текстурой, канвас под неё и панель со сценой.
    ///
    /// <para>Обе сборки повторялись в двух файлах каждая, и повторялись ТОЧНО:
    /// «камера рисует в текстуру, канвас смотрит в камеру» — четыре строки, где
    /// важен порядок (канвасу нужна уже настроенная камера) и где легко забыть
    /// <c>planeDistance</c>, отчего слои начинают отсекаться и тест падает не
    /// тем, что проверял.</para>
    ///
    /// <para>Отдельно про снос: камеру надо ОТВЯЗАТЬ от текстуры до
    /// уничтожения, иначе Unity держит текстуру живой и следующий тест рисует
    /// в чужую. Это та же парная работа, что и подмена активной текстуры при
    /// чтении, — и её тоже нельзя доверять внимательности пишущего.</para>
    /// </summary>
    public static class TestStage
    {
        /// <summary>Камера, рисующая в свежую текстуру заданного размера.</summary>
        public static Camera Camera(out RenderTexture rt, int size = 256, Color? clear = null, int depth = 0)
        {
            rt = new RenderTexture(size, size, depth);
            var cam = new GameObject("t-cam", typeof(Camera)).GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = clear ?? Color.black;
            cam.targetTexture = rt;
            return cam;
        }

        /// <summary>Канвас, который рисует в эту камеру.</summary>
        public static GameObject Canvas(Camera cam)
        {
            var go = new GameObject("t-canvas", typeof(UnityEngine.Canvas), typeof(UnityEngine.UI.CanvasScaler));
            var canvas = go.GetComponent<UnityEngine.Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 1f;   // забыть — и слои начнут отсекаться
            return go;
        }

        /// <summary>СЛОЙ ФИГУРЫ во весь кадр родителя — тело, одежда, всё,
        /// что складывается в куклу.
        ///
        /// <para>Три компонента и растяжка на все стороны — обряд, который
        /// стоял дословно в двух пиксельных тестах перехода. Дословно и
        /// значит: одинаковый набор компонентов, одинаковые якоря, одинаковое
        /// имя объекта. Разойдись якоря — и тест сравнивал бы цвета кадра, в
        /// котором слой стоит не там, а виноватым выглядел бы шейдер.</para>
        /// </summary>
        public static GameObject Layer(Transform parent, Color c)
        {
            var go = new GameObject("layer", typeof(RectTransform), typeof(CanvasRenderer),
                                    typeof(UnityEngine.UI.Image));
            go.transform.SetParent(parent, false);
            Stretch((RectTransform)go.transform);
            go.GetComponent<UnityEngine.UI.Image>().color = c;
            return go;
        }

        /// <summary>Растянуть на весь родительский прямоугольник: якоря по
        /// углам и нулевые отступы. Половина обряда — забыть её значит
        /// получить слой размером в точку.</summary>
        public static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        /// <summary>Отвязать и снести камеру вместе с её текстурой.</summary>
        public static void Drop(Camera cam)
        {
            if (cam == null) return;
            cam.targetTexture = null;    // ДО уничтожения, иначе текстура живёт дальше
            Object.Destroy(cam.gameObject);
        }

        /// <summary>Панель с VnStage на ней: то, что нужно любому тесту
        /// поведения сцены. Панель возвращается отдельно — её сносит
        /// вызывающий, и делать это надо через DestroyImmediate.</summary>
        public static VnStage Panel(string name, out GameObject go, out PanelSettings panel)
        {
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            go = new GameObject(name);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = panel;
            return go.AddComponent<VnStage>();
        }
    }
}
