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

        // СЦЕНА РИСУЕТСЯ ОДНИМ СПОСОБОМ — uGUI-канвасом (60 кадров, Spine,
        // шейдеры, меши). Вторая реализация на UI Toolkit жила здесь за
        // переключателем UseCanvasScene и была снесена: продукт всегда шёл
        // канвасом, а поддерживать приходилось обе.
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
        private VisualElement _hintHost;   // centred motion host: keeps the card's layout transform intact
        private VisualElement _hintCard;   // top-center popup for the `hint` op
        private Label _hintLabel;
        private IVisualElementScheduledItem _hintHide; // auto-dismiss timer (duration>0)
        private FxLayer _fx;
        private StageAudio _audio;
        private StageMenu _menu;
        private TapBurstLayer _tapBurst;
        private Dictionary<string, CastEntity> _cast;
        private readonly Dictionary<string, LvnAnim> _talkAnims = new Dictionary<string, LvnAnim>(); // actor id → lip-sync anim
        private LvnPlayer _player;
        private CancellationTokenSource _cts;
        // Ждём ли касания по реплике. СВОЙСТВО, а не поле: слой `ui` обязан
        // узнавать об этом сразу, а точек сброса восемь — рано или поздно одна
        // осталась бы без оповещения. «Идёт реплика» — это именно ожидание
        // чтения, а не видимость окна: окно остаётся на экране и после того,
        // как игра ушла дальше, и по нему стадию определять нельзя.
        private bool _awaitingTapFlag;
        private bool _awaitingTap
        {
            get => _awaitingTapFlag;
            set
            {
                if (_awaitingTapFlag == value) return;
                _awaitingTapFlag = value;
                NotifyUiStage();
            }
        }
        private bool _awaitingWait;
        // Wait generation: a hotspot jump mid-`wait` cancels the pending timer —
        // otherwise WaitCoroutine's deferred Advance() fired AFTER the jump and
        // swallowed a beat somewhere else (the timed-hotspot-screen pattern).
        private int _waitGen;
        // Current on-screen beat — restored after a live theme rebuild so ApplyTheme
        // is safe to call mid-line (realtime theming keeps the line/choices visible).
        private bool _sayUp;
        // Every text/choice beat replaces the previous card through a short
        // dissolve. The generation invalidates delayed callbacks on reset/new beat.
        private int _dialogueSwapGeneration;
        private bool _choiceCommitInFlight;
        // A rebuilt UIDocument has no old pixels to fade out even if _sayUp
        // remembers that the player is parked on a line. The first rerender onto
        // that fresh surface must install the current card directly.
        private bool _dialogueSurfaceFresh;
        private IReadOnlyList<LvnOption> _curChoices;

        /// <summary>Public access to the underlying player for save/load.</summary>
        public LvnPlayer Player => _player;

        /// <summary>Открыть квик-меню извне (единый навбар); pane "history" —
        /// сразу в историю.</summary>
        public void OpenQuickMenu(string pane = null) => _menu?.Open(pane);

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
            // The platform BACK (Android back = Escape in Unity): close the
            // TOPMOST surface — the story panel (wardrobe…) first, then the
            // quick menu. The reader itself never exits on back. Модаль
            // оболочки поверх сцены (магазин из гейта) забирает «назад» себе —
            // см. LvnModalGuard.
            if (Input.GetKeyDown(KeyCode.Escape) && !LvnModalGuard.AnyOpen)
            {
                if (_panelHost != null && _panelHost.IsOpen)
                    PanelCancelRequested?.Invoke();
                else if (_menu != null && _menu.IsOpen)
                    _menu.Close();
            }
            // [lvn-perf] frame-hitch watchdog: any frame past 150 ms is a felt
            // freeze — log it with the in-flight spine work so a hitch can be
            // attributed (or ruled out) at a glance. Skips the very first frames
            // after a scene load, which are always heavy and not interesting.
            float dt = Time.unscaledDeltaTime;
            if (dt > 0.15f && Time.frameCount > 10)
                Debug.Log($"[lvn-perf] FRAME HITCH {(dt * 1000f):F0}ms at frame {Time.frameCount}"
                          + (_spineLoading.Count > 0 ? $" (spine builds in flight: {string.Join(",", _spineLoading)})" : ""));

            // СТОРОЖ БЕЛОГО ПОЛОТНА (охота 26.08). RawImage фона рисует свой
            // color сплошняком, когда текстуры нет, а после первой постановки
            // color=white — выгруженная текстура превращает кадр в белое пятно.
            // Логируем ПЕРЕХОД в это состояние (не каждый кадр), чтобы в логе
            // было видно, ЧТО случилось прямо перед ним.
            if (_renderer is CanvasSceneRenderer csr)
            {
                bool blank = csr.BackdropBlankWhite;
                if (blank != _bgWasBlankWhite)
                {
                    _bgWasBlankWhite = blank;
                    Debug.Log($"[lvn-bg] полотно {(blank ? "СТАЛО ПУСТЫМ И БЕЛЫМ" : "снова с картинкой")}: "
                              + $"{csr.BackdropState}, HasBackdrop={HasBackdrop}, epoch={_stageEpoch}, кадр {Time.frameCount}");
                }
                if (Time.unscaledTime >= _nextDeadLayerCheck)
                {
                    _nextDeadLayerCheck = Time.unscaledTime + 0.5f;
                    HealDeadActors(csr);
                }
            }
        }

        private float _nextDeadLayerCheck;

        /// <summary>САМОЛЕЧЕНИЕ КУКЛЫ. Спрайт может умереть уже ПОСЛЕ того, как
        /// его поставили: выгрузка забирает текстуру из-под живого актёра, и
        /// Image без спрайта заливает свой прямоугольник сплошным цветом —
        /// «после выхода из главы героиня пропадает, остаётся белый
        /// прямоугольник» (Илья 26.08). Раньше это чинил только игрок, руками
        /// перевыбрав вещь в гардеробе: тот путь пере-собирает облик. Здесь тот
        /// же реплей последней команды, но по факту опустевших слоёв — сцена
        /// возвращает себе картинку сама, кто бы ни забрал спрайты.</summary>
        private void HealDeadActors(CanvasSceneRenderer csr)
        {
            var dead = csr.ActorsWithDeadLayers();
            if (dead == null) return;
            foreach (var id in dead)
            {
                if (!_actorCmds.ContainsKey(id)) continue;
                Debug.LogWarning($"[lvn-actor] {id}: слои остались без спрайтов "
                                 + "(их выгрузили из-под живой куклы) — пересобираем облик");
                RefreshWardrobeActor(id, null);
            }
        }

        private bool _bgWasBlankWhite;

        /// <summary>Картинка на полотне ЕСТЬ (факт, не флаг). Хост держит этим
        /// инвариант «в меню всегда есть фон».</summary>
        public bool BackdropHasArt => (_renderer as CanvasSceneRenderer)?.BackdropHasArt ?? false;

        /// <summary>Диагностика «белого прямоугольника» ПО СЦЕНЕ: дерево
        /// оболочки уже показало, что светлого в нём нет, значит пятно рисует
        /// UGUI. Печатаем каждую видимую поверхность, которая рисует СПЛОШНОЙ
        /// светлый цвет без картинки — Image без спрайта и RawImage без
        /// текстуры выглядят именно так.</summary>
        public void DumpOpaqueGraphics()
        {
            var root = (_renderer as CanvasSceneRenderer)?.Root;
            if (root == null) { Debug.Log("[lvn-white] сцена: корня нет"); return; }
            var sb = new StringBuilder("[lvn-white] сплошные светлые поверхности СЦЕНЫ:\n");
            int found = 0;
            foreach (var g in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
            {
                if (g == null || !g.isActiveAndEnabled) continue;
                var c = g.color;
                if (c.a < 0.35f) continue;
                bool hasArt = (g is UnityEngine.UI.Image im && im.sprite != null)
                              || (g is UnityEngine.UI.RawImage ri && ri.texture != null);
                if (hasArt) continue;                       // с картинкой — не наш случай
                if ((c.r + c.g + c.b) / 3f < 0.55f) continue; // тёмное пятно не заметить
                var rt = g.rectTransform;
                var size = rt.rect.size;
                if (size.x < 80f || size.y < 80f) continue;
                found++;
                sb.AppendLine($"  {g.GetType().Name} '{HierarchyPath(g.transform)}' "
                              + $"цвет=#{ColorUtility.ToHtmlStringRGBA(c)} размер={size.x:0}x{size.y:0}");
            }
            if (found == 0) sb.AppendLine("  — сплошных светлых поверхностей нет");
            Debug.Log(sb.ToString());
        }

        private static string HierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
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

            // Scene = background + actors + camera. Канвас-сцена живёт на
            // СОСЕДНЕМ канвасе ПОД этой UI Toolkit-панелью, поэтому окно, выборы
            // и меню всегда рисуются поверх сцены.
            {
                // sortingOrder below the panel (10) so the UITK chrome composites on top.
                var scene = new World.WorldStage(transform, sortingOrder: 0);
                scene.SetBackgroundColor(Color.black);
                _renderer = new CanvasSceneRenderer(scene);
                LvnAsync.Fire(ApplyDefaultBackdropAsync(scene), "ApplyDefaultBackdrop"); // seamless tiled filler instead of flat black
            }

            _particles = new ParticleField();
            ResolveFont();
            _panelHost = null; // died with the previous panel root — recreate lazily
            _dialogue = new DialogueBox(Theme);
            _dialogue.RevealingChanged += OnDialogueRevealing; // луп клавиатуры
            SetSayVisible(_sayUp); // the empty skinned frame must not sit on a bare stage
            _choices = new ChoiceList(Theme);
            _fx = new FxLayer();

            _labelLayer = new VisualElement { name = "vn-labels", pickingMode = PickingMode.Ignore };
            _labelLayer.style.position = Position.Absolute;
            _labelLayer.style.left = 0; _labelLayer.style.right = 0; _labelLayer.style.top = 0; _labelLayer.style.bottom = 0;

            // Шрифт темы — на корень: unityFontDefinition наследуется вниз, и
            // сцена, интерфейс и диалог получают одну гарнитуру разом. Ставить
            // его в каждом экране значит однажды забыть про один из них.
            LvnFonts.ApplyDefault(root);

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

            // Тап-салют (ui.stage.tap_burst): сердечки из точки КАЖДОГО
            // касания — trickle-down видит тап раньше кнопок и не мешает им.
            if (!string.IsNullOrEmpty(Theme?.TapBurst))
            {
                _tapBurst = new TapBurstLayer();
                root.Add(_tapBurst); // поверх всего хрома
                root.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (_tapBurst != null && Theme?.TapBurst == "hearts")
                        _tapBurst.Burst(_tapBurst.WorldToLocal(evt.position));
                }, TrickleDown.TrickleDown);
            }
            _choices.OnSelected += OnChoiceSelected;
            _choices.VisibleChanged += OnChoicesVisibleChanged;
            // Окно растёт вместе с текстом — стопка выборов сторонится его в
            // реальном времени, а не по одному замеру на реплику.
            WireChoiceGeometrySync();

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
            // Follows the theme's rollback switch — a title that cut the feature
            // (ui.menu.show_rollback:false) cuts the gesture with it.
            root.RegisterCallback<WheelEvent>(evt =>
            {
                if (InputBlocked) return;
                if (Theme != null && !Theme.MenuShowRollback) return;
                if (evt.delta.y < 0f && RollbackStep()) evt.StopPropagation();
            });

            // Audio channels (music/ambient/sfx) live in their own component.
            _audio = gameObject.AddComponent<StageAudio>();

            _cts ??= new CancellationTokenSource(); // OnEnable usually made it; safety for a direct Build()

            // A disable/enable cycle rebuilt the chrome: an open quick menu died
            // with the old panel WITHOUT running Close() — its input block must
            // not orphan (the panel-host block re-derives from IsOpen anyway).
            _inputBlockedFlag = false;
            _panelInputGuardUntil = 0f;

            if (_player != null)
            {
                // The chrome was rebuilt under a LIVE player (a disable/enable
                // cycle — see OnDisable): re-render the scene and the current
                // beat on the new panel, the same recipe rollback uses.
                _player.OnSay -= RecordSay; // OnDisable unhooked it; twice would double-log
                _player.OnSay += RecordSay; // resubscribe even before the first say exists
                _dialogueSurfaceFresh = true;
                var snap = _player.PopCurrent();
                if (snap != null)
                {
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
            LvnAsync.Fire(EnsureThemeImagesAsync(), "EnsureThemeImages");
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
            catch { }   // разбор темы главы: кривое поле не повод не начать главу
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
            if (_dialogue != null) _dialogue.RemoveFromHierarchy();
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
            _dialogue.RevealingChanged += OnDialogueRevealing; // луп клавиатуры
            _dialogue.SetUserOpacity(LvnPrefs.DialogOpacity);
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
            // RebuildChrome replaces the VisualElements themselves. Event
            // subscriptions belong to those instances and must be wired again.
            WireChoiceGeometrySync();

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
                _dialogue.SetSpeaker(beat.who, DialogueSideForCurrentSpeaker(beat.who));
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

            // ЗВУКИ ИНТЕРФЕЙСА (manifest ui.sounds) едут тем же ленивым путём:
            // перестраивать оформление под них не нужно, места воспроизведения
            // читают поля напрямую. Хост без звука просто молчит.
            async Task Clip(string url, System.Action<AudioClip> assign)
            {
                if (string.IsNullOrEmpty(url)) return;
                try { assign(await Assets.LoadAudioAsync(url, _cts.Token)); }
                catch { /* хост может не везти звук вовсе — это не ошибка главы */ }
            }
            if (_sndClick == null) await Clip(Theme.ClickSoundUrl, c => _sndClick = c);
            if (_sndChoice == null) await Clip(Theme.ChoiceSoundUrl, c => _sndChoice = c);
            if (_sndType == null) await Clip(Theme.TypeSoundUrl, c => _sndType = c);
        }

        // Звук отклика на действие игрока (клик/выбор) + луп клавиатуры,
        // который печать строки водит через RevealingChanged диалога.
        private AudioClip _sndClick, _sndChoice, _sndType;

        private void PlayUiSound(AudioClip clip) =>
            _audio?.PlayUi(clip, Theme != null ? Theme.UiSoundVolume : 1f);

        // Печать началась → луп клавиатуры; кончилась (естественно, тапом или
        // сменой строки) → тишина. Клип может доехать позже первой реплики —
        // не страшно: следующая печать его подхватит.
        private void OnDialogueRevealing(bool on)
        {
            if (on) _audio?.PlayTypingLoop(_sndType, Theme != null ? Theme.UiSoundVolume : 1f);
            else _audio?.StopTypingLoop();
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
                LvnAsync.Fire(LoadContentFontAsync(src), "LoadContentFont");
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
                ReleaseActive3DSet();
                _renderer = null;
                _uiRoot = null;
                _menu = null;
                _labelLayer = null;
                _hintHide?.Pause(); _hintHide = null;
                _hintHost = null; _hintCard = null; _hintLabel = null;
                _labelEls.Clear();
                _labelTmpl.Clear();
                if (_audio != null) { Destroy(_audio); _audio = null; }
            }
        }

        private void OnPrefsChanged()
        {
            _dialogue?.SetUserOpacity(LvnPrefs.DialogOpacity);
        }

        private void OnDestroy()
        {
            ReleaseActive3DSet();
            Assets?.UnloadAll();
            // The spine integration's static cache holds SkeletonData/materials
            // built around textures UnloadAll just destroyed — flush it with them.
            LvnSpineBridge.ClearCache?.Invoke();
            if (_pendingThumb != null) Destroy(_pendingThumb);
        }

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
            // Окно гардероба ПОДНИМАЕТСЯ НАД репликой, а не меняется с ней
            // местами. Диалог и рама — один и тот же полупрозрачный скин в
            // одном месте экрана: гаснущие одновременно, они на ~80 мс роняли
            // суммарную плотность почти вдвое, и сквозь окно разово «вспыхивал»
            // фон (живой репорт «фон мелькнул перед гардеробом»). Рама рисуется
            // над диалогом — пусть встанет плотной, и только потом реплика
            // гаснет, уже полностью укрытая.
            if (_menu != null)
            {
                _menu.Close();
                _menu.style.visibility = Visibility.Hidden;
            }
            await PanelHost.ShowAsync(content);
            await FadeDialogueAsync(true);
        }

        /// <summary>Dismiss the shared window and bring the dialogue back.</summary>
        public async Task HidePanelAsync()
        {
            // Симметрично показу: диалог возвращается ПОД стоящей рамой, и
            // только затем рама уходит — плотность окна не проваливается.
            // PanelOpen/InputBlocked держатся до конца: Resume() зовётся после
            // этой задачи, следующая реплика не стартует под чужим хромом.
            await FadeDialogueAsync(false);
            if (_panelHost != null) await _panelHost.HideAsync();
            ArmPanelInputGuard(0.12f);
        }

        /// <summary>Fade the dialogue box (and choices) out/in — the shared
        /// window replaces it visually, so both never fight for the bottom.</summary>
        public void SetDialogueFaded(bool faded)
            => LvnAsync.Fire(FadeDialogueAsync(faded), "PanelDialogueFade");

        private async Task FadeDialogueAsync(bool faded)
        {
            float to = faded ? 0f : 1f;
            float seconds = 0.18f * VnTheme.MotionDurationScale;
            // The story panel OWNS the screen while it's up (the genre rule):
            // the quick-menu chrome hides with the dialogue — no burger over
            // the wardrobe, no half-working Exit under a held story.
            if (_menu != null)
            {
                if (faded) _menu.Close();
                _menu.style.visibility = faded ? Visibility.Hidden : Visibility.Visible;
            }
            var dialogue = _dialogue != null
                ? ScreenFx.FadeAsync(_dialogue, faded ? 1f : 0f, to, seconds, _cts?.Token ?? default)
                : Task.CompletedTask;
            var choices = _choices != null
                ? ScreenFx.FadeAsync(_choices, faded ? 1f : 0f, to, seconds, _cts?.Token ?? default)
                : Task.CompletedTask;
            await Task.WhenAll(dialogue, choices);
        }

        /// <summary>The platform back pressed while the shared story panel is
        /// open — the panel's OWNER dismisses its content (the wardrobe sheet's
        /// cancel). The stage can't: it only hosts the frame.</summary>
        public Action PanelCancelRequested;

        /// <summary>Close the quick menu if it's open (host screens that take
        /// over from a menu tap call this so the scrim doesn't linger).</summary>
        public void CloseQuickMenu() => _menu?.Close();

        /// <summary>True while the shared story panel (wardrobe…) is up — the
        /// quick-menu chrome polls this and keeps itself off the screen.</summary>
        public bool PanelOpen => _panelHost != null && _panelHost.IsOpen;

        /// <summary>ПОСМОТРЕТЬ, ЧТО НАДЕЛ. Панель занимает низ экрана, а наряд
        /// нарисован в полный рост — примеряя, игрок видит героиню по пояс.
        /// Этот режим убирает с глаз панель и весь интерфейс, НЕ закрывая
        /// панель: примерка продолжается, история по-прежнему не принимает
        /// касаний, а любое касание возвращает всё назад.
        ///
        /// <para>Отдельный режим, а не переиспользование долгого нажатия:
        /// то живёт на реплике и отпускается вместе с пальцем, это — на
        /// открытой панели и держится, пока игрок смотрит.</para></summary>
        public bool PanelPeeking { get; private set; }

        public void SetPanelPeek(bool on)
        {
            if (PanelPeeking == on) return;
            PanelPeeking = on;
            if (_panelHost != null)
                _panelHost.style.visibility = on ? Visibility.Hidden : Visibility.Visible;
            SetChromeHidden(on);
        }
    }
}
