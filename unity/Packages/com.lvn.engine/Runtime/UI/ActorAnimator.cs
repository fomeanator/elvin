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

        private sealed class Active { public LvnAnim anim; public float start; public Action onDone; }

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
        public void ClearLayers() { _layers.Clear(); _baseSprite.Clear(); }
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
            ResetTargets();
        }

        // One composite step over every active channel. Internal so tests can call
        // it with a controlled Clock instead of relying on the scheduler.
        internal void Composite()
        {
            float now = Clock();
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
                        float v = Sample(tr, t);
                        if (string.IsNullOrEmpty(tr.layer))
                        {
                            if (tr.prop == "screen_x") ssx = v;       // move the whole actor across the screen
                            else if (tr.prop == "screen_y") ssy = v;
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
                if (!anim.loop && elapsed >= dur) (done ??= new List<string>()).Add(kv.Key);
            }

            rig.ApplyTo(_rig);
            if (_slot != null)
            {
                _slot.style.left = Length.Percent((_baseX + ssx) * 100f);
                _slot.style.top = Length.Percent((_baseY + ssy) * 100f);
            }
            foreach (var pair in _layers)
            {
                var id = pair.Key;
                var img = pair.Value;
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
            if (_channels.Count == 0) StopAll();
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

        internal static float Sample(LvnAnimTrack tr, float t)
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
                    float u = t1 > t0 ? (t - t0) / (t1 - t0) : 0f;
                    u = Ease(tr.ease, Mathf.Clamp01(u));
                    return Mathf.Lerp(V(keys[i]), V(keys[i + 1]), u);
                }
            }
            return V(last);
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
