using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Lvn.UI;
using Lvn.UI.World;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// СТЕКЛО ДЕРЖАТ, ПОКА ОНО НА ЭКРАНЕ — и снова, когда вернулось.
    ///
    /// <para>Подложка стекла считает пользователей: на нуле она гасит свою
    /// текстуру и отключается. «Взять» стояло ОДИН раз при создании слоя, а
    /// «отдать» висело на отсоединении от панели — то есть срабатывало столько
    /// раз, сколько элемент уходил с экрана.</para>
    ///
    /// <para>Пара разъезжалась на первом же возврате, и оба конца при этом
    /// выглядели правильно: вызовы на месте, каждый по отдельности верен.
    /// Видно только счётом событий, а не чтением.</para>
    /// </summary>
    public class GlassHoldTests
    {
        private GameObject _camGo, _docGo;
        private RenderTexture _rt;

        private static int Users(LvnGlass g)
        {
            var f = typeof(LvnGlass).GetField("_users", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(f, "поле счётчика переименовали — тест не о том");
            return (int)f.GetValue(g);
        }

        [SetUp]
        public void SetUp()
        {
            _camGo = new GameObject("t-cam", typeof(Camera));
            _docGo = new GameObject("t-doc", typeof(UIDocument));
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            _rt = new RenderTexture(64, 64, 16);
            settings.targetTexture = _rt;
            _docGo.GetComponent<UIDocument>().panelSettings = settings;
        }

        [TearDown]
        public void TearDown()
        {
            Object.Destroy(_docGo);
            Object.Destroy(_camGo);
            if (_rt != null) { _rt.Release(); Object.Destroy(_rt); _rt = null; }
        }

        [UnityTest]
        public IEnumerator ВернувшийсяНаЭкранСноваДержитПодложку()
        {
            var glass = LvnGlass.Ensure(_camGo.GetComponent<Camera>());
            Assert.IsNotNull(glass, "стекло не завелось — проверять нечем");
            var root = _docGo.GetComponent<UIDocument>().rootVisualElement;
            yield return null;

            var host = new VisualElement();
            host.style.width = 100f;
            host.style.height = 50f;
            root.Add(host);
            UiGlass.Apply(host, 1f, new Color(0f, 0f, 0f, 0f));
            yield return null;
            Assert.AreEqual(1, Users(glass), "стекло не взято при показе");

            host.RemoveFromHierarchy();
            yield return null;
            Assert.AreEqual(0, Users(glass), "стекло не отдано при уходе с экрана");

            root.Add(host);
            yield return null;
            Assert.AreEqual(1, Users(glass),
                "вернувшийся слой не просит подложку — она погашена под живой панелью");

            host.RemoveFromHierarchy();
            yield return null;
            Assert.AreEqual(0, Users(glass), "второй уход не отдал стекло");
        }

        [UnityTest]
        public IEnumerator ДваУходаПодрядНеУводятСчётВМинус()
        {
            var glass = LvnGlass.Ensure(_camGo.GetComponent<Camera>());
            var root = _docGo.GetComponent<UIDocument>().rootVisualElement;
            yield return null;

            var host = new VisualElement();
            root.Add(host);
            UiGlass.Apply(host, 1f, new Color(0f, 0f, 0f, 0f));
            yield return null;

            host.RemoveFromHierarchy();
            yield return null;
            host.RemoveFromHierarchy();   // повтор: событие уже было
            yield return null;

            Assert.AreEqual(0, Users(glass), "счёт ушёл в минус — следующий показ не включит стекло");
        }
    }
}
