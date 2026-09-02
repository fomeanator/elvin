using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Lvn.UI;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// ПЕРЕСБОРКА НЕ ЗАБИРАЕТ МЕСТО ИГРОКА.
    ///
    /// <para>Проверять это можно только на живой панели: <c>scrollOffset</c>
    /// зажимается по РАССЧИТАННОЙ высоте содержимого, а без раскладки она ноль
    /// — в EditMode список «стоит в начале» всегда, и тест был бы зелёным при
    /// любой реализации.</para>
    /// </summary>
    public class ScrollKeepingTests
    {
        private GameObject _go;
        private ScrollView _view;

        private static void Fill(ScrollView v, int rows)
        {
            for (int i = 0; i < rows; i++)
            {
                var row = new Label("строка " + i);
                row.style.height = 40f;
                v.Add(row);
            }
        }

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("scroll-keeping", typeof(UIDocument));
            var doc = _go.GetComponent<UIDocument>();
            doc.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _view = LvnScroll.Vertical();
            _view.style.width = 400f;
            _view.style.height = 200f;
            doc.rootVisualElement.Add(_view);
            Fill(_view, 40);
        }

        [TearDown]
        public void TearDown() => Object.Destroy(_go);

        /// <summary>Ждать РАСКЛАДКИ, а не «двух кадров»: панель UIDocument
        /// строится не на первом кадре, и число кадров — это про среду, а не
        /// про проверяемое. Пока высота содержимого нулевая, прокрутка
        /// зажимается в ноль, и тест зелен при любой реализации.</summary>
        private IEnumerator LaidOut()
        {
            for (int i = 0; i < 240; i++)
            {
                if (_view.contentContainer.layout.height > 300f) yield break;
                yield return null;
            }
            Assert.Fail("список так и не получил раскладку — проверять нечего");
        }

        [UnityTest]
        public IEnumerator ПересборкаВозвращаетМестоПрокрутки()
        {
            yield return LaidOut();

            _view.scrollOffset = new Vector2(0f, 400f);
            yield return null;
            float was = _view.scrollOffset.y;
            Assert.Greater(was, 1f, "список не прокрутился — проверять нечего");

            LvnScroll.Keeping(_view, () => { _view.Clear(); Fill(_view, 40); });
            yield return LaidOut();
            yield return new WaitForSecondsRealtime(0.15f);   // и страховка успела

            Assert.AreEqual(was, _view.scrollOffset.y, 1f,
                "пересборка отбросила игрока в начало списка");
        }

        [UnityTest]
        public IEnumerator БезПересборкиМестоНеТрогается()
        {
            yield return LaidOut();
            _view.scrollOffset = new Vector2(0f, 300f);
            yield return null;
            float was = _view.scrollOffset.y;

            LvnScroll.Keeping(_view, () => { });
            yield return null;

            Assert.AreEqual(was, _view.scrollOffset.y, 1f);
        }

        [UnityTest]
        public IEnumerator СписокСталКорочеМестаНеПридумывает()
        {
            yield return LaidOut();
            _view.scrollOffset = new Vector2(0f, 1200f);
            yield return null;

            // Пересборка на ТРИ строки: возвращать некуда, и «вернуть 1200»
            // означало бы пустоту под последней строкой.
            LvnScroll.Keeping(_view, () => { _view.Clear(); Fill(_view, 3); });
            yield return new WaitForSecondsRealtime(0.15f);

            Assert.LessOrEqual(_view.scrollOffset.y, 1f,
                "короткий список прокручен за своё содержимое");
        }

        [Test]
        public void ПустойСписокНеРоняет()
        {
            int ran = 0;
            Assert.DoesNotThrow(() => LvnScroll.Keeping(null, () => ran++));
            Assert.AreEqual(1, ran, "пересборку обязаны выполнить и без списка");
            Assert.DoesNotThrow(() => LvnScroll.Keeping(_view, null));
        }
    }
}
