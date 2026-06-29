using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.Tests
{
    /// The uGUI (Canvas) actor renderer drives the SAME LvnAnim data onto
    /// RectTransform / CanvasGroup / Image — the 60fps path. Verified headlessly
    /// by stepping its compositor with a controlled clock.
    public class WorldActorTests
    {
        [TearDown]
        public void RestoreClock() => ActorAnimator.Clock = () => Time.realtimeSinceStartup;

        private static List<object[]> K(params object[][] keys) => new List<object[]>(keys);
        private static Sprite NewSprite() => Sprite.Create(new Texture2D(2, 2), new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));

        [Test]
        public void Compositor_DrivesUguiTargets()
        {
            var go = new GameObject("actor", typeof(RectTransform));
            var actor = go.AddComponent<WorldActor>();
            actor.ContentSize = new Vector2(1000f, 2000f);
            actor.SetSlotBase(Vector2.zero);

            var open = NewSprite();
            var closed = NewSprite();
            actor.Configure(new List<Sprite> { open }, new List<string> { "eyes" });
            actor.SetFrames(new Dictionary<string, Dictionary<string, Sprite>>
            {
                { "eyes", new Dictionary<string, Sprite> { { "open", open }, { "closed", closed } } },
            });

            var anim = new LvnAnim
            {
                loop = false, duration = 1f,
                tracks = new List<LvnAnimTrack>
                {
                    new LvnAnimTrack { prop = "screen_y", keys = K(new object[] { 0f, 0f }, new object[] { 1f, 0.5f }) },
                    new LvnAnimTrack { prop = "scalex",   keys = K(new object[] { 0f, 1f }, new object[] { 1f, 0.2f }) },
                    new LvnAnimTrack { prop = "rotation", keys = K(new object[] { 0f, 0f }, new object[] { 1f, 360f }) },
                    new LvnAnimTrack { prop = "alpha",    keys = K(new object[] { 0f, 1f }, new object[] { 1f, 0.4f }) },
                    new LvnAnimTrack { layer = "eyes", prop = "frame", axis = "eyes",
                        keys = K(new object[] { 0f, "open" }, new object[] { 0.5f, "closed" }) },
                },
            };

            ActorAnimator.Clock = () => 0f;
            actor.Play("base", anim);
            actor.Tick(0.5f);

            var slot = (RectTransform)actor.transform;
            var rig = (RectTransform)actor.transform.Find("rig");
            var group = rig.GetComponent<CanvasGroup>();
            var eyes = rig.GetComponentInChildren<Image>();

            Assert.AreEqual(-500f, slot.anchoredPosition.y, 1f, "screen_y 0→0.5: -(0.25 * 2000)");
            Assert.AreEqual(0.6f, rig.localScale.x, 0.01f, "scalex 1→0.2 at t=0.5");
            Assert.AreEqual(1f, rig.localScale.y, 0.01f, "scaley untouched");
            Assert.AreEqual(180f, rig.localEulerAngles.z, 1f, "rotation 0→360 at t=0.5");
            Assert.AreEqual(0.7f, group.alpha, 0.01f, "alpha 1→0.4 at t=0.5");
            Assert.AreSame(closed, eyes.sprite, "frame stepped to 'closed' by t=0.5");

            Object.DestroyImmediate(go);
        }
    }
}
