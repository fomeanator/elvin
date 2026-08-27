using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The in-game quick menu: two floating buttons (menu ☰ and rollback ↩) that
    /// unfold into Save / Load / History / Auto / Settings panels — the standard
    /// VN chrome, built from the engine's own primitives (LvnSaveStore, LvnPrefs,
    /// the stage's backlog and rollback). Lives as a top layer inside the stage's
    /// UIDocument; while a sheet is open the stage's tap-to-advance is blocked.
    /// </summary>
    public sealed partial class StageMenu : VisualElement
    {
        private const int SlotCount = 6;
        private const string QuickSlot = "quick"; // the one-tap save; shown in Load

        private readonly VnStage _stage;
        private readonly VnTheme _theme;
        private readonly VisualElement _fabRow;
        private VisualElement _scrim;

        public bool IsOpen { get; private set; }

        /// <summary>Бургер живёт во внешнем навбаре — свой фаб не рисуем.</summary>
        public static bool ExternalBurger;

        /// <summary>Единая панель настроек хоста: пункт «Настройки» зовёт её
        /// вместо внутренней (решение Ильи 26.08 — никаких двух настроек).</summary>
        public static Action ExternalSettings;

        /// <summary>Открыть квик-меню извне (бургер единого навбара).
        /// <paramref name="pane"/> "history" — сразу в историю.</summary>
        public void Open(string pane = null) { _pendingPane = pane; OpenSheet(); }

        private string _pendingPane;

        // Every chrome string resolves through the theme's label map (manifest
        // ui.menu.labels) so a novel ships its own language; English is the
        // engine default.
        private string L(string key, string fallback) =>
            _theme.MenuLabels != null && _theme.MenuLabels.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)
                ? v : fallback;

        public StageMenu(VnStage stage, VnTheme theme)
        {
            _stage = stage;
            _theme = theme ?? new VnTheme();

            name = "vn-menu";
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0; style.bottom = 0;
            pickingMode = PickingMode.Ignore; // the closed layer never eats stage taps
            WatchDetach();

            // Floating buttons, top-right under the shell HUD strip. Which ones
            // exist — and every colour below — comes from the theme (manifest.ui.menu).
            _fabRow = new VisualElement();
            _fabRow.style.position = Position.Absolute;
            _fabRow.style.top = Length.Percent(8.5f);
            _fabRow.style.right = 10;
            _fabRow.style.flexDirection = FlexDirection.Row;
            // Mode badge: AUTO ▷ / SKIP ▶▶ while a hands-free mode runs — the
            // player must SEE why the game advances itself (and a tap on the
            // badge turns the mode off). Sits left of the buttons.
            _modeBadge = new Button(() =>
            {
                if (_stage.Skipping) _stage.StopSkip();
                else LvnPrefs.AutoAdvance = false;
            });
            _modeBadge.style.height = 44;
            _modeBadge.style.marginRight = 8;
            _modeBadge.style.paddingLeft = 12; _modeBadge.style.paddingRight = 12;
            _modeBadge.style.fontSize = 20;
            _modeBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
            _modeBadge.style.color = _theme.MenuTextColor;
            _modeBadge.style.backgroundColor = _theme.MenuFabColor;
            LvnChrome.Round(_modeBadge, 22);
            LvnChrome.ClearBorder(_modeBadge);
            _modeBadge.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            if (_theme.Font != null) _modeBadge.style.unityFont = new StyleFont(_theme.Font);
            _modeBadge.style.display = DisplayStyle.None;
            _fabRow.Add(_modeBadge);

            if (_theme.MenuShowRollback) _fabRow.Add(Fab("↩", () => _stage.RollbackStep()));
            // Единый навбар приложения несёт бургер сам — фаб-дубликат
            // выключается хостом (ExternalBurger).
            if (_theme.MenuShowMenu && !ExternalBurger) _fabRow.Add(BurgerFab(OpenSheet));
            Add(_fabRow);

            // Cheap poll keeps the badge honest across every way a mode can flip
            // (menu, settings, a stopping tap, a choice ending skip).
            schedule.Execute(RefreshModeBadge).Every(250);
        }

        private Button _modeBadge;

        private void RefreshModeBadge()
        {
            // The story panel owns the screen: whatever path raised it, the
            // burger/rollback chrome stays away while it's up (self-healing —
            // this poll runs anyway).
            _fabRow.style.display = _stage.PanelOpen ? DisplayStyle.None : DisplayStyle.Flex;
            if (_stage.PanelOpen) return;
            string label = _stage.Skipping ? L("skip", "Skip").ToUpperInvariant() + " ▶▶"
                : LvnPrefs.AutoAdvance ? L("auto", "Auto").ToUpperInvariant() + " ▷"
                : null;
            _modeBadge.style.display = label == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (label != null && _modeBadge.text != label) _modeBadge.text = label;
        }

        private VisualElement Fab(string glyph, Action onClick)
        {
            var b = new Button(onClick) { text = glyph };
            b.style.width = 44; b.style.height = 44;
            b.style.marginLeft = 8;
            b.style.fontSize = 22;
            b.style.color = _theme.MenuTextColor;
            b.style.backgroundColor = _theme.MenuFabColor;
            LvnChrome.Round(b, 22);
            LvnChrome.ClearBorder(b);
            // A press on the chrome must never bubble into tap-to-advance.
            b.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            if (_theme.Font != null) b.style.unityFont = new StyleFont(_theme.Font);
            return b;
        }

        // The menu button draws its hamburger as three bars instead of the "☰"
        // glyph — Android's default runtime font lacks it (tofu on device;
        // desktop fonts happen to cover it, so the editor never showed the bug).
        private VisualElement BurgerFab(Action onClick)
        {
            var b = (Button)Fab("", onClick);
            b.style.alignItems = Align.Center;
            b.style.justifyContent = Justify.Center;
            for (int i = 0; i < 3; i++)
            {
                var bar = new VisualElement();
                bar.pickingMode = PickingMode.Ignore;
                bar.style.width = 18; bar.style.height = 2;
                bar.style.marginTop = i == 0 ? 0 : 3;
                bar.style.backgroundColor = _theme.MenuTextColor;
                b.Add(bar);
            }
            return b;
        }

        // ── sheet ────────────────────────────────────────────────────────────

        // Та же страховка, что у листа истории: снесли сцену с открытым
        // квик-меню — поверхность уходит вместе с элементом.
        private void WatchDetach()
        {
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                if (IsOpen) LvnScreenDirector.Current.Close(LvnScreenDirector.QuickMenu);
            });
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                if (IsOpen) LvnScreenDirector.Current.Open(LvnScreenDirector.QuickMenu);
            });
        }

        private void OpenSheet()
        {
            if (IsOpen) return;
            IsOpen = true;
            // Ввод больше не гасится флагом вручную: экран знает, что квик-меню
            // на нём стоит, и держит ввод сам (см. VnStage.InputBlocked).
            LvnScreenDirector.Current.Open(LvnScreenDirector.QuickMenu);
            // Snapshot the CLEAN frame first — it becomes the thumbnail of any
            // save made from this menu. The scrim waits one frame for it.
            _stage.CaptureMenuThumb(OpenSheetChrome);
        }

        private void OpenSheetChrome()
        {
            if (!IsOpen) return; // closed before the capture frame ended

            // Full-screen scrim: swallows every tap; tapping empty space closes.
            _scrim = new VisualElement();
            _scrim.style.position = Position.Absolute;
            _scrim.style.left = 0; _scrim.style.right = 0; _scrim.style.top = 0; _scrim.style.bottom = 0;
            _scrim.style.backgroundColor = _theme.MenuScrimColor;
            _scrim.RegisterCallback<PointerDownEvent>(e =>
            {
                e.StopPropagation();
                if (e.target == _scrim) Close();
            });
            Add(_scrim);

            if (_pendingPane == "history") { _pendingPane = null; ShowHistory(); }
            else ShowMain();
        }

        /// <summary>Close every open sheet/panel and unblock the stage.</summary>
        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            LvnScreenDirector.Current.Close(LvnScreenDirector.QuickMenu);
            _scrim?.RemoveFromHierarchy();
            _scrim = null;
            DestroyThumbs();
        }



        // Swap the scrim's content for a fresh panel.
        private VisualElement Panel(string title)
        {
            DestroyThumbs();
            _scrim.Clear();
            var p = new VisualElement();
            p.style.position = Position.Absolute;
            p.style.left = Length.Percent(8); p.style.right = Length.Percent(8);
            p.style.top = Length.Percent(12); p.style.bottom = Length.Percent(12);
            p.style.backgroundColor = _theme.MenuBgColor;
            p.style.paddingLeft = 18; p.style.paddingRight = 18;
            p.style.paddingTop = 14; p.style.paddingBottom = 14;
            LvnChrome.Round(p, _theme.MenuCornerRadius + 2f);
            p.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _scrim.Add(p);

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.justifyContent = Justify.SpaceBetween;
            head.style.marginBottom = 10;
            var t = Text(title, 34, FontStyle.Bold);
            head.Add(t);
            var back = new Button(ShowMain) { text = "‹" };
            StyleGhost(back);
            head.Add(back);
            p.Add(head);
            return p;
        }

        private void ShowMain()
        {
            _scrim.Clear();
            var sheet = new VisualElement();
            sheet.style.position = Position.Absolute;
            sheet.style.right = 12;
            sheet.style.top = Length.Percent(10);
            sheet.style.width = 310;
            sheet.style.backgroundColor = _theme.MenuBgColor;
            sheet.style.paddingTop = 8; sheet.style.paddingBottom = 8;
            LvnChrome.Round(sheet, _theme.MenuCornerRadius);
            sheet.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _scrim.Add(sheet);

            // Продукт объявляет, чего в его меню НЕТ (ui.menu.hide): автосейвная
            // игра прячет ручные сейвы и историю данными, а не форком оболочки.
            bool Hidden(string key)
                => _theme.MenuHidden != null && _theme.MenuHidden.Contains(key);

            if (!Hidden("quick_save"))
                sheet.Add(Item(L("quick_save", "Quick save"), () =>
                {
                    _stage.SaveToSlot(QuickSlot);
                    Close();
                }));
            if (!Hidden("save"))
                sheet.Add(Item(L("save", "Save"), () => ShowSlots(saveMode: true)));
            if (!Hidden("load"))
                sheet.Add(Item(L("load", "Load"), () => ShowSlots(saveMode: false)));
            if (!Hidden("history"))
                sheet.Add(Item(L("history", "History"), ShowHistory));
            if (!Hidden("auto"))
                sheet.Add(Item(LvnPrefs.AutoAdvance ? L("auto", "Auto") + " ✓" : L("auto", "Auto"), () =>
                {
                    LvnPrefs.AutoAdvance = !LvnPrefs.AutoAdvance;
                    Close(); // hands-free mode starts/stops right away
                }));
            if (!Hidden("skip"))
                sheet.Add(Item(L("skip", "Skip"), () =>
                {
                    Close();
                    _stage.StartSkip(); // fast-forward until a choice or a tap
                }));
            if (!Hidden("settings"))
                sheet.Add(Item(L("settings", "Settings"), () =>
                {
                    if (ExternalSettings != null) { Close(); ExternalSettings(); }
                    else ShowSettings();
                }));
            // Live story variables — the player's stats. Only when the running
            // story actually has some, so stat-less novels never show a dead entry.
            if (!Hidden("stats") && _theme.MenuShowStats && _stage.Player != null && _stage.Player.Vars.Count > 0)
                sheet.Add(Item(L("stats", "Stats"), ShowStats));
            // The CG gallery — only when the title curates one (manifest
            // title.gallery), so novels without CGs never show a dead entry.
            if (!Hidden("gallery") && _stage.Gallery != null && _stage.Gallery.Count > 0)
                sheet.Add(Item(L("gallery", "Gallery"), ShowGallery));
            // Host-registered items (achievements, gallery, a debug screen…) —
            // the embedding game's own entries, between Settings and Exit.
            foreach (var kv in _customItems)
            {
                var cb = kv.Value;
                sheet.Add(Item(kv.Key, () => cb(_stage)));
            }
            sheet.Add(Item(L("exit", "Exit to menu"), () =>
            {
                // Autosaves, then signals the host loop back to the title screen —
                // the carousel's Continue returns to this exact line.
                Close();
                _stage.RequestExit();
            }));
            sheet.Add(Item(L("close", "Close"), Close));
        }

        // ── host extension: custom menu items ────────────────────────────────
        private static readonly Dictionary<string, Action<VnStage>> _customItems
            = new Dictionary<string, Action<VnStage>>();

        /// <summary>Add (or replace) a menu item supplied by the EMBEDDING game —
        /// e.g. "Достижения" opening the host's own screen. Appears between
        /// Settings and Exit the next time the menu opens. The callback receives
        /// the running stage (close the menu yourself via stage if needed).</summary>
        public static void AddMenuItem(string label, Action<VnStage> onClick)
        {
            if (string.IsNullOrEmpty(label) || onClick == null) return;
            _customItems[label] = onClick;
        }

        /// <summary>Remove a host-registered menu item by its label.</summary>
        public static void RemoveMenuItem(string label) => _customItems.Remove(label ?? "");

        private VisualElement Item(string label, Action onClick)
        {
            var b = new Button(onClick) { text = label };
            b.style.height = 64;
            b.style.fontSize = 26;
            b.style.color = _theme.MenuTextColor;
            b.style.backgroundColor = Color.clear;
            b.style.unityTextAlign = TextAnchor.MiddleLeft;
            b.style.paddingLeft = 20;
            LvnChrome.ClearBorder(b);
            if (_theme.Font != null) b.style.unityFont = new StyleFont(_theme.Font);
            return b;
        }

        // ── save / load slots ────────────────────────────────────────────────





        // ── history ──────────────────────────────────────────────────────────


        // ── CG gallery ───────────────────────────────────────────────────────




        // ── stats (live story variables) ─────────────────────────────────────










        // ── settings ─────────────────────────────────────────────────────────






        // ── little style helpers ─────────────────────────────────────────────

        private Label Text(string s, int size, FontStyle weight, bool dim = false)
        {
            var l = new Label(s);
            l.style.fontSize = size;
            l.style.unityFontStyleAndWeight = weight;
            l.style.color = dim ? _theme.MenuDimTextColor : _theme.MenuTextColor;
            l.style.whiteSpace = WhiteSpace.Normal;
            if (_theme.Font != null) l.style.unityFont = new StyleFont(_theme.Font);
            return l;
        }

        private void StyleGhost(Button b)
        {
            b.style.backgroundColor = Color.clear;
            b.style.color = _theme.MenuTextColor;
            b.style.fontSize = 38;
            b.style.width = 52; b.style.height = 46;
            LvnChrome.ClearBorder(b);
            if (_theme.Font != null) b.style.unityFont = new StyleFont(_theme.Font);
        }

        private static string Trunc(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
