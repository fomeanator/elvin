using System.Collections.Generic;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// The paper-doll FK solver: composes per-layer LOCAL transforms (from
    /// animation tracks and springs) down a parent chain into per-layer WORLD
    /// transforms, all in the actor-box space (fractions 0..1, y down, degrees
    /// clockwise — the UI Toolkit convention; the Canvas path flips signs).
    ///
    /// Elements stay FLAT in the visual tree (draw order = layer list order —
    /// a back arm can be the body's child yet draw behind it); only the
    /// transforms compose. Rotation composes additively and scale
    /// multiplicatively per axis — exact for translate+rotate+uniform scale,
    /// the standard paper-doll approximation for non-uniform scale.
    /// Pure and unit-tested; both renderers consume its output.
    /// </summary>
    internal static class BoneSolver
    {
        /// <summary>One layer's solve input. Rect/pivot are in box fractions;
        /// Tx/Ty are the LOCAL translation in box fractions; Angle in degrees.</summary>
        public struct Bone
        {
            public string Id;
            public string Parent;      // null/empty = root
            public Vector2 Pivot;      // in box space (already resolved from the layer rect)
            public float Tx, Ty;       // local translate, box fractions
            public float Angle;        // local rotation, degrees
            public float Sx, Sy;       // local scale
        }

        /// <summary>One layer's solved world transform: where its pivot ended
        /// up (box fractions), the accumulated angle and per-axis scale.</summary>
        public struct Pose
        {
            public Vector2 PivotWorld;
            public float Angle;
            public float Sx, Sy;
        }

        /// <summary>Solve every bone. Bones may arrive in any order; cycles and
        /// unknown parents degrade to root (never throw — content is data).</summary>
        public static Dictionary<string, Pose> Solve(IReadOnlyList<Bone> bones)
        {
            var byId = new Dictionary<string, int>();
            for (int i = 0; i < bones.Count; i++)
                if (!string.IsNullOrEmpty(bones[i].Id) && !byId.ContainsKey(bones[i].Id))
                    byId[bones[i].Id] = i;

            var solved = new Dictionary<string, Pose>();
            var visiting = new HashSet<string>();
            foreach (var b in bones)
                if (!string.IsNullOrEmpty(b.Id))
                    SolveOne(b.Id, bones, byId, solved, visiting);
            return solved;
        }

        private static Pose SolveOne(string id, IReadOnlyList<Bone> bones,
            Dictionary<string, int> byId, Dictionary<string, Pose> solved, HashSet<string> visiting)
        {
            if (solved.TryGetValue(id, out var done)) return done;
            var b = bones[byId[id]];

            Pose pose;
            if (string.IsNullOrEmpty(b.Parent) || !byId.ContainsKey(b.Parent) || !visiting.Add(id))
            {
                // root (or a broken/cyclic parent — degrade to root)
                pose = new Pose
                {
                    PivotWorld = b.Pivot + new Vector2(b.Tx, b.Ty),
                    Angle = b.Angle,
                    Sx = b.Sx, Sy = b.Sy,
                };
            }
            else
            {
                var p = SolveOne(b.Parent, bones, byId, solved, visiting);
                visiting.Remove(id);
                var parent = bones[byId[b.Parent]];
                // Where the parent's motion carries THIS bone's pivot: rotate/
                // scale the rest offset (pivot - parentPivot) by the parent's
                // world transform, from the parent's world pivot.
                var rest = b.Pivot + new Vector2(b.Tx, b.Ty) - parent.Pivot;
                rest = new Vector2(rest.x * p.Sx, rest.y * p.Sy);
                float rad = p.Angle * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
                // y-down clockwise-positive rotation
                var carried = new Vector2(rest.x * cos - rest.y * sin, rest.x * sin + rest.y * cos);
                pose = new Pose
                {
                    PivotWorld = p.PivotWorld + carried,
                    Angle = p.Angle + b.Angle,
                    Sx = p.Sx * b.Sx, Sy = p.Sy * b.Sy,
                };
            }
            solved[id] = pose;
            return pose;
        }

        // ── spring joints (secondary motion) ─────────────────────────────────
        // A sprung layer swings from its pivot's MOTION and settles by itself:
        // a damped angular spring kicked by the pivot's horizontal travel
        // (hair/tails/cloth read as swinging opposite to the move, then easing
        // back). Pure integration step — the animator owns the state.

        /// <summary>How much one box-width of pivot travel kicks the spring, in
        /// degrees. The feel constant of the whole system.</summary>
        public const float SwingPerUnit = 240f;

        /// <summary>How much of the parent's rotation the sprung layer initially
        /// ABSORBS (inertia — hair keeps its world orientation for a beat, then
        /// the spring pulls it along). The VRM spring-bone behaviour.</summary>
        public const float RotationInertia = 0.8f;

        public struct SpringState { public float Angle, Velocity, LastRigidAngle; public Vector2 LastPivot; public bool Primed; }

        /// <summary>Advance a spring joint one tick. <paramref name="pivotWorld"/> /
        /// <paramref name="rigidAngle"/> are the RIGID solve's pivot position and
        /// accumulated angle this tick; the returned state's Angle is added to the
        /// layer's (and its children's) rotation.</summary>
        public static SpringState SpringStep(SpringState s, Vector2 pivotWorld, float rigidAngle,
            float stiffness, float damping, float dt)
        {
            if (dt <= 0f) return s;
            if (!s.Primed) { s.Primed = true; s.LastPivot = pivotWorld; s.LastRigidAngle = rigidAngle; return s; }
            var delta = pivotWorld - s.LastPivot;
            float dAng = rigidAngle - s.LastRigidAngle;
            s.LastPivot = pivotWorld;
            s.LastRigidAngle = rigidAngle;
            // Inertia: absorb part of the parent's turn instantly (dt-independent),
            // so the layer lags and the spring then pulls it back into line.
            s.Angle -= dAng * RotationInertia;
            s.Velocity += (-stiffness * s.Angle) * dt - damping * s.Velocity * dt
                          - delta.x * SwingPerUnit; // travel kicks the swing opposite the motion
            s.Angle += s.Velocity * dt;
            return s;
        }
    }
}
