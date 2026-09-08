using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Lvn.UI;
using Lvn.UI.Screens;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// БУРГЕР ХОДИТ ЗА РЕЖИМОМ на живом экране: в главе есть, в витрине нет.
    ///
    /// <para>Проверка живёт в PlayMode не из прихоти: правду о режиме бар
    /// слышит сигналом Режиссёра, а подписан он на него только пока висит в
    /// панели (<c>LvnLeash.WhileOnScreen</c>). Вне панели сигнал до бара не
    /// доходит — и EditMode-проверка мерила бы не то. Там остался вид ПРИ
    /// РОЖДЕНИИ (TopBarBurgerTests), здесь — смена режима.</para>
    /// </summary>
    public class TopBarBurgerLiveTests
    {
        private GameObject _go;

        [SetUp]
        public void Up()
        {
            _go = new GameObject("topbar-panel", typeof(UIDocument));
            var doc = _go.GetComponent<UIDocument>();
            doc.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
        }

        [TearDown]
        public void Down()
        {
            LvnScreenDirector.Current.AnnounceChapter(false);
            if (_go != null) Object.DestroyImmediate(_go);
        }

        [UnityTest]
        public IEnumerator Burger_FollowsTheMode_OnALivePanel()
        {
            var root = _go.GetComponent<UIDocument>().rootVisualElement;
            LvnScreenDirector.Current.AnnounceChapter(false);

            var bar = new LvnTopBar();
            root.Add(bar);
            yield return null;   // панель прикрепляет элемент — тогда и подписка

            var burger = bar.Q<VisualElement>("burger");
            Assert.IsNotNull(burger, "бургер не найден в баре");
            Assert.AreEqual(DisplayStyle.None, burger.style.display.value,
                "в витрине бургер остался на экране");

            LvnScreenDirector.Current.AnnounceChapter(true);
            yield return null;
            Assert.AreEqual(DisplayStyle.Flex, burger.style.display.value,
                "в главе бургер не вернулся — уйти из главы стало нечем");

            LvnScreenDirector.Current.AnnounceChapter(false);
            yield return null;
            Assert.AreEqual(DisplayStyle.None, burger.style.display.value,
                "вышли в витрину, а бургер остался");
        }
    }
}
