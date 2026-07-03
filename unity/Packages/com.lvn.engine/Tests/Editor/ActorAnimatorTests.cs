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
        public void Yoyo_PingPongsInsteadOfRestarting()
        {
            ActorAnimator.Clock = () => 0f;
            var rig = new VisualElement();
            var a = new ActorAnimator(rig);
            var tracks = new List<LvnAnimTrack> {
                new LvnAnimTrack { prop = "rotation", keys = K(new object[] { 0f, 0f }, new object[] { 1f, 20f }) } };
            a.Play("c", new LvnAnim { loop = true, yoyo = true, duration = 1f, tracks = tracks });

            ActorAnimator.Clock = () => 1.9f; a.Composite();
            Assert.AreEqual(2f, rig.style.rotate.value.angle.value, 0.5f,
                "yoyo at elapsed 1.9 of a 1s loop is on the way BACK (t=0.1)");

            a.StopAll();
            a.Play("c", new LvnAnim { loop = true, yoyo = false, duration = 1f, tracks = tracks, }); // start=1.9
            ActorAnimator.Clock = () => 3.8f; a.Composite();
            Assert.AreEqual(18f, rig.style.rotate.value.angle.value, 0.5f,
                "plain loop restarts: elapsed 1.9 → t=0.9");
        }

        [Test]
        public void Step_HoldsUntilTheNextKey()
        {
            var tr = new LvnAnimTrack { prop = "y", interp = "step",
                keys = K(new object[] { 0f, 0f }, new object[] { 1f, 10f }) };
            Assert.AreEqual(0f, ActorAnimator.Sample(tr, 0.5f), 0.001f, "holds the left key mid-segment");
            Assert.AreEqual(0f, ActorAnimator.Sample(tr, 0.99f), 0.001f);
            Assert.AreEqual(10f, ActorAnimator.Sample(tr, 1f), 0.001f, "jumps exactly at the key");
        }

        [Test]
        public void Spline_PassesThroughKeysAndRoundsTheMiddle()
        {
            object[][] keys = { new object[] { 0f, 0f }, new object[] { 1f, 10f }, new object[] { 2f, 0f } };
            var lin = new LvnAnimTrack { prop = "y", keys = K(keys) };
            var spl = new LvnAnimTrack { prop = "y", interp = "spline", keys = K(keys) };
            // through every key, exactly like linear
            Assert.AreEqual(0f, ActorAnimator.Sample(spl, 0f), 0.001f);
            Assert.AreEqual(10f, ActorAnimator.Sample(spl, 1f), 0.001f);
            Assert.AreEqual(0f, ActorAnimator.Sample(spl, 2f), 0.001f);
            // Catmull-Rom bulges above the straight chord on the way up
            Assert.AreEqual(5.625f, ActorAnimator.Sample(spl, 0.5f), 0.01f);
            Assert.Greater(ActorAnimator.Sample(spl, 0.5f), ActorAnimator.Sample(lin, 0.5f));
        }

        [Test]
        public void OrientAngle_FollowsThePathTangent()
        {
            LvnAnimTrack T(string prop, float v0, float v1) => new LvnAnimTrack
            { prop = prop, keys = K(new object[] { 0f, v0 }, new object[] { 1f, v1 }) };
            // down-right diagonal (y grows downward on screen) → +45°
            Assert.AreEqual(45f, ActorAnimator.OrientAngle(T("screen_x", 0f, 1f), T("screen_y", 0f, 1f), 0.5f, 1f), 0.5f);
            // horizontal → 0°; up-right → -45°
            Assert.AreEqual(0f, ActorAnimator.OrientAngle(T("screen_x", 0f, 1f), T("screen_y", 0.3f, 0.3f), 0.5f, 1f), 0.5f);
            Assert.AreEqual(-45f, ActorAnimator.OrientAngle(T("screen_x", 0f, 1f), T("screen_y", 1f, 0f), 0.5f, 1f), 0.5f);
        }

        [Test]
        public void Orient_RotatesTheRigAlongThePath()
        {
            ActorAnimator.Clock = () => 0f;
            var slot = new VisualElement();
            var rig = new VisualElement();
            var a = new ActorAnimator(rig);
            a.SetSlot(slot, 0f, 0f);
            a.Play("move", new LvnAnim
            {
                loop = false, duration = 1f,
                tracks = new List<LvnAnimTrack>
                {
                    new LvnAnimTrack { prop = "screen_x", orient = true, keys = K(new object[] { 0f, 0f }, new object[] { 1f, 0.5f }) },
                    new LvnAnimTrack { prop = "screen_y", keys = K(new object[] { 0f, 0f }, new object[] { 1f, 0.5f }) },
                },
            });
            ActorAnimator.Clock = () => 0.5f; a.Composite();
            Assert.AreEqual(45f, rig.style.rotate.value.angle.value, 0.5f, "actor faces along the diagonal path");
        }

        [Test]
        public void InterpAndOrient_DeserializeFromJson()
        {
            const string json = @"{ ""prop"": ""screen_x"", ""interp"": ""spline"", ""orient"": true,
                ""keys"": [[0,0],[1,0.5]] }";
            var tr = JsonConvert.DeserializeObject<LvnAnimTrack>(json);
            Assert.AreEqual("spline", tr.interp);
            Assert.IsTrue(tr.orient);
        }

        // Arc-length: a spline path with wildly uneven key spacing still moves
        // at constant speed — equal time covers equal distance.
        [Test]
        public void ArcLength_EqualTimeCoversEqualDistance()
        {
            // Path: a long straight run crammed into the FIRST tenth of the
            // timeline, then a short run over the rest — grossly uneven speed
            // without arc-length warping.
            var x = new LvnAnimTrack { prop = "screen_x", interp = "spline",
                keys = K(new object[] { 0f, 0f }, new object[] { 0.1f, 0.8f }, new object[] { 1f, 1f }) };
            var y = new LvnAnimTrack { prop = "screen_y", interp = "spline",
                keys = K(new object[] { 0f, 0f }, new object[] { 0.1f, 0f }, new object[] { 1f, 0f }) };

            float[] cache = null;
            float PosAt(float t)
            {
                var tw = ActorAnimator.ArcTime(x, y, t, 1f, ref cache);
                return ActorAnimator.Sample(x, tw, easeless: true);
            }
            // quarter points of TIME must land near quarter points of DISTANCE
            Assert.AreEqual(0.25f, PosAt(0.25f), 0.06f);
            Assert.AreEqual(0.50f, PosAt(0.50f), 0.06f);
            Assert.AreEqual(0.75f, PosAt(0.75f), 0.06f);
            Assert.AreEqual(1.00f, PosAt(1.00f), 0.01f);
        }

        [Test]
        public void WarpProgress_DegeneratePathFallsBackToLinearTime()
        {
            var flat = new float[] { 0f, 0f, 0f }; // zero-length path
            Assert.AreEqual(0.5f, ActorAnimator.WarpProgress(flat, 0.5f, 1f), 0.001f);
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
