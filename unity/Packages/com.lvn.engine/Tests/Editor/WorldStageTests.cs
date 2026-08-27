using System.Collections.Generic;
using Lvn.UI;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.Tests
{
    /// The Canvas scene path (uGUI) — единственный путь сцены,
    /// and the WorldStage assembles a real Canvas → GameRoot → (bg, content) tree.
    public class WorldStageTests
    {
        private static Sprite NewSprite() => Sprite.Create(new Texture2D(2, 2), new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));

        [Test]
        public void Placement_MapsScreenFractionsToRect()
        {
            var go = new GameObject("slot", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            var size = new Vector2(1080f, 1920f);

            WorldPlacement.Apply(rt, Placement.Standing(0.5f), size);

            Assert.AreEqual(new Vector2(0f, 1f), rt.anchorMin, "anchored to top-left");
            Assert.AreEqual(0.5f, rt.pivot.x, 0.001f, "anchor_x 0.5 → pivot.x");
            Assert.AreEqual(0f, rt.pivot.y, 0.001f, "anchor_y 1 (feet) → pivot.y 0 (uGUI bottom)");
            Assert.AreEqual(0.69f * 1080f, rt.sizeDelta.x, 0.1f, "default width fraction (standard novel pose, ~1.5×)");
            Assert.AreEqual(0.93f * 1920f, rt.sizeDelta.y, 0.1f, "default height fraction (standard novel pose, ~1.5×)");
            Assert.AreEqual(540f, rt.anchoredPosition.x, 0.1f, "X 0.5 → 540");
            Assert.AreEqual(-1920f, rt.anchoredPosition.y, 0.1f, "Y 1 → -1920 (down from top)");
            Assert.AreEqual(1f, rt.localScale.x, 0.001f, "not flipped");

            Object.DestroyImmediate(go);
        }

        [Test]
        public void Placement_FlipAndRotation()
        {
            var go = new GameObject("slot", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            var p = Placement.Standing(0.25f);
            p.Flip = true;
            p.Rotation = 30f;

            WorldPlacement.Apply(rt, p, new Vector2(1000f, 2000f));

            Assert.AreEqual(-1f, rt.localScale.x, 0.001f, "flip mirrors X");
            Assert.AreEqual(0f, Mathf.DeltaAngle(rt.localEulerAngles.z, -30f), 0.5f, "rotation negated (clockwise)");
            Assert.AreEqual(250f, rt.anchoredPosition.x, 0.1f, "X 0.25 → 250");

            Object.DestroyImmediate(go);
        }

        // ── Габарит фигуры внутри холста ───────────────────────────────────────
        // Рост персонажа задаётся ростом персонажа, а не тем, сколько воздуха
        // художник оставил вокруг него в png.

        [Test]
        public void Placement_AspectLock_WithoutFigureData_FitsTheWholeCanvas()
        {
            var go = new GameObject("slot", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            var p = Placement.Standing(0.5f);
            p.Width = 0.69f; p.Height = 0.93f;
            p.BoxAspect = 0.5f; // холст 1:2

            WorldPlacement.Apply(rt, p, new Vector2(1080f, 1920f));

            // Прежнее правило: коробка вписана в заказ, ширина здесь у́же.
            Assert.AreEqual(0.69f * 1080f, rt.sizeDelta.x, 0.5f, "ширина осталась заказанной");
            Assert.AreEqual(0.69f * 1080f / 0.5f, rt.sizeDelta.y, 0.5f, "высота follows the aspect");
            Assert.AreEqual(0.5f, rt.pivot.x, 0.001f, "без данных о фигуре якорь как был");
            Assert.AreEqual(0f, rt.pivot.y, 0.001f);

            Object.DestroyImmediate(go);
        }

        [Test]
        public void Placement_SidePadding_DoesNotStealHeight()
        {
            var go = new GameObject("slot", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            var p = Placement.Standing(0.5f);
            p.Width = 0.9f; p.Height = 1f;
            p.BoxAspect = 0.8f;                    // ШИРОКИЙ холст…
            p.ContentX = 0.25f; p.ContentY = 0f;   // …в котором фигура — половина
            p.ContentW = 0.5f; p.ContentH = 1f;

            WorldPlacement.Apply(rt, p, new Vector2(1000f, 2000f));

            // 900 заказанной ширины хватает на фигуру шириной до 900/0.5 = 1800
            // единиц холста, то есть на всю заказанную высоту. Пока ширину мерил
            // холст, тот же заказ ронял высоту до 900/0.8 = 1125.
            Assert.AreEqual(2000f, rt.sizeDelta.y, 0.5f, "высота осталась заказанной");
            Assert.AreEqual(1600f, rt.sizeDelta.x, 0.5f, "холст с полями шире заказа — и это нормально");
            Assert.AreEqual(800f, rt.sizeDelta.x * p.ContentW, 0.5f, "фигура при этом у́же экрана");

            Object.DestroyImmediate(go);
        }

        [Test]
        public void Placement_SidePadding_StillClampsTheFigureToTheOrderedWidth()
        {
            var go = new GameObject("slot", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            var p = Placement.Standing(0.5f);
            p.Width = 0.4f; p.Height = 1f;          // узкий заказ по ширине
            p.BoxAspect = 0.8f;
            p.ContentX = 0.25f; p.ContentY = 0f;
            p.ContentW = 0.5f; p.ContentH = 1f;

            WorldPlacement.Apply(rt, p, new Vector2(1000f, 2000f));

            Assert.AreEqual(400f, rt.sizeDelta.x * p.ContentW, 0.5f, "ширину ограничивает ФИГУРА…");
            Assert.AreEqual(1000f, rt.sizeDelta.y, 0.5f, "…и высота идёт за ней по аспекту холста");

            Object.DestroyImmediate(go);
        }

        [Test]
        public void Placement_TopPadding_IsHeight_NotPadding()
        {
            var go = new GameObject("slot", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            var p = Placement.Standing(0.5f);
            p.Width = 1f; p.Height = 0.9f;
            p.BoxAspect = 0.25f;                    // узкий высокий кадр
            p.ContentX = 0f; p.ContentY = 0.5f;     // ребёнок в нижней половине кадра
            p.ContentW = 1f; p.ContentH = 0.5f;

            WorldPlacement.Apply(rt, p, new Vector2(1000f, 2000f));

            // Кадр остаётся кадром: персонаж, нарисованный в его нижней половине,
            // и на экране вдвое ниже — нормализуй мы высоту по фигуре, ребёнок
            // сравнялся бы ростом со взрослым.
            Assert.AreEqual(1800f, rt.sizeDelta.y, 0.5f, "высота = заказанная доля экрана");
            Assert.AreEqual(900f, rt.sizeDelta.y * p.ContentH, 0.5f, "фигура — половина кадра");

            Object.DestroyImmediate(go);
        }

        [Test]
        public void Placement_FigureBox_PutsTheFeetOnTheBaseline()
        {
            var go = new GameObject("slot", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            var p = Placement.Standing(0.5f);       // AnchorY = 1 — ноги
            p.Width = 1f; p.Height = 1f;
            p.BoxAspect = 1f;
            p.ContentX = 0.25f; p.ContentY = 0.1f;  // воздух сверху И снизу
            p.ContentW = 0.5f; p.ContentH = 0.6f;

            WorldPlacement.Apply(rt, p, new Vector2(1000f, 2000f));

            // Пивот стоит на нижней кромке ФИГУРЫ (0.1+0.6 = 0.7 сверху),
            // иначе персонаж висел бы над полом на высоту прозрачного поля.
            Assert.AreEqual(0.5f, rt.pivot.x, 0.001f, "по горизонтали — середина фигуры");
            Assert.AreEqual(0.3f, rt.pivot.y, 0.001f, "по вертикали — подошвы, не низ файла");

            Object.DestroyImmediate(go);
        }

        [Test]
        public void Stage_BuildsCanvasHierarchyAndPlacesActor()
        {
            var host = new GameObject("host", typeof(RectTransform));
            var stage = new WorldStage(host.transform, sortingOrder: 4);

            var canvas = stage.Root.GetComponent<Canvas>();
            Assert.IsNotNull(canvas, "canvas built");
            // Overlay without a camera; through-camera when one exists (real
            // gaussian blur path) — visually identical, so both are correct.
            if (canvas.renderMode == RenderMode.ScreenSpaceCamera)
                Assert.IsNotNull(canvas.worldCamera, "camera mode must carry its camera");
            else
                Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode);
            Assert.AreEqual(4, canvas.sortingOrder, "sorts below the UITK chrome");
            Assert.IsNotNull(stage.Root.GetComponent<CanvasScaler>(), "scaler built");
            Assert.IsNotNull(stage.Root.GetComponent<WorldCameraRig>(), "camera rig on canvas");

            stage.SetBackgroundColor(Color.black);

            var actor = stage.ApplyActor("mara", new List<Sprite> { NewSprite() }, Placement.Standing(0.5f));
            Assert.IsTrue(stage.HasActor("mara"));
            Assert.AreSame(actor, stage.ActorFor("mara"));
            Assert.AreEqual(0f, actor.Slot.pivot.y, 0.001f, "placed via WorldPlacement");
            Assert.IsTrue(actor.gameObject.activeSelf, "shown");

            // СМЕНА ГОВОРЯЩЕГО НЕ КРАСИТ КАСТ. Автоматическое приглушение
            // не-говорящего отменено: художник рисует свет сам, а движок,
            // притемняя слои, ломал его работу и заодно перестраивал канвас на
            // каждой реплике. Проверка держит именно нейтральный цвет — иначе
            // приглушение вернётся тихо, «мелким улучшением».
            stage.ApplyActor("guest", new List<Sprite> { NewSprite() }, Placement.Standing(0.75f));
            stage.SetSpeaker("mara");
            var guestGfx = stage.ActorFor("guest").GetComponentInChildren<UnityEngine.UI.Graphic>();
            Assert.AreEqual(1f, guestGfx.color.r, 0.001f, "не-говорящего притемнили — это отменено");
            Assert.AreEqual(1f, guestGfx.color.a, 0.001f, "альфа принадлежит постановке и переходу");

            Object.DestroyImmediate(host);
        }

        [Test]
        public void Stage_ZOrder_SurvivesLateApplies()
        {
            var host = new GameObject("host", typeof(RectTransform));
            var stage = new WorldStage(host.transform);

            var pEnemy = Placement.Standing(0.5f); pEnemy.Z = 10;
            var pHands = Placement.Standing(0.5f); pHands.Z = 80;

            var enemy = stage.ApplyActor("enemy", new List<Sprite> { NewSprite() }, pEnemy);
            var hands = stage.ApplyActor("hands", new List<Sprite> { NewSprite() }, pHands);
            // The bug: SetSiblingIndex(z) clamped z=10 to "last child" on a small
            // canvas, so the skeleton's hurt-pose re-apply (landing AFTER the
            // hands' attack-pose) drew the skeleton over the hands. Null layers:
            // re-configuring art calls Destroy(), which EditMode forbids — the
            // sibling sort under test doesn't depend on the art.
            stage.ApplyActor("enemy", null, pEnemy);

            Assert.Less(enemy.transform.GetSiblingIndex(), hands.transform.GetSiblingIndex(),
                "z=10 stays under z=80 no matter which apply lands last");

            // No-z objects keep the classic "shown later = on top" order between
            // themselves (birth-order tie-break)…
            var first = stage.ApplyActor("first", new List<Sprite> { NewSprite() }, Placement.Standing(0.25f));
            var second = stage.ApplyActor("second", new List<Sprite> { NewSprite() }, Placement.Standing(0.75f));
            Assert.Less(first.transform.GetSiblingIndex(), second.transform.GetSiblingIndex(),
                "no-z: creation order preserved");
            // …and sit below explicit positive z (default z = 0).
            Assert.Less(second.transform.GetSiblingIndex(), enemy.transform.GetSiblingIndex(),
                "no-z (0) stacks under z=10");

            Object.DestroyImmediate(host);
        }

        [Test]
        public void Stage_PreloadedPlacementStaysHiddenUntilArtStartsEntrance()
        {
            var host = new GameObject("host", typeof(RectTransform));
            var stage = new WorldStage(host.transform);
            var p = Placement.Standing(0.25f);
            p.EnterTransition = TransitionType.Drift;
            p.TransitionDuration = 1f;

            var actor = stage.PlaceActor("mara", p);
            var placedX = actor.Slot.anchoredPosition.x;

            Assert.IsFalse(actor.gameObject.activeSelf,
                "pre-load placement must not consume the entrance on an empty slot");

            stage.ApplyActor("mara", new List<Sprite> { NewSprite() }, p);
            var group = actor.GetComponent<CanvasGroup>();

            Assert.IsTrue(actor.gameObject.activeSelf, "art arrival starts the real entrance");
            Assert.AreEqual(0f, group.alpha, 0.001f, "entrance begins transparent");
            // Снос живёт на СВОЁМ узле: слот принадлежит постановке и обязан
            // стоять там, куда его поставили, даже пока герой въезжает.
            Assert.AreEqual(placedX, actor.Slot.anchoredPosition.x, 0.001f,
                "переход сдвинул слот — позицию слота держит постановка");
            Assert.Less(actor.Transition.anchoredPosition.x, 0f,
                "left-side actor starts outside its final position");

            Object.DestroyImmediate(host);
        }

        [Test]
        public void CanvasRenderer_ReusesLoadedArtOnAnimatedReshow()
        {
            var host = new GameObject("host", typeof(RectTransform));
            var stage = new WorldStage(host.transform);
            var renderer = new CanvasSceneRenderer(stage);
            var shown = Placement.Standing(0.75f);
            stage.ApplyActor("mara", new List<Sprite> { NewSprite() }, shown);

            var hidden = shown;
            hidden.Show = false;
            renderer.ApplyActor("mara", null, hidden, null, null, null);
            Assert.IsFalse(stage.ActorFor("mara").gameObject.activeSelf);

            shown.EnterTransition = TransitionType.Fade;
            shown.TransitionDuration = 1f;
            renderer.PlaceActor("mara", shown);
            renderer.ApplyActor("mara", null, shown, null, null, null);

            var actor = stage.ActorFor("mara");
            Assert.IsTrue(actor.gameObject.activeSelf, "existing layers may be shown without repeating their URL");
            Assert.AreEqual(0f, actor.GetComponent<CanvasGroup>().alpha, 0.001f);

            Object.DestroyImmediate(host);
        }
    }
}
