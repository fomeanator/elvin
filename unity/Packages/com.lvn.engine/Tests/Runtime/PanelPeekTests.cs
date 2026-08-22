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
    /// «ВО ВЕСЬ РОСТ» — режим, у которого есть ровно один смертельный исход:
    /// интерфейс убрали и не вернули. Панель занимает низ экрана, поэтому
    /// примеряющий видит героиню по пояс; кнопка в шапке убирает панель и весь
    /// интерфейс, чтобы наряд читался целиком.
    ///
    /// <para>Опасность в том, что при открытой панели ввод ЗАБЛОКИРОВАН — это
    /// её нормальное состояние. Значит возврат обязан проверяться РАНЬШЕ
    /// блокировки, иначе на пустом экране не останется ничего нажимаемого:
    /// ни панели, ни диалога, ни меню. Здесь это и закреплено.</para>
    /// </summary>
    public class PanelPeekTests
    {
        private GameObject _go;
        private VnStage _stage;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("peek-stage", typeof(UIDocument));
            var doc = _go.GetComponent<UIDocument>();
            doc.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _stage = _go.AddComponent<VnStage>();
        }

        [TearDown]
        public void TearDown() => Object.Destroy(_go);

        [UnityTest]
        public IEnumerator PeekHidesTheWindowAndATapBringsItBack()
        {
            _stage.Play("{\"script\":[{\"op\":\"say\",\"text\":\"примерка\"}]}");
            yield return null;

            var sheet = new Label("гардероб");
            LvnAsync.Fire(_stage.ShowPanelAsync(sheet), "панель");
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.IsTrue(_stage.PanelOpen, "sanity: панель открыта");

            _stage.SetPanelPeek(true);
            Assert.IsTrue(_stage.PanelPeeking);
            Assert.IsTrue(_stage.PanelOpen, "примерка НЕ прерывается — панель всё ещё открыта");
            Assert.IsTrue(_stage.InputBlocked, "история не должна продвигаться, пока смотрят наряд");

            _stage.SetPanelPeek(false);
            Assert.IsFalse(_stage.PanelPeeking, "интерфейс обязан возвращаться");
            Assert.IsTrue(_stage.PanelOpen, "и панель остаётся той же самой");
        }

        /// <summary>Возврат живёт ВЫШЕ блокировки ввода — иначе спрятанный
        /// интерфейс не достать. Проверяется на живом касании, а не на вызове
        /// метода: сломать это можно ровно одной переставленной строкой.</summary>
        [UnityTest]
        public IEnumerator ATapReturnsTheUiEvenThoughInputIsBlocked()
        {
            _stage.Play("{\"script\":[{\"op\":\"say\",\"text\":\"примерка\"}]}");
            yield return null;
            LvnAsync.Fire(_stage.ShowPanelAsync(new Label("гардероб")), "панель");
            yield return new WaitForSecondsRealtime(0.4f);

            _stage.SetPanelPeek(true);
            Assert.IsTrue(_stage.InputBlocked, "sanity: при открытой панели ввод заблокирован");

            var root = _go.GetComponent<UIDocument>().rootVisualElement;
            using (var down = PointerDownEvent.GetPooled())
            {
                down.target = root;
                root.SendEvent(down);
            }
            yield return null;

            Assert.IsFalse(_stage.PanelPeeking,
                "касание не вернуло интерфейс — игрок остался на пустом экране без единой кнопки");
        }
    }
}
