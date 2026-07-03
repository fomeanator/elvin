using System.Collections.Generic;
using Lvn.UI;
using Lvn.Content;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// The paper-doll FK solver: parent chains carry children around pivots,
    /// springs swing from motion and settle. Pure math — no scene needed.
    public class BoneSolverTests
    {
        private static List<object[]> K(params object[][] keys) => new List<object[]>(keys);

        private static BoneSolver.Bone B(string id, string parent, float pivotX, float pivotY,
            float tx = 0, float ty = 0, float angle = 0, float sx = 1, float sy = 1) =>
            new BoneSolver.Bone { Id = id, Parent = parent, Pivot = new Vector2(pivotX, pivotY),
                Tx = tx, Ty = ty, Angle = angle, Sx = sx, Sy = sy };

        [Test]
        public void Root_MovesByItsOwnLocalTransform()
        {
            var poses = BoneSolver.Solve(new List<BoneSolver.Bone> { B("body", null, 0.5f, 0.8f, tx: 0.1f, angle: 10f) });
            Assert.AreEqual(0.6f, poses["body"].PivotWorld.x, 1e-4f);
            Assert.AreEqual(0.8f, poses["body"].PivotWorld.y, 1e-4f);
            Assert.AreEqual(10f, poses["body"].Angle, 1e-4f);
        }

        [Test]
        public void ParentRotation_SwingsTheChildAroundTheParentPivot()
        {
            // Shoulder at (0.5, 0.5); hand pivot 0.2 to the RIGHT of it. Parent
            // rotates 90° clockwise (y down) → the hand should end up 0.2 BELOW.
            var poses = BoneSolver.Solve(new List<BoneSolver.Bone>
            {
                B("arm",  null,  0.5f, 0.5f, angle: 90f),
                B("hand", "arm", 0.7f, 0.5f),
            });
            Assert.AreEqual(0.5f, poses["hand"].PivotWorld.x, 1e-3f, "carried onto the vertical");
            Assert.AreEqual(0.7f, poses["hand"].PivotWorld.y, 1e-3f, "0.2 below the shoulder");
            Assert.AreEqual(90f, poses["hand"].Angle, 1e-3f, "child inherits the rotation");
        }

        [Test]
        public void GrandChild_ComposesTheWholeChain()
        {
            var poses = BoneSolver.Solve(new List<BoneSolver.Bone>
            {
                B("body", null,   0.5f, 0.5f, angle: 90f),
                B("arm",  "body", 0.7f, 0.5f, angle: 90f),
                B("hand", "arm",  0.9f, 0.5f),
            });
            // body turns arm to (0.5,0.7); arm's own 90° turns hand (0.2 right of
            // arm in rest) to point BACK along -x from the arm's new spot.
            Assert.AreEqual(180f, poses["hand"].Angle, 1e-3f);
            Assert.AreEqual(0.3f, poses["hand"].PivotWorld.x, 1e-3f);
            Assert.AreEqual(0.7f, poses["hand"].PivotWorld.y, 1e-3f);
        }

        [Test]
        public void ParentScale_StretchesTheOffsetAndInherits()
        {
            var poses = BoneSolver.Solve(new List<BoneSolver.Bone>
            {
                B("body", null,   0.5f, 0.5f, sx: 2f, sy: 2f),
                B("head", "body", 0.5f, 0.3f),
            });
            Assert.AreEqual(0.1f, poses["head"].PivotWorld.y, 1e-3f, "offset (−0.2) doubled by parent scale");
            Assert.AreEqual(2f, poses["head"].Sx, 1e-3f);
        }

        [Test]
        public void BrokenParents_DegradeToRootInsteadOfThrowing()
        {
            var poses = BoneSolver.Solve(new List<BoneSolver.Bone>
            {
                B("a", "ghost", 0.4f, 0.4f, tx: 0.1f), // unknown parent
                B("b", "c", 0.5f, 0.5f),               // cycle
                B("c", "b", 0.6f, 0.6f),
            });
            Assert.AreEqual(0.5f, poses["a"].PivotWorld.x, 1e-4f, "unknown parent → plain root");
            Assert.IsTrue(poses.ContainsKey("b") && poses.ContainsKey("c"), "cycle solved, no hang");
        }

        // ── springs ──────────────────────────────────────────────────────────

        [Test]
        public void Spring_KickedByMotion_ThenSettlesToRest()
        {
            var s = new BoneSolver.SpringState();
            s = BoneSolver.SpringStep(s, new Vector2(0.5f, 0.5f), 0f, 12f, 6f, 1f / 60f); // prime
            s = BoneSolver.SpringStep(s, new Vector2(0.6f, 0.5f), 0f, 12f, 6f, 1f / 60f); // pivot jumps right
            Assert.Less(s.Angle, 0f, "hair swings OPPOSITE the travel");

            for (int i = 0; i < 600; i++) // ten still seconds
                s = BoneSolver.SpringStep(s, new Vector2(0.6f, 0.5f), 0f, 12f, 6f, 1f / 60f);
            Assert.AreEqual(0f, s.Angle, 0.5f, "settles back to rest");
            Assert.AreEqual(0f, s.Velocity, 1f, "and stops");
        }

        [Test]
        public void Spring_AbsorbsParentRotation_ThenCatchesUp()
        {
            // The VRM behaviour: the parent snaps 30° — hair keeps its world
            // orientation for a beat (negative local offset), then settles to 0.
            var s = new BoneSolver.SpringState();
            s = BoneSolver.SpringStep(s, Vector2.zero, 0f, 12f, 6f, 1f / 60f);  // prime
            s = BoneSolver.SpringStep(s, Vector2.zero, 30f, 12f, 6f, 1f / 60f); // parent turns
            Assert.Less(s.Angle, -15f, "most of the turn is absorbed as lag");
            for (int i = 0; i < 900; i++)
                s = BoneSolver.SpringStep(s, Vector2.zero, 30f, 12f, 6f, 1f / 60f);
            Assert.AreEqual(0f, s.Angle, 0.5f, "catches up with the parent");
        }

        // End-to-end doll: the demo entity's exact chain (body → head → hair
        // with a spring, body → arm) driven through the real compositor.
        [Test]
        public void Doll_IdleBob_CarriesChainAndSwingsHair()
        {
            ActorAnimator.Clock = () => 0f;
            var rig = new UnityEngine.UIElements.VisualElement();
            var a = new ActorAnimator(rig);
            var imgs = new Dictionary<string, UnityEngine.UIElements.Image>();
            foreach (var lid in new[] { "body", "arm", "head", "hair" })
            {
                var img = new UnityEngine.UIElements.Image();
                imgs[lid] = img;
                a.SetLayer(lid, img, null);
            }
            a.SetLayerBone("body", null, new Vector2(0.5f, 0.9f), new Vector4(0.26f, 0.35f, 0.48f, 0.63f), 0f, 6f);
            a.SetLayerBone("arm", "body", new Vector2(0.49f, 0.07f), new Vector4(0.60f, 0.36f, 0.12f, 0.26f), 0f, 6f);
            a.SetLayerBone("head", "body", new Vector2(0.5f, 0.84f), new Vector4(0.29f, 0.08f, 0.42f, 0.32f), 0f, 6f);
            a.SetLayerBone("hair", "head", new Vector2(0.5f, 0.15f), new Vector4(0.24f, 0.06f, 0.52f, 0.44f), 12f, 5f);

            var idle = new LvnAnim
            {
                loop = true, duration = 2.8f,
                tracks = new List<LvnAnimTrack>
                {
                    new LvnAnimTrack { layer = "body", prop = "y", keys = K(new object[]{0f,0f}, new object[]{1.4f,0.015f}, new object[]{2.8f,0f}) },
                    new LvnAnimTrack { layer = "head", prop = "rotation", keys = K(new object[]{0f,-3f}, new object[]{1.4f,3f}, new object[]{2.8f,-3f}) },
                },
            };
            a.Play("base", idle);

            // walk a second of ticks so the spring integrates
            for (int f = 1; f <= 60; f++)
            {
                float tf = f / 60f;
                ActorAnimator.Clock = () => tf;
                a.Composite();
            }

            var headRot = imgs["head"].style.rotate.value.angle.value;
            var hairRot = imgs["hair"].style.rotate.value.angle.value;
            var hairTy = imgs["hair"].style.translate.value.y.value;
            Assert.AreNotEqual(0f, headRot, "head is turning");
            Assert.AreNotEqual(headRot, hairRot, "hair LAGS the head (spring), not welded to it");
            Assert.Greater(Mathf.Abs(hairTy), 1e-3f, "body bob carries the hair down the chain");
        }

        [Test]
        public void Spring_FirstTickPrimesWithoutKick()
        {
            var s = BoneSolver.SpringStep(new BoneSolver.SpringState(), new Vector2(0.9f, 0.1f), 0f, 12f, 6f, 1f / 60f);
            Assert.AreEqual(0f, s.Angle, 1e-5f, "an actor appearing on stage must not start swinging");
        }
    }
}
