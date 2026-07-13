using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UI;

namespace Lvn.UI.World
{
    /// <summary>
    /// The Canvas scene: the uGUI mirror of the UITK "world" layer in
    /// <see cref="VnStage"/> (background + actors + camera). Builds a
    /// <c>Canvas → GameRoot → (Background, Content)</c> hierarchy — the exact shape
    /// the production app (Liminal) renders the scene with — so character
    /// animation ticks per-frame at 60fps and can host Spine
    /// (<c>SkeletonGraphic</c>) without a RenderTexture bridge. The dialogue and
    /// choice chrome stay on UI Toolkit above this canvas.
    ///
    /// <para>The GameRoot carries the camera transform (shake/zoom/pan) so the
    /// scene moves while the chrome stays put. Each actor is a
    /// <see cref="WorldActor"/> placed by <see cref="WorldPlacement"/>; its own
    /// <see cref="CanvasGroup"/> carries placement opacity and speaker-dim, while
    /// the actor's internal rig group carries animation alpha (the two multiply).</para>
    /// </summary>
    public sealed class WorldStage
    {
        private readonly GameObject _canvasGo;
        private readonly RectTransform _gameRoot;
        private readonly RectTransform _content;
        private readonly WorldBackground _bg;
        private readonly WorldCameraRig _camera;
        private readonly Vector2 _reference;

        private readonly Dictionary<string, WorldActor> _actors = new Dictionary<string, WorldActor>();
        private readonly Dictionary<string, CanvasGroup> _slotGroups = new Dictionary<string, CanvasGroup>();
        private readonly Dictionary<string, float> _baseOpacity = new Dictionary<string, float>();
        private int _nextSibling;

        public GameObject Root => _canvasGo;
        public WorldBackground Background => _bg;

        /// <param name="parent">Where to park the canvas GameObject (e.g. the VnStage's transform).</param>
        /// <param name="sortingOrder">Canvas sort order — keep below the UITK panel's so chrome draws on top.</param>
        /// <param name="reference">Reference resolution (canvas units); default 1080×1920 portrait.</param>
        public WorldStage(Transform parent, int sortingOrder = 0, Vector2? reference = null)
        {
            _reference = reference ?? new Vector2(1080f, 1920f);

            _canvasGo = new GameObject("vn-world-canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            _canvasGo.transform.SetParent(parent, false);
            var canvas = _canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = _canvasGo.GetComponent<CanvasScaler>();
            // Registered with LvnPanel so the stage canvas scales EXACTLY like the
            // UITK chrome panel above it (same reference, same orientation match) —
            // a mismatch here made sprites and dialogue scale apart on non-reference
            // screens.
            LvnPanel.RegisterScaler(scaler);
            scaler.referenceResolution = _reference; // honour a custom reference if one was passed
            // No GraphicRaycaster: the scene is purely visual here; tap-to-advance
            // and choices live on the UITK panel above, so we never steal input.

            _gameRoot = NewStretch("game-root", _canvasGo.transform);
            _bg = new WorldBackground(_gameRoot);
            _content = NewStretch("content", _gameRoot);

            _camera = _canvasGo.AddComponent<WorldCameraRig>();
            _camera.Bind(_gameRoot);
        }

        // ── background ───────────────────────────────────────────────────────
        public void SetBackgroundSprite(Sprite sprite) => _bg.SetSprite(sprite);
        public void SetBackgroundColor(Color color) => _bg.SetColor(color);

        // ── camera ───────────────────────────────────────────────────────────
        public void Shake(float amplitude, float seconds) => _camera.Shake(amplitude, seconds);
        public void Zoom(float factor, float seconds) => _camera.Zoom(factor, seconds);
        public void Pan(float x, float y, float seconds) => _camera.Pan(x, y, seconds);
        public void ResetCamera(float seconds) => _camera.Reset(seconds);

        // ── actors ───────────────────────────────────────────────────────────

        /// <summary>The live content rect in canvas units — the device's real size
        /// (width-matched to the reference, so its height is the true screen height,
        /// taller than 1920 on a modern phone). Falls back to the reference before the
        /// canvas has laid out. This is what actor placement maps against so Y=1 lands
        /// on the actual screen bottom, matching the full-bleed background.</summary>
        private Vector2 ContentSize()
        {
            // The canvas is width-matched to the reference (matchWidthOrHeight=0), so the
            // logical width is always the reference width and the logical height is that
            // width × the device aspect. Derive it from Screen — always valid — rather
            // than _content.rect, which lags a layout pass and would leave actors floating
            // at the 1920 reference bottom until the rect settles ("не всегда съезжает").
            float sw = Screen.width, sh = Screen.height;
            if (sw > 1f && sh > 1f) return new Vector2(_reference.x, _reference.x * (sh / sw));
            return _reference;
        }

        /// <summary>Get (or create) the <see cref="WorldActor"/> for an id.</summary>
        public WorldActor EnsureActor(string id)
        {
            if (_actors.TryGetValue(id, out var a) && a != null) return a;
            var go = new GameObject("vn-obj-" + id, typeof(RectTransform), typeof(CanvasGroup));
            go.transform.SetParent(_content, false);
            go.transform.SetSiblingIndex(_nextSibling++);
            a = go.AddComponent<WorldActor>();
            a.ContentSize = _reference;
            _actors[id] = a;
            _slotGroups[id] = go.GetComponent<CanvasGroup>();
            _baseOpacity[id] = 1f;
            return a;
        }

        /// <summary>Place / update / show an object as a stack of layer sprites —
        /// the Canvas equivalent of <c>ActorLayer.Apply</c>. A null/empty
        /// <paramref name="layers"/> list leaves the current art unchanged.</summary>
        public WorldActor ApplyActor(string id, IReadOnlyList<Sprite> layers, Placement p,
            IReadOnlyList<string> layerIds = null, IReadOnlyList<Vector4> layerRects = null,
            IReadOnlyList<Lvn.Content.SpriteCatalog.ResolvedLayer> layerDefs = null)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var a = EnsureActor(id);

            if (layers != null && layers.Count > 0)
                a.Configure(layers, layerIds, layerRects, layerDefs);

            // Size/pivot/flip come from the fixed reference so a character is the SAME
            // size on every device. But the VERTICAL position maps against the REAL
            // content height (the canvas is width-matched, so _content is the device's
            // true height — taller than 1920 on a modern phone). The background is
            // full-bleed to that rect, so without this actors stop at the 1920 reference
            // bottom (≈82% on a tall phone) and float; remapping Y grounds Y=1 on the
            // actual screen bottom — the figure just slides down, unchanged in size.
            WorldPlacement.Apply(a.Slot, p, _reference);
            var content = ContentSize();
            if (content.y > _reference.y + 0.5f)
            {
                var pos = a.Slot.anchoredPosition;
                pos.y = -p.Y * content.y;
                a.Slot.anchoredPosition = pos;
            }
            a.ContentSize = new Vector2(_reference.x, content.y);
            a.SetSlotBase(a.Slot.anchoredPosition);

            if (p.Z.HasValue) { a.transform.SetSiblingIndex(Mathf.Max(0, p.Z.Value)); }

            _baseOpacity[id] = p.Opacity;
            if (_slotGroups.TryGetValue(id, out var g) && g != null) g.alpha = p.Opacity;

            a.gameObject.SetActive(p.Show);
            if (!p.Show) a.StopAll();
            // An emotion/outfit swap rebuilt the layer Images (default white) — re-tint
            // so a dimmed actor doesn't pop back to full brightness mid-line.
            if (_focusK.TryGetValue(id, out var focus) && focus < 1f && _slotGroups.TryGetValue(id, out var fg))
                SetFocus(id, fg, focus);
            return a;
        }

        // Focus dim as a COLOR, not alpha: a CanvasGroup fade applies per-graphic,
        // so a layered actor's translucent clothes reveal the body layer beneath
        // (an accidental x-ray). Tinting every Graphic toward black darkens the
        // composite while its alpha stays intact. Alpha stays owned by placement
        // and animation tracks (they read-modify-write only the .a channel, so
        // the RGB dim survives them).
        private readonly Dictionary<string, float> _focusK = new Dictionary<string, float>();

        private void SetFocus(string id, CanvasGroup slot, float k)
        {
            _focusK[id] = k;
            if (slot == null) return;
            foreach (var gfx in slot.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
            {
                var c = gfx.color;
                gfx.color = new Color(k, k, k, c.a);
            }
        }

        /// <summary>Full brightness for the speaker, dim everyone else (null = undim).</summary>
        public void SetSpeaker(string id)
        {
            foreach (var kv in _slotGroups)
                SetFocus(kv.Key, kv.Value, (id == null || kv.Key == id) ? 1f : 0.55f);
        }

        // Loose name key (lower-case, letters/digits only) so a slot id and a say's
        // who match without an exact spelling agreement — see ActorLayer.HighlightSpeaker.
        private static string NameKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Classic VN focus: the speaking character goes full opacity, the rest
        /// present dim. Speaker matched to a slot by loose name key; when off-stage
        /// (narration) the current focus is kept.</summary>
        public void HighlightSpeaker(string who)
        {
            var target = NameKey(who);
            if (target == "") return;
            bool present = false;
            foreach (var kv in _slotGroups)
                if (kv.Value != null && NameKey(kv.Key) == target) { present = true; break; }
            if (!present) return;
            foreach (var kv in _slotGroups)
                SetFocus(kv.Key, kv.Value, NameKey(kv.Key) == target ? 1f : 0.55f);
        }

        public bool HasActor(string id) => _actors.TryGetValue(id, out var a) && a != null;
        public WorldActor ActorFor(string id) => _actors.TryGetValue(id, out var a) ? a : null;

        // ── animation (id-based, mirrors ActorLayer so VnStage calls one API) ──
        public void SetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames)
            => ActorFor(id)?.SetFrames(frames);
        public void PlayAnim(string id, string channel, LvnAnim anim) { if (!string.IsNullOrEmpty(channel) && anim != null) ActorFor(id)?.Play(channel, anim); }
        public void PlayAnimQueued(string id, string channel, LvnAnim anim) { if (!string.IsNullOrEmpty(channel) && anim != null) ActorFor(id)?.PlayQueued(channel, anim); }
        public void StopAnim(string id, string target) { var a = ActorFor(id); if (a == null) return; if (string.IsNullOrEmpty(target) || target == "all") a.StopScript(); else a.StopTarget(target); }
        public void EnsureIdle(string id, LvnAnim idle) => ActorFor(id)?.EnsureIdle(id, idle);
        public void EnsureBlink(string id, LvnAnim blink) => ActorFor(id)?.EnsureBlink(id, blink);
        public void Talk(string id, LvnAnim talk, bool on) => ActorFor(id)?.Talk(talk, on);
        public void PlayGesture(string id, LvnAnim anim, LvnAnim idle) => ActorFor(id)?.PlayGesture(anim, idle);

        public void RemoveAll()
        {
            foreach (var a in _actors.Values) if (a != null) Object.Destroy(a.gameObject);
            _actors.Clear();
            _slotGroups.Clear();
            _baseOpacity.Clear();
            _focusK.Clear();
            _nextSibling = 0;
            _bg.SetColor(Color.clear);
        }

        /// <summary>Tear the whole canvas down (chapter teardown / disable).</summary>
        public void Dispose()
        {
            if (_canvasGo != null) Object.Destroy(_canvasGo);
        }

        // ── helpers ──────────────────────────────────────────────────────────
        private static RectTransform NewStretch(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            return rt;
        }
    }
}
