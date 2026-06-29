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
            Assert.AreEqual(0.46f * 1080f, rt.sizeDelta.x, 0.1f, "default width fraction");
            Assert.AreEqual(0.62f * 1920f, rt.sizeDelta.y, 0.1f, "default height fraction");
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
            var guestGroup = stage.ActorFor("guest").GetComponent<CanvasGroup>();
            Assert.Less(guestGroup.alpha, 1f, "non-speaker dimmed");

            Object.DestroyImmediate(host);
        }
    }
}
