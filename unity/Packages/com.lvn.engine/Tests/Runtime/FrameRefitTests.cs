using System.Collections;
using System.Collections.Generic;
using Lvn.UI;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Lvn.Tests
{
    /// <summary>
    /// ФИГУРА ДОГОНЯЕТ КАДР, КОГДА ЭКРАН ВСТАЛ.
    ///
    /// <para>Постановка меряется по размеру экрана в момент команды. Но экран
    /// не всегда уже стоит: в редакторе окно игры первые кадры — полоска в
    /// тридцать раз шире высоты, на телефоне поворот проходит через
    /// промежуточные размеры. Команда, попавшая в такой момент, считала
    /// полосу сцены по мусорному кадру — и героиня уезжала на тридцать
    /// экранов вправо (стенд 12.09: <c>[lvn-move] hill: слот (31966, …)</c>,
    /// ровно тот «ГГ летает» из репорта тестировщика). Раз поставленная,
    /// фигура не пересчитывалась: её никто не звал заново.</para>
    ///
    /// <para>Теперь сцена помнит, по какому кадру ставила, и при смене кадра
    /// ставит всех заново тем же размещением. Кадр здесь подменён зондом —
    /// иначе экран в тесте не сменить.</para>
    /// </summary>
    public sealed class FrameRefitTests
    {
        private GameObject _host;
        private System.Func<Vector2> _probe;

        [SetUp]
        public void SetUp() => _probe = WorldStage.FrameProbe;

        [TearDown]
        public void TearDown()
        {
            WorldStage.FrameProbe = _probe;
            if (_host != null) Object.Destroy(_host);
        }

        private static Sprite NewSprite()
        {
            var tex = new Texture2D(4, 4);
            return Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
        }

        [UnityTest]
        public IEnumerator ActorPlacedOnAWildFrame_FollowsTheScreenOnceItSettles()
        {
            var frame = new Vector2(3440f, 114f);              // окно игры ещё полоска
            WorldStage.FrameProbe = () => frame;
            _host = new GameObject("host", typeof(RectTransform));
            var stage = new WorldStage(_host.transform, sortingOrder: 0);

            var actor = stage.ApplyActor("mara", new List<Sprite> { NewSprite() }, Placement.Standing(0.5f));
            yield return null;
            float wild = actor.Slot.anchoredPosition.x;
            Assert.Greater(wild, 5000f, "по полоске фигура и правда улетает за экран — стенд это и показал");

            frame = new Vector2(1170f, 2532f);                 // экран телефона встал
            Assert.IsTrue(stage.RefitToFrame(), "смена кадра замечена и фигуры поставлены заново");
            yield return null;
            // Центр — единственное место, которое зажим у кромок не трогает:
            // проверяем сам пересчёт, а не правило зажима.
            Assert.AreEqual(0.5f * 1080f, actor.Slot.anchoredPosition.x, 1f,
                "после смены кадра фигура стоит там, где просила команда: середина опорного кадра");
            Assert.IsFalse(stage.RefitToFrame(), "кадр не менялся — переставлять нечего");
        }
    }
}
