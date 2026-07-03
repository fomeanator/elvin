using System.Collections.Generic;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// The paper-doll FK solver: parent chains carry children around pivots,
    /// springs swing from motion and settle. Pure math — no scene needed.
    public class BoneSolverTests
    {
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

        [Test]
        public void Spring_FirstTickPrimesWithoutKick()
        {
            var s = BoneSolver.SpringStep(new BoneSolver.SpringState(), new Vector2(0.9f, 0.1f), 0f, 12f, 6f, 1f / 60f);
            Assert.AreEqual(0f, s.Angle, 1e-5f, "an actor appearing on stage must not start swinging");
        }
    }
}
