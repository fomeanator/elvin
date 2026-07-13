using System;
using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace Lvn.UI
{
    public enum TransitionType
    {
        None,
        Fade,
        SlideLeft,
        SlideRight,
        Pop,
    }

    /// <summary>
    /// Where to put a stage object, all in screen fractions so a script controls
    /// it without knowing the resolution: the object's <see cref="AnchorX"/>/
    /// <see cref="AnchorY"/> point (0..1 of the object) is placed at
    /// <see cref="X"/>/<see cref="Y"/> (0..1 of the screen), sized by
    /// <see cref="Width"/>/<see cref="Height"/>, ordered by <see cref="Z"/>, with
    /// optional <see cref="Flip"/>, <see cref="Rotation"/> and <see cref="Opacity"/>.
    /// Defaults give the classic standing character: bottom-centre anchored.
    /// </summary>
    public struct Placement
    {
        // Standard VN framing defaults (screen fractions): a large figure, bottom-
        // anchored (feet at the screen edge). ~1.5× the classic 0.46/0.62. A per-op
        // width=/height= overrides; ui.stage.actor_scale multiplies these.
        public const float DefaultWidth = 0.69f;
        public const float DefaultHeight = 0.93f;

        public bool Show;
        /// <summary>Lock the box to this width/height ratio (from the entity's
        /// <c>aspect</c>): the placed Width/Height become maximums and the box
        /// shrinks to match — required for layered/boned art registration.</summary>
        public float? BoxAspect;
        public float X, Y;          // screen position of the anchor point (0..1)
        public float? Width, Height; // size as a fraction of the screen (0..1)
        public float AnchorX, AnchorY;
        public int? Z;
        public bool Flip;
        public float Rotation;       // degrees
        public float Opacity;
        public float HoverOpacity;
        public TransitionType EnterTransition;
        public TransitionType ExitTransition;
        public float TransitionDuration;

        public static Placement Standing(float x) => new Placement
        {
            Show = true, X = x, Y = 1f, AnchorX = 0.5f, AnchorY = 1f, Opacity = 1f,
        };
    }

    /// <summary>
    /// The object layer (z-order 1): every actor or prop is a slot placed by a
    /// <see cref="Placement"/> and drawn as a bottom-to-top stack of sprite
    /// layers. Characters are just objects that also dim when not speaking — the
    /// same `Apply` puts <em>any</em> sprite on screen from a script.
    /// </summary>
    public sealed class ActorLayer : VisualElement
    {
        private readonly Dictionary<string, VisualElement> _slots = new Dictionary<string, VisualElement>();
        private readonly Dictionary<string, VisualElement> _rigs = new Dictionary<string, VisualElement>(); // animation wrapper per slot
        private readonly Dictionary<string, ActorAnimator> _animators = new Dictionary<string, ActorAnimator>();
        private readonly Dictionary<VisualElement, int> _z = new Dictionary<VisualElement, int>();
        private readonly Dictionary<string, Action> _onClick = new Dictionary<string, Action>();
        private readonly Dictionary<string, float> _hoverOpacity = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _baseOpacity = new Dictionary<string, float>();
        private int _nextZ;

        public ActorLayer()
        {
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.top = 0;
            style.bottom = 0;
            pickingMode = PickingMode.Ignore;
        }

        /// <summary>Place / update / hide an object as a stack of layer sprites.
        /// A null/empty list leaves the current art unchanged. When
        /// <paramref name="onClick"/> is set the object becomes a tappable hotspot
        /// (and swallows the tap so it doesn't also advance the dialogue).</summary>
        public void Apply(string id, IReadOnlyList<Sprite> layers, Placement p, Action onClick = null,
            IReadOnlyList<string> layerIds = null, IReadOnlyList<Vector4> layerRects = null,
            IReadOnlyList<SpriteCatalog.ResolvedLayer> layerDefs = null)
        {
            if (string.IsNullOrEmpty(id)) return;

            if (!_slots.TryGetValue(id, out var slot))
            {
                slot = new VisualElement { name = "vn-obj-" + id, pickingMode = PickingMode.Ignore };
                slot.style.position = Position.Absolute;
                var capturedId = id;
                slot.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (_onClick.TryGetValue(capturedId, out var cb) && cb != null)
                    {
                        cb();
                        evt.StopPropagation();
                    }
                });
                slot.RegisterCallback<MouseEnterEvent>(_ =>
                {
                    if (_hoverOpacity.TryGetValue(capturedId, out var hover))
                        slot.style.opacity = hover;
                });
                slot.RegisterCallback<MouseLeaveEvent>(_ =>
                {
                    if (_baseOpacity.TryGetValue(capturedId, out var baseOp))
                        slot.style.opacity = baseOp;
                });
                // Aspect-locked boxes re-fit whenever layout hands us a new size.
                slot.RegisterCallback<GeometryChangedEvent>(_ => FitAspect(capturedId));
                Add(slot);
                _slots[id] = slot;
                _z[slot] = _nextZ++;
            }

            _onClick[id] = onClick;
            _hoverOpacity[id] = p.HoverOpacity;
            _baseOpacity[id] = p.Opacity;
            _aspect[id] = p.BoxAspect ?? 0f;
            // Only hotspots are pickable; everything else lets taps fall through
            // to the stage's tap-to-advance.
            slot.pickingMode = onClick != null ? PickingMode.Position : PickingMode.Ignore;

            if (layers != null && layers.Count > 0)
            {
                var rig = EnsureRig(slot, id);
                rig.Clear();
                var animator = AnimatorFor(id);
                animator?.ClearLayers();
                animator?.SetSlot(slot, p.X, p.Y); // for screen_x/screen_y travel
                // A layer is a bone when it has bone data OR when someone attaches
                // to it (a plain root like "body" must still exist in the chain).
                HashSet<string> boneParents = null;
                if (layerDefs != null)
                    foreach (var d0 in layerDefs)
                        if (!string.IsNullOrEmpty(d0.Parent))
                            (boneParents ??= new HashSet<string>()).Add(d0.Parent);
                for (int i = 0; i < layers.Count; i++)
                {
                    var sprite = layers[i];
                    if (sprite == null) continue;
                    var img = new Image { sprite = sprite, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                    img.style.position = Position.Absolute;
                    // A partial overlay (rect with w,h > 0) is placed at its sub-rect;
                    // otherwise the layer fills the whole actor box (the default).
                    var r = layerRects != null && i < layerRects.Count ? layerRects[i] : Vector4.zero;
                    if (r.z > 0f && r.w > 0f)
                    {
                        img.style.left = Length.Percent(r.x * 100f);
                        img.style.top = Length.Percent(r.y * 100f);
                        img.style.width = Length.Percent(r.z * 100f);
                        img.style.height = Length.Percent(r.w * 100f);
                    }
                    else
                    {
                        img.style.left = 0; img.style.right = 0; img.style.top = 0; img.style.bottom = 0;
                    }
                    rig.Add(img);
                    // register a named layer so blink / lip-sync can target it
                    var lid = layerIds != null && i < layerIds.Count ? layerIds[i] : null;
                    if (!string.IsNullOrEmpty(lid))
                    {
                        animator?.SetLayer(lid, img, sprite);
                        // bone metadata: parent joint / pivot / spring (paper-doll FK)
                        if (layerDefs != null && i < layerDefs.Count)
                        {
                            var d = layerDefs[i];
                            if (!string.IsNullOrEmpty(d.Parent) || d.Spring > 0f
                                || (boneParents != null && boneParents.Contains(lid)))
                                animator?.SetLayerBone(lid, d.Parent, new Vector2(d.Px, d.Py), r, d.Spring, d.Damping);
                        }
                    }
                }
            }

            // Default actor size = the standard VN framing (Placement.Default*): a large,
            // bottom-anchored figure. VnStage pre-fills Width/Height from the theme scale,
            // so these ?? fallbacks only fire on a raw placement.
            slot.style.width = Length.Percent((p.Width ?? Placement.DefaultWidth) * 100f);
            slot.style.height = Length.Percent((p.Height ?? Placement.DefaultHeight) * 100f);
            slot.style.left = Length.Percent(p.X * 100f);
            slot.style.top = Length.Percent(p.Y * 100f);
            // Translate so the object's own anchor point lands on (X, Y); UITK
            // percent translate is relative to the element's own size.
            slot.style.translate = new Translate(Length.Percent(-p.AnchorX * 100f), Length.Percent(-p.AnchorY * 100f), 0);
            slot.style.scale = new Scale(new Vector2(p.Flip ? -1f : 1f, 1f));
            slot.style.rotate = new Rotate(new Angle(p.Rotation, AngleUnit.Degree));
            slot.style.opacity = p.Opacity;

            // A departing object with an exit animation stays visible until the
            // animation finishes (which then hides it). Without an exit animation,
            // apply the requested visibility immediately.
            if (!p.Show) StopAnims(id); // no looping idle on a hidden actor
            bool exitWithAnim = !p.Show && p.ExitTransition != TransitionType.None;
            if (!exitWithAnim)
                slot.style.display = p.Show ? DisplayStyle.Flex : DisplayStyle.None;

            if (p.Show && p.EnterTransition != TransitionType.None)
                PlayTransition(slot, p.EnterTransition, p.TransitionDuration, p, exiting: false);
            else if (exitWithAnim)
                PlayTransition(slot, p.ExitTransition, p.TransitionDuration, p, exiting: true);

            if (p.Z.HasValue)
            {
                _z[slot] = p.Z.Value;
                Sort((a, b) => ZOf(a).CompareTo(ZOf(b)));
            }

            // An emotion/outfit swap rebuilt the layer images — re-tint them so a
            // dimmed actor doesn't pop back to full brightness mid-line.
            if (_focusK.TryGetValue(id, out var focus) && focus < 1f)
                SetFocus(id, slot, focus);
        }

        // The animation wrapper between the slot (placement/anchor/flip) and the
        // layer sprites, so animating it never fights the slot's positioning.
        // Layered/boned art must keep its authored proportions on every screen:
        // the placed percent box is a MAXIMUM, the real box shrinks to the
        // entity's aspect (see LvnSpriteEntity.aspect). Percent-sized boxes
        // (aspect 0) are untouched.
        private readonly Dictionary<string, float> _aspect = new Dictionary<string, float>();

        private void FitAspect(string id)
        {
            if (!_aspect.TryGetValue(id, out var a) || a <= 0f) return;
            if (!_slots.TryGetValue(id, out var slot)) return;
            var r = slot.layout;
            if (float.IsNaN(r.width) || float.IsNaN(r.height) || r.height <= 0f) return;
            float want = r.height * a;
            if (Mathf.Abs(r.width - want) < 0.5f) return; // already fitted
            if (want <= r.width + 0.5f) slot.style.width = want;
            else slot.style.height = r.width / a;
        }

        /// <summary>The object's on-screen rect, normalized 0..1 with a top-left
        /// origin (transforms included) — drop-target hit-testing and the drag
        /// pipeline use it. Null when the object/layout isn't ready.</summary>
        public Rect? ScreenRect(string id)
        {
            if (string.IsNullOrEmpty(id) || !_slots.TryGetValue(id, out var slot)) return null;
            var mine = worldBound;
            if (float.IsNaN(mine.width) || mine.width <= 0f || mine.height <= 0f) return null;
            var wb = slot.worldBound;
            if (float.IsNaN(wb.width) || wb.width <= 0f) return null;
            return new Rect((wb.x - mine.x) / mine.width, (wb.y - mine.y) / mine.height,
                wb.width / mine.width, wb.height / mine.height);
        }

        private VisualElement EnsureRig(VisualElement slot, string id)
        {
            if (_rigs.TryGetValue(id, out var rig) && rig.parent == slot) return rig;
            rig = new VisualElement { name = "vn-rig-" + id, pickingMode = PickingMode.Ignore };
            rig.style.position = Position.Absolute;
            rig.style.left = 0; rig.style.right = 0; rig.style.top = 0; rig.style.bottom = 0;
            rig.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(100), 0); // feet
            slot.Add(rig);
            _rigs[id] = rig;
            return rig;
        }

        private ActorAnimator AnimatorFor(string id)
        {
            if (_animators.TryGetValue(id, out var a)) return a;
            if (!_rigs.TryGetValue(id, out var rig)) return null;
            a = new ActorAnimator(rig);
            _animators[id] = a;
            return a;
        }

        /// <summary>Preloaded frame sprites for blink/lip-sync: layerId → axisValue → sprite.</summary>
        public void SetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames)
        {
            AnimatorFor(id)?.SetFrames(frames);
        }

        /// <summary>Start (or keep) the looping idle on the <c>base</c> channel
        /// (whole-actor bob). No-op if already playing, so repeated <c>actor</c>
        /// commands don't restart it.</summary>
        public void EnsureIdle(string id, LvnAnim idle)
        {
            var a = AnimatorFor(id);
            if (a == null || idle == null) return;
            if (!ReferenceEquals(a.Current("base"), idle)) a.Play("base", idle);
        }

        /// <summary>Looping blink (or any auto layer anim) on its own channel — runs
        /// alongside idle/talk since it targets a different layer.</summary>
        public void EnsureBlink(string id, LvnAnim blink)
        {
            var a = AnimatorFor(id);
            if (a == null || blink == null) return;
            if (!ReferenceEquals(a.Current("blink"), blink)) a.Play("blink", blink);
        }

        /// <summary>Toggle the talk (lip-sync) loop while an actor speaks.</summary>
        public void Talk(string id, LvnAnim talk, bool on)
        {
            var a = AnimatorFor(id);
            if (a == null) return;
            if (on) { if (talk != null && !ReferenceEquals(a.Current("talk"), talk)) a.Play("talk", talk); }
            else a.Stop("talk");
        }

        /// <summary>Play a one-shot gesture on the rig; pause idle while it runs and
        /// resume it after (gesture and idle share the actor transform).</summary>
        public void PlayGesture(string id, LvnAnim anim, LvnAnim idle)
        {
            var a = AnimatorFor(id);
            if (a == null || anim == null) return;
            if (anim.loop) { a.Play("gesture", anim); return; }
            a.Stop("base");
            a.Play("gesture", anim, onDone: () => { if (idle != null) EnsureIdle(id, idle); });
        }

        /// <summary>Play a script-driven animation on an arbitrary channel (e.g.
        /// <c>script</c>) — lets .lvns <c>anim</c>/<c>move</c> tween any prop/layer
        /// alongside the built-in idle/blink/talk channels (they composite).</summary>
        public void PlayAnim(string id, string channel, LvnAnim anim)
        {
            if (string.IsNullOrEmpty(channel) || anim == null) return;
            AnimatorFor(id)?.Play(channel, anim);
        }

        /// <summary>mode=queue: play after the current anim on this channel finishes.</summary>
        public void PlayAnimQueued(string id, string channel, LvnAnim anim)
        {
            if (string.IsNullOrEmpty(channel) || anim == null) return;
            AnimatorFor(id)?.PlayQueued(channel, anim);
        }

        /// <summary>Stop script animation: target "all"/null = every script lane,
        /// else a specific channel or the derived "script:&lt;target&gt;".</summary>
        public void StopAnim(string id, string target)
        {
            if (!_animators.TryGetValue(id, out var a)) return;
            if (string.IsNullOrEmpty(target) || target == "all") a.StopScript();
            else a.StopTarget(target);
        }

        /// <summary>Stop every animation on an actor and reset its transforms.</summary>
        public void StopAnims(string id)
        {
            if (_animators.TryGetValue(id, out var a)) a.StopAll();
        }

        // Focus dim as a COLOR, not opacity: fading a layered actor per-layer lets
        // a translucent clothes layer reveal the body layer underneath (an
        // accidental x-ray). Tinting every layer toward black dims the composite
        // while its alpha stays intact. Remembered per id so a re-Apply (an
        // emotion/outfit swap rebuilds the layer images) keeps the current focus.
        private readonly Dictionary<string, float> _focusK = new Dictionary<string, float>();

        private void SetFocus(string id, VisualElement slot, float k)
        {
            _focusK[id] = k;
            var tint = new Color(k, k, k, 1f);
            slot.Query<Image>().ForEach(img => img.tintColor = tint);
        }

        /// <summary>Full brightness for the speaker, dim for everyone else (null = undim all).</summary>
        public void SetSpeaker(string id)
        {
            foreach (var kv in _slots)
                SetFocus(kv.Key, kv.Value, id == null || kv.Key == id ? 1f : 0.55f);
        }

        // Loose name key for speaker↔slot matching: lower-case, letters/digits only.
        // A slot id ("Нина_Павловна") and a say's who ("Нина Павловна") fold to the
        // same key, so the classic "speaker bright, rest dimmed" works without the
        // script and the stage having to agree on an exact id spelling.
        private static string NameKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Classic VN focus: the character who is speaking goes full opacity,
        /// everyone else present dims. Matches the speaker to a slot by loose name key.
        /// When the speaker isn't on stage (narration / off-screen voice) the current
        /// dim is left as-is, so prose about the scene doesn't make everyone flicker.</summary>
        public void HighlightSpeaker(string who)
        {
            var target = NameKey(who);
            if (target == "") return;
            bool present = false;
            foreach (var kv in _slots)
                if (NameKey(kv.Key) == target) { present = true; break; }
            if (!present) return; // speaker has no sprite — keep the current focus
            foreach (var kv in _slots)
                SetFocus(kv.Key, kv.Value, NameKey(kv.Key) == target ? 1f : 0.55f);
        }

        public void RemoveAll()
        {
            foreach (var a in _animators.Values) a.StopAll();
            _animators.Clear();
            _rigs.Clear();
            Clear();
            _slots.Clear();
            _z.Clear();
            _onClick.Clear();
            _hoverOpacity.Clear();
            _baseOpacity.Clear();
            _focusK.Clear();
            _nextZ = 0;
        }

        private int ZOf(VisualElement e) => _z.TryGetValue(e, out var z) ? z : 0;

        // Plays an enter (appear) or exit (disappear) transition. The animation
        // always runs 0→1 and lerps the actual property between the right
        // from/to for the direction; an exit hides the slot on completion.
        private void PlayTransition(VisualElement slot, TransitionType type, float duration, Placement p, bool exiting)
        {
            if (duration <= 0f) duration = 0.3f;
            int ms = Mathf.Max(1, Mathf.RoundToInt(duration * 1000f));
            float x = p.X * 100f;

            switch (type)
            {
                case TransitionType.Fade:
                {
                    float from = exiting ? p.Opacity : 0f, to = exiting ? 0f : p.Opacity;
                    slot.style.opacity = from;
                    Finish(slot.experimental.animation
                        .Start(0f, 1f, ms, (e, t) => e.style.opacity = Mathf.Lerp(from, to, t))
                        .Ease(Easing.InOutSine), exiting, slot);
                    break;
                }
                case TransitionType.SlideLeft:
                {
                    float from = exiting ? x : -20f, to = exiting ? -20f : x;
                    slot.style.left = Length.Percent(from);
                    Finish(slot.experimental.animation
                        .Start(0f, 1f, ms, (e, t) => e.style.left = Length.Percent(Mathf.Lerp(from, to, t)))
                        .Ease(Easing.OutCubic), exiting, slot);
                    break;
                }
                case TransitionType.SlideRight:
                {
                    float from = exiting ? x : 120f, to = exiting ? 120f : x;
                    slot.style.left = Length.Percent(from);
                    Finish(slot.experimental.animation
                        .Start(0f, 1f, ms, (e, t) => e.style.left = Length.Percent(Mathf.Lerp(from, to, t)))
                        .Ease(Easing.OutCubic), exiting, slot);
                    break;
                }
                case TransitionType.Pop:
                {
                    float from = exiting ? 1f : 0f, to = exiting ? 0f : 1f;
                    slot.style.scale = new Scale(new Vector2(from, from));
                    Finish(slot.experimental.animation
                        .Start(0f, 1f, ms, (e, t) => { float s = Mathf.Lerp(from, to, t); e.style.scale = new Scale(new Vector2(s, s)); })
                        .Ease(exiting ? Easing.InBack : Easing.OutBack), exiting, slot);
                    break;
                }
            }
        }

        // On an exit animation, hide the slot once it finishes so a faded-out
        // character actually leaves the screen.
        private static void Finish(ValueAnimation<float> anim, bool exiting, VisualElement slot)
        {
            if (exiting) anim.OnCompleted(() => slot.style.display = DisplayStyle.None);
        }

        /// <summary>Named horizontal placement presets — the common VN slots from
        /// far-left to far-right (plus a few in between). A script can ignore
        /// these and give an explicit x fraction instead.</summary>
        public static float SlotX(string position)
        {
            switch (position)
            {
                case "far_left": return 0.12f;
                case "left": return 0.25f;
                case "center_left": return 0.38f;
                case "center": return 0.50f;
                case "center_right": return 0.62f;
                case "right": return 0.75f;
                case "far_right": return 0.88f;
                default: return 0.50f;
            }
        }
    }
}
