using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The drop-in stage: a <see cref="MonoBehaviour"/> that composes the
    /// reference layers (background → actors → dialogue → choices) into a
    /// <see cref="UIDocument"/> and plays a <c>.lvn</c> through an
    /// <see cref="LvnPlayer"/>. Implements <see cref="ILvnStage"/> itself, so
    /// dropping it on a GameObject with a UIDocument and a script TextAsset is a
    /// playable game. Swap <see cref="Theme"/> to restyle, assign
    /// <see cref="Assets"/> to load art.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed partial class VnStage : MonoBehaviour, ILvnStage
    {
        [Tooltip("Look-and-feel for the built-in components.")]
        public VnTheme Theme = new VnTheme();

        [Tooltip("A .lvn file as a TextAsset; played on enable. Optional — call Play() instead.")]
        public TextAsset Script;

        /// <summary>Resolves <c>sprite_url</c>s to sprites. Null → solid-colour
        /// backgrounds and no character art. Assign in code before play.</summary>
        public ILvnAssets Assets;

        /// <summary>Optional sprite/entity catalog (from <c>manifest.sprites</c>).
        /// When set, <c>actor</c>/<c>obj</c>/<c>bg id="..."</c> resolve their
        /// layers (with conditional <c>when</c> display) from it instead of raw
        /// urls. Assign from the host's manifest before play.</summary>
        public SpriteCatalog Catalog;

        /// <summary>Optional localization catalog (<c>text_id</c> → string) for the
        /// active language. Assign before Play — or mid-chapter (a language switch):
        /// the running player picks it up and renders subsequent lines with it.</summary>
        public System.Collections.Generic.IReadOnlyDictionary<string, string> Strings
        {
            get => _strings;
            set { _strings = value; if (_player != null) _player.Strings = value; }
        }
        private System.Collections.Generic.IReadOnlyDictionary<string, string> _strings;

        [Tooltip("Optional content folder. If set and Assets is unwired, the stage " +
                 "loads sprites from here via DirectoryAssets — so a scene plays with " +
                 "art straight from Play, no code. Editor/standalone file paths.")]
        public string ContentRoot;

        [Tooltip("Render the scene (background + actors + camera) on a uGUI Canvas " +
                 "instead of UI Toolkit — the 60fps / Spine path. Dialogue and choices " +
                 "stay on UI Toolkit above it. Off by default (UITK scene).")]
        public bool UseCanvasScene;

        private VisualElement _world;      // the camera target (UITK path)
        private ISceneRenderer _renderer;  // bg + actors + camera, renderer-agnostic
        private ParticleField _particles;
        private DialogueBox _dialogue;
        // Safe-area hosts: dialogue/choices/labels and the quick menu are inset to
        // Screen.safeArea; scene, weather and FX veils stay full-bleed (see Build).
        private SafeAreaElement _chromeSafe, _menuSafe;
        private ChoiceList _choices;
        private VisualElement _labelLayer; // reactive HUD/stat text overlay (the `text` op)
        private readonly Dictionary<string, Label> _labelEls = new Dictionary<string, Label>();
        private readonly Dictionary<string, string> _labelTmpl = new Dictionary<string, string>(); // id → live `{expr}` template
        private VisualElement _hintCard;   // top-center popup for the `hint` op
        private Label _hintLabel;
        private IVisualElementScheduledItem _hintHide; // auto-dismiss timer (duration>0)
        private FxLayer _fx;
        private StageAudio _audio;
        private StageMenu _menu;
        private Dictionary<string, CastEntity> _cast;
        private readonly Dictionary<string, LvnAnim> _talkAnims = new Dictionary<string, LvnAnim>(); // actor id → lip-sync anim
        private LvnPlayer _player;
        private CancellationTokenSource _cts;
        private bool _awaitingTap;
        private bool _awaitingWait;
        // Current on-screen beat — restored after a live theme rebuild so ApplyTheme
        // is safe to call mid-line (realtime theming keeps the line/choices visible).
        private bool _sayUp;
        private IReadOnlyList<LvnOption> _curChoices;

        /// <summary>Public access to the underlying player for save/load.</summary>
        public LvnPlayer Player => _player;

        private readonly List<(string who, string text, string style)> _backlog
            = new List<(string, string, string)>();

        /// <summary>Read-only access to the dialogue history.</summary>
        public IReadOnlyList<(string who, string text, string style)> Backlog => _backlog;

        private bool _built;
        private VisualElement _uiRoot; // panel root — normalizes the pointer position
        // Clickable hotspots for the Canvas scene (which has no uGUI raycaster) —
        // hit-tested in OnPointerDown against each actor's real on-screen RectTransform
        // (so the clickable area matches the visible sprite exactly).
        private readonly List<(string id, System.Action onClick)> _hotspots = new List<(string, System.Action)>();

        // UIDocument's rootVisualElement can be null in OnEnable (it initializes
        // its panel on its own OnEnable, and script order isn't guaranteed), so we
        // also try in Start, by which point the panel is ready. Whichever sees a
        // non-null root first builds; the other is a no-op.
        // Renew the cancellation source on every enable — it is the token every
        // asset load uses. Build() is gated by `_built`, so without this a
        // disable/enable cycle would leave the source cancelled (from OnDisable)
        // and every bg/actor/audio load would throw immediately → a blank stage.
        private void OnEnable() { _cts?.Dispose(); _cts = new CancellationTokenSource(); Build(); }
        private void Start() => Build();
        // Start runs once per component lifetime — after a disable/enable cycle
        // it can't retry a Build whose panel wasn't ready yet, so keep a cheap
        // per-frame guard until the chrome exists.
        private void Update()
        {
            if (!_built) Build();
            // [lvn-perf] frame-hitch watchdog: any frame past 150 ms is a felt
            // freeze — log it with the in-flight spine work so a hitch can be
            // attributed (or ruled out) at a glance. Skips the very first frames
            // after a scene load, which are always heavy and not interesting.
            float dt = Time.unscaledDeltaTime;
            if (dt > 0.15f && Time.frameCount > 10)
                Debug.Log($"[lvn-perf] FRAME HITCH {(dt * 1000f):F0}ms at frame {Time.frameCount}"
                          + (_spineLoading.Count > 0 ? $" (spine builds in flight: {string.Join(",", _spineLoading)})" : ""));
        }

        private void Build()
        {
            if (_built) return;
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null) return; // panel not ready yet — Start will retry
            _uiRoot = root;
            _built = true;
            LvnPlayer.Log = m => Debug.Log("[LVN] " + m); // full step trace to the console

            if (Assets == null && !string.IsNullOrEmpty(ContentRoot))
                Assets = new DirectoryAssets(ContentRoot);
            root.Clear();
            root.style.flexGrow = 1;

            // Scene = background + actors + camera. Two interchangeable renderers:
            // the uGUI Canvas (60fps / Spine) sits on a sibling canvas *below* this
            // UITK panel; the UITK path wraps them in a "vn-world" element. Either
            // way the dialogue/choice chrome draws above the scene.
            if (UseCanvasScene)
            {
                // sortingOrder below the panel (10) so the UITK chrome composites on top.
                var scene = new World.WorldStage(transform, sortingOrder: 0);
                scene.SetBackgroundColor(Color.black);
                _renderer = new CanvasSceneRenderer(scene);
                _ = ApplyDefaultBackdropAsync(scene); // seamless tiled filler instead of flat black
            }
            else
            {
                _world = new VisualElement { name = "vn-world", pickingMode = PickingMode.Ignore };
                _world.style.position = Position.Absolute;
                _world.style.left = 0; _world.style.right = 0; _world.style.top = 0; _world.style.bottom = 0;
                var bg = new BackgroundLayer();
                var actors = new ActorLayer();
                _world.Add(bg);
                _world.Add(actors);
                _renderer = new UitkSceneRenderer(bg, actors, new CameraRig(_world));
            }

            _particles = new ParticleField();
            ResolveFont();
            _panelHost = null; // died with the previous panel root — recreate lazily
            _dialogue = new DialogueBox(Theme);
            SetSayVisible(_sayUp); // the empty skinned frame must not sit on a bare stage
            _choices = new ChoiceList(Theme);
            _fx = new FxLayer();

            _labelLayer = new VisualElement { name = "vn-labels", pickingMode = PickingMode.Ignore };
            _labelLayer.style.position = Position.Absolute;
            _labelLayer.style.left = 0; _labelLayer.style.right = 0; _labelLayer.style.top = 0; _labelLayer.style.bottom = 0;

            if (_world != null) root.Add(_world);
            root.Add(_particles);   // weather sits over the scene, under the UI
            // Chrome lives inside the device SAFE AREA (never under a notch /
            // home indicator); the scene, weather and the FX veil stay full-bleed
            // so art and fades cover the physical screen edge to edge.
            _chromeSafe = new SafeAreaElement();
            _chromeSafe.Add(_dialogue);
            _chromeSafe.Add(_choices);
            _chromeSafe.Add(_labelLayer); // HUD/stat labels above dialogue/choices
            root.Add(_chromeSafe);
            root.Add(_fx);          // top: fades/dim veil everything below
            _menu = new StageMenu(this, Theme);
            _menuSafe = new SafeAreaElement();
            _menuSafe.Add(_menu);   // quick menu above even the FX veil — always reachable
            root.Add(_menuSafe);
            _choices.OnSelected += OnChoiceSelected;
            _choices.VisibleChanged += OnChoicesVisibleChanged;

            // Reactive tick: re-evaluate every live label's {expr} template against the
            // current variables so on-screen stats track changes (incl. background ones).
            root.schedule.Execute(RefreshLabels).Every(200);

            // Auto-advance: hands-free reading — once a line finishes revealing and
            // its reading delay passes, advance as if tapped. Choices always wait.
            root.schedule.Execute(AutoAdvanceTick).Every(100);

            // Skip: fast-forward gear (~13 lines/s), stops on anything interactive.
            root.schedule.Execute(SkipTick).Every(75);

            // Player comfort settings (dialogue window opacity now, live on change).
            _dialogue.SetUserOpacity(LvnPrefs.DialogOpacity);
            LvnPrefs.Changed -= OnPrefsChanged;
            LvnPrefs.Changed += OnPrefsChanged;
            // Wardrobe equips re-apply the actor live if it's on screen.
            LvnWardrobe.Changed -= OnWardrobeChanged;
            LvnWardrobe.Changed += OnWardrobeChanged;

            root.pickingMode = PickingMode.Position;
            root.RegisterCallback<PointerDownEvent>(OnPointerDown);
            root.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            root.RegisterCallback<PointerUpEvent>(OnPointerUp);
            root.RegisterCallback<PointerCancelEvent>(_ => OnPointerCancelled());
            root.RegisterCallback<PointerCaptureOutEvent>(_ => OnPointerCancelled());
            // Desktop convenience (the Ren'Py convention): wheel-up steps back one beat.
            root.RegisterCallback<WheelEvent>(evt =>
            {
                if (InputBlocked) return;
                if (evt.delta.y < 0f && RollbackStep()) evt.StopPropagation();
            });

            // Audio channels (music/ambient/sfx) live in their own component.
            _audio = gameObject.AddComponent<StageAudio>();

            _cts ??= new CancellationTokenSource(); // OnEnable usually made it; safety for a direct Build()

            if (_player != null)
            {
                // The chrome was rebuilt under a LIVE player (a disable/enable
                // cycle — see OnDisable): re-render the scene and the current
                // beat on the new panel, the same recipe rollback uses.
                var snap = _player.PopCurrent();
                if (snap != null)
                {
                    _player.OnSay += RecordSay; // OnDisable unhooked it
                    _player.Restore(snap);
                    _suppressDupSay = true; // the re-run beat is already in the backlog
                    int at = _player.Index;
                    _player.ReplayVisuals(at);
                    _player.ContinueFrom(at);
                }
            }
            else if (Script != null) Play(Script.text);
        }

        /// <summary>Replace the visual theme. If the stage is already built, the
        /// dialogue box and choice list are recreated with the new look — so a
        /// manifest-driven theme (<see cref="VnThemeBuilder"/>) can be applied
        /// after construction. Call before the first chapter plays.</summary>
        public void ApplyTheme(VnTheme theme)
        {
            Theme = theme ?? new VnTheme();
            if (!_built) return; // Build() will pick up the new Theme
            RebuildChrome();
            // Resolve any manifest-driven background-image urls to sprites, then
            // rebuild once more so the dialogue/choices show their skinned panels.
            _ = EnsureThemeImagesAsync();
        }

        // The default backdrop behind the canvas scene: a seamless texture tiled
        // as a fine grid, so letterboxed scenes (a width-fit Spine leaves bars)
        // sit on a pattern instead of flat black. Overridden by any real `bg`.
        private async System.Threading.Tasks.Task ApplyDefaultBackdropAsync(World.WorldStage scene)
        {
            if (Assets == null || scene == null) return;
            try
            {
                var spr = await Assets.LoadSpriteAsync("/content/ui/tile-bg.jpg", _cts.Token);
                if (spr != null && spr.texture != null) scene.Background.SetTile(spr.texture, 140f);
            }
            catch { }
        }

        // Recreate the dialogue box and choice list from the current Theme, keeping
        // their z-order (…, dialogue, choices, fx). Used by ApplyTheme and after the
        // theme's background images finish loading.
        private void RebuildChrome()
        {
            var root = GetComponent<UIDocument>()?.rootVisualElement;
            if (root == null || _fx == null) return;

            if (_choices != null)
            {
                _choices.OnSelected -= OnChoiceSelected;
                _choices.VisibleChanged -= OnChoicesVisibleChanged;
                _choices.RemoveFromHierarchy();
            }
            if (_dialogue != null) { _dialogue.RevealTicked -= OnRevealTicked; _dialogue.RemoveFromHierarchy(); }
            // The shared window wears the theme too — drop it so the next use
            // rebuilds it with the fresh skin. NEVER while it's open: a live
            // re-theme (content sync) during an in-story wardrobe would orphan
            // the hosted content, its await would never resolve, and the held
            // script would soft-lock. An open window just keeps the old skin
            // until it closes.
            if (_panelHost != null && !_panelHost.IsOpen)
            { _panelHost.RemoveFromHierarchy(); _panelHost = null; }

            ResolveFont();
            _dialogue = new DialogueBox(Theme);
            _dialogue.SetUserOpacity(LvnPrefs.DialogOpacity);
            _dialogue.RevealTicked += OnRevealTicked;
            SetSayVisible(_sayUp); // a re-theme between lines must not reveal the empty frame
            _choices = new ChoiceList(Theme);
            // Rebuilt chrome goes back into the safe-area container, before the
            // label layer — keeps z-order: dialogue, choices, labels (fx above).
            var chromeHost = (VisualElement)_chromeSafe ?? root;
            int labelIndex = _labelLayer != null ? chromeHost.IndexOf(_labelLayer) : -1;
            if (labelIndex < 0) labelIndex = chromeHost.childCount;
            chromeHost.Insert(labelIndex, _dialogue);
            chromeHost.Insert(labelIndex + 1, _choices);
            _choices.OnSelected += OnChoiceSelected;
            _choices.VisibleChanged += OnChoicesVisibleChanged;

            // The quick menu is themeable too (manifest.ui.menu) — rebuild it with
            // the fresh theme, keeping it the topmost layer.
            _menu?.Close();
            _menu?.RemoveFromHierarchy();
            _menu = new StageMenu(this, Theme);
            ((VisualElement)_menuSafe ?? root).Add(_menu);

            // Restore the visible beat onto the fresh chrome so a live theme change
            // never blanks the line/choices the player is mid-reading (the text is
            // set instantly — no typewriter restart on each live tweak).
            if (_sayUp && _backlog.Count > 0)
            {
                var beat = _backlog[_backlog.Count - 1];
                _dialogue.SetSpeaker(beat.who);
                _dialogue.ApplyStyle(beat.style);
                _dialogue.SetText(beat.text);
            }
            if (_curChoices != null) _choices.Present(_curChoices);
        }

        // Load the theme's background-image urls (panel/nameplate/choice buttons)
        // through ILvnAssets and assign the resolved sprites onto the Theme, then
        // rebuild the chrome so they show. Each url loads at most once (skipped when
        // the sprite is already set), so this is safe to call after every ApplyTheme.
        private async Task EnsureThemeImagesAsync()
        {
            if (Theme == null || Assets == null || _cts == null) return;

            async Task<bool> Resolve(string url, System.Action<Sprite> assign)
            {
                if (string.IsNullOrEmpty(url)) return false;
                var sprite = await Assets.LoadSpriteAsync(url, _cts.Token);
                if (sprite == null) return false;
                assign(sprite);
                return true;
            }

            bool any = false;
            if (Theme.PanelSprite == null) any |= await Resolve(Theme.PanelImageUrl, s => Theme.PanelSprite = s);
            if (Theme.PlateSprite == null) any |= await Resolve(Theme.PlateImageUrl, s => Theme.PlateSprite = s);
            if (Theme.ChoiceSprite == null) any |= await Resolve(Theme.ChoiceImageUrl, s => Theme.ChoiceSprite = s);
            if (Theme.ChoiceHoverSprite == null) any |= await Resolve(Theme.ChoiceHoverImageUrl, s => Theme.ChoiceHoverSprite = s);

            if (any && _built) RebuildChrome();

            // UI sound clips ride the same lazy pattern (no chrome rebuild needed —
            // the play sites read the fields directly). Missing audio stays silent.
            async Task Clip(string url, System.Action<AudioClip> assign)
            {
                if (string.IsNullOrEmpty(url)) return;
                try { assign(await Assets.LoadAudioAsync(url, _cts.Token)); }
                catch { /* silent if the host ships no audio */ }
            }
            if (_sndClick == null) await Clip(Theme.ClickSoundUrl, c => _sndClick = c);
            if (_sndChoice == null) await Clip(Theme.ChoiceSoundUrl, c => _sndChoice = c);
            if (_sndType == null) await Clip(Theme.TypeSoundUrl, c => _sndType = c);
        }

        // Resolve the theme's typeface (manifest ui.dialogue.font) when no
        // explicit Font is assigned. Two forms:
        //   "MyFont"              — a font baked into the build (Resources);
        //   "/content/fonts/x.ttf" — a CUSTOM font served with the content —
        // downloaded into the disk cache (offline-safe like every other asset),
        // loaded from the file, applied via a chrome rebuild, and warmed with
        // the current chapter's glyph corpus so the late arrival doesn't hitch.
        private string _fontUrlLoading; // content url already being fetched (dedup)

        private void ResolveFont()
        {
            if (Theme == null || Theme.Font != null || string.IsNullOrEmpty(Theme.FontResourcePath)) return;
            var src = Theme.FontResourcePath;
            if (src.StartsWith("/"))
            {
                if (_fontUrlLoading == src) return; // fetch already in flight / done
                _fontUrlLoading = src;
                _ = LoadContentFontAsync(src);
                return;
            }
            Theme.Font = Resources.Load<Font>(src);
        }

        private async Task LoadContentFontAsync(string url)
        {
            try
            {
                var ca = Assets as CachingAssets;
                if (ca == null) return;
                var path = await ca.EnsureCachedFileAsync(url, _cts != null ? _cts.Token : default);
                var font = LvnFonts.FromFile(path);
                // The theme may have been swapped while the font downloaded —
                // only apply if it still asks for this exact url.
                if (font == null || Theme == null || Theme.FontResourcePath != url) return;
                Theme.Font = font;
                LvnFonts.Prewarm(font, _prewarmCorpus); // chapter may already be playing
                RebuildChrome(); // dialogue/choices re-skin with the new face
            }
            catch { /* best-effort: the panel default font keeps rendering */ }
            // Release the dedup guard: per-chapter theme rebuilds null out
            // Theme.Font, and the NEXT ResolveFont must be able to re-apply —
            // by then it's a cache hit (file on disk + LvnFonts path cache).
            finally { _fontUrlLoading = null; }
        }

        // A content-served font for ONE element (`text … font="/content/…"`):
        // fetched into the cache, applied when ready. A cached font lands the
        // same frame; a cold one swaps the face a moment after the label shows.
        private async Task ApplyContentFontAsync(VisualElement el, string url)
        {
            try
            {
                var ca = Assets as CachingAssets;
                if (ca == null) return;
                var path = await ca.EnsureCachedFileAsync(url, _cts != null ? _cts.Token : default);
                var font = LvnFonts.FromFile(path);
                if (font == null || el == null || el.panel == null) return;
                LvnFonts.Apply(el, font);
                LvnFonts.Prewarm(font, _prewarmCorpus);
            }
            catch { /* label keeps the theme face */ }
        }

        private void OnDisable()
        {
            _cts?.Cancel();
            if (_player != null) _player.OnSay -= RecordSay;
            if (_choices != null) _choices.OnSelected -= OnChoiceSelected;
            if (_dialogue != null) _dialogue.RevealTicked -= OnRevealTicked;
            LvnPrefs.Changed -= OnPrefsChanged;
            LvnWardrobe.Changed -= OnWardrobeChanged;

            // UIDocument tears its panel down on disable and brings up a FRESH
            // empty root on the next enable — everything Build() made is orphaned
            // on the dead one. Drop the flag and the objects that outlive the
            // panel, so the next enable rebuilds the chrome (and re-renders a
            // live player's current beat) instead of leaving a blank stage.
            if (_built)
            {
                _built = false;
                _renderer?.Teardown();
                _renderer = null;
                _world = null;
                _uiRoot = null;
                _menu = null;
                _labelLayer = null;
                _hintHide?.Pause(); _hintHide = null;
                _hintCard = null; _hintLabel = null;
                _labelEls.Clear();
                _labelTmpl.Clear();
                if (_audio != null) { Destroy(_audio); _audio = null; }
            }
        }

        private void OnPrefsChanged()
        {
            _dialogue?.SetUserOpacity(LvnPrefs.DialogOpacity);
        }

        // ── skip (fast-forward) ──────────────────────────────────────────────
        // The genre's re-read gear: lines fly by until something needs the
        // player — a choice, a tap, the chapter's end, an opened menu.

        /// <summary>True while fast-forward is running.</summary>
        public bool Skipping { get; private set; }

        /// <summary>Fast-forward lines until a choice, a tap, or the chapter ends.</summary>
        public void StartSkip()
        {
            if (_player == null || _player.Finished) return;
            Skipping = true;
        }

        public void StopSkip() => Skipping = false;

        private void SkipTick()
        {
            if (!Skipping) return;
            if (_player == null || _player.Finished || _player.AtChoice)
            {
                Skipping = false; // something needs the player — gear down
                return;
            }
            if (InputBlocked || _chromeHidden || _awaitingWait || _awaitingInput) return; // paused, not cancelled
            if (_dialogue != null && _dialogue.IsRevealing) { _dialogue.Complete(); return; }
            if (_awaitingTap)
            {
                _awaitingTap = false;
                _player.Advance();
            }
        }

        // ── auto-advance ─────────────────────────────────────────────────────
        // Reading delay after the reveal completes, scaled by line length and the
        // player's preference — the standard hands-free mode.
        private float _autoRevealDoneAt = -1f;
        private int _lastSayLength;

        /// <summary>Extra gate a host/menu can close to hold auto-advance (and
        /// tap handling) while an overlay is up. An open shared panel
        /// (<see cref="VnPanelHost"/>) blocks implicitly, so a wardrobe sheet or
        /// in-script screen can't be tapped/auto-advanced through.</summary>
        public bool InputBlocked
        {
            get => _inputBlockedFlag || (_panelHost != null && _panelHost.IsOpen);
            set => _inputBlockedFlag = value;
        }
        private bool _inputBlockedFlag;

        /// <summary>Set when the player asks to leave the chapter (the quick
        /// menu's Exit). The host's play loop watches it and returns to the
        /// title screen; position and stats are already autosaved, so Continue
        /// brings the player back to this exact line.</summary>
        public bool ExitRequested { get; private set; }

        /// <summary>Player-initiated exit to the menu: autosave the position,
        /// then signal the host loop.</summary>
        public void RequestExit()
        {
            AutosaveNow();
            ExitRequested = true;
        }

        /// <summary>Host acknowledgment — called by the play loop once it has
        /// acted on the request (and by Play for a fresh chapter).</summary>
        public void ClearExitRequest() => ExitRequested = false;

        // ── look-ahead prefetch ──────────────────────────────────────────────
        // While the player reads a line, warm the assets the next few beats will
        // show — the decode happens during the pause, so a cold bg/portrait never
        // pops in a frame late mid-scene. Bounded per beat (the sprite cache and
        // in-flight dedup make repeats free); the set resets with the stage.
        private readonly HashSet<string> _prefetched = new HashSet<string>();

        private void PrefetchAhead()
        {
            if (_player == null || Assets == null) return;
            const int lookAhead = 25, maxSprites = 6, maxAudio = 2;
            List<string> sprites = null, audio = null;
            bool spineKicked = false;
            foreach (var c in _player.PeekForward(lookAhead))
            {
                var op = (string)c["op"];
                if (op == "bg" || op == "actor" || op == "obj")
                {
                    var url = (string)c["sprite_url"];
                    // A Spine actor carries no sprite_url — its (heavy) assets
                    // live in the catalog. Warm the WHOLE scene (json + atlas +
                    // every page + bg, 2K variants preferred): the un-prefetched
                    // SECOND page of a multi-page atlas used to decode
                    // synchronously in the reveal frame. Fire-and-forget,
                    // deduped per actor id — and at most ONE new spine per pass:
                    // each page decode is a main-thread hit, and kicking several
                    // scenes at once stacked those hits into a visible stutter
                    // right at chapter entry. Later beats warm the rest.
                    if (string.IsNullOrEmpty(url) && (op == "actor" || op == "obj"))
                    {
                        var sp = Catalog?.Get((string)c["id"]);
                        if (sp != null && sp.kind == "spine" && sp.spine != null)
                        {
                            var spineId = (string)c["id"];
                            // A skeleton that's already built (e.g. the scene
                            // currently showing right after a resume, when
                            // _prefetched starts empty) must not eat the
                            // one-per-pass slot — otherwise the pause before
                            // the NEXT scene warms nothing and its build lands
                            // cold in the reveal frame.
                            if (_spineActors.TryGetValue(spineId, out var builtGo) && builtGo != null)
                            {
                                _prefetched.Add("spine:" + spineId);
                                continue;
                            }
                            if (!spineKicked && _prefetched.Add("spine:" + spineId))
                            {
                                spineKicked = true;
                                _ = PrefetchSpineAsync(spineId, sp);
                            }
                            continue;
                        }
                    }
                    // A LAYERED catalog character (id-based, no sprite_url) was a
                    // prefetch blind spot — its five layers all fetched cold in
                    // the reveal frame. Resolve the layers it will show and warm
                    // their bytes like any direct url.
                    if (string.IsNullOrEmpty(url) && Catalog != null)
                    {
                        var cid = (string)c["id"];
                        if (!string.IsNullOrEmpty(cid) && Catalog.Has(cid))
                        {
                            try
                            {
                                if (op == "bg")
                                {
                                    foreach (var u in Catalog.Resolve(cid, AxesFrom(c), CatalogCond()))
                                        if (!string.IsNullOrEmpty(u) && _prefetched.Add(u))
                                            (sprites ??= new List<string>()).Add(u);
                                }
                                else
                                {
                                    foreach (var rl in Catalog.ResolveLayers(cid, AxesOf(c), CatalogCond()))
                                        if (!string.IsNullOrEmpty(rl.Url) && _prefetched.Add(rl.Url))
                                            (sprites ??= new List<string>()).Add(rl.Url);
                                }
                            }
                            catch { /* a bad catalog entry must not kill the prefetch */ }
                        }
                        continue;
                    }
                    if (string.IsNullOrEmpty(url) || !_prefetched.Add(url)) continue;
                    (sprites ??= new List<string>()).Add(url);
                }
                else if (op == "audio")
                {
                    var url = (string)c["url"];
                    if (string.IsNullOrEmpty(url) || !_prefetched.Add(url)) continue;
                    (audio ??= new List<string>()).Add(url);
                }
                if ((sprites?.Count ?? 0) >= maxSprites && (audio?.Count ?? 0) >= maxAudio) break;
            }
            if (sprites != null && sprites.Count > maxSprites) sprites.RemoveRange(maxSprites, sprites.Count - maxSprites);
            if (audio != null && audio.Count > maxAudio) audio.RemoveRange(maxAudio, audio.Count - maxAudio);
            if (sprites != null) _ = Assets.PreloadAsync(sprites, "sprite", _cts.Token);
            if (audio != null) _ = Assets.PreloadAsync(audio, "audio", _cts.Token);
        }

        // Chapter-entry warmup for PLAIN art — the sibling of WarmUpcomingSpineAsync.
        // The loading screen stages the BYTES onto disk; this DECODES the first
        // beats' background and character layers into the sprite cache behind the
        // entry fade, so the opening beats never pay a decode in the reveal frame.
        // Spine entities are skipped (their own warmup builds the full skeleton).
        internal async Task WarmUpcomingArtAsync(int lookAhead, int maxSprites = 12)
        {
            if (_player == null || Assets == null) return;
            var urls = new List<string>();
            var seen = new HashSet<string>();
            void Take(string u)
            {
                if (!string.IsNullOrEmpty(u) && seen.Add(u) && urls.Count < maxSprites) urls.Add(u);
            }
            foreach (var c in _player.PeekForward(lookAhead))
            {
                var op = (string)c["op"];
                if (op != "bg" && op != "actor" && op != "obj") continue;
                Take((string)c["sprite_url"]);
                var id = (string)c["id"];
                if (!string.IsNullOrEmpty(id) && Catalog != null && Catalog.Has(id))
                {
                    var e = Catalog.Get(id);
                    if (e == null || e.kind != "spine")
                    {
                        try
                        {
                            if (op == "bg")
                                foreach (var u in Catalog.Resolve(id, AxesFrom(c), CatalogCond())) Take(u);
                            else
                                foreach (var rl in Catalog.ResolveLayers(id, AxesOf(c), CatalogCond())) Take(rl.Url);
                        }
                        catch { /* a bad catalog entry must not kill the warmup */ }
                    }
                }
                if (urls.Count >= maxSprites) break;
            }
            if (urls.Count == 0) return;
            var loads = new List<Task>(urls.Count);
            foreach (var u in urls) loads.Add(WarmOneAsync(u));
            await Task.WhenAll(loads);

            async Task WarmOneAsync(string u)
            {
                try { await Assets.LoadSpriteAsync(u, _cts.Token); }
                catch { /* warmup is best-effort */ }
            }
        }

        private void AutoAdvanceTick()
        {
            if (!LvnPrefs.AutoAdvance || InputBlocked || _chromeHidden
                || _player == null || _player.Finished || _player.AtChoice
                || !_awaitingTap || _awaitingWait || _awaitingInput
                || EntryGatePending
                || _dialogue == null || _dialogue.IsRevealing)
            {
                _autoRevealDoneAt = -1f;
                return;
            }
            // First tick after the reveal finished: start the reading timer.
            if (_autoRevealDoneAt < 0f)
            {
                _autoRevealDoneAt = Time.realtimeSinceStartup;
                return;
            }
            float delay = (0.55f + 0.035f * _lastSayLength) * LvnPrefs.AutoDelayScale;
            if (Time.realtimeSinceStartup - _autoRevealDoneAt < delay) return;
            _autoRevealDoneAt = -1f;
            _awaitingTap = false;
            _player.Advance();
        }

        private void OnDestroy()
        {
            Assets?.UnloadAll();
            // The spine integration's static cache holds SkeletonData/materials
            // built around textures UnloadAll just destroyed — flush it with them.
            LvnSpineBridge.ClearCache?.Invoke();
            if (_pendingThumb != null) Destroy(_pendingThumb);
        }

        /// <summary>
        /// Live-edit hot-swap: replace the running chapter's script WITHOUT
        /// restarting it, when the edit didn't change the command structure. The
        /// player keeps its position, variables and call stack, the stage keeps its
        /// current background/actors, and the beat on screen is re-rendered so an
        /// edit to the visible line shows at once. Returns false when nothing is
        /// playing or the structure changed — the caller should then <see
        /// cref="Play"/> from the top.
        /// </summary>
        public bool TryHotSwap(string lvnJson)
        {
            if (_player == null || _player.Finished) return false;
            LvnDocument doc;
            try { doc = LvnDocument.Parse(lvnJson); }
            catch { return false; }
            if (!_player.TryReplaceScript(doc)) return false;
            _cast = SpriteComposer.ParseCast(doc.Cast); // cast metadata is safe to refresh in place
            _player.RerenderCurrent();
            return true;
        }

        /// <summary>Wipe the stage to a clean slate NOW — actors, background, FX,
        /// dialogue. The host calls this when a chapter starts (before the script
        /// finishes downloading) so the previous chapter never lingers during the
        /// load, not only when the previous one ended.</summary>
        public void ClearStage()
        {
            if (!_built) return;
            ResetStage();
            _sayUp = false;
            _curChoices = null;
            _dialogue?.SetSpeaker(null);
            _dialogue?.SetText(string.Empty);
        }

        // The dialogue frame is chrome for a LINE — between chapters (and while
        // the next chapter's script/art loads) there is no line, and the empty
        // skinned box floating over a bare stage read as a glitch. Hidden on
        // every stage reset, shown again by the first ShowSay.
        private void SetSayVisible(bool on)
        {
            if (_dialogue != null)
                _dialogue.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Persistent variables to preload into the next chapter BEFORE it
        /// runs (set by the host from its state store). With the imported global
        /// defaults marked `default:true`, these carried-in values survive the
        /// chapter's init block — so relationship/route/memory stats flow from one
        /// chapter to the next and across sessions.</summary>
        public Newtonsoft.Json.Linq.JObject SeedVars;

        /// <summary>Parse and start playing a .lvn document.
        /// <paramref name="warmIntroSpine"/>: pass false when a snapshot restore
        /// follows immediately (resume/load) — the intro warmup would otherwise
        /// build the CHAPTER-OPENING spine that the restore is about to discard,
        /// doubling the entry wait with a scene the player isn't even on.</summary>
        public void Play(string lvnJson, bool warmIntroSpine = true)
        {
            var doc = LvnDocument.Parse(lvnJson);
            LvnPlayer.Log?.Invoke("════ PLAY scene=" + doc.Scene + " (" + (doc.Script?.Count ?? 0) + " cmds) ════");
            ExitRequested = false; // a fresh chapter is a fresh run
            _entryGateArmed = true; // the first say defers to the entry choreography
            _cast = SpriteComposer.ParseCast(doc.Cast);
            PrewarmGlyphs(doc); // rasterize the chapter's glyphs NOW, not mid-typewriter
            ResetStage();
            _player = new LvnPlayer(doc, this);
            _player.Strings = Strings; // localization catalog (text_id → string), if any
            if (SeedVars != null)      // carry stats in before the init defaults run
                foreach (var p in SeedVars.Properties()) _player.Vars[p.Name] = p.Value;
            _player.OnSay += RecordSay;
            ++_startGen;
            // warmIntroSpine=false ⇒ a RestoreSnapshot follows immediately and
            // advances via ContinueFrom. Running the intro here anyway (the old
            // behaviour) kicked the chapter-opening spine's build just to have
            // the restore reset it — the player watched the WRONG scene load
            // before their saved one.
            if (warmIntroSpine) StartWithSpineWarmup(_player, _startGen);
        }

        // The dialogue font is a DYNAMIC SDF asset — a glyph seen for the first
        // time rasterizes into the atlas on the render thread, a visible hitch in
        // the middle of a typewriter reveal. The chapter's full text corpus is
        // known here (script + localization catalog), so bake every distinct
        // character into the atlas up-front, behind the loading screen.
        private string _prewarmCorpus = ""; // kept so a late-arriving theme font warms too

        private void PrewarmGlyphs(LvnDocument doc)
        {
            var sb = new System.Text.StringBuilder(8192);
            if (doc?.Script != null)
                foreach (var c in doc.Script)
                {
                    if (!(c is JObject o)) continue;
                    sb.Append((string)o["text"]).Append((string)o["who"]);
                    if (o["options"] is JArray opts)
                        foreach (var opt in opts)
                            sb.Append((string)opt["text"]).Append((string)opt["cost"]);
                }
            if (Strings != null)
                foreach (var v in Strings.Values) sb.Append(v);
            _prewarmCorpus = sb.ToString();
            if (Theme != null && Theme.Font != null) // else: warms when the font arrives
                LvnFonts.Prewarm(Theme.Font, _prewarmCorpus);
        }

        // Bumped by every fresh start AND every snapshot restore, so a pending
        // intro warmup can tell its run was superseded. Pinning the player
        // reference alone is not enough: a resume REUSES the player Play just
        // created, and the stale warmup's Advance() would push the restored
        // chapter one beat past its saved position.
        private int _startGen;

        // The staged opening: everything the first beats show is built hidden
        // BEFORE the intro advances — the first Spine scene (skeleton build) AND
        // the plain art (background + character layers, decoded into the sprite
        // cache) warm in parallel behind the entry fade. Otherwise the
        // typewriter starts and then freezes mid-sentence while art decodes.
        // Capped: a dead network can't hold the intro hostage — whatever missed
        // the window loads on-demand exactly as before.
        private async void StartWithSpineWarmup(LvnPlayer player, int gen)
        {
            // Plain art warms in the BACKGROUND — it races the reader, never the
            // intro (holding the first beat hostage to 12 decodes read as a
            // multi-second black screen). Only an imminent Spine scene gates the
            // start: its skeleton build is the one cost that visibly freezes the
            // typewriter mid-line if it lands cold.
            _ = WarmUpcomingArtAsync(12);
            try { await WarmUpcomingSpineAsync(12); }
            catch (System.OperationCanceledException) { return; }
            catch { /* warmup is best-effort; the show path reloads what it needs */ }
            if (_player == player && _startGen == gen) player.Advance();
        }

        /// <summary>
        /// Wipe the stage to a clean slate before a chapter plays. Without this,
        /// actors, the background and effect veils left on screen by the previous
        /// chapter (or a live hot-reload) bleed into the new one — e.g. a character
        /// standing on the very first beat, before any <c>actor</c> command runs.
        /// </summary>
        // Bumped on every stage reset (chapter change / load). An async content
        // apply (bg, actor, spine, audio) captures it before its first await and
        // bails if it changed — otherwise a slow load from the PREVIOUS chapter
        // resolves after the reset and paints the new one (ghost actor, wrong bg,
        // wrong music). The shared _cts only cancels on OnDisable, not here.
        private int _stageEpoch;

        /// <summary>True if <paramref name="epoch"/> is still the current stage
        /// generation — a content apply calls this after each await and stops
        /// touching the stage once it's stale.</summary>
        private bool StageCurrent(int epoch) => _stageEpoch == epoch;

        private void ResetStage()
        {
            _stageEpoch++; // supersede any in-flight content apply from the old scene
            // Close the quick menu FIRST: it may be mid-open (IsOpen + InputBlocked
            // set, its clean-frame screenshot coroutine pending). The StopAllCoroutines
            // below would kill that coroutine before its OpenSheetChrome callback,
            // stranding InputBlocked=true forever — a soft-lock. Close() resets both.
            _menu?.Close();
            // Kill any in-flight `wait` coroutine — it reads the _player field, so
            // after Play() swaps in a new player it would otherwise fire Advance()
            // on the fresh chapter when its old timer elapses.
            StopAllCoroutines();
            _hotspots.Clear();
            HasBackdrop = false;
            _renderer?.RemoveAll();
            _renderer?.ResetCamera(0f);
            _talkAnims.Clear();
            _renderer?.ClearBackground();
            _particles?.Set("rain", false);
            _particles?.Set("snow", false);
            _fx?.Clear(0f);
            _fx?.ClearBlur(0f);
            _backlog.Clear();
            _prefetched.Clear(); // the next chapter/load re-warms from scratch
            SetChromeHidden(false); // never carry a hidden UI across a reset
            StopSkip();             // fast-forward dies with the scene it was skipping
            _awaitingTap = false;
            _awaitingWait = false;
            _sayUp = false;
            SetSayVisible(false);
            _curChoices = null;
            StopChoiceTimer();
            CloseInput();
            _awaitingInput = false;
            _audio?.StopVoice();
            _draggables.Clear();
            _placements.Clear();
            _actorCmds.Clear();
            _dragId = null;
            _dragCandidate = null;
            foreach (var kv in _spineActors) if (kv.Value != null) Destroy(kv.Value);
            UnpinAllSpinePages(); // release page-texture pins so the LRU can reclaim them
            _spineActors.Clear();
            _spineLoading.Clear();
            _spinePendingPlay.Clear();
            _choices?.Dismiss(); // clear any on-screen choice buttons (avoid stale clicks)
            _labelLayer?.Clear();
            _labelEls.Clear();
            _labelTmpl.Clear();
            _hintHide?.Pause(); _hintHide = null;
            _hintCard = null; _hintLabel = null; // detached by the Clear above
        }

        private void RecordSay(string who, string text, string style)
        {
            // After a rollback, the restored beat re-runs and would duplicate its
            // own backlog entry — swallow exactly that one repeat.
            if (_suppressDupSay)
            {
                _suppressDupSay = false;
                if (_backlog.Count > 0)
                {
                    var last = _backlog[_backlog.Count - 1];
                    if (last.who == who && last.text == text) return;
                }
            }
            _backlog.Add((who, text, style));
            // Read tracking: remember the line, and if fast-forward is in
            // read-only gear, stop it the moment something NEW comes up — the
            // line stays on screen with its typewriter, exactly where the
            // player's actual reading resumes.
            bool wasNew = LvnReadStore.MarkRead(_saveTitleId, who, text);
            if (wasNew && Skipping && LvnPrefs.SkipReadOnly) StopSkip();
            // Rolling autosave so a crash mid-scene loses a few lines at most.
            if (++_saySinceAutosave >= 5)
            {
                _saySinceAutosave = 0;
                AutosaveNow();
            }
        }

        private bool _suppressDupSay;

        /// <summary>True when there is a previous beat to roll back to.</summary>
        public bool CanRollback => _player != null && _player.CanRollback && !_awaitingWait;

        /// <summary>Step one beat back (a mis-tap safety net): restore the previous
        /// say/choice's snapshot — variables as they were BEFORE it ran, so a picked
        /// option's set/inc is undone — rebuild the scene there and re-show it.
        /// Returns false when already at the first beat.</summary>
        public bool RollbackStep() => RollbackSteps(1);

        /// <summary>Roll back several beats in one hop (clamped to the recorded
        /// history) — the History panel's tap-to-return. The same recipe as a
        /// single step, but one scene rebuild instead of N.</summary>
        public bool RollbackSteps(int steps)
        {
            if (_player == null || _awaitingWait || steps < 1) return false;
            int actual = Mathf.Min(steps, _player.HistoryDepth - 1);
            var snap = _player.PopRollback(actual);
            if (snap == null) return false;

            // ResetStage wipes the dialogue history; a rewind must keep it minus
            // the beats being undone (their re-runs are dedup'd in RecordSay).
            var kept = new List<(string who, string text, string style)>(_backlog);
            for (int i = 0; i < actual; i++)
            {
                if (kept.Count > 0) kept.RemoveAt(kept.Count - 1);
                // A trailing choice mark belongs to the pick being undone: the
                // options re-present and the (re-)pick records a fresh mark.
                while (kept.Count > 0 && kept[kept.Count - 1].style == "choice")
                    kept.RemoveAt(kept.Count - 1);
            }

            ResetStage();
            _backlog.AddRange(kept);
            _suppressDupSay = true;

            _player.Restore(snap);
            int at = _player.Index;
            _player.ReplayVisuals(at);
            _player.ContinueFrom(at);
            return true;
        }

        // ── press handling: tap advances, a LONG press hides the UI ─────────
        // The genre staple: hold anywhere and the whole chrome (dialogue box,
        // choices, HUD labels, quick menu — and the shell HUD via the event)
        // fades away so the player can admire the art; release brings it back,
        // and that release never counts as a tap. Because a press can now mean
        // two things, the tap action fires on POINTER UP, not down.

        private const long LongPressMs = 450;
        private const float PressDriftSq = 400f; // ~20px of drift cancels tap & hold

        // UI sounds (manifest ui.sounds), loaded by EnsureThemeImagesAsync. The
        // typewriter blip is throttled by wall time: the reveal event fires on
        // eighth-glyph steps, far denser than a blip can stay pleasant.
        private AudioClip _sndClick, _sndChoice, _sndType;
        private float _lastTypeBlip;
        private const float TypeBlipMinGap = 0.055f;

        private void PlayUiSound(AudioClip clip) =>
            _audio?.PlayUi(clip, Theme != null ? Theme.UiSoundVolume : 1f);

        private void OnRevealTicked()
        {
            if (_sndType == null) return;
            if (_audio != null && _audio.VoicePlaying) return; // the actor speaks — no blip under it
            float now = Time.unscaledTime;
            if (now - _lastTypeBlip < TypeBlipMinGap) return;
            _lastTypeBlip = now;
            PlayUiSound(_sndType);
        }

        private bool _chromeHidden;
        private bool _pressTracking, _suppressTap;
        private Vector2 _pressPos;
        private IVisualElementScheduledItem _longPress;

        /// <summary>Raised after any successful save (autosave, quick, slots) —
        /// the host's hook for cloud sync / analytics. Argument: the slot name.</summary>
        public event Action<string> Saved;

        // ── the shared bottom window (VnPanelHost) ───────────────────────────
        // One dialogue-skinned frame on the dialogue layer that hosts ANY
        // content (wardrobe, shop, minigames): showing it fades the dialogue
        // out and slides the frame up; new content cross-fades inside the same
        // frame. Lazily created; dropped with the chrome on rebuild.
        private VnPanelHost _panelHost;

        /// <summary>The stage's shared content window (created on demand, on
        /// the dialogue layer, wearing the dialogue's exact skin).</summary>
        public VnPanelHost PanelHost
        {
            get
            {
                if (_panelHost == null)
                {
                    _panelHost = new VnPanelHost(Theme);
                    var root = GetComponent<UIDocument>()?.rootVisualElement;
                    if (root != null)
                    {
                        int fxIndex = _fx != null ? root.IndexOf(_fx) : -1;
                        root.Insert(fxIndex < 0 ? root.childCount : fxIndex, _panelHost);
                    }
                }
                return _panelHost;
            }
        }

        /// <summary>Show host content in the shared window: the dialogue fades
        /// out and the same-skinned frame takes its place (or cross-fades from
        /// whatever it was already showing).</summary>
        public async Task ShowPanelAsync(VisualElement content)
        {
            SetDialogueFaded(true);
            await PanelHost.ShowAsync(content);
        }

        /// <summary>Dismiss the shared window and bring the dialogue back.</summary>
        public async Task HidePanelAsync()
        {
            if (_panelHost != null) await _panelHost.HideAsync();
            SetDialogueFaded(false);
        }

        /// <summary>Fade the dialogue box (and choices) out/in — the shared
        /// window replaces it visually, so both never fight for the bottom.</summary>
        public void SetDialogueFaded(bool faded)
        {
            float to = faded ? 0f : 1f;
            if (_dialogue != null)
                _ = ScreenFx.FadeAsync(_dialogue, faded ? 1f : 0f, to, 0.18f, _cts?.Token ?? default);
            if (_choices != null)
                _ = ScreenFx.FadeAsync(_choices, faded ? 1f : 0f, to, 0.18f, _cts?.Token ?? default);
        }

        /// <summary>Raised when the long-press art view hides/shows the chrome —
        /// the host mirrors it onto its own HUD.</summary>
        public event Action<bool> ChromeHiddenChanged;

        /// <summary>Fires when the choice list appears/disappears — the shell's
        /// reading-mode HUD listens (visible while a priced choice is up).</summary>
        public event Action<bool> ChoicesVisibleChanged;

        private void OnChoicesVisibleChanged(bool visible) => ChoicesVisibleChanged?.Invoke(visible);

        private void SetChromeHidden(bool hidden)
        {
            if (_chromeHidden == hidden) return;
            _chromeHidden = hidden;
            var vis = hidden ? Visibility.Hidden : Visibility.Visible;
            if (_dialogue != null) _dialogue.style.visibility = vis;
            if (_choices != null) _choices.style.visibility = vis;
            if (_labelLayer != null) _labelLayer.style.visibility = vis;
            if (_menu != null) _menu.style.visibility = vis;
            ChromeHiddenChanged?.Invoke(hidden);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (InputBlocked) return; // an overlay (quick menu) owns the screen
            if (_player == null || _player.Finished) return;
            if (_awaitingWait || _awaitingInput) return;
            if (evt.target is Button) return; // buttons (choices etc.) own their press

            _pressTracking = true;
            _suppressTap = false;
            _pressPos = evt.position;

            // A press on a draggable object arms a drag CANDIDATE: below the
            // drift threshold a release is still a tap (on_click works); past it
            // the object starts following the pointer instead.
            _dragCandidate = DraggableAt(evt.position);

            _longPress?.Pause();
            _longPress = _uiRoot?.schedule.Execute(() =>
            {
                if (!_pressTracking || _dragId != null) return;
                _suppressTap = true;      // this press is an art view, not a tap
                SetChromeHidden(true);
            });
            _longPress?.ExecuteLater(LongPressMs);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_pressTracking) return;
            if (_dragId != null) { DragMove(evt.position); return; }
            if (((Vector2)evt.position - _pressPos).sqrMagnitude <= PressDriftSq) return;
            _suppressTap = true; // a drag is neither a tap nor a hold
            _longPress?.Pause();
            if (_dragCandidate != null) DragBegin(_dragCandidate, evt.position);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            bool wasTracking = _pressTracking;
            _pressTracking = false;
            _longPress?.Pause();
            _dragCandidate = null;

            if (_dragId != null) { DragEnd(evt.position); return; }
            if (_chromeHidden) { SetChromeHidden(false); return; } // release restores, swallows the tap
            if (!wasTracking || _suppressTap) return;
            if (Skipping) { StopSkip(); return; } // a tap during fast-forward just stops it
            HandleTap(evt.position);
        }

        private void OnPointerCancelled()
        {
            // Touch cancelled / capture lost mid-hold — never strand a hidden UI
            // or a half-dragged object.
            _pressTracking = false;
            _dragCandidate = null;
            if (_dragId != null) DragEnd(_pressPos);
            _longPress?.Pause();
            SetChromeHidden(false);
        }

        private void HandleTap(Vector2 pos)
        {
            if (InputBlocked) return;
            if (EntryGatePending) return; // the chapter-title card owns the screen
            if (_player == null || _player.Finished) return;
            if (_awaitingWait || _awaitingInput) return;

            // Canvas-scene hotspots: there's no uGUI raycaster, so a tap is routed
            // here. Test it against each obj's normalized placement rect (top-left
            // origin, matching both placement.Y and UITK's y-down). Topmost
            // (last-placed) wins; a hit fires its on_click and swallows the advance.
            // A point-and-click screen (the Canvas scene has registered hotspots):
            // only hotspots act. A hit fires its on_click; a miss is IGNORED (it must
            // not advance/re-print the room). Hotspots win over tap-to-advance.
            if (_hotspots.Count > 0 && _uiRoot != null)
            {
                float pw = _uiRoot.layout.width, ph = _uiRoot.layout.height;
                var hit = HotspotAt(pos, pw, ph);
                if (hit != null)
                {
                    LvnPlayer.Log?.Invoke($"[click {pos.x:0},{pos.y:0} of {pw:0}x{ph:0}] → HOTSPOT");
                    // Hotspots stay armed (no clear): clicking another object jumps
                    // straight to it (its on_click GoTo overrides the cursor), so no
                    // phantom "dismiss" tap is needed. A MISS falls through to the
                    // normal tap-advance below — so descriptions and the ending are
                    // still dismissable by tapping empty space.
                    if (_dialogue.IsRevealing) _dialogue.Complete();
                    hit();
                    return;
                }
                LvnPlayer.Log?.Invoke($"[click {pos.x:0},{pos.y:0} of {pw:0}x{ph:0}] → miss → advance");
                // fall through to tap-to-advance
            }

            if (_dialogue.IsRevealing) { PlayUiSound(_sndClick); _dialogue.Complete(); return; }
            if (_awaitingTap)
            {
                PlayUiSound(_sndClick);
                _awaitingTap = false;
                _player.Advance();
            }
        }

        // The hotspot under a pointer — topmost (last-placed) first; null if none.
        // Works from the EVENT position (not Input.mousePosition, which is dead in
        // the Device Simulator / touch). Both the pointer and each actor's real
        // on-screen rect are normalized to 0..1 top-left, so it's independent of
        // pixel scale and aspect (and panel-vs-canvas coordinate differences).
        private System.Action HotspotAt(Vector2 panelPos, float panelW, float panelH)
        {
            if (_renderer == null || panelW <= 0f || panelH <= 0f) return null;
            float nx = panelPos.x / panelW, ny = panelPos.y / panelH; // UITK: top-left, y-down
            for (int i = _hotspots.Count - 1; i >= 0; i--)
            {
                // Renderer-normalized rect (0..1, top-left origin); null when the
                // renderer does its own picking or the actor is gone.
                var r = _renderer.ActorScreenRect(_hotspots[i].id);
                if (r == null) continue;
                if (r.Value.Contains(new Vector2(nx, ny))) return _hotspots[i].onClick;
            }
            return null;
        }


        /// <summary>Host hook clearing a REAL (wallet-priced) option: spend
        /// <c>amount</c> of <c>currency</c>, true on success. Null → priced
        /// options pick through for free (engine-only setups stay playable).</summary>
        public Func<string, long, Task<bool>> ChoiceSpend;

        private void OnChoiceSelected(int index)
        {
            LvnOption picked = default;
            bool found = false;
            if (_curChoices != null)
                foreach (var o in _curChoices)
                    if (o.Index == index) { picked = o; found = true; break; }

            // A wallet-priced option must clear the spend BEFORE it consumes the
            // choice — a refused spend leaves the menu up (nothing advanced).
            if (found && !string.IsNullOrEmpty(picked.WalletCurrency)
                && picked.WalletAmount > 0 && ChoiceSpend != null)
            {
                _ = SpendThenChooseAsync(index, picked);
                return;
            }
            CommitChoice(index, found ? picked.Text : null);
        }

        private async Task SpendThenChooseAsync(int index, LvnOption picked)
        {
            int epoch = _stageEpoch;
            bool paid = false;
            try { paid = await ChoiceSpend(picked.WalletCurrency, picked.WalletAmount); }
            catch { /* a wallet failure must never crash the choice UI */ }
            if (!StageCurrent(epoch) || _player == null || !_player.AtChoice) return;
            if (!paid)
            {
                ApplyHint(new JObject
                {
                    ["text"] = $"Не хватает {picked.WalletAmount} {picked.WalletCurrency}",
                    ["duration"] = 3
                });
                return; // menu stays up; the player picks something else
            }
            CommitChoice(index, picked.Text);
        }

        private void CommitChoice(int index, string pickedText)
        {
            StopChoiceTimer(); // the pick beat the clock
            PlayUiSound(_sndChoice != null ? _sndChoice : _sndClick);
            _choices.Dismiss();
            _curChoices = null;
            _awaitingTap = false;
            // Ignore a click on a stale button (the beat moved on via load/hot-reload
            // and these options no longer apply) instead of throwing.
            if (_player == null || !_player.AtChoice) return;
            // History: record which branch was taken (rendered as a marked line).
            if (!string.IsNullOrEmpty(pickedText)) _backlog.Add((null, pickedText, "choice"));
            _player.Choose(index);
            _player.Advance();
            // A picked branch is exactly what a crash must not lose — autosave here.
            AutosaveNow();
        }

        // ── ILvnStage ─────────────────────────────────────────────────────────

        /// <summary>Entry choreography gate, set by the host per chapter entry:
        /// the loader reveal + chapter-title card play OVER the dressed stage,
        /// and the FIRST line must not start typing under them. ShowSay defers
        /// its first reveal until this completes; taps and auto-advance hold
        /// too. Null = no hold (resume, cross-chapter load).</summary>
        public Task EntryGate;
        private bool _entryGateArmed; // only the first say of a run defers

        private bool EntryGatePending => EntryGate != null && !EntryGate.IsCompleted;

        private async Task DeferredFirstSayAsync(Task gate, string who, string text, string style)
        {
            int epoch = _stageEpoch;
            try { await gate; } catch { /* choreography failures never eat the line */ }
            if (!StageCurrent(epoch)) return; // chapter changed while the title played
            ShowSay(who, text, style);        // _entryGateArmed already consumed
        }

        public void ShowSay(string who, string text, string style)
        {
            if (_entryGateArmed)
            {
                _entryGateArmed = false;
                var gate = EntryGate;
                if (gate != null && !gate.IsCompleted)
                {
                    _ = DeferredFirstSayAsync(gate, who, text, style);
                    return; // the dressed stage waits under the title card
                }
            }
            SetSayVisible(true);
            _dialogue.SetSpeaker(who);
            _dialogue.ApplyStyle(style);
            _dialogue.SuppressAdvanceHint(false); // a plain line invites the tap again
            _dialogue.Reveal(text);
            // Voice-over: the line's clip starts with its text; the previous line's
            // voice stops (never overlaps). Silent lines just stop the old one.
            if (_audio != null)
                _ = _audio.PlayVoiceAsync(_player?.CurrentVoiceUrl, Assets, _cts != null ? _cts.Token : default);
            _lastSayLength = text?.Length ?? 0; // drives the auto-advance reading delay
            _autoRevealDoneAt = -1f;
            _awaitingTap = true;
            _sayUp = true;
            _curChoices = null;
            PrefetchAhead(); // warm the next beats' art/audio while the player reads

            // Classic VN focus: the speaker is at full brightness, everyone else
            // present dims — so a two-shot reads as "this one is talking" instead of
            // a flat row. actor_map'd speakers carry their true actor id (who_id);
            // without it the loose name↔slot key match applies.
            SceneHighlightSpeaker(_player?.CurrentSpeakerId ?? who);

            // Lip-sync: only the speaking actor's mouth moves while the line is up.
            var spId = _player?.CurrentSpeakerId ?? ResolveSpeakerId(who);
            foreach (var kv in _talkAnims) SceneTalk(kv.Key, kv.Value, kv.Key == spId);
        }

        // Scene calls go through the ISceneRenderer seam — path-specific behaviour
        // lives inside UitkSceneRenderer / CanvasSceneRenderer, not in per-call-site
        // conditionals here. These thin aliases keep historical call names readable.
        private void SceneSetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames) => _renderer?.SetFrames(id, frames);
        private void SceneEnsureIdle(string id, LvnAnim a) => _renderer?.EnsureIdle(id, a);
        private void SceneEnsureBlink(string id, LvnAnim a) => _renderer?.EnsureBlink(id, a);
        private void ScenePlayGesture(string id, LvnAnim g, LvnAnim idle) => _renderer?.PlayGesture(id, g, idle);
        private void ScenePlayAnim(string id, string channel, LvnAnim a) => _renderer?.PlayAnim(id, channel, a);
        private void ScenePlayAnimQueued(string id, string channel, LvnAnim a) => _renderer?.PlayAnimQueued(id, channel, a);
        private void SceneStopAnim(string id, string target) => _renderer?.StopAnim(id, target);
        private void SceneTalk(string id, LvnAnim t, bool on) => _renderer?.Talk(id, t, on);
        private void SceneHighlightSpeaker(string who) => _renderer?.HighlightSpeaker(who);

        // ── save / load ──────────────────────────────────────────────────────
        // `save [slot=name]` writes the player snapshot (cursor + vars + call stack)
        // to PlayerPrefs; `load [slot=name]` restores it, rebuilds the scene from the
        // saved point (ReplayVisuals) and resumes. Default slot is "quick".
        private string SaveKey(JObject cmd)
        {
            var slot = (string)cmd["slot"];
            // Namespaced by title id — two novels in one app (or the IDE preview
            // next to a game) must not read each other's quick saves.
            var ns = string.IsNullOrEmpty(_saveTitleId) ? "" : _saveTitleId + "_";
            return "lvn_save_" + ns + (string.IsNullOrEmpty(slot) ? "quick" : slot);
        }

        private void SaveSlot(JObject cmd)
        {
            if (_player == null) return;
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(_player.Save());
                PlayerPrefs.SetString(SaveKey(cmd), json);
                PlayerPrefs.Save();
                LvnPlayer.Log?.Invoke("saved → " + SaveKey(cmd));
            }
            catch (System.Exception e) { Debug.LogWarning("[lvn] save failed: " + e.Message); }
        }

        private void LoadSlot(JObject cmd)
        {
            if (_player == null) return;
            var json = PlayerPrefs.GetString(SaveKey(cmd), "");
            if (string.IsNullOrEmpty(json))
            {
                // Legacy fallback: saves written before keys were title-namespaced.
                var slot = (string)cmd["slot"];
                json = PlayerPrefs.GetString("lvn_save_" + (string.IsNullOrEmpty(slot) ? "quick" : slot), "");
            }
            LvnPlayer.LvnSnapshot snap = null;
            if (!string.IsNullOrEmpty(json))
                try { snap = Newtonsoft.Json.JsonConvert.DeserializeObject<LvnPlayer.LvnSnapshot>(json); }
                catch (System.Exception e) { Debug.LogWarning("[lvn] load parse failed: " + e.Message); }

            if (snap == null || snap.Vars == null)
            {
                _player.ContinueFrom(_player.Index + 1); // no/invalid save → skip the load op
                return;
            }
            RestoreSnapshot(snap);
        }

        /// <summary>Restore a snapshot of the CURRENT chapter in place: clean the
        /// stage, restore cursor/vars/call stack (position resolves via its label
        /// anchor), rebuild the scene's visuals/FX/audio up to that point, resume.
        /// The shared machinery behind the in-script `load` op, the save/load
        /// panel and the autosave resume.</summary>
        public async void RestoreSnapshot(LvnPlayer.LvnSnapshot snap)
        {
            // async void: an escaped exception here would take down the frame with
            // no caller to observe it — catch, log, and try to resume anyway.
            try { await RestoreSnapshotAsync(snap); }
            catch (System.Exception e)
            {
                Debug.LogError("[lvn] restore failed: " + e);
                try { _player?.Advance(); } catch { /* stage unusable; error already logged */ }
            }
        }

        private async Task RestoreSnapshotAsync(LvnPlayer.LvnSnapshot snap)
        {
            if (_player == null) return;
            if (snap == null) { _player.Advance(); return; } // no snapshot after all — play from the top (Play skipped its own advance expecting us)
            var player = _player;               // pin: a re-entry mid-await must not resume a dead run
            int gen = ++_startGen;              // supersede a pending intro warmup (see StartWithSpineWarmup)
            ResetStage();                       // clean slate
            player.Restore(snap);               // cursor (via label anchor) + vars + call stack
            player.ClearHistory();              // the rollback trail no longer describes the path here
            int at = player.Index;              // the anchor-relocated cursor, not the raw saved index
            at = player.ResumeRenderIndex(at);  // step back onto the say the player was reading (never skip a seen beat)
            player.ReplayVisuals(at);           // rebuild bg / actors / FX / audio up to the saved point
            // Veil the half-built stage: between ReplayVisuals and the settled
            // builds only the bg is up — the player saw that as a white flash
            // on resume. NOT alpha 0: the Canvas would cull the children's
            // draw calls and defeat the spine warm pulse (PSO compile +
            // texture upload need real draws); 1/255 is imperceptible but
            // keeps the GPU warm. (Same trick as LvnSpineFader.WarmAlpha —
            // that constant lives in the optional Spine assembly.)
            const float veilWarmAlpha = 1f / 255f;
            CanvasGroup veil = null;
            if (_renderer is CanvasSceneRenderer veilCanvas && veilCanvas.Root != null)
            {
                veil = veilCanvas.Root.GetComponent<CanvasGroup>();
                if (veil == null) veil = veilCanvas.Root.AddComponent<CanvasGroup>();
                veil.alpha = veilWarmAlpha;
            }
            // The staged opening, resume flavour: ReplayVisuals fires its spine
            // builds without awaiting them — rendering the saved beat now would
            // typewrite over a still-building stage and freeze mid-sentence.
            var t0 = Time.realtimeSinceStartup;
            int inFlight = PendingSpineBuilds;
            try { await SpineBuildsSettled(); } catch { }
            if (inFlight > 0)
                Debug.Log($"[lvn] resume warmed {inFlight} spine build(s) in {(Time.realtimeSinceStartup - t0):F2}s before rendering");
            if (veil != null)
            {
                // Reveal the fully built scene with a short fade instead of a pop.
                for (float a = veilWarmAlpha; a < 1f && _player == player && _startGen == gen; a += Time.unscaledDeltaTime / 0.15f)
                {
                    veil.alpha = a;
                    await Task.Yield();
                }
                // Only finish the reveal if we're STILL the current restore — a
                // newer one may have re-veiled this same canvas to warm-alpha, and
                // slamming it to 1 here would flash its half-built stage.
                if (_player == player && _startGen == gen) veil.alpha = 1f;
            }
            if (_player == player && _startGen == gen)
                player.ContinueFrom(at); // resume → renders the saved beat
        }

        // ── persistent save slots (per title, survive restarts) ─────────────

        /// <summary>Save-slot namespace + labels, set by the host per chapter entry
        /// (title id keys the slot store; script url tags snapshots so a slot is
        /// only restored into the chapter it belongs to).</summary>
        public void SetSaveContext(string titleId, string chapterId, string scriptUrl)
        {
            _saveTitleId = titleId;
            _saveChapterId = chapterId;
            _saveScriptUrl = scriptUrl;
        }

        private string _saveTitleId, _saveChapterId, _saveScriptUrl;
        private int _saySinceAutosave;

        /// <summary>The title id save slots are namespaced under (host-set).</summary>
        public string SaveTitleId => _saveTitleId;

        /// <summary>Write the current position into a named persistent slot.</summary>
        public bool SaveToSlot(string slot)
        {
            if (_player == null || string.IsNullOrEmpty(slot)) return false;
            var snap = _player.Save();
            snap.ScriptUrl = _saveScriptUrl;
            var last = _backlog.Count > 0 ? _backlog[_backlog.Count - 1].text : "";
            if (!LvnSaveStore.Put(_saveTitleId, slot, new LvnSaveSlot
            {
                Snap = snap,
                ChapterId = _saveChapterId,
                Preview = last,
            })) return false;
            // Manual slots get the scene screenshot captured when the menu
            // opened (null wipes a stale one). The rolling autosave doesn't —
            // its capture moment would be arbitrary.
            if (slot != LvnSaveStore.AutoSlot)
                LvnSaveStore.WriteThumb(_saveTitleId, slot, _pendingThumb);
            LvnPlayer.Log?.Invoke("saved slot '" + slot + "' @#" + snap.Index);
            Saved?.Invoke(slot); // host hook: cloud sync, achievements, analytics
            return true;
        }

        public bool LoadFromSlot(string slot)
        {
            var s = LvnSaveStore.Get(_saveTitleId, slot);
            if (s?.Snap == null || _player == null) return false;
            if (!string.IsNullOrEmpty(s.Snap.ScriptUrl) && s.Snap.ScriptUrl != _saveScriptUrl) return false;
            RestoreSnapshot(s.Snap);
            return true;
        }

        /// <summary>Host hook for loading a slot that belongs to ANOTHER chapter:
        /// resolve the chapter by <c>Snap.ScriptUrl</c>, fetch its script, play it
        /// and restore. Wired by NovelApp; when null, cross-chapter slots simply
        /// aren't loadable (greyed out in the menu).</summary>
        public Func<LvnSaveSlot, Task<bool>> CrossChapterLoader;

        /// <summary>Load a slot wherever it points: in-place for the current
        /// chapter, via <see cref="CrossChapterLoader"/> for another one.</summary>
        public async Task<bool> LoadFromSlotAsync(string slot)
        {
            if (LoadFromSlot(slot)) return true;
            var s = LvnSaveStore.Get(_saveTitleId, slot);
            if (s?.Snap == null || CrossChapterLoader == null) return false;
            try { return await CrossChapterLoader(s); }
            catch (Exception e)
            {
                Debug.LogWarning("[lvn] cross-chapter load failed: " + e.Message);
                return false;
            }
        }

        /// <summary>True when the slot exists and is reachable — taken in the
        /// current chapter, or in another one the host can route to.</summary>
        public bool CanLoadSlot(string slot)
        {
            var s = LvnSaveStore.Get(_saveTitleId, slot);
            if (s?.Snap == null) return false;
            if (string.IsNullOrEmpty(s.Snap.ScriptUrl) || s.Snap.ScriptUrl == _saveScriptUrl) return true;
            return CrossChapterLoader != null;
        }

        /// <summary>Autosave into the reserved slot now — called by the host on
        /// app pause, and internally on choices / every few lines.</summary>
        public void AutosaveNow()
        {
            if (_player == null || _player.Finished) return;
            SaveToSlot(LvnSaveStore.AutoSlot);
        }

        // A persistent reactive text label (`text id=… x= y= anchor= «{expr}»`): a
        // HUD/stat readout placed like an actor but living in the UITK overlay. Its
        // {expr} template is re-evaluated on the reactive tick, so the shown value
        // tracks the variable. Re-issuing the same id updates it; `hide` removes it.
        private void ApplyText(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id) || _labelLayer == null) return;

            if (BoolOr(cmd["hide"], false))
            {
                if (_labelEls.TryGetValue(id, out var old)) { old.RemoveFromHierarchy(); _labelEls.Remove(id); }
                _labelTmpl.Remove(id);
                return;
            }

            bool fresh = !_labelEls.TryGetValue(id, out var el);
            if (fresh)
            {
                el = new Label { name = "lbl-" + id, pickingMode = PickingMode.Ignore };
                el.style.position = Position.Absolute;
                el.style.whiteSpace = WhiteSpace.Normal;
                _labelLayer.Add(el);
                _labelEls[id] = el;
            }

            // A repeat `text <id>` MERGES into the live label — omitted fields keep
            // their current values (actor-op semantics: later fields win). So a
            // label is styled ONCE and then driven with bare `text code «…»`
            // updates, instead of re-stating x/y/size/color on every beat.
            // Save/load is safe: ReplayVisuals re-runs text ops in order, so the
            // styled declaration always lands before its bare updates.

            // placement: x/y are screen percents; anchor picks the label's reference point
            var xN = NumOrNull(cmd["x"]);
            if (fresh || xN != null) el.style.left = Length.Percent(Mathf.Clamp(xN ?? 3f, 0f, 100f));
            var yN = NumOrNull(cmd["y"]);
            if (fresh || yN != null) el.style.top = Length.Percent(Mathf.Clamp(yN ?? 3f, 0f, 100f));
            // width: explicit `w` (screen %), else capped at the right screen edge —
            // an absolute label otherwise grows past the screen instead of wrapping.
            var wN = NumOrNull(cmd["w"]);
            if (fresh || wN != null || xN != null)
                el.style.maxWidth = Length.Percent(Mathf.Clamp(wN ?? (97f - (xN ?? 3f)), 1f, 100f));
            if (fresh || cmd["anchor"] != null)
            {
                var (tx, ty) = LabelAnchor((string)cmd["anchor"]);
                el.style.translate = new Translate(Length.Percent(tx), Length.Percent(ty));
            }

            // look: per-label font / size / colour, falling back to the theme
            if (fresh || cmd["color"] != null)
                el.style.color = UiColor.Parse((string)cmd["color"], Theme.TextColor);
            if (fresh || cmd["size"] != null)
                el.style.fontSize = (int)NumOr(cmd["size"], Theme.BodyFontSize);
            var fontPath = (string)cmd["font"];
            if (fresh || !string.IsNullOrEmpty(fontPath))
            {
                // Same dual form as the theme font: "/content/…" = a font served
                // with the content (fetched into the cache, applied when ready);
                // anything else = a Resources name baked into the build.
                if (!string.IsNullOrEmpty(fontPath) && fontPath.StartsWith("/"))
                    _ = ApplyContentFontAsync(el, fontPath);
                else
                {
                    Font font = !string.IsNullOrEmpty(fontPath) ? Resources.Load<Font>(fontPath) : Theme.Font;
                    LvnFonts.Apply(el, font); // SDF path; no-op when null (theme default)
                }
            }

            if (fresh || cmd["text"] != null)
            {
                var tmpl = (string)cmd["text"] ?? "";
                if (tmpl.Length != 0 && _strings != null && _strings.TryGetValue(tmpl, out var trTmpl))
                    tmpl = trTmpl; // localization catalog, keyed by the source template
                _labelTmpl[id] = tmpl;
                el.text = TextInterpolation.Apply(tmpl, _player?.Vars); // immediate paint; tick keeps it live
            }
        }

        // Re-evaluate every live label's template against the current variables.
        private void RefreshLabels()
        {
            if (_labelTmpl.Count == 0) return;
            var vars = _player?.Vars;
            foreach (var kv in _labelTmpl)
                if (_labelEls.TryGetValue(kv.Key, out var el))
                {
                    var t = TextInterpolation.Apply(kv.Value, vars);
                    if (el.text != t) el.text = t;
                }
        }

        private static float NumOr(JToken t, float dflt) => NumOrNull(t) ?? dflt;

        // Nullable numeric read: absent → null, malformed → null (never throws), so
        // one bad field can't abort the whole chapter. A number written as a string
        // ("0.5") is still accepted.
        private static float? NumOrNull(JToken t)
        {
            if (t == null) return null;
            try { return (float)t; } catch { }
            try
            {
                if (float.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    return f;
            }
            catch { }
            return null;
        }

        private static int? IntOrNull(JToken t)
        {
            var f = NumOrNull(t);
            return f == null ? (int?)null : (int)Mathf.Round(f.Value);
        }

        // Tolerant boolean read: absent → dflt, and true/false/1/0 written as a
        // string or number are all accepted rather than throwing an invalid cast.
        private static bool BoolOr(JToken t, bool dflt)
        {
            if (t == null) return dflt;
            try { return (bool)t; } catch { }
            switch (t.ToString().Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": return true;
                case "false": case "0": case "no": return false;
                default: return dflt;
            }
        }

        // Translate fractions for a label anchor (default top-left, so x/y read as an
        // inset from the corner). center → -50%, right/bottom → -100%.
        private static (float, float) LabelAnchor(string anchor)
        {
            string a = string.IsNullOrEmpty(anchor) ? "top-left" : anchor.ToLowerInvariant();
            float tx = a.Contains("left") ? 0f : a.Contains("right") ? -100f : -50f;
            float ty = a.Contains("top") ? 0f : a.Contains("bottom") ? -100f : -50f;
            return (tx, ty);
        }

        // A script-driven `anim` command: deserialize its LvnAnim payload and play
        // it on the named channel (default "script") of an already-shown entity, so
        // .lvns can tween any prop/layer or move a sprite along a path live.
        private void ApplyAnim(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id)) return;
            // Stop form: `anim id=x stop=all` / `stop=<channel/prop>`.
            var stop = (string)cmd["stop"];
            if (!string.IsNullOrEmpty(stop)) { SceneStopAnim(id, stop); return; }
            var payload = cmd["anim"];
            if (payload == null) return;
            LvnAnim anim;
            try { anim = payload.ToObject<LvnAnim>(); }
            catch { return; }
            if (anim == null || anim.tracks == null || anim.tracks.Count == 0) return;
            // Channel: explicit if given, else derived from the first track's target
            // (e.g. "script:rotation", "script:face:y") — so distinct properties run
            // and compose at once, while re-animating the same property replaces it.
            var channel = (string)cmd["channel"];
            if (string.IsNullOrEmpty(channel))
            {
                var t0 = anim.tracks[0];
                channel = "script:" + (string.IsNullOrEmpty(t0.layer) ? "" : t0.layer + ":") + t0.prop;
            }
            // mode=queue → chain after the current anim on this channel (non-blocking)
            if ((string)cmd["mode"] == "queue") ScenePlayAnimQueued(id, channel, anim);
            else ScenePlayAnim(id, channel, anim);
        }

        // Speaker label → on-stage actor id (mirrors the authoring speakerEntity
        // rule: actor_map alias, else the lowercased name).
        private string ResolveSpeakerId(string who)
        {
            if (string.IsNullOrEmpty(who)) return null;
            var sb = new StringBuilder(who.Length);
            foreach (var c in who.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        public void ShowChoice(IReadOnlyList<LvnOption> options)
        {
            _awaitingTap = false;
            _curChoices = options;
            _dialogue?.SuppressAdvanceHint(true); // a choice is up — don't invite a tap
            _choices.Present(options);
            // A timed choice races the player: countdown bar over the options,
            // expiry takes the timeout branch (VnStage.Input.cs).
            StartChoiceTimer(_player != null ? _player.CurrentChoiceTimeout : 0f);
        }

        public void ApplyStage(JObject command)
        {
            switch ((string)command["op"])
            {
                case "bg": _ = ApplyBgAsync(command); break;
                case "actor": _ = ApplyActorAsync(command); break;
                case "obj": _ = ApplyActorAsync(command); break; // any placeable sprite
                case "anim": ApplyAnim(command); break; // script-driven tween / path
                case "fade": ApplyFade(command); break;
                case "dim": ApplyDim(command); break;
                case "flash": ApplyFlash(command); break;
                case "tint": ApplyTint(command); break;
                case "blur": ApplyBlur(command); break;
                case "camera": ApplyCamera(command); break;
                case "particles":
                    _particles.Set((string)command["type"], BoolOr(command["on"], true));
                    break;
                case "audio": _ = _audio.ApplyAsync(command, Assets, _cts.Token); break;
                case "text": ApplyText(command); break; // reactive HUD/stat label
                case "save": SaveSlot(command); break;
                case "load": LoadSlot(command); break;
                case "text_pace": ApplyTextPace(command); break;
                case "wait":
                    _awaitingWait = true;
                    StartCoroutine(WaitCoroutine(command));
                    break;
                case "input": ApplyInput(command); break; // text entry → story var
                case "preload":
                    _ = PreloadAssetsAsync(command);
                    break;
                case "hint": ApplyHint(command); break;
                // unknown-but-registered ops are simply not drawn.
            }
        }

        // `hint text="…" show=true [duration=0]` — a small card that pops up
        // top-center over the scene: a tutorial nudge, a stat unlock, a note tied
        // to a specific beat. `show=false` (or empty text) dismisses it; a positive
        // `duration` auto-dismisses after that many seconds. Text interpolates
        // {vars} like dialogue. Lives on the HUD layer, ignores the pointer.
        private void ApplyHint(JObject cmd)
        {
            if (_labelLayer == null) return;
            var text = (string)cmd["text"] ?? "";
            bool show = BoolOr(cmd["show"], true) && text.Length > 0;

            _hintHide?.Pause();
            _hintHide = null;

            if (!show)
            {
                if (_hintCard != null) _hintCard.style.display = DisplayStyle.None;
                return;
            }

            if (_hintCard == null)
            {
                _hintCard = new VisualElement { name = "vn-hint", pickingMode = PickingMode.Ignore };
                _hintCard.style.position = Position.Absolute;
                _hintCard.style.maxWidth = Length.Percent(72);
                _hintCard.style.paddingLeft = 22; _hintCard.style.paddingRight = 22;
                _hintCard.style.paddingTop = 12; _hintCard.style.paddingBottom = 12;
                // top-center pill at 12% — clear of the shell HUD strip (the
                // old 5% sat underneath it), per the mobile-VN standard.
                _hintCard.style.left = Length.Percent(50);
                _hintCard.style.top = Length.Percent(12);
                _hintCard.style.translate = new Translate(Length.Percent(-50), Length.Percent(0));
                _hintLabel = new Label { name = "vn-hint-text", pickingMode = PickingMode.Ignore };
                _hintLabel.style.whiteSpace = WhiteSpace.Normal;
                _hintLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                _hintCard.Add(_hintLabel);
                _labelLayer.Add(_hintCard);
            }

            var bg = Theme != null ? Theme.PanelColor : new Color(0.05f, 0.05f, 0.08f, 0.9f);
            _hintCard.style.backgroundColor = bg;
            float r = Theme != null ? Theme.PanelCornerRadius : 12f;
            _hintCard.style.borderTopLeftRadius = r; _hintCard.style.borderTopRightRadius = r;
            _hintCard.style.borderBottomLeftRadius = r; _hintCard.style.borderBottomRightRadius = r;

            _hintLabel.style.color = Theme != null ? Theme.TextColor : Color.white;
            _hintLabel.style.fontSize = Theme != null ? Theme.BodyFontSize : 30;
            if (Theme != null) LvnFonts.Apply(_hintLabel, Theme.Font);
            _hintLabel.text = TextInterpolation.Apply(text, _player?.Vars);

            _hintCard.style.display = DisplayStyle.Flex;

            float dur = NumOr(cmd["duration"], 0f);
            if (dur > 0f)
                _hintHide = _labelLayer.schedule
                    .Execute(() => { if (_hintCard != null) _hintCard.style.display = DisplayStyle.None; })
                    .StartingIn((long)(dur * 1000f));
        }

        public void OnEnd()
        {
            // The chapter is finished — its mid-chapter autosave must not hijack the
            // next entry back to a stale position.
            LvnSaveStore.Delete(_saveTitleId, LvnSaveStore.AutoSlot);
            // Garbage-collect the scene when the chapter ends: without this the last
            // actors keep their (looping) animations running and bleed into the menu
            // or the next chapter. ResetStage stops coroutines, removes actors,
            // clears the background and FX.
            ResetStage();
            _dialogue.SetSpeaker(null);
            _dialogue.SetText(string.Empty);
        }

        // ── wait / preload ──────────────────────────────────────────────────

        private IEnumerator WaitCoroutine(JObject cmd)
        {
            float ms = NumOr(cmd["ms"], 1000f);
            yield return new WaitForSecondsRealtime(ms / 1000f);
            _awaitingWait = false;
            if (_player != null && !_player.Finished)
                _player.Advance();
        }

        private async Task PreloadAssetsAsync(JObject cmd)
        {
            if (Assets == null) return;

            var spriteUrls = new List<string>();
            var audioUrls = new List<string>();

            void Add(string url, string kind)
            {
                if (string.IsNullOrEmpty(url)) return;
                if (kind == "audio") audioUrls.Add(url);
                else spriteUrls.Add(url); // a Spine texture warms as a sprite too
            }

            // Batch form (`assets=[…]`) OR the terse single-asset form
            // (`preload url=… kind=…`) — the latter is how a chapter warms one
            // heavy Spine texture before its actor appears, killing the pop-in.
            if (cmd["assets"] is JArray assetArray)
                foreach (var a in assetArray)
                    Add((string)((JObject)a)["url"], (string)((JObject)a)["kind"]);
            else
                Add((string)cmd["url"], (string)cmd["kind"]);

            if (spriteUrls.Count == 0 && audioUrls.Count == 0) return;

            var tasks = new List<Task>();
            if (spriteUrls.Count > 0)
                tasks.Add(Assets.PreloadAsync(spriteUrls, "sprite", _cts.Token));
            if (audioUrls.Count > 0)
                tasks.Add(Assets.PreloadAsync(audioUrls, "audio", _cts.Token));
            await Task.WhenAll(tasks);
        }

        // ── stage command helpers ─────────────────────────────────────────────

        private void ApplyFade(JObject cmd)
        {
            var to = (string)cmd["to"] ?? "black";
            float dur = NumOr(cmd["duration"], 0.5f);
            if (to == "clear" || to == "none") _fx.Clear(dur);
            else _fx.Fade(to == "white" ? Color.white : Color.black, dur);
        }

        private void ApplyDim(JObject cmd)
        {
            float alpha = NumOr(cmd["alpha"], 0.4f);
            float dur = NumOr(cmd["duration"], 0.5f);
            _fx.Dim(alpha, dur);
        }

        private void ApplyFlash(JObject cmd)
        {
            if (LvnPrefs.ReduceMotion) return; // vestibular/photosensitivity comfort
            var colour = ParseColor((string)cmd["color"], Color.white);
            float dur = NumOr(cmd["duration"], 0.2f);
            _fx.Flash(colour, dur);
        }

        private void ApplyTint(JObject cmd)
        {
            var colour = ParseColor((string)cmd["color"], Color.white);
            float alpha = NumOr(cmd["alpha"], 0.3f);
            float dur = NumOr(cmd["duration"], 0.5f);
            _fx.Tint(colour, alpha, dur);
        }

        private void ApplyBlur(JObject cmd)
        {
            float alpha = NumOr(cmd["alpha"], 0.5f);
            float dur = NumOr(cmd["duration"], 0.5f);
            // Real gaussian of the scene frame when the renderer can (canvas
            // path + built-in pipeline); the FxLayer white veil is the fallback
            // for platforms without a camera hook. Never both.
            if (_renderer != null && _renderer.TryBlur(Mathf.Clamp01(alpha), dur))
            {
                _fx.ClearBlur(0f);
                return;
            }
            if (alpha <= 0f) _fx.ClearBlur(dur);
            else _fx.Blur(alpha, dur);
        }

        private void ApplyTextPace(JObject cmd)
        {
            float cps = NumOr(cmd["cps"], 0f);
            TypewriterClock.GlobalCps = cps;
        }

        internal static TransitionType ParseTransition(string name)
        {
            if (string.IsNullOrEmpty(name)) return TransitionType.None;
            switch (name.ToLowerInvariant())
            {
                case "fade": return TransitionType.Fade;
                case "slide_left": return TransitionType.SlideLeft;
                case "slide_right": return TransitionType.SlideRight;
                case "pop": return TransitionType.Pop;
                default: return TransitionType.None;
            }
        }

        internal static Color ParseColor(string name, Color fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            switch (name.ToLowerInvariant())
            {
                case "white": return Color.white;
                case "black": return Color.black;
                case "red": return Color.red;
                case "blue": return Color.blue;
                case "green": return Color.green;
                case "yellow": return Color.yellow;
                case "cyan": return Color.cyan;
                case "magenta": return Color.magenta;
                case "cold":
                case "tint_cold": return new Color(0.6f, 0.7f, 1f, 1f);
                case "warm":
                case "tint_warm": return new Color(1f, 0.85f, 0.7f, 1f);
                case "sepia": return new Color(0.76f, 0.6f, 0.42f, 1f);
                default: return fallback;
            }
        }

        private void ApplyCamera(JObject cmd)
        {
            float dur = NumOr(cmd["duration"], 0.3f);
            switch ((string)cmd["action"])
            {
                case "shake":
                {
                    if (LvnPrefs.ReduceMotion) break; // comfort setting: no screen shake
                    float amp = NumOr(cmd["amplitude"], 8f);
                    _renderer?.Shake(amp, dur);
                    break;
                }
                case "zoom":
                {
                    float factor = NumOr(cmd["factor"], 1.2f);
                    _renderer?.Zoom(factor, dur);
                    break;
                }
                case "pan":
                {
                    float px = NumOr(cmd["x"], 0f);
                    float py = NumOr(cmd["y"], 0f);
                    _renderer?.Pan(px, py, dur);
                    break;
                }
                case "reset":
                    _renderer?.ResetCamera(dur);
                    break;
            }
        }


        // ── scene-critical sprite acquisition ────────────────────────────────
        // A backdrop or actor layer is not an optional decoration: if its fetch
        // hits a bad moment (mobile networks flap for seconds at a time — live
        // field case: a mid-warm connection reset pinned the offline flag for
        // 2s and the chapter played on a black stage forever), the element must
        // keep trying and dress itself the moment the world allows. Exponential
        // backoff, an instant wake on the offline→online transition, and a
        // stillWanted predicate so a superseded element never zombie-applies.
        private async Task<Sprite> LoadSceneSpriteAsync(string url, string what, Func<bool> stillWanted)
        {
            const int MaxAttempts = 8; // backoff sums to ~2 min — a real outage, not a flap
            string lastErr = null;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (Assets == null || _cts == null || _cts.IsCancellationRequested || !stillWanted()) return null;
                try
                {
                    var s = await Assets.LoadSpriteAsync(url, _cts.Token);
                    if (s != null)
                    {
                        if (attempt > 1) Debug.Log($"[stage] {what} {url} recovered (attempt {attempt})");
                        return s;
                    }
                    lastErr = "no data (404 or decode failed)";
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex) { lastErr = ex.Message; }
                if (attempt == MaxAttempts) break;
                float delay = Lvn.Content.LvnBackoff.DelaySeconds(attempt + 1);
                Debug.LogWarning($"[stage] {what} {url} unavailable (attempt {attempt}): {lastErr} — retry in {delay:F0}s or on reconnect");
                await WaitRetryWindowAsync(delay);
            }
            Debug.LogWarning($"[stage] {what} {url} gave up after {MaxAttempts} attempts: {lastErr}");
            return null;
        }

        // The backoff delay, cut short the instant connectivity returns — the
        // scene re-dresses within a frame of the network healing instead of
        // sitting out the rest of a 30s backoff window.
        private async Task WaitRetryWindowAsync(float seconds)
        {
            var wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<bool> onChange = online => { if (online) wake.TrySetResult(true); };
            Lvn.Content.LvnNetworkStatus.Changed += onChange;
            try
            {
                await Task.WhenAny(
                    Task.Delay(TimeSpan.FromSeconds(Math.Max(0.5f, seconds)), _cts.Token),
                    wake.Task);
            }
            catch (OperationCanceledException) { }
            finally { Lvn.Content.LvnNetworkStatus.Changed -= onChange; }
        }

        // Monotonic backdrop generation: a retrying older bg must never paint
        // over a newer one that already landed (or is in flight).
        private int _bgGen;

        private async Task ApplyBgAsync(JObject cmd)
        {
            var url = (string)cmd["sprite_url"];
            // bg id="porch" — resolve the catalog entity to its (first) layer url.
            if (string.IsNullOrEmpty(url))
            {
                var id = (string)cmd["id"];
                if (Catalog != null && Catalog.Has(id))
                {
                    var urls = Catalog.Resolve(id, AxesFrom(cmd), CatalogCond());
                    if (urls.Count > 0) url = urls[0];
                }
            }
            if (string.IsNullOrEmpty(url)) return;
            // The script reached this bg — that's the unlock moment, independent of
            // whether the sprite itself loads (a cache miss doesn't unsee the CG).
            UnlockGalleryFor(url);
            if (Assets == null) return;
            int epoch = _stageEpoch;
            int gen = ++_bgGen;
            var sprite = await LoadSceneSpriteAsync(url, "bg",
                () => StageCurrent(epoch) && _bgGen == gen);
            if (sprite == null) return;
            if (!StageCurrent(epoch) || _bgGen != gen) return; // a chapter change / newer bg won
            _renderer?.SetBackground(sprite);
            HasBackdrop = true; // the entry reveal (host) waits for the first one
        }

        /// <summary>True once the CURRENT scene has an applied background — the
        /// host holds its opaque chapter loader until this flips, so the fade
        /// always reveals a dressed stage, never a black frame.</summary>
        public bool HasBackdrop { get; private set; }

        /// <summary>The title's curated CG list (manifest title.gallery), set by the
        /// host per chapter entry. Non-empty ⇒ the quick menu shows a Gallery item;
        /// a shown <c>bg</c> whose url matches an item unlocks it forever.</summary>
        public System.Collections.Generic.IReadOnlyList<Lvn.Content.LvnGalleryItem> Gallery { get; set; }

        private void UnlockGalleryFor(string url)
        {
            if (Gallery == null) return;
            foreach (var g in Gallery)
                if (g != null && g.url == url)
                    LvnGalleryStore.Unlock(_saveTitleId, g.id);
        }

        // Evaluates a layer's `when` condition against the player's vars, so a
        // conditional sprite layer appears only when its expression holds.
        private System.Func<string, bool> CatalogCond() => expr =>
        {
            if (_player == null || string.IsNullOrEmpty(expr)) return false;
            try { return LvnExpression.EvaluateBool(expr, _player.Vars); }
            catch { return false; }
        };

        internal static readonly HashSet<string> ReservedActorFields = new HashSet<string>
        {
            "op", "id", "show", "position", "x", "y", "width", "height", "scale",
            "anchor", "anchor_x", "anchor_y", "z", "flip", "mirror", "rotation", "opacity",
            "on_click", "hover_opacity", "breathing", "sprite_url", "body_url", "clothes_url", "hair_url",
            "transition", "transition_duration", "enter", "exit", "play",
        };

        // The last actor command per id — RefreshActor replays it so a wardrobe
        // change re-resolves the SAME pose/placement with the new equipment.
        private readonly Dictionary<string, JObject> _actorCmds = new Dictionary<string, JObject>();

        // Per-actor apply generation: rapid wardrobe browsing fires overlapping
        // ApplyActorAsync calls whose sprite loads finish out of order — only
        // the NEWEST may touch the renderer, or an older outfit "wins" by
        // arriving late.
        private readonly Dictionary<string, int> _actorGen = new Dictionary<string, int>();

        /// <summary>Re-apply an on-screen actor from its last command (art
        /// re-resolves against the current variables + wardrobe). No-op when
        /// the actor isn't on stage.</summary>
        public void RefreshActor(string id)
        {
            if (!string.IsNullOrEmpty(id) && _actorCmds.TryGetValue(id, out var cmd))
                _ = ApplyActorAsync(cmd);
        }

        /// <summary>Ensure an actor is ON stage — used by the in-story wardrobe so it
        /// always has the active hero to dress, even when the beat left the stage empty
        /// (imported novels open the wardrobe without staging anyone). Replays the
        /// actor's last pose forcing it visible, or stages it fresh (centred) from its
        /// catalog entity. No-op for an empty id.</summary>
        public void EnsureActorShown(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            // Already on stage (the story/import staged her) → do NOTHING. Re-applying
            // would reload the whole layered composite and lag the wardrobe open.
            if (_placements.TryGetValue(id, out var pl) && pl.Show) return;
            JObject cmd;
            if (_actorCmds.TryGetValue(id, out var last) && (string)last["op"] == "actor")
            {
                cmd = (JObject)last.DeepClone();
                cmd["show"] = true; // in case the last op hid her
            }
            else
            {
                cmd = new JObject { ["op"] = "actor", ["id"] = id, ["show"] = true, ["position"] = "center" };
            }
            _ = ApplyActorAsync(cmd);
        }

        private void OnWardrobeChanged(string entity) => RefreshActor(entity);

        private async Task ApplyActorAsync(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id)) return;
            int epoch = _stageEpoch; // the scene this apply belongs to (see ResetStage)
            int gen = (_actorGen.TryGetValue(id, out var g) ? g : 0) + 1;
            _actorGen[id] = gen; // this call owns the actor until a newer one starts

            // Spine entities render through the optional spine-unity bridge —
            // a different pipeline entirely (runtime skeleton, own animations).
            var spineEntity = Catalog != null ? Catalog.Get(id) : null;
            if (spineEntity != null && spineEntity.kind == "spine" && spineEntity.spine != null)
            {
                await ApplySpineAsync(id, spineEntity, cmd);
                return;
            }

            // Resolve the layer urls, in priority order:
            //   1. catalog id (manifest.sprites) — layered, with conditional `when`;
            //   2. per-doc cast block — layered by the command's axes;
            //   3. direct body/clothes/hair layers, or a single sprite_url.
            List<string> urls;
            List<string> urlIds = null;      // parallel layer ids (catalog path), for blink/lip-sync
            List<Vector4> urlRects = null;    // parallel per-layer sub-rects (x,y,w,h); w≤0 = fill
            List<SpriteCatalog.ResolvedLayer> urlDefs = null; // parallel full defs (bones: parent/pivot/spring)
            if (Catalog != null && Catalog.Has(id))
            {
                var rls = Catalog.ResolveLayers(id, AxesOf(cmd), CatalogCond());
                urls = new List<string>(rls.Count);
                urlIds = new List<string>(rls.Count);
                urlRects = new List<Vector4>(rls.Count);
                urlDefs = rls;
                foreach (var rl in rls) { urls.Add(rl.Url); urlIds.Add(rl.Id); urlRects.Add(new Vector4(rl.X, rl.Y, rl.W, rl.H)); }
            }
            else if (_cast != null && _cast.TryGetValue(id, out var entity))
            {
                urls = SpriteComposer.Resolve(entity, AxesFrom(cmd));
            }
            else
            {
                urls = new List<string>();
                var body = (string)cmd["body_url"]; if (!string.IsNullOrEmpty(body)) urls.Add(body);
                var clothes = (string)cmd["clothes_url"]; if (!string.IsNullOrEmpty(clothes)) urls.Add(clothes);
                var hair = (string)cmd["hair_url"]; if (!string.IsNullOrEmpty(hair)) urls.Add(hair);
                if (urls.Count == 0)
                {
                    var sp = (string)cmd["sprite_url"]; if (!string.IsNullOrEmpty(sp)) urls.Add(sp);
                }
            }

            // Build the click action + placement SYNCHRONOUSLY (everything here runs
            // before the first `await` below). For the Canvas scene we also place the
            // actor and register its hotspot NOW — so it's clickable the instant the
            // obj command runs, before the next command (the room's narration `say`)
            // shows. Otherwise the hotspot armed only a few frames later (after the
            // async art load), and a tap in that gap fell through to "advance",
            // re-printing the room — the "first click does nothing" bug.
            System.Action onClick = null;
            var clickField = cmd["on_click"];
            if (clickField != null)
            {
                if (clickField.Type == JTokenType.Object)
                {
                    var clickObj = (JObject)clickField;
                    var target = (string)clickObj["goto"];
                    var setOps = clickObj["set"] as JObject;
                    onClick = () =>
                    {
                        if (_player == null) return;
                        if (setOps != null)
                        {
                            foreach (var prop in setOps.Properties())
                                _player.Vars[prop.Name] = prop.Value;
                        }
                        if (!string.IsNullOrEmpty(target))
                            _player.GoTo(target);
                        _awaitingTap = false;
                        _curChoices = null;
                        _choices.Dismiss();
                        _player.Advance();
                    };
                }
                else
                {
                    var clickTarget = (string)clickField;
                    if (!string.IsNullOrEmpty(clickTarget))
                        onClick = () =>
                        {
                            if (_player == null) return;
                            _player.GoTo(clickTarget);
                            _awaitingTap = false;
                            _curChoices = null;
                            _choices.Dismiss();
                            _player.Advance();
                        };
                }
            }

            bool fresh = !_placements.TryGetValue(id, out var prevPl);
            var placement = fresh ? PlacementFrom(cmd) : PlacementFrom(cmd, prevPl);
            // Stage framing: on a FRESH actor, fill the theme's baseline/scale wherever
            // the op left it unset, so every novel gets the standard bottom-anchored
            // pose — tunable from ui.stage without editing the script. A follow-up op
            // inherits via the sticky merge above.
            if (Theme != null)
            {
                // Size/baseline seed the FIRST show; a sticky update inherits them from
                // the previous placement, so only apply on a fresh actor.
                if (fresh)
                {
                    if (cmd["y"] == null) placement.Y = Theme.ActorBaselineY;
                    if (cmd["width"] == null) placement.Width = Placement.DefaultWidth * Theme.ActorScale;
                    if (cmd["height"] == null) placement.Height = Placement.DefaultHeight * Theme.ActorScale;
                }
                // Spread must re-apply on EVERY op that positions by slot: the autostage
                // re-emits position= on each emotion change, so the sticky merge recomputes
                // X from SlotX (0.25/0.75) and would snap the actor back to the un-spread
                // column after the first line. Only when X came from position, not x=.
                if (cmd["x"] == null && cmd["position"] != null && Theme.ActorSpread != 1f)
                    placement.X = 0.5f + (placement.X - 0.5f) * Theme.ActorSpread;
            }
            // Layered/boned entities declare the aspect their art was authored in —
            // the renderer locks the box to it so layers register pixel-exact.
            var aspectEntity = Catalog != null ? Catalog.Get(id) : null;
            if (aspectEntity != null && aspectEntity.aspect > 0f)
                placement.BoxAspect = aspectEntity.aspect;
            // Place first so the slot exists before the (async) art arrives — a
            // no-op on renderers that apply placement together with the art.
            _renderer?.PlaceActor(id, placement);
            _hotspots.RemoveAll(h => h.id == id);
            // Manual hotspot hit-testing only applies to renderers that expose
            // actor rects (the canvas path); the UITK path uses element picking.
            if (onClick != null && placement.Show && UseCanvasScene) _hotspots.Add((id, onClick));

            // Drag & drop: `draggable=true` arms the object; on_drop maps
            // target ids to labels ("bag:apple_in_bag"), on_drop_miss is the
            // released-anywhere-else branch (default: it just stays put).
            if (cmd["draggable"] != null)
            {
                if (BoolOr(cmd["draggable"], false))
                    _draggables[id] = new DragInfo
                    {
                        Home = placement,
                        Drop = ParseDropMap((string)cmd["on_drop"]),
                        MissLabel = (string)cmd["on_drop_miss"],
                        BoundToScreen = (string)cmd["drag_bounds"] != "none",
                    };
                else
                    _draggables.Remove(id);
            }

            // Now load the layer sprites (async) and set them on the placed actor.
            List<Sprite> layers = null;
            List<string> layerIds = null;
            List<Vector4> layerRects = null;
            List<SpriteCatalog.ResolvedLayer> layerDefs = null;
            if (urls != null && urls.Count > 0 && Assets != null)
            {
                layers = new List<Sprite>(urls.Count);
                layerIds = urlIds != null ? new List<string>(urls.Count) : null;
                layerRects = urlRects != null ? new List<Vector4>(urls.Count) : null;
                layerDefs = urlDefs != null ? new List<SpriteCatalog.ResolvedLayer>(urls.Count) : null;
                // Layers load IN PARALLEL — a five-layer character used to pay
                // five sequential fetch+decode round-trips on a cold cache; the
                // loader dedups in-flight urls and decodes on workers, so the
                // wall time is now the slowest layer, not the sum. Order is
                // preserved by index (z-order = author order).
                var loads = new Task<Sprite>[urls.Count];
                for (int i = 0; i < urls.Count; i++)
                    loads[i] = LoadLayerAsync(urls[i]);
                for (int i = 0; i < urls.Count; i++)
                {
                    var s = await loads[i];
                    if (s != null)
                    {
                        layers.Add(s);
                        layerIds?.Add(i < urlIds.Count ? urlIds[i] : null);
                        layerRects?.Add(i < urlRects.Count ? urlRects[i] : Vector4.zero);
                        layerDefs?.Add(i < urlDefs.Count ? urlDefs[i] : default);
                    }
                }
            }

            // A chapter change landed while our sprites loaded — this actor
            // belongs to a scene that no longer exists; never resurrect it on the
            // clean stage (the ghost-actor bug: a per-id gen doesn't catch an id
            // the new chapter never uses, so it's never superseded).
            if (!StageCurrent(epoch)) return;

            // Same self-healing acquisition as the backdrop: a layer that hits a
            // network flap keeps retrying (and wakes on reconnect) for as long as
            // THIS apply is still the actor's newest — a faceless/bodyless actor
            // must not survive a 2-second connectivity blip.
            Task<Sprite> LoadLayerAsync(string u) => LoadSceneSpriteAsync(u, "actor layer",
                () => StageCurrent(epoch) && (!_actorGen.TryGetValue(id, out var curGen) || curGen == gen));
            // A newer apply started while our sprites loaded — ITS art must win;
            // this stale pass may not touch the renderer (late-arrival outfit bug).
            if (_actorGen.TryGetValue(id, out var cur) && cur != gen) return;

            _renderer?.ApplyActor(id, layers, placement, onClick, layerIds, layerRects, layerDefs);
            _placements[id] = placement; // the sticky base for the next command
            _actorCmds[id] = cmd;        // wardrobe changes replay this in place

            // Animations (rigged entities): idle (whole-actor) + blink (a layer)
            // auto-run on show; play="name" fires a one-shot gesture; an
            // auto:"speaking" anim is remembered for lip-sync while this actor talks.
            var animEntity = Catalog != null ? Catalog.Get(id) : null;
            if (animEntity != null && animEntity.anim != null && animEntity.anim.Count > 0)
            {
                await PreloadFramesAsync(id, animEntity);

                LvnAnim idle = null, blink = null, talk = null;
                foreach (var kv in animEntity.anim)
                {
                    var a = kv.Value;
                    if (a == null) continue;
                    if (a.auto == "speaking") { talk = a; continue; }
                    if (a.auto == "true") { if (HasLayerTrack(a)) blink = blink ?? a; else idle = idle ?? a; }
                }
                _talkAnims[id] = talk; // null clears it

                var playName = (string)cmd["play"];
                if (!string.IsNullOrEmpty(playName) && animEntity.anim.TryGetValue(playName, out var gesture))
                    ScenePlayGesture(id, gesture, idle);
                else if (placement.Show && idle != null)
                    SceneEnsureIdle(id, idle);
                if (placement.Show && blink != null) SceneEnsureBlink(id, blink);
            }
        }

        private static bool HasLayerTrack(LvnAnim a)
        {
            if (a.tracks == null) return false;
            foreach (var t in a.tracks) if (t != null && !string.IsNullOrEmpty(t.layer)) return true;
            return false;
        }

        // Preload the sprite variants a frame track needs (e.g. eyes=open/closed),
        // so blink/lip-sync swaps are instant. Resolves each layer's url template
        // with axis=value via the catalog.
        private async Task PreloadFramesAsync(string id, LvnSpriteEntity entity)
        {
            if (entity.anim == null || entity.layers == null || Assets == null || Catalog == null) return;
            var frames = new Dictionary<string, Dictionary<string, Sprite>>();
            foreach (var anim in entity.anim.Values)
            {
                if (anim?.tracks == null) continue;
                foreach (var tr in anim.tracks)
                {
                    if (tr == null || tr.prop != "frame" || string.IsNullOrEmpty(tr.layer) || string.IsNullOrEmpty(tr.axis) || tr.keys == null) continue;
                    string template = null;
                    foreach (var l in entity.layers) if (l != null && l.id == tr.layer) { template = l.url; break; }
                    if (string.IsNullOrEmpty(template)) continue;
                    if (!frames.TryGetValue(tr.layer, out var map)) frames[tr.layer] = map = new Dictionary<string, Sprite>();
                    foreach (var key in tr.keys)
                    {
                        var val = key != null && key.Length > 1 ? key[1]?.ToString() : null;
                        if (string.IsNullOrEmpty(val) || map.ContainsKey(val)) continue;
                        var url = Catalog.FillFor(id, template, new Dictionary<string, string> { { tr.axis, val } });
                        if (string.IsNullOrEmpty(url)) continue;
                        try { var sp = await Assets.LoadSpriteAsync(url, _cts.Token); if (sp != null) map[val] = sp; }
                        catch { }
                    }
                }
            }
            if (frames.Count > 0) SceneSetFrames(id, frames);
        }

        // Build placement from the command — everything in screen fractions so a
        // script controls any object's position, size, anchor, z, flip, rotation
        // and opacity without knowing the resolution.
        /// <summary>Sticky placement: merge an actor command over the actor's
        /// LAST applied placement — only fields the command explicitly mentions
        /// change, so <c>actor id=knight play="Jump"</c> keeps the position a
        /// drag, a move-follow-up or an earlier command left him at.
        /// Transitions are one-shot and always come from the command.</summary>
        internal static Placement PlacementFrom(JObject cmd, Placement prev)
        {
            var p = prev;
            p.Show = BoolOr(cmd["show"], true); // re-issuing an actor shows it (existing semantics)
            if (cmd["x"] != null || cmd["position"] != null)
                p.X = NumOrNull(cmd["x"]) ?? ActorLayer.SlotX((string)cmd["position"]);
            if (cmd["y"] != null) p.Y = NumOr(cmd["y"], p.Y);
            if (cmd["width"] != null) p.Width = NumOrNull(cmd["width"]);
            if (cmd["height"] != null) p.Height = NumOrNull(cmd["height"]);
            if (cmd["z"] != null) p.Z = IntOrNull(cmd["z"]);
            if (cmd["flip"] != null || cmd["mirror"] != null) p.Flip = BoolOr(cmd["flip"] ?? cmd["mirror"], false);
            if (cmd["rotation"] != null) p.Rotation = NumOr(cmd["rotation"], 0f);
            if (cmd["opacity"] != null) p.Opacity = NumOr(cmd["opacity"], 1f);
            if (cmd["hover_opacity"] != null) p.HoverOpacity = NumOr(cmd["hover_opacity"], 1f);
            p.EnterTransition = ParseTransition((string)cmd["enter"]);
            p.ExitTransition = ParseTransition((string)cmd["exit"]);
            p.TransitionDuration = NumOr(cmd["transition_duration"], 0.3f);
            var anch = (string)cmd["anchor"];
            if (!string.IsNullOrEmpty(anch))
            {
                var parts = anch.Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ax)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ay))
                { p.AnchorX = ax; p.AnchorY = ay; }
            }
            else
            {
                if (cmd["anchor_x"] != null) p.AnchorX = NumOr(cmd["anchor_x"], p.AnchorX);
                if (cmd["anchor_y"] != null) p.AnchorY = NumOr(cmd["anchor_y"], p.AnchorY);
            }
            return p;
        }

        internal static Placement PlacementFrom(JObject cmd)
        {
            var p = new Placement
            {
                Show = BoolOr(cmd["show"], true),
                X = NumOrNull(cmd["x"]) ?? ActorLayer.SlotX((string)cmd["position"]),
                Y = NumOr(cmd["y"], 1f),
                Width = NumOrNull(cmd["width"]),
                Height = NumOrNull(cmd["height"]),
                AnchorX = 0.5f,
                AnchorY = 1f,
                Z = IntOrNull(cmd["z"]),
                Flip = BoolOr(cmd["flip"] ?? cmd["mirror"], false), // `mirror` is an authoring alias for flip
                Rotation = NumOr(cmd["rotation"], 0f),
                Opacity = NumOr(cmd["opacity"], 1f),
                HoverOpacity = NumOr(cmd["hover_opacity"], 1f),
                EnterTransition = ParseTransition((string)cmd["enter"]),
                ExitTransition = ParseTransition((string)cmd["exit"]),
                TransitionDuration = NumOr(cmd["transition_duration"], 0.3f),
            };

            var anchor = (string)cmd["anchor"];
            if (!string.IsNullOrEmpty(anchor))
            {
                var parts = anchor.Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ax)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ay))
                {
                    p.AnchorX = ax;
                    p.AnchorY = ay;
                }
            }
            else
            {
                if (cmd["anchor_x"] != null) p.AnchorX = NumOr(cmd["anchor_x"], p.AnchorX);
                if (cmd["anchor_y"] != null) p.AnchorY = NumOr(cmd["anchor_y"], p.AnchorY);
            }
            return p;
        }

        // Like AxesFrom but with {var} interpolation against the player's variables,
        // so equipment can be data-driven: `actor hero armor={arm} weapon={wpn}`.
        // An axis that resolves to empty or stays unresolved is DROPPED, leaving its
        // {axis} token unfilled → that layer is skipped (the "nothing equipped" case).
        private Dictionary<string, string> AxesOf(JObject cmd)
        {
            var axes = AxesFrom(cmd);
            var vars = _player?.Vars;
            // Axes whose raw value was a {var} template (e.g. the imported protagonist's
            // outfit={Wardrobe.mainCh_Clothes}) are variable-DRIVEN, not story-forced
            // literals — a live wardrobe preview may override those in realtime, while a
            // literal costume the writer pinned stays put. Track them for MergeInto.
            var templated = new HashSet<string>();
            foreach (var k in new List<string>(axes.Keys))
            {
                var v = axes[k];
                bool wasTemplate = !string.IsNullOrEmpty(v) && v.IndexOf('{') >= 0;
                if (wasTemplate)
                {
                    templated.Add(k);
                    if (vars != null) v = TextInterpolation.Apply(v, vars);
                }
                if (string.IsNullOrEmpty(v) || v.IndexOf('{') >= 0) axes.Remove(k); // no value → no layer
                else axes[k] = v;
            }
            // The player's wardrobe fills axes the script left unset — a story-forced
            // literal still wins, but a preview overrides a variable-driven axis.
            LvnWardrobe.MergeInto(axes, (string)cmd["id"], templated);
            return axes;
        }

        // The actor command's free-form named fields (pose, emotion, prop, …) —
        // everything outside the reserved layout/control set — are the cast axes.
        internal static Dictionary<string, string> AxesFrom(JObject cmd)
        {
            var axes = new Dictionary<string, string>();
            foreach (var p in cmd.Properties())
            {
                if (ReservedActorFields.Contains(p.Name)) continue;
                switch (p.Value.Type)
                {
                    case JTokenType.String:
                    case JTokenType.Integer:
                    case JTokenType.Float:
                    case JTokenType.Boolean:
                        axes[p.Name] = p.Value.ToString();
                        break;
                }
            }
            return axes;
        }
    }
}
