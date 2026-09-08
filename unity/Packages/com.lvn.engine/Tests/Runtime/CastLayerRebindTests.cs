using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Lvn.UI.World;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// КОМАНДА «УБРАТЬ ФИГУРЫ» ОБЯЗАНА СРАБОТАТЬ, даже если ссылка на слой
    /// потерялась.
    ///
    /// <para>Живой дефект 08.09: на вкладке «Фон» в гардеробе героиня не
    /// пряталась — в редакторе. В собранном APK та же сборка пряталась
    /// исправно. В логе редактора команда доходила и умирала в риге:
    /// «[lvn-cast] слой фигур не привязан — гасить нечего», сразу после
    /// «раздел „backdrop“: фигуры УБИРАЕМ».</para>
    ///
    /// <para>Слой при этом был на месте — терялась именно ССЫЛКА на его
    /// <c>CanvasGroup</c>, привязанная однажды при рождении сцены. Поэтому
    /// страж и обрывает ссылку нарочно: сцена цела, а команда должна дойти
    /// до слоя всё равно.</para>
    /// </summary>
    public class CastLayerRebindTests
    {
        [UnityTest]
        public IEnumerator CastFade_HidesActors_EvenAfterTheLayerReferenceIsLost()
        {
            var host = new GameObject("stage-host", typeof(RectTransform));
            var stage = new WorldStage(host.transform, sortingOrder: 0);
            yield return null;   // канвасу нужен кадр, чтобы разложиться

            var rig = Object.FindAnyObjectByType<WorldCameraRig>();
            Assert.IsNotNull(rig, "риг сцены не найден");

            // Слой фигур — тот же, что сцена создала: ищем его по иерархии,
            // а не по внутренней ссылке рига (её мы сейчас и оборвём).
            var cast = rig.transform.Find(WorldCameraRig.GameRootName + "/" + WorldCameraRig.CastName);
            Assert.IsNotNull(cast, "слой фигур не создан сценой");

            rig.BindCast(null);              // ← ровно то, что случилось живьём
            stage.CastFade(0f, 0f);          // «Фон»: фигуры убираем

            var group = cast.GetComponent<CanvasGroup>();
            Assert.IsNotNull(group, "слой остался без CanvasGroup — гасить нечем");
            Assert.AreEqual(0f, group.alpha, 0.001f,
                "фигуры не погасли: команда снова умерла в риге");

            Object.DestroyImmediate(host);
        }
    }
}
