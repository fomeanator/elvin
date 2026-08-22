using System.Collections;
using System.Collections.Generic;
using Lvn.UI;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// ЦЕНА ОДНОЙ РЕПЛИКИ. Показ актёра — не редкое событие: в живой главе
    /// продукта 775 команд `actor` на 730 реплик, то есть почти каждая строка
    /// диалога заново применяет актёра. Всё, что переход делает лишнего, игрок
    /// чувствует как микрозадержки — их и ловят эти проверки.
    /// </summary>
    public class ActorChurnTests
    {
        private GameObject _host;

        [TearDown]
        public void TearDown() { if (_host != null) Object.Destroy(_host); }

        private static Sprite NewSprite()
        {
            var tex = new Texture2D(4, 4);
            return Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
        }

        /// <summary>Тот же облик — тот же набор объектов. Слои пересоздавались
        /// на КАЖДОМ применении: Destroy+Create всех Image, перестройка канваса
        /// и мусор на каждой строке диалога.</summary>
        [UnityTest]
        public IEnumerator SameLookRebuildsNothing()
        {
            _host = new GameObject("host", typeof(RectTransform));
            var stage = new WorldStage(_host.transform, sortingOrder: 0);
            var art = new List<Sprite> { NewSprite(), NewSprite() };

            var actor = stage.ApplyActor("mara", art, Placement.Standing(0.5f));
            yield return null;
            var first = Ids(actor.Rig);
            Assert.AreEqual(2, first.Count, "два слоя построены");

            stage.ApplyActor("mara", art, Placement.Standing(0.5f));   // та же реплика, тот же вид
            yield return null;
            CollectionAssert.AreEqual(first, Ids(actor.Rig),
                "слои пересобраны при неизменившемся облике — это и есть рывок на каждой реплике");

            stage.ApplyActor("mara", new List<Sprite> { NewSprite(), NewSprite() },
                Placement.Standing(0.5f));                              // сменилась эмоция/наряд
            yield return null;
            CollectionAssert.AreNotEqual(first, Ids(actor.Rig),
                "новый облик обязан пересобраться, иначе на экране останется старый");
        }

        /// <summary>У каждого трансформа один хозяин: постановка держит слот,
        /// анимация — rig, переход — свой узел между ними. Пока переход писал
        /// в слот, они с анимацией перетирали позицию друг друга каждый кадр.</summary>
        [UnityTest]
        public IEnumerator TransitionOwnsItsOwnTransform()
        {
            _host = new GameObject("host", typeof(RectTransform));
            var stage = new WorldStage(_host.transform, sortingOrder: 0);
            var actor = stage.ApplyActor("mara", new List<Sprite> { NewSprite() }, Placement.Standing(0.5f));
            yield return null;

            Assert.AreSame(actor.Slot, actor.Transition.parent, "узел перехода висит под слотом");
            Assert.AreSame(actor.Transition, actor.Rig.parent, "анимация живёт под узлом перехода");

            var slotBefore = actor.Slot.anchoredPosition;
            LvnFade.Play(actor.GetComponent<CanvasGroup>(), 1f, 0f, 0.4f, actor.Transition,
                new Vector2(60f, 0f));
            yield return new WaitForSecondsRealtime(0.15f);

            Assert.AreNotEqual(0f, actor.Transition.anchoredPosition.x,
                "снос обязан двигать узел перехода");
            Assert.AreEqual(slotBefore, actor.Slot.anchoredPosition,
                "переход трогает чужое поле — позицию слота держит постановка");
        }

        private static List<int> Ids(Transform rig)
        {
            var ids = new List<int>();
            foreach (var img in rig.GetComponentsInChildren<Image>(true)) ids.Add(img.GetInstanceID());
            return ids;
        }
    }
}
