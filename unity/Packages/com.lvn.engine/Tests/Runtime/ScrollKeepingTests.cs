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
        private RenderTexture _rt;

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
            // ПАНЕЛИ НУЖНА ТЕКСТУРА. Без цели рисования панель UI Toolkit в
            // безголовом прогоне не тикает вовсе: раскладка не считается,
            // высота содержимого остаётся нулевой, и прокрутка зажимается в
            // ноль — проверка стала бы зелёной при любой реализации. Рецепт
            // тот же, что у проверок стекла на панели.
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            _rt = new RenderTexture(400, 300, 24);
            settings.targetTexture = _rt;
            settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            doc.panelSettings = settings;
            _view = LvnScroll.Vertical();
            _view.style.width = 400f;
            _view.style.height = 200f;
            doc.rootVisualElement.Add(_view);
            Fill(_view, 40);
        }

        [TearDown]
        public void TearDown()
        {
            Object.Destroy(_go);
            if (_rt != null) { _rt.Release(); Object.Destroy(_rt); _rt = null; }
        }

        /// <summary>
        /// Ждать РАСКЛАДКИ, а не «столько-то кадров»: число кадров — это про
        /// среду, а не про проверяемое.
        ///
        /// <para>А если раскладки нет вовсе — ПРОПУСТИТЬ, а не упасть и не
        /// пройти. Замерено 02.09: панель UIDocument в безголовом прогоне не
        /// считает раскладку даже с целью рисования (<c>targetTexture</c>) и
        /// даже с графикой — тем же упирается соседняя проверка стекла.
        /// Без раскладки высота содержимого нулевая, <c>scrollOffset</c>
        /// зажимается ползунком в ноль, и проверка стала бы зелёной при любой
        /// реализации, включая пустую. Зелёная проверка, которая ничего не
        /// проверяет, хуже пропущенной: пропуск виден в отчёте.</para>
        /// </summary>
        private IEnumerator LaidOut()
        {
            for (int i = 0; i < 240; i++)
            {
                if (_view.contentContainer.layout.height > 300f) yield break;
                yield return null;
            }
            Assert.Ignore("панель UITK в этой среде не считает раскладку — прокрутку проверить нечем");
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
