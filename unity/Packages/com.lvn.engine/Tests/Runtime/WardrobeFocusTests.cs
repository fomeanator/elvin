using System.Collections;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// В ГАРДЕРОБЕ СТОИТ РОВНО ОДИН ГЕРОЙ.
    ///
    /// <para>Примерка — это зеркало: на сцене должен остаться тот, кого одевают,
    /// и никто больше. На живом снимке от партнёра их двое, наложенных друг на
    /// друга: под выбранным просвечивает предыдущий. Разобрать такое на глаз
    /// нельзя — переключение асинхронное, и «кто кого не дождался» видно только
    /// по состоянию сцены.</para>
    /// </summary>
    public class WardrobeFocusTests
    {
        private GameObject _go;
        private VnStage _stage;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("wardrobe-stage", typeof(UIDocument));
            var doc = _go.GetComponent<UIDocument>();
            doc.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _stage = _go.AddComponent<VnStage>();
        }

        [TearDown]
        public void TearDown() => Object.Destroy(_go);

        private static string TwoOnStage() => @"{""script"":[
            {""op"":""actor"",""id"":""katya"",""show"":true,""position"":""left""},
            {""op"":""actor"",""id"":""matvey"",""show"":true,""position"":""right""},
            {""op"":""say"",""text"":""оба на сцене""}
        ]}";

        [UnityTest]
        public IEnumerator OpeningTheWardrobeLeavesExactlyOneActor()
        {
            _stage.Play(TwoOnStage());
            yield return new WaitForSecondsRealtime(0.5f);
            CollectionAssert.AreEquivalent(new[] { "katya", "matvey" }, _stage.ActorsOnStage(),
                "sanity: сцена начинается с двух героев");

            var focus = _stage.FocusWardrobeActorAsync("katya");
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!focus.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            yield return new WaitForSecondsRealtime(0.6f);

            CollectionAssert.AreEqual(new[] { "katya" }, _stage.ActorsOnStage(),
                "в гардеробе остался лишний герой — он и просвечивает под примеряемым");
        }

        [UnityTest]
        public IEnumerator SwitchingTheMannequinDoesNotStackThem()
        {
            _stage.Play(TwoOnStage());
            yield return new WaitForSecondsRealtime(0.5f);

            var first = _stage.FocusWardrobeActorAsync("katya");
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!first.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;

            // Игрок жмёт вторую «таблетку» — того, кого одевали, надо убрать.
            var second = _stage.FocusWardrobeActorAsync("matvey");
            deadline = Time.realtimeSinceStartup + 5f;
            while (!second.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            yield return new WaitForSecondsRealtime(0.6f);

            CollectionAssert.AreEqual(new[] { "matvey" }, _stage.ActorsOnStage(),
                "после смены героя на сцене осталось двое — ровно тот баг со снимка");
        }

        /// <summary>Быстрые нажатия по «таблеткам» — самый частый способ увидеть
        /// наложение: три переключения подряд, не дожидаясь ухода.</summary>
        [UnityTest]
        public IEnumerator RapidSwitchesStillEndWithOne()
        {
            _stage.Play(TwoOnStage());
            yield return new WaitForSecondsRealtime(0.5f);

            var a = _stage.FocusWardrobeActorAsync("katya");
            yield return new WaitForSecondsRealtime(0.05f);
            var b = _stage.FocusWardrobeActorAsync("matvey");
            yield return new WaitForSecondsRealtime(0.05f);
            var c = _stage.FocusWardrobeActorAsync("katya");

            float deadline = Time.realtimeSinceStartup + 6f;
            while ((!a.IsCompleted || !b.IsCompleted || !c.IsCompleted)
                   && Time.realtimeSinceStartup < deadline) yield return null;
            yield return new WaitForSecondsRealtime(0.8f);

            CollectionAssert.AreEqual(new[] { "katya" }, _stage.ActorsOnStage(),
                "быстрое перещёлкивание оставило на сцене нескольких героев разом");
        }

        /// <summary>
        /// СКРЫТИЕ НИКОГО НЕ СТАВИТ — и подписываться под позой не вправе.
        ///
        /// <para>Гардероб убирает манекен своей командой. Место фигуры при этом
        /// остаётся прежним, авторским, — но записывался в память отправитель
        /// СКРЫТИЯ. Дальше правило «авторская команда не наследует чужую позу»
        /// видело гардероб, объявляло следующую авторскую команду свежей и
        /// уводило фигуру в слот по умолчанию, обнуляя её явный z. Игрок видит
        /// это как «закрыл гардероб — героиня переехала».</para>
        ///
        /// <para>На экране правило невидимо, поэтому проверяется по памяти
        /// сцены напрямую.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator HidingDoesNotClaimAuthorshipOfThePose()
        {
            _stage.Play(TwoOnStage());
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.IsTrue(_stage.Memory.TryPoseSender("katya", out var author),
                "sanity: позу кто-то поставил");
            Assert.AreEqual(LvnSender.Story, author, "sanity: ставил её сценарий");

            _stage.HideActor("katya", LvnSender.Wardrobe);
            yield return new WaitForSecondsRealtime(0.4f);

            Assert.IsTrue(_stage.Memory.TryPoseSender("katya", out var afterHide));
            Assert.AreEqual(LvnSender.Story, afterHide,
                "скрытие расписалось под чужой позой — следующая авторская команда уведёт фигуру в слот по умолчанию");
        }
    }
}
