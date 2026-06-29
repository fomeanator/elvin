using System.Collections.Generic;
using Lvn.Content;
using Lvn.UI;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// The animation curve sampler + compositor that drives rigged actor
    /// animations. Sampling is pure; the compositor is driven with a controlled
    /// clock so screen/scale/frame routing is verified without a live panel.
    public class ActorAnimatorTests
    {
        [TearDown]
        public void RestoreClock() => ActorAnimator.Clock = () => Time.realtimeSinceStartup;

        private static List<object[]> K(params object[][] keys) => new List<object[]>(keys);
        private static Sprite NewSprite() => Sprite.Create(new Texture2D(2, 2), new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f));

        private static LvnAnimTrack Track(string ease, params object[][] keys) =>
            new LvnAnimTrack { prop = "y", ease = ease, keys = new List<object[]>(keys) };

        [Test]
        public void Linear_InterpolatesBetweenKeys()
        {
            var tr = Track("linear", new object[] { 0f, 0f }, new object[] { 1f, 10f });
            Assert.AreEqual(0f, ActorAnimator.Sample(tr, 0f), 0.001f);
            Assert.AreEqual(5f, ActorAnimator.Sample(tr, 0.5f), 0.001f);
            Assert.AreEqual(10f, ActorAnimator.Sample(tr, 1f), 0.001f);
        }

        [Test]
        public void Clamps_BeforeFirstAndAfterLast()
        {
            var tr = Track("linear", new object[] { 0.2f, 3f }, new object[] { 0.8f, 7f });
            Assert.AreEqual(3f, ActorAnimator.Sample(tr, 0f), 0.001f, "before first key holds the first value");
            Assert.AreEqual(7f, ActorAnimator.Sample(tr, 1f), 0.001f, "after last key holds the last value");
        }

        [Test]
        public void MultiSegment_PicksTheRightSpan()
        {
            var tr = Track("linear", new object[] { 0f, 0f }, new object[] { 1f, 10f }, new object[] { 2f, 0f });
            Assert.AreEqual(10f, ActorAnimator.Sample(tr, 1f), 0.001f);
            Assert.AreEqual(5f, ActorAnimator.Sample(tr, 1.5f), 0.001f, "interpolates the second span back down");
        }

        [Test]
        public void Frame_StepsToTheLastKeyAtOrBeforeT()
        {
            // a blink: eyes open until ~t=0.9, then closed
            var tr = new LvnAnimTrack
            {
                prop = "frame", layer = "eyes", axis = "eyes",
                keys = new List<object[]> { new object[] { 0f, "open" }, new object[] { 0.9f, "closed" }, new object[] { 1.0f, "open" } },
            };
            Assert.AreEqual("open", ActorAnimator.SampleFrame(tr, 0f));
            Assert.AreEqual("open", ActorAnimator.SampleFrame(tr, 0.5f));
            Assert.AreEqual("closed", ActorAnimator.SampleFrame(tr, 0.95f));
            Assert.AreEqual("open", ActorAnimator.SampleFrame(tr, 1.0f));
        }

        [Test]
        public void ScreenAndScale_MoveSlotAndSquashRig()
        {
            ActorAnimator.Clock = () => 0f;
            var slot = new VisualElement();
            var rig = new VisualElement();
            var a = new ActorAnimator(rig);
            a.SetSlot(slot, 0.5f, 0.1f); // base position: x=0.5, y=0.1 (screen fractions)

            var fall = new LvnAnim
            {
                loop = false, duration = 1f,
                tracks = new List<LvnAnimTrack>
                {
                    new LvnAnimTrack { prop = "screen_y", keys = K(new object[] { 0f, 0f }, new object[] { 1f, 0.8f }) },
                    new LvnAnimTrack { prop = "scalex",   keys = K(new object[] { 0f, 1f }, new object[] { 1f, 0.2f }) },
                    new LvnAnimTrack { prop = "rotation", keys = K(new object[] { 0f, 0f }, new object[] { 1f, 360f }) },
                },
            };
            a.Play("fall", fall);

            ActorAnimator.Clock = () => 0.5f;
            a.Composite();

            Assert.AreEqual(50f, slot.style.top.value.value, 0.5f, "screen_y: (0.1 + 0.4) of screen");
            Assert.AreEqual(50f, slot.style.left.value.value, 0.5f, "no screen_x → base x=0.5 held");
            Assert.AreEqual(0.6f, rig.style.scale.value.value.x, 0.01f, "scalex 1→0.2 at t=0.5");
            Assert.AreEqual(1f, rig.style.scale.value.value.y, 0.01f, "scaley untouched");
            Assert.AreEqual(180f, rig.style.rotate.value.angle.value, 1f, "rotation 0→360 at t=0.5");
        }

        [Test]
        public void Frame_SwapsTheTargetLayerSprite()
        {
            ActorAnimator.Clock = () => 0f;
            var rig = new VisualElement();
            var a = new ActorAnimator(rig);
            var img = new Image();
            var open = NewSprite();
            var closed = NewSprite();
            a.SetLayer("eyes", img, open);
            a.SetFrames(new Dictionary<string, Dictionary<string, Sprite>>
            {
                { "eyes", new Dictionary<string, Sprite> { { "open", open }, { "closed", closed } } },
            });

            var blink = new LvnAnim
            {
                loop = true, duration = 1f,
                tracks = new List<LvnAnimTrack>
                {
                    new LvnAnimTrack { layer = "eyes", prop = "frame", axis = "eyes",
                        keys = K(new object[] { 0f, "open" }, new object[] { 0.5f, "closed" }) },
                },
            };
            a.Play("blink", blink);

            ActorAnimator.Clock = () => 0.1f; a.Composite();
            Assert.AreSame(open, img.sprite, "eyes open early in the cycle");
            ActorAnimator.Clock = () => 0.6f; a.Composite();
            Assert.AreSame(closed, img.sprite, "eyes closed after the blink key");
        }

        [Test]
        public void Queue_PlaysAfterCurrentFinishes()
        {
            ActorAnimator.Clock = () => 0f;
            var rig = new VisualElement();
            var a = new ActorAnimator(rig);
            // current: rotation 0→20 over 1s (one-shot)
            a.Play("c", new LvnAnim { loop = false, duration = 1f, tracks = new List<LvnAnimTrack> {
                new LvnAnimTrack { prop = "rotation", keys = K(new object[] { 0f, 0f }, new object[] { 1f, 20f }) } } });
            // queued: constant rotation 100 (clearly distinct), starts only after the first ends
            a.PlayQueued("c", new LvnAnim { loop = false, duration = 1f, tracks = new List<LvnAnimTrack> {
                new LvnAnimTrack { prop = "rotation", keys = K(new object[] { 0f, 100f }, new object[] { 1f, 100f }) } } });

            ActorAnimator.Clock = () => 0.5f; a.Composite();
            Assert.AreEqual(10f, rig.style.rotate.value.angle.value, 0.5f, "current anim runs; queued NOT applied yet");

            ActorAnimator.Clock = () => 1.05f; a.Composite(); // first finishes → dequeues the next
            ActorAnimator.Clock = () => 1.5f; a.Composite();   // ~0.45s into the queued step
            Assert.AreEqual(100f, rig.style.rotate.value.angle.value, 0.5f, "queued anim now running after the first finished");
        }

        [Test]
        public void Anim_DeserializesFromManifestJson()
        {
            // exactly the shape an authored catalog entry carries.
            const string json = @"{
              ""name"": ""Mara"",
              ""kind"": ""rigged"",
              ""anim"": {
                ""idle"": { ""auto"": ""true"", ""loop"": true, ""duration"": 3.0,
                  ""tracks"": [ { ""prop"": ""y"", ""ease"": ""inOutSine"", ""keys"": [[0,0],[1.5,0.012],[3,0]] } ] },
                ""wave"": { ""loop"": false, ""duration"": 0.8,
                  ""tracks"": [ { ""prop"": ""rotation"", ""keys"": [[0,0],[0.5,4],[0.8,0]] } ] }
              }
            }";
            var e = JsonConvert.DeserializeObject<LvnSpriteEntity>(json);
            Assert.AreEqual("rigged", e.kind);
            Assert.IsTrue(e.anim.ContainsKey("idle"));
            Assert.AreEqual("true", e.anim["idle"].auto);
            Assert.IsTrue(e.anim["idle"].loop);
            Assert.AreEqual(3.0f, e.anim["idle"].duration, 0.001f);
            var track = e.anim["idle"].tracks[0];
            Assert.AreEqual("y", track.prop);
            Assert.AreEqual(0.012f, System.Convert.ToSingle(track.keys[1][1]), 0.0001f); // [time=1.5, value=0.012]
            Assert.IsFalse(e.anim["wave"].loop);
        }

        [Test]
        public void Easing_ChangesTheMidpointButNotEndpoints()
        {
            var lin = Track("linear", new object[] { 0f, 0f }, new object[] { 1f, 10f });
            var eased = Track("inOutSine", new object[] { 0f, 0f }, new object[] { 1f, 10f });
            // endpoints identical regardless of easing
            Assert.AreEqual(ActorAnimator.Sample(lin, 0f), ActorAnimator.Sample(eased, 0f), 0.001f);
            Assert.AreEqual(ActorAnimator.Sample(lin, 1f), ActorAnimator.Sample(eased, 1f), 0.001f);
            // inOutSine at 0.5 is also 0.5 → same midpoint here, but a non-symmetric
            // curve (outBack) overshoots, proving easing is applied:
            var back = Track("outBack", new object[] { 0f, 0f }, new object[] { 1f, 10f });
            Assert.Greater(ActorAnimator.Sample(back, 0.6f), ActorAnimator.Sample(lin, 0.6f));
        }
    }
}
