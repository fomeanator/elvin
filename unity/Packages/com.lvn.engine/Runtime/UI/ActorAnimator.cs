using System;
using System.Collections.Generic;
using System.Globalization;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace Lvn.UI
{
    /// <summary>
    /// Per-actor animation compositor. Runs several named animations at once on
    /// independent <em>channels</em> — e.g. <c>base</c> (idle bob, whole-actor
    /// transform), <c>blink</c> (eyes layer frame-swap), <c>talk</c> (mouth layer
    /// while speaking), <c>gesture</c> (one-shot). A single scheduler tick
    /// composites every active channel: null-layer tracks drive the rig wrapper's
    /// transform; layer tracks drive that layer's transform; <c>frame</c> tracks
    /// swap the layer's sprite. Pure sampling math is static (and unit-tested).
    /// </summary>
    internal sealed class ActorAnimator
    {
        private readonly VisualElement _rig;
        private VisualElement _slot;        // the placed slot — screen_x/y move it
        private float _baseX, _baseY;       // its placement (screen fractions)
        private readonly Dictionary<string, Image> _layers = new Dictionary<string, Image>();
        private readonly Dictionary<string, Sprite> _baseSprite = new Dictionary<string, Sprite>();
        private Dictionary<string, Dictionary<string, Sprite>> _frames; // layerId -> axisValue -> sprite
        private readonly Dictionary<string, Active> _channels = new Dictionary<string, Active>();
        private readonly Dictionary<string, Queue<LvnAnim>> _queue = new Dictionary<string, Queue<LvnAnim>>(); // mode=queue: pending steps per channel
        private IVisualElementScheduledItem _tick;

        private sealed class Active
        {
            public LvnAnim anim; public float start; public Action onDone;
            public float[] Arc; // arc-length table for a spline path pair (built lazily)
        }

        // Time source — overridable so tests can drive Composite() deterministically.
        internal static Func<float> Clock = () => Time.realtimeSinceStartup;

        public ActorAnimator(VisualElement rig) { _rig = rig; }

        /// <summary>The slot + its placement, so <c>screen_x</c>/<c>screen_y</c> tracks
        /// move the whole actor across the screen (added to this base).</summary>
        public void SetSlot(VisualElement slot, float baseX, float baseY) { _slot = slot; _baseX = baseX; _baseY = baseY; }

        /// <summary>Register a rendered layer image + its default sprite, so frame
        /// tracks can swap it and resets restore it.</summary>
        public void SetLayer(string id, Image img, Sprite baseSprite)
        {
            if (string.IsNullOrEmpty(id) || img == null) return;
            _layers[id] = img;
            _baseSprite[id] = baseSprite;
        }
        public void ClearLayers() { _layers.Clear(); _baseSprite.Clear(); _bones.Clear(); }

        // ── bones (paper-doll FK + springs) ───────────────────────────────────
        private sealed class BoneMeta
        {
            public string Parent;
            public Vector2 PivotBox;   // rest pivot in actor-box fractions
            public Vector4 Rect;       // layer rect in box fractions (full box when w/h ≤ 0)
            public float Spring, Damping;
            public BoneSolver.SpringState State;
        }
        private readonly Dictionary<string, BoneMeta> _bones = new Dictionary<string, BoneMeta>();
        private float _lastTick = -1f;

        /// <summary>Register a layer's bone: its parent joint, pivot (fractions of
        /// the layer's own rect) and optional spring. The layer's element then
        /// composes through the FK chain each tick; the tick keeps running while
        /// any bone has a live spring.</summary>
        public void SetLayerBone(string id, string parent, Vector2 pivotInRect, Vector4 rect, float spring, float damping)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (rect.z <= 0f || rect.w <= 0f) rect = new Vector4(0f, 0f, 1f, 1f);
            _bones[id] = new BoneMeta
            {
                Parent = parent,
                PivotBox = new Vector2(rect.x + pivotInRect.x * rect.z, rect.y + pivotInRect.y * rect.w),
                Rect = rect,
                Spring = spring,
                Damping = damping,
            };
            if (_layers.TryGetValue(id, out var img))
                img.style.transformOrigin = new TransformOrigin(
                    Length.Percent(pivotInRect.x * 100f), Length.Percent(pivotInRect.y * 100f));
            // springs animate even with no script channels — keep a tick alive
            if (spring > 0f && _tick == null) _tick = _rig.schedule.Execute(Composite).Every(16);
        }

        // Compose every bone layer's world pose from the animated locals; springs
        // run as a second pass so their swing carries children too.
        private Dictionary<string, BoneSolver.Pose> SolveBones(Dictionary<string, XForm> lx, float dt, Vector2 slotPos)
        {
            var bones = new List<BoneSolver.Bone>(_bones.Count);
            foreach (var kv in _bones)
            {
                var m = kv.Value;
                var l = lx != null && lx.TryGetValue(kv.Key, out var x) ? x : XForm.Identity;
                bones.Add(new BoneSolver.Bone
                {
                    Id = kv.Key, Parent = m.Parent, Pivot = m.PivotBox,
                    Tx = l.Tx * m.Rect.z, Ty = l.Ty * m.Rect.w, // own-size fractions → box
                    Angle = l.Rot, Sx = l.Scx, Sy = l.Scy,
                });
            }
            var poses = BoneSolver.Solve(bones);

            bool anySpring = false;
            for (int i = 0; i < bones.Count; i++)
            {
                var m = _bones[bones[i].Id];
                if (m.Spring <= 0f) continue;
                m.State = BoneSolver.SpringStep(m.State, poses[bones[i].Id].PivotWorld + slotPos, poses[bones[i].Id].Angle, m.Spring, m.Damping, dt);
                if (Mathf.Abs(m.State.Angle) > 0.01f || Mathf.Abs(m.State.Velocity) > 0.01f) anySpring = true;
                var b = bones[i]; b.Angle += m.State.Angle; bones[i] = b;
            }
            return anySpring ? BoneSolver.Solve(bones) : poses;
        }

        // Apply a solved pose to the layer's element: move its pivot to the
        // solved spot (percent of own size), spin/scale around it.
        private void ApplyPose(VisualElement el, BoneMeta m, BoneSolver.Pose p)
        {
            var d = p.PivotWorld - m.PivotBox;
            el.style.translate = new Translate(
                Length.Percent(d.x / m.Rect.z * 100f), Length.Percent(d.y / m.Rect.w * 100f), 0);
            el.style.rotate = new Rotate(new Angle(p.Angle, AngleUnit.Degree));
            el.style.scale = new Scale(new Vector2(p.Sx, p.Sy));
        }
        public void SetFrames(Dictionary<string, Dictionary<string, Sprite>> frames) { _frames = frames; }

        public bool Has(string channel) => _channels.ContainsKey(channel);
        public LvnAnim Current(string channel) => _channels.TryGetValue(channel, out var a) ? a.anim : null;

        public void Play(string channel, LvnAnim anim, Action onDone = null)
        {
            if (anim == null || anim.tracks == null || anim.tracks.Count == 0) { onDone?.Invoke(); return; }
            _channels[channel] = new Active { anim = anim, start = Clock(), onDone = onDone };
            if (_tick == null) _tick = _rig.schedule.Execute(Composite).Every(16);
        }

        /// <summary>Play after the current anim on this channel finishes (mode=queue).
        /// If the lane is free it plays now; queued steps run FIFO. A looping current
        /// anim never finishes, so don't queue behind one.</summary>
        public void PlayQueued(string channel, LvnAnim anim)
        {
            if (anim == null || anim.tracks == null || anim.tracks.Count == 0) return;
            if (!_channels.ContainsKey(channel)) { Play(channel, anim); return; }
            if (!_queue.TryGetValue(channel, out var q)) _queue[channel] = q = new Queue<LvnAnim>();
            q.Enqueue(anim);
            if (_tick == null) _tick = _rig.schedule.Execute(Composite).Every(16);
        }

        public void Stop(string channel)
        {
            if (_channels.Remove(channel) && _channels.Count == 0) StopAll();
        }

        /// <summary>Stop every script-driven lane (channels under "script:"),
        /// leaving engine lanes (base/blink/talk/gesture) running.</summary>
        public void StopScript()
        {
            List<string> rm = null;
            foreach (var k in _channels.Keys) if (k.StartsWith("script:")) (rm ??= new List<string>()).Add(k);
            foreach (var k in new List<string>(_queue.Keys)) if (k.StartsWith("script:")) _queue.Remove(k);
            if (rm == null) return;
            foreach (var k in rm) _channels.Remove(k);
            if (_channels.Count == 0) StopAll(); // else the next composite re-bakes from the survivors
        }

        /// <summary>Stop one lane by exact name or by the derived "script:&lt;target&gt;".</summary>
        public void StopTarget(string target)
        {
            _queue.Remove(target);
            _queue.Remove("script:" + target);
            bool removed = _channels.Remove(target);
            removed |= _channels.Remove("script:" + target);
            if (removed && _channels.Count == 0) StopAll();
        }

        public void StopAll()
        {
            _channels.Clear();
            _queue.Clear();
            _tick?.Pause();
            _tick = null;
            _lastTick = -1f; // don't let a paused span feed the springs as one huge dt
            ResetTargets();
        }

        // One composite step over every active channel. Internal so tests can call
        // it with a controlled Clock instead of relying on the scheduler.
        internal void Composite()
        {
            float now = Clock();
            float dt = _lastTick >= 0f ? Mathf.Clamp(now - _lastTick, 0f, 0.1f) : 0f;
            _lastTick = now;
            var rig = new XForm();
            float ssx = 0f, ssy = 0f; // screen-space offset for the slot
            Dictionary<string, XForm> lx = null;
            Dictionary<string, string> lf = null;
            List<string> done = null;

            foreach (var kv in _channels)
            {
                var act = kv.Value;
                var anim = act.anim;
                float dur = Mathf.Max(0.0001f, anim.duration);
                float elapsed = now - act.start;
                float t = anim.loop ? (anim.yoyo ? Mathf.PingPong(elapsed, dur) : Mathf.Repeat(elapsed, dur)) : Mathf.Min(elapsed, dur);

                // A spline path pair moves at constant speed: warp its sample time
                // through the arc-length table (other tracks keep wall time).
                LvnAnimTrack sx = null, sy = null;
                foreach (var tr in anim.tracks)
                {
                    if (tr == null || !string.IsNullOrEmpty(tr.layer) || tr.keys == null) continue;
                    if (tr.prop == "screen_x") sx = tr;
                    else if (tr.prop == "screen_y") sy = tr;
                }
                bool arcPath = sx != null && sy != null && sx.interp == "spline" && sy.interp == "spline";
                float pt = arcPath ? ArcTime(sx, sy, t, dur, ref act.Arc) : t;

                LvnAnimTrack orientX = null, pathY = null; // move … orient=true: face along the path
                foreach (var tr in anim.tracks)
                {
                    if (tr == null || tr.keys == null || tr.keys.Count == 0 || string.IsNullOrEmpty(tr.prop)) continue;
                    if (tr.prop == "frame")
                    {
                        if (string.IsNullOrEmpty(tr.layer)) continue;
                        (lf ??= new Dictionary<string, string>())[tr.layer] = SampleFrame(tr, t);
                    }
                    else
                    {
                        bool onPath = arcPath && (tr == sx || tr == sy);
                        float v = Sample(tr, onPath ? pt : t, easeless: onPath);
                        if (string.IsNullOrEmpty(tr.layer))
                        {
                            if (tr.prop == "screen_x") { ssx = v; if (tr.orient) orientX = tr; } // move the whole actor across the screen
                            else if (tr.prop == "screen_y") { ssy = v; pathY = tr; }
                            else rig.Apply(tr.prop, v);
                        }
                        else
                        {
                            lx ??= new Dictionary<string, XForm>();
                            if (!lx.TryGetValue(tr.layer, out var x)) lx[tr.layer] = x = new XForm();
                            x.Apply(tr.prop, v);
                        }
                    }
                }
                if (orientX != null && pathY != null)
                    rig.Apply("rotation", OrientAngle(orientX, pathY, arcPath ? pt : t, dur));
                if (!anim.loop && elapsed >= dur) (done ??= new List<string>()).Add(kv.Key);
            }

            rig.ApplyTo(_rig);
            if (_slot != null)
            {
                _slot.style.left = Length.Percent((_baseX + ssx) * 100f);
                _slot.style.top = Length.Percent((_baseY + ssy) * 100f);
            }
            // Bone layers compose through the FK chain (+ springs); the rest keep
            // their plain per-layer transforms.
            // The slot's screen position feeds the springs too, so dragging or a
            // `move` travel makes cloth/hair sway exactly like local motion does.
            var poses = _bones.Count > 0
                ? SolveBones(lx, dt, new Vector2(_baseX + ssx, _baseY + ssy)) : null;

            foreach (var pair in _layers)
            {
                var id = pair.Key;
                var img = pair.Value;
                if (poses != null && _bones.TryGetValue(id, out var bm) && poses.TryGetValue(id, out var pose))
                {
                    ApplyPose(img, bm, pose);
                    img.style.opacity = lx != null && lx.TryGetValue(id, out var xa) ? xa.Al : 1f;
                }
                else
                    (lx != null && lx.TryGetValue(id, out var x) ? x : XForm.Identity).ApplyTo(img);
                if (lf != null && lf.TryGetValue(id, out var frameVal))
                {
                    if (_frames != null && _frames.TryGetValue(id, out var map) && map.TryGetValue(frameVal, out var sp) && sp != null)
                        img.sprite = sp;
                }
                else if (_baseSprite.TryGetValue(id, out var bs) && bs != null)
                {
                    img.sprite = bs; // no active frame track → default frame
                }
            }

            if (done != null)
                foreach (var c in done)
                {
                    var cb = _channels.TryGetValue(c, out var a) ? a.onDone : null;
                    _channels.Remove(c);
                    cb?.Invoke();
                    // mode=queue: start the next step on this lane (keeps it alive)
                    if (_queue.TryGetValue(c, out var q) && q.Count > 0)
                        _channels[c] = new Active { anim = q.Dequeue(), start = now };
                    if (_queue.TryGetValue(c, out var q2) && q2.Count == 0) _queue.Remove(c);
                }
            // Springs keep swinging after their driving channel ends — let them
            // decay before the scheduler goes to sleep.
            if (_channels.Count == 0 && !AnySpringLive()) StopAll();
        }

        private bool AnySpringLive()
        {
            foreach (var kv in _bones)
            {
                var m = kv.Value;
                if (m.Spring > 0f && (Mathf.Abs(m.State.Angle) > 0.05f || Mathf.Abs(m.State.Velocity) > 0.5f))
                    return true;
            }
            return false;
        }

        private void ResetTargets()
        {
            XForm.Identity.ApplyTo(_rig);
            if (_slot != null)
            {
                _slot.style.left = Length.Percent(_baseX * 100f);
                _slot.style.top = Length.Percent(_baseY * 100f);
            }
            foreach (var pair in _layers)
            {
                XForm.Identity.ApplyTo(pair.Value);
                if (_baseSprite.TryGetValue(pair.Key, out var bs) && bs != null) pair.Value.sprite = bs;
            }
        }

        // ── pure sampling (static, unit-tested) ──────────────────────────────
        private static float F(object o) =>
            o == null ? 0f : Convert.ToSingle(o, CultureInfo.InvariantCulture);

        internal static float Sample(LvnAnimTrack tr, float t) => Sample(tr, t, easeless: false);

        internal static float Sample(LvnAnimTrack tr, float t, bool easeless)
        {
            var keys = tr.keys;
            float K0(object[] k) => k != null && k.Length > 0 ? F(k[0]) : 0f;
            float V(object[] k) => k != null && k.Length > 1 ? F(k[1]) : 0f;

            if (t <= K0(keys[0])) return V(keys[0]);
            var last = keys[keys.Count - 1];
            if (t >= K0(last)) return V(last);
            for (int i = 0; i < keys.Count - 1; i++)
            {
                float t0 = K0(keys[i]), t1 = K0(keys[i + 1]);
                if (t >= t0 && t <= t1)
                {
                    if (tr.interp == "step") return V(keys[i]); // hold until the next key
                    float u = t1 > t0 ? (t - t0) / (t1 - t0) : 0f;
                    u = easeless ? Mathf.Clamp01(u) : Ease(tr.ease, Mathf.Clamp01(u));
                    if (tr.interp == "spline")
                    {
                        // Catmull-Rom through the key values (ends clamped) — the
                        // curve passes through every key, unlike a fitted Bezier.
                        float p0 = V(keys[Mathf.Max(0, i - 1)]);
                        float p1 = V(keys[i]);
                        float p2 = V(keys[i + 1]);
                        float p3 = V(keys[Mathf.Min(keys.Count - 1, i + 2)]);
                        return 0.5f * ((2f * p1) + (-p0 + p2) * u
                            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u * u
                            + (-p0 + 3f * p1 - 3f * p2 + p3) * u * u * u);
                    }
                    return Mathf.Lerp(V(keys[i]), V(keys[i + 1]), u);
                }
            }
            return V(last);
        }

        // ── arc-length (constant speed along a spline path) ──────────────────
        // Per-axis Catmull-Rom makes speed vary with key spacing. For a spline
        // path pair we warp time so equal TIME covers equal DISTANCE, and the
        // easing curve drives progress along the length (the spec's model),
        // instead of easing each segment separately.

        /// <summary>Cumulative length of the raw (unesased) path at uniform time
        /// steps. Built once per playing anim; ~64 samples is visually exact.</summary>
        internal static float[] BuildArcTable(LvnAnimTrack x, LvnAnimTrack y, float dur, int samples = 64)
        {
            var cum = new float[samples + 1];
            float px = Sample(x, 0f, easeless: true), py = Sample(y, 0f, easeless: true);
            for (int i = 1; i <= samples; i++)
            {
                float t = dur * i / samples;
                float cx = Sample(x, t, easeless: true), cy = Sample(y, t, easeless: true);
                cum[i] = cum[i - 1] + Mathf.Sqrt((cx - px) * (cx - px) + (cy - py) * (cy - py));
                px = cx; py = cy;
            }
            return cum;
        }

        /// <summary>Map progress <paramref name="u01"/> (0..1 along the LENGTH)
        /// back to the raw sample time that reaches that distance.</summary>
        internal static float WarpProgress(float[] cum, float u01, float dur)
        {
            int n = cum.Length - 1;
            float total = cum[n];
            if (total <= 0f || dur <= 0f) return u01 * dur; // degenerate path → linear time
            float target = Mathf.Clamp01(u01) * total;
            int lo = 0, hi = n;
            while (lo < hi) { int mid = (lo + hi) / 2; if (cum[mid] < target) lo = mid + 1; else hi = mid; }
            if (lo == 0) return 0f;
            float seg = cum[lo] - cum[lo - 1];
            float frac = seg > 0f ? (target - cum[lo - 1]) / seg : 0f;
            return dur * (lo - 1 + frac) / n;
        }

        // The warped sample time for a spline path pair at wall time t: easing
        // drives progress along the length, the table converts it to raw time.
        internal static float ArcTime(LvnAnimTrack x, LvnAnimTrack y, float t, float dur, ref float[] cache)
        {
            cache ??= BuildArcTable(x, y, dur);
            float u = Ease(x.ease, Mathf.Clamp01(t / dur));
            return WarpProgress(cache, u, dur);
        }

        /// <summary>Tangent angle of a screen-space path pair at time <paramref name="t"/>,
        /// in degrees, y-down clockwise-positive (the UI Toolkit rotate convention;
        /// the Canvas path negates it). Central difference over the sampled curve, so
        /// it respects easing and spline interpolation.</summary>
        internal static float OrientAngle(LvnAnimTrack xTr, LvnAnimTrack yTr, float t, float dur)
        {
            float eps = Mathf.Max(0.0005f, dur / 200f);
            float t0 = Mathf.Max(0f, t - eps), t1 = Mathf.Min(dur, t + eps);
            float dx = Sample(xTr, t1) - Sample(xTr, t0);
            float dy = Sample(yTr, t1) - Sample(yTr, t0);
            if (dx == 0f && dy == 0f) return 0f;
            return Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
        }

        // Frame tracks step: the value of the last key whose time is <= t.
        internal static string SampleFrame(LvnAnimTrack tr, float t)
        {
            var keys = tr.keys;
            string cur = keys[0].Length > 1 ? keys[0][1]?.ToString() : null;
            for (int i = 0; i < keys.Count; i++)
            {
                float time = keys[i].Length > 0 ? F(keys[i][0]) : 0f;
                if (time <= t && keys[i].Length > 1) cur = keys[i][1]?.ToString();
                else if (time > t) break;
            }
            return cur;
        }

        private static float Ease(string name, float u)
        {
            switch (name)
            {
                case "inOutSine": return Easing.InOutSine(u);
                case "outCubic": return Easing.OutCubic(u);
                case "outBack": return Easing.OutBack(u);
                case "inBack": return Easing.InBack(u);
                default: return u;
            }
        }

        // Mutable transform accumulator applied to a rig or a layer element.
        // x/y translate by a fraction of the element's own size; scale is
        // non-uniform (scalex/scaley), or uniform via "scale".
        private sealed class XForm
        {
            public float Tx, Ty, Rot;
            public float Scx = 1f, Scy = 1f, Al = 1f;
            public static readonly XForm Identity = new XForm();

            public void Apply(string prop, float v)
            {
                switch (prop)
                {
                    case "x": Tx = v; break;
                    case "y": Ty = v; break;
                    case "scale": Scx = v; Scy = v; break;
                    case "scalex": Scx = v; break;
                    case "scaley": Scy = v; break;
                    case "rotation": Rot = v; break;
                    case "alpha": Al = v; break;
                }
            }

            public void ApplyTo(VisualElement el)
            {
                el.style.translate = new Translate(Length.Percent(Tx * 100f), Length.Percent(Ty * 100f), 0);
                el.style.scale = new Scale(new Vector2(Scx, Scy));
                el.style.rotate = new Rotate(new Angle(Rot, AngleUnit.Degree));
                el.style.opacity = Al;
            }
        }
    }
}
