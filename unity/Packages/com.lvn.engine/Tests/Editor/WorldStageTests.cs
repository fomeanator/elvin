using System.Collections.Generic;
using Lvn.UI;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.Tests
{
    /// The Canvas scene path (uGUI) — placement math mirrors the UITK ActorLayer,
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

            // speaker-dim: the non-speaker drops below its base opacity.
            stage.ApplyActor("guest", new List<Sprite> { NewSprite() }, Placement.Standing(0.75f));
            stage.SetSpeaker("mara");
            // dimming is a COLOUR tint (alpha stays free for transitions)
            var guestGfx = stage.ActorFor("guest").GetComponentInChildren<UnityEngine.UI.Graphic>();
            Assert.Less(guestGfx.color.r, 1f, "non-speaker dimmed (colour tint)");

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
            Assert.Less(actor.Slot.anchoredPosition.x, placedX,
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
