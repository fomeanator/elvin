using System.Collections;
using Lvn;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// СОФТЛОК ОБЩЕГО ОКНА — не косметика, а замирание игры.
    ///
    /// <para><see cref="VnStage.InputBlocked"/> выводится из
    /// <see cref="VnPanelHost.IsOpen"/>: пока окно считается открытым, история
    /// не принимает касаний. Значит любой путь, на котором окно остаётся
    /// «открытым», но невидимым, — это конец игры для игрока: тапать не по
    /// чему, а история стоит.</para>
    ///
    /// <para>Такой путь был: скрытие проверяет своё поколение и при перебивке
    /// выходит РАНЬШЕ, чем снимет <c>IsOpen</c>. Показ, который его перебил,
    /// раму не восстанавливал — она оставалась полупрозрачной и сдвинутой.
    /// Воспроизводится обычным для гардероба «закрыл и тут же открыл».</para>
    /// </summary>
    public class PanelHostSoftlockTests
    {
        private GameObject _go;
        private VnPanelHost _host;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("panel-host", typeof(UIDocument));
            var doc = _go.GetComponent<UIDocument>();
            doc.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _host = new VnPanelHost(new VnTheme());
            doc.rootVisualElement.Add(_host);
        }

        [TearDown]
        public void TearDown() => Object.Destroy(_go);

        /// <summary>Показ посреди ухода обязан вернуть окно в рабочий вид.</summary>
        [UnityTest]
        public IEnumerator ShowDuringHideLeavesTheWindowUsable()
        {
            var content = new Label("гардероб");
            _host.TransitionSeconds = 0.25f;

            LvnAsync.Fire(_host.ShowAsync(content), "показ");
            yield return new WaitForSecondsRealtime(0.35f);
            Assert.IsTrue(_host.IsOpen, "окно должно было открыться");

            LvnAsync.Fire(_host.HideAsync(), "уход");
            yield return new WaitForSecondsRealtime(0.08f);   // середина ухода
            LvnAsync.Fire(_host.ShowAsync(content), "показ поверх ухода");
            yield return new WaitForSecondsRealtime(0.4f);

            var frame = _host.Q("vn-panel-host-frame");
            Assert.IsTrue(_host.IsOpen, "окно перестало считаться открытым");
            Assert.AreEqual(DisplayStyle.Flex, _host.resolvedStyle.display,
                "носитель окна скрыт, а окно считается открытым — ввод заблокирован навсегда");
            Assert.AreEqual(1f, frame.resolvedStyle.opacity, 0.01f,
                "рама осталась полупрозрачной от перебитого ухода");
        }

        /// <summary>А обычный уход обязан ДОВЕСТИ себя до конца: иначе окно
        /// закрылось на экране, но продолжает держать ввод.</summary>
        [UnityTest]
        public IEnumerator PlainHideReleasesTheInput()
        {
            var content = new Label("гардероб");
            _host.TransitionSeconds = 0.15f;

            LvnAsync.Fire(_host.ShowAsync(content), "показ");
            yield return new WaitForSecondsRealtime(0.25f);

            LvnAsync.Fire(_host.HideAsync(), "уход");
            yield return new WaitForSecondsRealtime(0.3f);

            Assert.IsFalse(_host.IsOpen, "после ухода окно обязано отпустить ввод");
            Assert.AreEqual(DisplayStyle.None, _host.resolvedStyle.display);
        }

        /// <summary>Два ухода подряд — тоже обычное дело (кнопка «закрыть» и
        /// системная «назад» в одном жесте). Второй доводит дело до конца.</summary>
        [UnityTest]
        public IEnumerator DoubleHideStillCloses()
        {
            var content = new Label("гардероб");
            _host.TransitionSeconds = 0.15f;

            LvnAsync.Fire(_host.ShowAsync(content), "показ");
            yield return new WaitForSecondsRealtime(0.25f);

            LvnAsync.Fire(_host.HideAsync(), "уход");
            yield return new WaitForSecondsRealtime(0.05f);
            LvnAsync.Fire(_host.HideAsync(), "второй уход");
            yield return new WaitForSecondsRealtime(0.35f);

            Assert.IsFalse(_host.IsOpen, "двойное закрытие оставило окно открытым");
        }
    }
}
