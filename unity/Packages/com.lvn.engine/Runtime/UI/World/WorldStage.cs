using System.Collections.Generic;
using Lvn.Content;
using Newtonsoft.Json.Linq;
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
        private readonly Camera _canvasCamera;
        private readonly float _canvasCameraDepthBefore;
        private readonly float _canvasCameraDepthForced;

        private readonly Dictionary<string, WorldActor> _actors = new Dictionary<string, WorldActor>();
        private readonly Dictionary<string, CanvasGroup> _slotGroups = new Dictionary<string, CanvasGroup>();
        private readonly Dictionary<string, float> _baseOpacity = new Dictionary<string, float>();
        // Paint order: explicit z per id (sticky; unset = 0) + birth order as the
        // tie-break. Mirrors ActorLayer._z — the two renderers must stack alike.
        private readonly Dictionary<string, int> _zExplicit = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _birth = new Dictionary<string, int>();
        private int _nextSibling;

        public GameObject Root => _canvasGo;
        public WorldBackground Background => _bg;

        /// <summary>The real camera-frame blur, when this platform supports it
        /// (built-in pipeline + a scene camera). Null → caller uses its veil.</summary>
        public LvnBlurEffect Blur { get; }

        /// <summary>Мультиэффект кадра (op `fx`); null там же, где null Blur.</summary>
        public LvnFxStack Fx { get; }

        /// <summary>Размытая копия кадра для «матового стекла» интерфейса; null
        /// там же, где null Blur.</summary>
        public LvnGlass Glass { get; }

        /// <param name="parent">Where to park the canvas GameObject (e.g. the VnStage's transform).</param>
        /// <param name="sortingOrder">Canvas sort order — keep below the UITK panel's so chrome draws on top.</param>
        /// <param name="reference">Reference resolution (canvas units); default 1080×1920 portrait.</param>
        /// <summary>The canvas' real logical width in units: the scaler matches
        /// WIDTH on portrait (= reference.x) and HEIGHT on landscape (width
        /// grows with the aspect) — same orientation rule as LvnPanel.</summary>
        private float LogicalWidth()
            => Screen.width > Screen.height && Screen.height > 0
                ? _reference.y * Screen.width / Screen.height
                : _reference.x;

        public WorldStage(Transform parent, int sortingOrder = 0, Vector2? reference = null)
        {
            _reference = reference ?? new Vector2(1080f, 1920f);

            _canvasGo = new GameObject("vn-world-canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            _canvasGo.transform.SetParent(parent, false);
            var canvas = _canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            // Render THROUGH a camera when one exists — visually identical to
            // overlay (same scaler; the UITK chrome panel always draws after
            // cameras) but the frame now passes OnRenderImage, so the `blur` op
            // can be a real gaussian instead of the white-veil imitation.
            // Built-in pipeline only: URP/HDRP never call OnRenderImage, so
            // there the canvas stays overlay and blur falls back to the veil.
            var cam = Camera.main;
            if (cam == null) cam = Object.FindAnyObjectByType<Camera>();
            if (cam != null && UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null)
            {
                // A content/demo scene may bring its own enabled screen camera.
                // Equal camera depths have undefined order; if that camera runs
                // after this one it clears the framebuffer AFTER the Canvas and
                // all actors "disappear" even though their sprites loaded. Make
                // the camera carrying the stage canvas deterministically last.
                float lastDepth = cam.depth;
                foreach (var other in Object.FindObjectsByType<Camera>())
                    if (other != null && other != cam && other.enabled &&
                        other.targetTexture == null)
                        lastDepth = Mathf.Max(lastDepth, other.depth);
                _canvasCamera = cam;
                _canvasCameraDepthBefore = cam.depth;
                _canvasCameraDepthForced = Mathf.Min(999f, lastDepth + 1f);
                cam.depth = _canvasCameraDepthForced;

                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 1f;
                Blur = LvnBlurEffect.Ensure(cam);
                Fx = LvnFxStack.Ensure(cam); // мультиэффект (op `fx`), тот же крюк
                // Fx lives on the camera and can therefore survive an older
                // WorldStage. A fresh stage must always begin with a clean frame.
                Fx.ResetImmediate();
                // Стекло — ПОСЛЕДНИМ: OnRenderImage идёт в порядке компонентов,
                // и подложка должна видеть кадр уже с эффектами. Иначе окно в
                // задымлённой сцене осталось бы прозрачным по чистому миру —
                // как окно из другой игры.
                Glass = LvnGlass.Ensure(cam);
            }

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
        public void SetBackgroundSprite(Sprite sprite)
        {
            Set3DBackdrop(null); // a painted background replaces a live set
            _bg.SetSprite(sprite);
        }
        public void SetBackgroundColor(Color color)
        {
            Set3DBackdrop(null);
            _bg.SetColor(color);
        }

        // ── 3D set as the background ─────────────────────────────────────────

        private Lvn3DBackdrop _backdrop;

        /// <summary>Stand a 3D set behind the scene: the prefab is built off-screen,
        /// filmed by its own camera, and the frame becomes the background. Null
        /// tears it down and returns to flat art.</summary>
        public void Set3DBackdrop(GameObject prefab)
        {
            if (prefab == null)
            {
                if (_backdrop != null)
                {
                    _camera.Echo = null;
                    // Вернуть карточке фона родной трансформ: противоход мог
                    // застыть на полпути, если сет снесли посреди встряски.
                    _bg.Transform.anchoredPosition = Vector2.zero;
                    _bg.Transform.localScale = Vector3.one;
                    _backdrop.Release();
                    _bg.SetLiveTexture(null);
                }
                return;
            }
            if (_backdrop == null)
            {
                _backdrop = Lvn3DBackdrop.Ensure(_canvasGo.transform);
                _backdrop.TextureChanged += tex =>
                {
                    // Device rotation replaces the RT object. Rebind the
                    // background immediately; a released texture renders black.
                    if (_backdrop != null && _backdrop.Active)
                        _bg.SetLiveTexture(tex);
                };
                // Shake/pan/zoom now carry into the set: the `camera` op moves
                // both layers at once instead of jolting sprites over a frozen world.
                var backdrop = _backdrop;
                _camera.Echo = (offset, zoom) =>
                {
                    // Встряска/наезд двигают МИР, а не картинку мира: карточка
                    // кадра противоходом пригвождена к экрану (родитель сдвинул
                    // на +x — карточка на -x), а само движение уходит в 3D-камеру.
                    // Иначе кадр дёргался бы дважды: как карточка и как ракурс.
                    if (zoom <= 0f) zoom = 1f;
                    var pin = -offset / zoom;
                    // Не трогать трансформ без изменения: сеттер будит layout
                    // канваса, а лямбда стреляет каждый кадр — и в покое тоже.
                    if (_bg.Transform.anchoredPosition != pin)
                        _bg.Transform.anchoredPosition = pin;
                    var s = Vector3.one / zoom;
                    if (_bg.Transform.localScale != s)
                        _bg.Transform.localScale = s;
                    backdrop.Echo(offset, zoom, LogicalWidth());
                };
            }
            _backdrop.SetSet(prefab);
            _bg.SetLiveTexture(_backdrop.Texture);
        }

        /// <summary>Move the 3D set's camera — the reason a built set replaces a
        /// folder of painted angles. No-op without a set standing.</summary>
        public void Frame3D(float? x, float? y, float? z,
                            float? pitch, float? yaw, float? fov, float seconds)
        {
            if (_backdrop == null || !_backdrop.Active) return;
            _backdrop.Frame(x, y, z, pitch, yaw, fov, seconds);
            _bg.SetLiveTexture(_backdrop.Texture); // the buffer is recreated on resize
        }

        /// <summary>True while a 3D set is standing.</summary>
        public bool Has3DBackdrop => _backdrop != null && _backdrop.Active;

        /// <summary>Force the filming mode of the standing set (`bg3d live=`).</summary>
        public void Set3DLive(bool live) => _backdrop?.SetLive(live);

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
            // Рождается СПРЯТАННЫМ. Переход входа играет на смене видимости, а
            // новый GameObject активен по умолчанию — из-за этого самый первый
            // показ героя, то есть КАЖДОЕ появление нового лица в главе,
            // проскакивал без анимации: переход был, а видел его только
            // повторный выход того же персонажа.
            go.SetActive(false);
            go.transform.SetParent(_content, false);
            _birth[id] = _nextSibling++;
            a = go.AddComponent<WorldActor>();
            a.ContentSize = _reference;
            _actors[id] = a;
            _slotGroups[id] = go.GetComponent<CanvasGroup>();
            _baseOpacity[id] = 1f;
            return a;
        }

        /// <summary>Create and position an actor slot without changing its
        /// visibility. Sprite art loads asynchronously, so revealing from this
        /// pre-load step would play the entrance on an empty container.</summary>
        public WorldActor PlaceActor(string id, Placement p)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var a = EnsureActor(id);
            ApplyPlacement(id, a, p);
            return a;
        }

        /// <summary>Whether the sprite actor already owns drawable art. A later
        /// show command may intentionally omit URLs and reuse these layers.</summary>
        public bool HasActorArt(string id)
        {
            var a = ActorFor(id);
            return a != null && a.Rig != null
                && a.Rig.GetComponentsInChildren<Graphic>(true).Length > 0;
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

            ApplyPlacement(id, a, p);

            _baseOpacity[id] = p.Opacity;
            _slotGroups.TryGetValue(id, out var g);

            // ПОЯВЛЕНИЕ И УХОД — ТОЛЬКО НА СМЕНЕ ВИДИМОСТИ. Смена позы или
            // эмоции идёт тем же путём `actor`, и если проявлять на каждом
            // применении, персонаж будет мигать на каждой реплике.
            bool wasVisible = a.gameObject.activeSelf;
            float dur = p.TransitionDuration;
            bool fadeIn = p.Show && !wasVisible && p.EnterTransition != TransitionType.None && dur > 0.001f;
            bool fadeOut = !p.Show && wasVisible && p.ExitTransition != TransitionType.None && dur > 0.001f;

            if (fadeIn && p.EnterTransition == TransitionType.Dissolve)
            {
                a.gameObject.SetActive(true);
                if (g != null) g.alpha = p.Opacity;
                Dissolve(a, 1f, 0f, dur);           // собирается из шума
                LvnFade.Play(g, p.Opacity, p.Opacity, dur); // тот же таймер, альфа не трогается
            }
            else if (fadeOut && p.ExitTransition == TransitionType.Dissolve)
            {
                Dissolve(a, 0f, 1f, dur);           // сгорает
                LvnFade.Play(g, g != null ? g.alpha : p.Opacity, g != null ? g.alpha : p.Opacity, dur,
                    () => { if (a != null) { a.gameObject.SetActive(false); Dissolve(a, 0f, 0f, 0f); } });
            }
            else if (fadeIn)
            {
                a.gameObject.SetActive(true);
                if (p.EnterTransition == TransitionType.Drift)
                {
                    // Кинематографичный вход: правый персонаж въезжает справа,
                    // левый — слева, с коротким федом.
                    float dir = LvnFade.DriftSign(p.X);
                    LvnFade.Play(g, 0f, p.Opacity, dur,
                        a.Slot, new Vector2(dir * _reference.x * 0.06f, 0f));
                }
                else LvnFade.Play(g, 0f, p.Opacity, dur);
            }
            else if (fadeOut)
            {
                // Уходящий остаётся видимым, пока идёт переход, и прячется его
                // хвостом. Спрятать сразу — значит не показать сам уход.
                if (p.ExitTransition == TransitionType.Drift)
                {
                    float dir = LvnFade.DriftSign(p.X);
                    LvnFade.Play(g, g != null ? g.alpha : p.Opacity, 0f, dur,
                        a.Slot, new Vector2(dir * _reference.x * 0.06f, 0f),
                        () =>
                        {
                            if (a == null) return;
                            a.gameObject.SetActive(false);
                        });
                }
                else LvnFade.Play(g, g != null ? g.alpha : p.Opacity, 0f, dur,
                    () => { if (a != null) a.gameObject.SetActive(false); });
            }
            else
            {
                LvnFade.Cancel(g);      // показ посреди ухода отменяет уход
                if (g != null) g.alpha = p.Opacity;
                a.gameObject.SetActive(p.Show);
            }
            if (!p.Show) a.StopAll();
            // An emotion/outfit swap rebuilt the layer Images (default white) — re-tint
            // so a dimmed actor doesn't pop back to full brightness mid-line.
            if (_focusK.TryGetValue(id, out var focus) && focus < 1f && _slotGroups.TryGetValue(id, out var fg))
                SetFocus(id, fg, focus);
            return a;
        }

        private void ApplyPlacement(string id, WorldActor a, Placement p)
        {

            // Size/pivot/flip come from the fixed reference so a character is the SAME
            // size on every device. But the VERTICAL position maps against the REAL
            // content height (the canvas is width-matched, so _content is the device's
            // true height — taller than 1920 on a modern phone). The background is
            // full-bleed to that rect, so without this actors stop at the 1920 reference
            // bottom (≈82% on a tall phone) and float; remapping Y grounds Y=1 on the
            // actual screen bottom — the figure just slides down, unchanged in size.
            // The REAL logical canvas (the scaler matches width on portrait,
            // height on landscape). Wider-than-9:16 viewports have LESS height
            // than the 1920 reference — an actor sized to the reference would
            // overflow the screen sideways (the "левую часть обрезает" bug), so
            // the sizing frame caps at the visible height. Tall phones keep the
            // fixed-reference size exactly as before.
            float lw = LogicalWidth();
            float lh = Screen.width > Screen.height && Screen.height > 0
                ? _reference.y
                : (Screen.width > 0 ? _reference.x * Screen.height / Screen.width : _reference.y);
            var sizeFit = new Vector2(_reference.x, Mathf.Min(lh, _reference.y));
            WorldPlacement.Apply(a.Slot, p, sizeFit);
            if (lh > _reference.y + 0.5f)
            {
                var pos = a.Slot.anchoredPosition;
                pos.y = -p.Y * lh;
                a.Slot.anchoredPosition = pos;
            }
            // WIDE screens: X was mapped against the 1080-unit portrait column
            // anchored at the canvas LEFT — on a landscape canvas (~2× wider)
            // the whole cast huddled in the left third ("right" ended at 22% of
            // the screen). Remap across the REAL logical width with soft edge
            // anchoring: left-half slots keep their inset from the LEFT edge,
            // right-half from the RIGHT (portrait-frame ratios, sqrt-softened),
            // clamped by the slot's own half-width so nothing is ever cut.
            if (lw > _reference.x + 0.5f)
            {
                float k = Mathf.Sqrt(_reference.x / lw);
                float x01 = p.X < 0.5f ? p.X * k
                    : p.X > 0.5f ? 1f - (1f - p.X) * k
                    : 0.5f;
                float halfW = a.Slot.sizeDelta.x * 0.5f / lw;
                x01 = Mathf.Clamp(x01, halfW + 0.01f, 1f - halfW - 0.01f);
                var pos = a.Slot.anchoredPosition;
                pos.x = x01 * lw;
                a.Slot.anchoredPosition = pos;
            }
            a.ContentSize = new Vector2(_reference.x, lh);
            a.SetSlotBase(a.Slot.anchoredPosition);

            // Re-sort on EVERY apply, not only when the command carries z=.
            // SetSiblingIndex(z) treated z as a CHILD INDEX: on a six-child canvas
            // z=10 and z=80 both clamped to "last", so whichever object finished
            // its async apply later drew on top (руки под скелетом в бою 1-на-1).
            if (p.Z.HasValue) _zExplicit[id] = p.Z.Value;
            ResortSiblings();
        }

        /// <summary>Шейдерное растворение спрайта: <paramref name="from"/> →
        /// <paramref name="to"/> за <paramref name="seconds"/>. Тот же материал и
        /// тот же параметр, что у опа <c>sfx dissolve=</c> — перехода со своей
        /// копией эффекта не бывает: разойдутся видом.</summary>
        private static void Dissolve(WorldActor a, float from, float to, float seconds)
        {
            if (a == null) return;
            if (from != to || seconds <= 0f)
                LvnSpriteFxDriver.Apply(a.gameObject, new JObject { ["dissolve"] = from, ["dur"] = 0f });
            if (seconds > 0f)
                LvnSpriteFxDriver.Apply(a.gameObject, new JObject { ["dissolve"] = to, ["dur"] = seconds });
        }

        // Deterministic stacking: sort by explicit z (unset = 0), birth order
        // breaks ties — so no-z novels keep their "shown later = on top" look,
        // while z-объекты stack by value no matter which async apply lands last.
        private void ResortSiblings()
        {
            var order = new List<KeyValuePair<string, WorldActor>>(_actors.Count);
            foreach (var kv in _actors) if (kv.Value != null) order.Add(kv);
            order.Sort((x, y) =>
            {
                int zx = _zExplicit.TryGetValue(x.Key, out var a) ? a : 0;
                int zy = _zExplicit.TryGetValue(y.Key, out var b) ? b : 0;
                if (zx != zy) return zx.CompareTo(zy);
                int bx = _birth.TryGetValue(x.Key, out var c) ? c : 0;
                int by = _birth.TryGetValue(y.Key, out var d) ? d : 0;
                return bx.CompareTo(by);
            });
            for (int i = 0; i < order.Count; i++)
                order[i].Value.transform.SetSiblingIndex(i);
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
                SetFocus(kv.Key, kv.Value, (id == null || kv.Key == id) ? 1f : 0.7f);
        }

        /// <summary>Classic VN focus: the speaking character goes full opacity, the rest
        /// present dim. Speaker matched to a slot by loose name key; when off-stage
        /// (narration) the current focus is kept.</summary>
        public void HighlightSpeaker(string who)
        {
            var target = Lvn.LvnKey.Normalize(who);
            if (target == "") return;
            bool present = false;
            foreach (var kv in _slotGroups)
                if (kv.Value != null && Lvn.LvnKey.Normalize(kv.Key) == target) { present = true; break; }
            if (!present) return;
            foreach (var kv in _slotGroups)
                SetFocus(kv.Key, kv.Value, Lvn.LvnKey.Normalize(kv.Key) == target ? 1f : 0.7f);
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
            // RemoveAll is the renderer half of VnStage.ResetStage.  Camera FX
            // are sticky within a chapter, but never across chapter/load bounds.
            Fx?.ResetImmediate();
            foreach (var a in _actors.Values) if (a != null) Object.Destroy(a.gameObject);
            _actors.Clear();
            _slotGroups.Clear();
            _baseOpacity.Clear();
            _focusK.Clear();
            _zExplicit.Clear();
            _birth.Clear();
            _nextSibling = 0;
            _bg.SetColor(Color.clear);
        }

        /// <summary>Tear the whole canvas down (chapter teardown / disable).</summary>
        public void Dispose()
        {
            // The blur lives on the CAMERA, which outlives this canvas — a
            // chapter that ended mid-`blur` must not haunt the next one.
            Blur?.FadeTo(0f, 0f);
            Fx?.ResetImmediate();
            // Respect a later owner that deliberately changed the depth.
            if (_canvasCamera != null &&
                Mathf.Approximately(_canvasCamera.depth, _canvasCameraDepthForced))
                _canvasCamera.depth = _canvasCameraDepthBefore;
            if (_canvasGo != null)
            {
                if (Application.isPlaying) Object.Destroy(_canvasGo);
                else Object.DestroyImmediate(_canvasGo);
            }
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
