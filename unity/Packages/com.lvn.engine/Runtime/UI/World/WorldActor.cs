using System;
using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.UI.World
{
    /// <summary>
    /// An actor rendered on a uGUI Canvas (RectTransform + Image layers), animated
    /// in the Update loop for smooth 60fps — the rendering path Liminal uses and
    /// the one that also hosts Spine (SkeletonGraphic). It plays the SAME
    /// <see cref="LvnAnim"/> data and channels (base/blink/talk/gesture) as the
    /// UITK path, reusing the static sampling in <see cref="ActorAnimator"/>; only
    /// the apply target differs (RectTransform/CanvasGroup/Image vs VisualElement).
    ///
    /// <para>Composition: this MonoBehaviour's RectTransform is the placed slot;
    /// a child <c>rig</c> RectTransform carries the animation transform and the
    /// Image layers, so animating the rig never fights the slot's placement.</para>
    /// </summary>
    public sealed class WorldActor : MonoBehaviour
    {
        private RectTransform _slot;
        private RectTransform _rig;
        private CanvasGroup _group;
        private readonly Dictionary<string, Image> _layers = new Dictionary<string, Image>();
        private readonly Dictionary<string, Sprite> _baseSprite = new Dictionary<string, Sprite>();
        private Dictionary<string, Dictionary<string, Sprite>> _frames;
        private readonly Dictionary<string, Active> _channels = new Dictionary<string, Active>();
        private readonly Dictionary<string, Queue<LvnAnim>> _queue = new Dictionary<string, Queue<LvnAnim>>(); // mode=queue pending steps
        private Vector2 _slotBase;

        /// <summary>Reference content size (canvas units) for screen_x/screen_y travel.</summary>
        public Vector2 ContentSize = new Vector2(1080f, 1920f);

        /// <summary>The placed slot RectTransform (this MonoBehaviour's own). The
        /// host positions it via <see cref="WorldPlacement"/>; animation only ever
        /// moves the child rig, so placement and animation never fight.</summary>
        public RectTransform Slot { get { EnsureRig(); return _slot; } }

        private sealed class Active
        {
            public LvnAnim anim; public float start; public Action onDone;
            public float[] Arc; // arc-length table for a spline path pair (built lazily)
        }

        private void Awake() => EnsureRig();

        private void EnsureRig()
        {
            if (_slot != null) return;
            _slot = (RectTransform)transform;
            _slotBase = _slot.anchoredPosition;
            var rigGo = new GameObject("rig", typeof(RectTransform), typeof(CanvasGroup));
            _rig = (RectTransform)rigGo.transform;
            _rig.SetParent(_slot, false);
            Stretch(_rig);
            _group = rigGo.GetComponent<CanvasGroup>();
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0f); // feet — match rotation/scale origin
        }

        /// <summary>Build (or rebuild) the layer Images from resolved sprites + ids.</summary>
        public void Configure(IReadOnlyList<Sprite> sprites, IReadOnlyList<string> layerIds, IReadOnlyList<Vector4> layerRects = null)
        {
            EnsureRig();
            for (int i = _rig.childCount - 1; i >= 0; i--) Destroy(_rig.GetChild(i).gameObject);
            _layers.Clear(); _baseSprite.Clear();
            if (sprites == null) return;
            for (int i = 0; i < sprites.Count; i++)
            {
                var sp = sprites[i];
                if (sp == null) continue;
                var go = new GameObject("layer" + i, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                var rt = (RectTransform)go.transform;
                rt.SetParent(_rig, false);
                // Partial overlay (rect w,h > 0) → anchored to its sub-rect of the box
                // (fractions, top-left origin → uGUI bottom-up anchors); else fill.
                var r = layerRects != null && i < layerRects.Count ? layerRects[i] : Vector4.zero;
                if (r.z > 0f && r.w > 0f)
                {
                    rt.anchorMin = new Vector2(r.x, 1f - (r.y + r.w));
                    rt.anchorMax = new Vector2(r.x + r.z, 1f - r.y);
                    rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                }
                else Stretch(rt);
                var img = go.GetComponent<Image>();
                img.sprite = sp;
                img.raycastTarget = false;
                img.preserveAspect = true;
                var lid = layerIds != null && i < layerIds.Count ? layerIds[i] : null;
                if (!string.IsNullOrEmpty(lid)) { _layers[lid] = img; _baseSprite[lid] = sp; }
            }
        }

        public void SetSlotBase(Vector2 anchored) { EnsureRig(); _slotBase = anchored; _slot.anchoredPosition = anchored; }
        public void SetFrames(Dictionary<string, Dictionary<string, Sprite>> frames) => _frames = frames;

        public bool Has(string channel) => _channels.ContainsKey(channel);
        public LvnAnim Current(string channel) => _channels.TryGetValue(channel, out var a) ? a.anim : null;

        public void Play(string channel, LvnAnim anim, Action onDone = null)
        {
            if (anim == null || anim.tracks == null || anim.tracks.Count == 0) { onDone?.Invoke(); return; }
            _channels[channel] = new Active { anim = anim, start = ActorAnimator.Clock(), onDone = onDone };
        }

        /// <summary>Play after the current anim on this channel finishes (mode=queue).
        /// Free lane → plays now; queued steps run FIFO. Don't queue behind a loop.</summary>
        public void PlayQueued(string channel, LvnAnim anim)
        {
            if (anim == null || anim.tracks == null || anim.tracks.Count == 0) return;
            if (!_channels.ContainsKey(channel)) { Play(channel, anim); return; }
            if (!_queue.TryGetValue(channel, out var q)) _queue[channel] = q = new Queue<LvnAnim>();
            q.Enqueue(anim);
        }
        public void Stop(string channel) { _channels.Remove(channel); _queue.Remove(channel); if (_channels.Count == 0) ResetTargets(); }
        public void StopAll() { _channels.Clear(); _queue.Clear(); ResetTargets(); }

        /// <summary>Stop every script-driven lane ("script:*"), leaving engine lanes.</summary>
        public void StopScript()
        {
            List<string> rm = null;
            foreach (var k in _channels.Keys) if (k.StartsWith("script:")) (rm ??= new List<string>()).Add(k);
            foreach (var k in new List<string>(_queue.Keys)) if (k.StartsWith("script:")) _queue.Remove(k);
            if (rm == null) return;
            foreach (var k in rm) _channels.Remove(k);
            if (_channels.Count == 0) ResetTargets(); // else the next tick re-bakes from the survivors
        }

        /// <summary>Stop one lane by exact name or by the derived "script:&lt;target&gt;".</summary>
        public void StopTarget(string target)
        {
            _queue.Remove(target);
            _queue.Remove("script:" + target);
            bool r = _channels.Remove(target);
            r |= _channels.Remove("script:" + target);
            if (r && _channels.Count == 0) ResetTargets();
        }

        public void EnsureIdle(string id, LvnAnim idle) { if (idle != null && !ReferenceEquals(Current("base"), idle)) Play("base", idle); }
        public void EnsureBlink(string id, LvnAnim blink) { if (blink != null && !ReferenceEquals(Current("blink"), blink)) Play("blink", blink); }
        public void Talk(LvnAnim talk, bool on) { if (on) { if (talk != null && !ReferenceEquals(Current("talk"), talk)) Play("talk", talk); } else Stop("talk"); }
        public void PlayGesture(LvnAnim anim, LvnAnim idle)
        {
            if (anim == null) return;
            if (anim.loop) { Play("gesture", anim); return; }
            Stop("base");
            Play("gesture", anim, onDone: () => { if (idle != null) EnsureIdle(null, idle); });
        }

        private void Update()
        {
            if (_channels.Count > 0) Tick(ActorAnimator.Clock());
        }

        // One composite step — internal so tests can drive it with ActorAnimator.Clock.
        internal void Tick(float now)
        {
            if (_rig == null) EnsureRig();
            float tx = 0f, ty = 0f, scx = 1f, scy = 1f, rot = 0f, al = 1f, sx = 0f, sy = 0f;
            var layerX = new Dictionary<string, float[]>(); // id -> {tx,ty,scx,scy,rot,al}
            Dictionary<string, string> layerFrame = null;
            List<string> done = null;

            foreach (var kv in _channels)
            {
                var act = kv.Value;
                var anim = act.anim;
                float dur = Mathf.Max(0.0001f, anim.duration);
                float elapsed = now - act.start;
                float t = anim.loop ? (anim.yoyo ? Mathf.PingPong(elapsed, dur) : Mathf.Repeat(elapsed, dur)) : Mathf.Min(elapsed, dur);

                // A spline path pair moves at constant speed (see ActorAnimator).
                LvnAnimTrack psx = null, psy = null;
                foreach (var tr in anim.tracks)
                {
                    if (tr == null || !string.IsNullOrEmpty(tr.layer) || tr.keys == null) continue;
                    if (tr.prop == "screen_x") psx = tr;
                    else if (tr.prop == "screen_y") psy = tr;
                }
                bool arcPath = psx != null && psy != null && psx.interp == "spline" && psy.interp == "spline";
                float pt = arcPath ? ActorAnimator.ArcTime(psx, psy, t, dur, ref act.Arc) : t;

                LvnAnimTrack orientX = null, pathY = null; // move … orient=true: face along the path
                foreach (var tr in anim.tracks)
                {
                    if (tr == null || tr.keys == null || tr.keys.Count == 0 || string.IsNullOrEmpty(tr.prop)) continue;
                    if (tr.prop == "frame")
                    {
                        if (!string.IsNullOrEmpty(tr.layer))
                            (layerFrame ??= new Dictionary<string, string>())[tr.layer] = ActorAnimator.SampleFrame(tr, t);
                        continue;
                    }
                    bool onPath = arcPath && (tr == psx || tr == psy);
                    float v = ActorAnimator.Sample(tr, onPath ? pt : t, easeless: onPath);
                    if (string.IsNullOrEmpty(tr.layer))
                    {
                        switch (tr.prop)
                        {
                            case "x": tx = v; break;
                            case "y": ty = v; break;
                            case "screen_x": sx = v; if (tr.orient) orientX = tr; break;
                            case "screen_y": sy = v; pathY = tr; break;
                            case "scale": scx = v; scy = v; break;
                            case "scalex": scx = v; break;
                            case "scaley": scy = v; break;
                            case "rotation": rot = v; break;
                            case "alpha": al = v; break;
                        }
                    }
                    else
                    {
                        if (!layerX.TryGetValue(tr.layer, out var a)) layerX[tr.layer] = a = new[] { 0f, 0f, 1f, 1f, 0f, 1f };
                        switch (tr.prop)
                        {
                            case "x": a[0] = v; break;
                            case "y": a[1] = v; break;
                            case "scale": a[2] = v; a[3] = v; break;
                            case "scalex": a[2] = v; break;
                            case "scaley": a[3] = v; break;
                            case "rotation": a[4] = v; break;
                            case "alpha": a[5] = v; break;
                        }
                    }
                }
                // OrientAngle is y-down clockwise-positive; Canvas euler z is
                // counter-clockwise-positive — negate.
                if (orientX != null && pathY != null)
                    rot = -ActorAnimator.OrientAngle(orientX, pathY, arcPath ? pt : t, dur);
                if (!anim.loop && elapsed >= dur) (done ??= new List<string>()).Add(kv.Key);
            }

            ApplyRig(_rig, _group, tx, ty, scx, scy, rot, al);
            _slot.anchoredPosition = _slotBase + new Vector2(sx * ContentSize.x, -sy * ContentSize.y);

            foreach (var pair in _layers)
            {
                var img = pair.Value;
                var lrt = (RectTransform)img.transform;
                if (layerX.TryGetValue(pair.Key, out var a)) ApplyRig(lrt, null, a[0], a[1], a[2], a[3], a[4], a[5], img);
                else ApplyRig(lrt, null, 0, 0, 1, 1, 0, 1, img);
                if (layerFrame != null && layerFrame.TryGetValue(pair.Key, out var fv))
                {
                    if (_frames != null && _frames.TryGetValue(pair.Key, out var map) && map.TryGetValue(fv, out var sp) && sp != null)
                        img.sprite = sp;
                }
                else if (_baseSprite.TryGetValue(pair.Key, out var bs) && bs != null) img.sprite = bs;
            }

            if (done != null)
                foreach (var c in done)
                {
                    var cb = _channels.TryGetValue(c, out var x) ? x.onDone : null;
                    _channels.Remove(c);
                    cb?.Invoke();
                    // mode=queue: start the next queued step on this lane
                    if (_queue.TryGetValue(c, out var q) && q.Count > 0)
                        _channels[c] = new Active { anim = q.Dequeue(), start = now };
                    if (_queue.TryGetValue(c, out var q2) && q2.Count == 0) _queue.Remove(c);
                }
            if (_channels.Count == 0) ResetTargets();
        }

        private void ApplyRig(RectTransform rt, CanvasGroup group, float tx, float ty, float scx, float scy, float rot, float al, Image img = null)
        {
            var size = rt.rect.size;
            rt.anchoredPosition = new Vector2(tx * size.x, -ty * size.y);
            rt.localScale = new Vector3(scx, scy, 1f);
            rt.localEulerAngles = new Vector3(0f, 0f, rot);
            if (group != null) group.alpha = al;
            else if (img != null) { var c = img.color; c.a = al; img.color = c; }
        }

        private void ResetTargets()
        {
            if (_rig == null) return;
            ApplyRig(_rig, _group, 0, 0, 1, 1, 0, 1);
            _slot.anchoredPosition = _slotBase;
            foreach (var pair in _layers)
            {
                ApplyRig((RectTransform)pair.Value.transform, null, 0, 0, 1, 1, 0, 1, pair.Value);
                if (_baseSprite.TryGetValue(pair.Key, out var bs) && bs != null) pair.Value.sprite = bs;
            }
        }
    }
}
