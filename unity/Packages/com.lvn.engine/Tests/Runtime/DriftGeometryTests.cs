using System.Collections;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// ГЕОМЕТРИЯ БОКОВОГО СНОСА. Вид <c>drift</c> обещает две вещи: герой
    /// приходит со своей стороны и К КОНЦУ ПЕРЕХОДА СТОИТ РОВНО ТАМ, КУДА ЕГО
    /// поставила постановка. Оба обещания ломались тихо — на глаз «вроде
    /// съехал куда-то», поэтому они закреплены числами.
    /// </summary>
    public class DriftGeometryTests
    {
        private GameObject _canvas, _actor;
        private RectTransform _slot;
        private CanvasGroup _group;
        private static readonly Vector2 Home = new Vector2(120f, -40f);
        private static readonly Vector2 Drift = new Vector2(48f, 0f);

        [SetUp]
        public void SetUp()
        {
            _canvas = new GameObject("drift-canvas", typeof(Canvas));
            _actor = new GameObject("drift-actor", typeof(RectTransform), typeof(CanvasGroup));
            _actor.transform.SetParent(_canvas.transform, false);
            _slot = (RectTransform)_actor.transform;
            _slot.anchoredPosition = Home;
            _group = _actor.GetComponent<CanvasGroup>();
            var art = new GameObject("art", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            art.transform.SetParent(_actor.transform, false);
        }

        [TearDown]
        public void TearDown()
        {
            Object.Destroy(_actor);
            Object.Destroy(_canvas);
        }

        /// <summary>Приглушённый герой (placement-opacity &lt; 1) обязан доехать
        /// домой. Снос вели АЛЬФОЙ, и вход, кончавшийся на 0.6, оставлял его
        /// сдвинутым на 40% сноса — до последнего кадра, где он прыгал на место.</summary>
        [UnityTest]
        public IEnumerator DimmedActorDriftsAllTheWayHome()
        {
            LvnFade.Play(_group, 0f, 0.6f, 0.4f, _slot, Drift);
            Assert.AreEqual(Home.x + Drift.x, _slot.anchoredPosition.x, 0.5f,
                "на старте входа герой должен стоять снесённым на полную");

            yield return new WaitForSecondsRealtime(0.36f);
            float left = Mathf.Abs(_slot.anchoredPosition.x - Home.x);
            Assert.Less(left, Drift.x * 0.25f,
                $"под конец входа осталось {left:F1}px сноса — снос ведёт альфа, а не прогресс");

            yield return new WaitForSecondsRealtime(0.2f);
            Assert.AreEqual(Home.x, _slot.anchoredPosition.x, 0.01f, "герой обязан кончить дома");
            Assert.AreEqual(Home.y, _slot.anchoredPosition.y, 0.01f);
        }

        /// <summary>Уход, перебитый показом, не должен «уводить дом». Новый
        /// переход запоминал ТЕКУЩУЮ (уже снесённую) позицию как родную, и
        /// каждый перебитый переход сдвигал героя ещё на шаг вбок.</summary>
        [UnityTest]
        public IEnumerator InterruptedDriftDoesNotWalkTheActorSideways()
        {
            for (var i = 0; i < 3; i++)
            {
                LvnFade.Play(_group, 1f, 0f, 0.5f, _slot, Drift);      // уход…
                yield return new WaitForSecondsRealtime(0.15f);         // …перебитый на середине
                LvnFade.Play(_group, _group.alpha, 1f, 0.2f, _slot, Drift);
                yield return new WaitForSecondsRealtime(0.3f);
            }
            Assert.AreEqual(Home.x, _slot.anchoredPosition.x, 0.01f,
                "после трёх перебитых переходов герой уехал от своего места");
        }

        /// <summary>Уход обязан ДОЕХАТЬ до нуля, а не оборваться скачком.
        /// Многослойный герой гаснет яркостью (чтобы одежда не просвечивала), и
        /// чистая яркость доводила его до непрозрачного чёрного силуэта, который
        /// снимался одним кадром: на замерах последние 10% перехода — прыжок из
        /// ясно видимого в ничто.</summary>
        [UnityTest]
        public IEnumerator LayeredExitEndsTransparentNotAsABlackCutout()
        {
            var second = new GameObject("art2", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            second.transform.SetParent(_actor.transform, false);   // два слоя → путь яркости
            yield return null;

            LvnFade.Play(_group, 1f, 0f, 0.5f, _slot, Drift);
            yield return new WaitForSecondsRealtime(0.45f);        // 90% пути

            Assert.Less(_group.alpha, 0.4f,
                $"на 90% ухода альфа {_group.alpha:F2} — герой всё ещё непрозрачен и исчезнет рывком");
            foreach (var g in _actor.GetComponentsInChildren<Image>())
                Assert.Greater(g.color.maxColorComponent, 0.2f,
                    "цвет упал в чёрный: на светлом фоне это чёрная вырезка вместо человека");
        }

        /// <summary>Постановка ГЛАВНЕЕ перехода: если посреди сноса пришла
        /// команда с новым местом, хвост перехода не имеет права вернуть героя
        /// на старое.</summary>
        [UnityTest]
        public IEnumerator PlacementDuringDriftWins()
        {
            LvnFade.Play(_group, 0f, 1f, 0.4f, _slot, Drift);
            yield return new WaitForSecondsRealtime(0.1f);

            var moved = new Vector2(-300f, -40f);
            _slot.anchoredPosition = moved;          // так делает ApplyPlacement
            LvnFade.Cancel(_group);

            Assert.AreEqual(moved.x, _slot.anchoredPosition.x, 0.01f,
                "переход отпустил снос по своей старой базе и утащил героя обратно");
        }
    }
}
