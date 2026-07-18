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
    public sealed class StageMenu : VisualElement
    {
        private const int SlotCount = 6;
        private const string QuickSlot = "quick"; // the one-tap save; shown in Load

        private readonly VnStage _stage;
        private readonly VnTheme _theme;
        private readonly VisualElement _fabRow;
        private VisualElement _scrim;

        public bool IsOpen { get; private set; }

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
            _modeBadge.style.fontSize = 17;
            _modeBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
            _modeBadge.style.color = _theme.MenuTextColor;
            _modeBadge.style.backgroundColor = _theme.MenuFabColor;
            Round(_modeBadge, 22);
            ClearBorder(_modeBadge);
            _modeBadge.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            if (_theme.Font != null) _modeBadge.style.unityFont = new StyleFont(_theme.Font);
            _modeBadge.style.display = DisplayStyle.None;
            _fabRow.Add(_modeBadge);

            if (_theme.MenuShowRollback) _fabRow.Add(Fab("↩", () => _stage.RollbackStep()));
            if (_theme.MenuShowMenu) _fabRow.Add(BurgerFab(OpenSheet));
            Add(_fabRow);

            // Cheap poll keeps the badge honest across every way a mode can flip
            // (menu, settings, a stopping tap, a choice ending skip).
            schedule.Execute(RefreshModeBadge).Every(250);
        }

        private Button _modeBadge;

        private void RefreshModeBadge()
        {
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
            Round(b, 22);
            ClearBorder(b);
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

        private void OpenSheet()
        {
            if (IsOpen) return;
            IsOpen = true;
            _stage.InputBlocked = true;
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

            ShowMain();
        }

        /// <summary>Close every open sheet/panel and unblock the stage.</summary>
        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            _stage.InputBlocked = false;
            _scrim?.RemoveFromHierarchy();
            _scrim = null;
            DestroyThumbs();
        }

        // Slot thumbnails shown by the CURRENT panel — destroyed on every panel
        // swap/close (they are per-view decoded textures, not cached sprites).
        private readonly List<Texture2D> _thumbs = new List<Texture2D>();

        private void DestroyThumbs()
        {
            foreach (var t in _thumbs) if (t != null) UnityEngine.Object.Destroy(t);
            _thumbs.Clear();
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
            Round(p, _theme.MenuCornerRadius + 2f);
            p.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _scrim.Add(p);

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.justifyContent = Justify.SpaceBetween;
            head.style.marginBottom = 10;
            var t = Text(title, 20, FontStyle.Bold);
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
            sheet.style.width = 240;
            sheet.style.backgroundColor = _theme.MenuBgColor;
            sheet.style.paddingTop = 8; sheet.style.paddingBottom = 8;
            Round(sheet, _theme.MenuCornerRadius);
            sheet.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _scrim.Add(sheet);

            sheet.Add(Item(L("quick_save", "Quick save"), () =>
            {
                _stage.SaveToSlot(QuickSlot);
                Close();
            }));
            sheet.Add(Item(L("save", "Save"), () => ShowSlots(saveMode: true)));
            sheet.Add(Item(L("load", "Load"), () => ShowSlots(saveMode: false)));
            sheet.Add(Item(L("history", "History"), ShowHistory));
            sheet.Add(Item(LvnPrefs.AutoAdvance ? L("auto", "Auto") + " ✓" : L("auto", "Auto"), () =>
            {
                LvnPrefs.AutoAdvance = !LvnPrefs.AutoAdvance;
                Close(); // hands-free mode starts/stops right away
            }));
            sheet.Add(Item(L("skip", "Skip"), () =>
            {
                Close();
                _stage.StartSkip(); // fast-forward until a choice or a tap
            }));
            sheet.Add(Item(L("settings", "Settings"), ShowSettings));
            // Live story variables — the player's stats. Only when the running
            // story actually has some, so stat-less novels never show a dead entry.
            if (_theme.MenuShowStats && _stage.Player != null && _stage.Player.Vars.Count > 0)
                sheet.Add(Item(L("stats", "Stats"), ShowStats));
            // The CG gallery — only when the title curates one (manifest
            // title.gallery), so novels without CGs never show a dead entry.
            if (_stage.Gallery != null && _stage.Gallery.Count > 0)
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
            b.style.height = 46;
            b.style.fontSize = 19;
            b.style.color = _theme.MenuTextColor;
            b.style.backgroundColor = Color.clear;
            b.style.unityTextAlign = TextAnchor.MiddleLeft;
            b.style.paddingLeft = 18;
            ClearBorder(b);
            if (_theme.Font != null) b.style.unityFont = new StyleFont(_theme.Font);
            return b;
        }

        // ── save / load slots ────────────────────────────────────────────────

        private void ShowSlots(bool saveMode)
        {
            var p = Panel(saveMode ? L("save", "Save") : L("load", "Load"));
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            var all = LvnSaveStore.Slots(_stage.SaveTitleId);

            // Engine-owned slots appear in load mode only: the rolling autosave
            // and the one-tap quick save.
            if (!saveMode && all.TryGetValue(LvnSaveStore.AutoSlot, out var auto) && auto?.Snap != null)
                scroll.Add(SlotRow(L("autosave", "Autosave"), auto, () => TryLoad(LvnSaveStore.AutoSlot)));
            if (!saveMode && all.TryGetValue(QuickSlot, out var quick) && quick?.Snap != null)
                scroll.Add(SlotRow(L("quick_slot", "Quick save"), quick, () => TryLoad(QuickSlot), thumbSlot: QuickSlot));

            for (int i = 0; i < SlotCount; i++)
            {
                var name = "slot" + (i + 1);
                all.TryGetValue(name, out var slot);
                var label = L("slot", "Slot") + " " + (i + 1);
                if (saveMode)
                {
                    var occupied = slot?.Snap != null; // an occupied slot asks before it's lost
                    scroll.Add(SlotRow(label, slot, () =>
                    {
                        if (occupied) ConfirmOverwrite(label, name);
                        else if (_stage.SaveToSlot(name)) ShowSlots(true); // refresh with the new stamp
                    }, thumbSlot: name));
                }
                else
                    scroll.Add(SlotRow(label, slot, () => TryLoad(name), enabled: _stage.CanLoadSlot(name), thumbSlot: name));
            }
        }

        // Overwriting a save is the one destructive tap in the whole menu — make
        // it a two-step: a small panel naming the slot, confirm or go back.
        private void ConfirmOverwrite(string label, string slotName)
        {
            var p = Panel(L("save", "Save"));
            var msg = Text(string.Format(L("overwrite_q", "Overwrite {0}?"), label), 16, FontStyle.Normal);
            msg.style.marginBottom = 12;
            p.Add(msg);
            p.Add(Item(L("overwrite", "Overwrite"), () =>
            {
                if (_stage.SaveToSlot(slotName)) ShowSlots(true);
            }));
            p.Add(Item(L("cancel", "Cancel"), () => ShowSlots(true)));
        }

        private async void TryLoad(string slot)
        {
            // Same-chapter slots restore in place; another chapter's slot routes
            // through the host (fetch that chapter's script, play, restore).
            if (await _stage.LoadFromSlotAsync(slot)) Close();
        }

        private VisualElement SlotRow(string label, LvnSaveSlot slot, Action onClick, bool enabled = true,
            string thumbSlot = null)
        {
            var row = new Button(onClick);
            row.style.height = 56;
            row.style.marginBottom = 6;
            var tint = _theme.MenuTextColor;
            row.style.backgroundColor = new Color(tint.r, tint.g, tint.b, 0.06f);
            row.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.style.paddingLeft = 12;
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            Round(row, Mathf.Max(4f, _theme.MenuCornerRadius - 4f));
            ClearBorder(row);
            row.SetEnabled(enabled);

            // The saved scene's screenshot, when one exists for this slot.
            var thumb = thumbSlot != null && slot?.Snap != null
                ? LvnSaveStore.LoadThumb(_stage.SaveTitleId, thumbSlot) : null;
            if (thumb != null)
            {
                _thumbs.Add(thumb);
                var img = new Image { image = thumb, scaleMode = ScaleMode.ScaleAndCrop, name = "slot-thumb" };
                img.style.width = 80;
                img.style.height = 45;
                img.style.marginRight = 10;
                img.style.flexShrink = 0;
                Round(img, 4f);
                row.Add(img);
            }

            var text = new VisualElement();
            text.style.flexDirection = FlexDirection.Column;
            text.style.justifyContent = Justify.Center;
            text.style.flexGrow = 1;
            string when = slot?.Snap == null ? L("empty", "— empty —")
                : DateTimeOffset.FromUnixTimeMilliseconds(slot.SavedAtUnixMs).ToLocalTime().ToString("dd.MM HH:mm");
            text.Add(Text(label + "   " + when, 15, FontStyle.Bold));
            if (!string.IsNullOrEmpty(slot?.Preview))
                text.Add(Text("«" + Trunc(slot.Preview, 46) + "»", 13, FontStyle.Italic, dim: true));
            row.Add(text);
            return row;
        }

        // ── history ──────────────────────────────────────────────────────────

        private void ShowHistory()
        {
            var p = Panel(L("history", "History"));
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            // Say-lines count from the end so a tap knows how many beats back its
            // line lives; the current line (0 back) and choice marks aren't jumps.
            var backlog = _stage.Backlog;
            int saysAfter = 0;
            for (int bi = backlog.Count - 1; bi >= 0; bi--)
                if (backlog[bi].style != "choice") saysAfter++;

            foreach (var (who, text, style) in backlog)
            {
                var line = new VisualElement();
                line.style.marginBottom = 8;
                if (style == "choice")
                {
                    // The branch the player took — indented, accented, arrowed.
                    var mark = Text("▸ " + text, 14, FontStyle.Italic);
                    mark.style.color = _theme.MenuFabColor;
                    line.style.marginLeft = 14;
                    line.Add(mark);
                }
                else
                {
                    saysAfter--;
                    if (!string.IsNullOrEmpty(who)) line.Add(Text(who, 14, FontStyle.Bold));
                    line.Add(Text(text, 15, FontStyle.Normal, dim: string.IsNullOrEmpty(who)));
                    // Tap-to-return: rewind to this line (the genre's history
                    // jump). Lines older than the snapshot history (or before a
                    // load, which clears it) aren't reachable — leave them inert.
                    int stepsBack = saysAfter;
                    int reach = _stage.Player != null ? _stage.Player.HistoryDepth - 1 : 0;
                    if (stepsBack > 0 && stepsBack <= reach)
                        line.RegisterCallback<PointerDownEvent>(e =>
                        {
                            e.StopPropagation();
                            Close();
                            _stage.RollbackSteps(stepsBack);
                        });
                }
                scroll.Add(line);
            }
            // Newest last — land the reader there.
            scroll.schedule.Execute(() =>
                scroll.scrollOffset = new Vector2(0, float.MaxValue)).ExecuteLater(50);
        }

        // ── CG gallery ───────────────────────────────────────────────────────

        private void ShowGallery()
        {
            var p = Panel(L("gallery", "Gallery"));
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            scroll.Add(grid);

            var unlocked = LvnGalleryStore.Unlocked(_stage.SaveTitleId);
            foreach (var item in _stage.Gallery)
            {
                if (item == null) continue;
                bool open = unlocked.Contains(item.id);

                var cell = new VisualElement();
                cell.style.width = Length.Percent(31);
                cell.style.marginRight = Length.Percent(2);
                cell.style.marginBottom = 12;

                var frame = new VisualElement();
                frame.style.height = 110;
                frame.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
                frame.style.justifyContent = Justify.Center;
                frame.style.alignItems = Align.Center;
                Round(frame, 8f);
                cell.Add(frame);

                if (open)
                {
                    var img = new Image { scaleMode = ScaleMode.ScaleAndCrop };
                    img.style.width = Length.Percent(100);
                    img.style.height = Length.Percent(100);
                    frame.Add(img);
                    LoadCg(img, item.url);
                    var full = item; // capture per cell
                    frame.RegisterCallback<PointerDownEvent>(e =>
                    {
                        e.StopPropagation();
                        ShowCgFull(full);
                    });
                    if (!string.IsNullOrEmpty(item.name))
                        cell.Add(Text(item.name, 12, FontStyle.Normal, dim: true));
                }
                else frame.Add(Text("?", 30, FontStyle.Bold, dim: true));

                grid.Add(cell);
            }
        }

        // Fullscreen viewer for one unlocked CG — chrome-free art, tap closes
        // back to the grid.
        private void ShowCgFull(Lvn.Content.LvnGalleryItem item)
        {
            DestroyThumbs();
            _scrim.Clear();
            var img = new Image { scaleMode = ScaleMode.ScaleToFit };
            img.style.position = Position.Absolute;
            img.style.left = 0; img.style.right = 0; img.style.top = 0; img.style.bottom = 0;
            _scrim.Add(img);
            LoadCg(img, item.url);
            img.RegisterCallback<PointerDownEvent>(e => { e.StopPropagation(); ShowGallery(); });
        }

        // Sprites come through the stage's asset chain (cache-aware); a panel
        // closed mid-load just orphans the element — nothing to cancel.
        private async void LoadCg(Image img, string url)
        {
            if (_stage.Assets == null || string.IsNullOrEmpty(url)) return;
            try
            {
                var sprite = await _stage.Assets.LoadSpriteAsync(url, System.Threading.CancellationToken.None);
                if (sprite != null && img.panel != null) img.sprite = sprite;
            }
            catch { /* a missing CG just leaves the dark frame */ }
        }

        // ── stats (live story variables) ─────────────────────────────────────

        // One panel answers "are my stats actually accruing?": every variable of
        // the RUNNING story, nested objects (global.*) flattened to dotted keys.
        // With ui.menu.stats_edit the rows become writable — the QA loop for a
        // stat-driven novel (nudge courage, reopen, watch the gate) without
        // replaying a chapter.
        private void ShowStats()
        {
            var p = Panel(L("stats", "Stats"));
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            var vars = _stage.Player?.Vars;
            if (vars == null || vars.Count == 0)
            {
                scroll.Add(Text(L("empty", "— empty —"), 15, FontStyle.Italic, dim: true));
                return;
            }
            var flat = new List<(string key, JToken val)>();
            foreach (var kv in vars) FlattenVar(kv.Key, kv.Value, flat);
            flat.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
            foreach (var (key, val) in flat) scroll.Add(StatRow(key, val));
        }

        // Leaves become rows; JObject nodes recurse into "parent.child" keys —
        // the exact dotted paths SetVar/GetVar/expressions read, so a row's key
        // is also its write address.
        private static void FlattenVar(string key, JToken val, List<(string, JToken)> into)
        {
            if (val is JObject o && o.Count > 0)
                foreach (var prop in o.Properties()) FlattenVar(key + "." + prop.Name, prop.Value, into);
            else into.Add((key, val));
        }

        private VisualElement StatRow(string key, JToken val)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.height = 34;
            row.style.marginBottom = 2;

            var name = Text(key, 15, FontStyle.Normal);
            name.style.flexGrow = 1;
            name.style.flexShrink = 1;
            name.style.overflow = Overflow.Hidden;
            row.Add(name);

            bool edit = _theme.MenuStatsEdit;
            var type = val?.Type ?? JTokenType.Null;
            if (type == JTokenType.Boolean)
            {
                if (edit)
                {
                    var t = new Toggle { value = val.Value<bool>() };
                    t.RegisterValueChangedCallback(e => _stage.Player.SetVar(key, new JValue(e.newValue)));
                    row.Add(t);
                }
                else row.Add(Text(val.Value<bool>() ? "true" : "false", 15, FontStyle.Bold));
            }
            else if (type == JTokenType.Integer || type == JTokenType.Float)
            {
                double d = val.Value<double>();
                if (edit)
                {
                    // − [value] + : steppers for the common nudge, the field for
                    // an exact number. Garbage input just doesn't commit.
                    var field = StatField(FormatNum(d), 64);
                    field.RegisterCallback<FocusOutEvent>(_ =>
                    {
                        if (double.TryParse(field.value.Replace(',', '.'),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var v))
                            _stage.Player.SetVar(key, new JValue(v % 1 == 0 ? (long)v : v));
                        else field.value = FormatNum(ReadNum(key, d));
                    });
                    row.Add(StatStep("−", () => Nudge(key, field, -1)));
                    row.Add(field);
                    row.Add(StatStep("+", () => Nudge(key, field, +1)));
                }
                else row.Add(Text(FormatNum(d), 15, FontStyle.Bold));
            }
            else if (type == JTokenType.String)
            {
                if (edit)
                {
                    var field = StatField((string)val, 120);
                    field.RegisterCallback<FocusOutEvent>(_ =>
                        _stage.Player.SetVar(key, new JValue(field.value ?? "")));
                    row.Add(field);
                }
                else row.Add(Text("«" + Trunc((string)val ?? "", 24) + "»", 15, FontStyle.Bold));
            }
            else
            {
                // null / arrays: show, don't edit — nothing in a story reads them
                // in a way a stepper could sensibly write.
                var s = val == null || type == JTokenType.Null ? "null"
                    : Trunc(val.ToString(Newtonsoft.Json.Formatting.None), 24);
                row.Add(Text(s, 14, FontStyle.Normal, dim: true));
            }
            return row;
        }

        private void Nudge(string key, TextField field, double by)
        {
            double v = ReadNum(key, 0) + by;
            _stage.Player.SetVar(key, new JValue(v % 1 == 0 ? (long)v : v));
            field.value = FormatNum(v);
        }

        // Re-read through the player so a stale row (story code changed the value
        // underneath an open panel) nudges the REAL current number, not the text.
        private double ReadNum(string key, double fallback)
        {
            if (_stage.Player == null) return fallback;
            try
            {
                var t = Lvn.LvnExpression.Evaluate(key, _stage.Player.Vars);
                return t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
                    ? t.Value<double>() : fallback;
            }
            catch { return fallback; }
        }

        private static string FormatNum(double d) => d % 1 == 0
            ? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private TextField StatField(string value, int width)
        {
            var f = new TextField { value = value };
            f.style.width = width;
            f.style.height = 28;
            f.style.marginLeft = 6; f.style.marginRight = 6;
            var input = f.Q("unity-text-input");
            if (input != null)
            {
                var tint = _theme.MenuTextColor;
                input.style.backgroundColor = new Color(tint.r, tint.g, tint.b, 0.08f);
                input.style.color = _theme.MenuTextColor;
                input.style.unityTextAlign = TextAnchor.MiddleCenter;
                ClearBorder(input);
                Round(input, 6f);
            }
            if (_theme.Font != null) f.style.unityFont = new StyleFont(_theme.Font);
            return f;
        }

        private Button StatStep(string glyph, Action onClick)
        {
            var b = new Button(onClick) { text = glyph };
            b.style.width = 30; b.style.height = 28;
            b.style.fontSize = 18;
            b.style.color = _theme.MenuTextColor;
            var tint = _theme.MenuTextColor;
            b.style.backgroundColor = new Color(tint.r, tint.g, tint.b, 0.08f);
            ClearBorder(b);
            Round(b, 6f);
            if (_theme.Font != null) b.style.unityFont = new StyleFont(_theme.Font);
            return b;
        }

        // ── settings ─────────────────────────────────────────────────────────

        private void ShowSettings()
        {
            var p = Panel(L("settings", "Settings"));
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            scroll.Add(SliderRow(L("text_speed", "Text speed"), 0.25f, 3f, LvnPrefs.TextSpeed, v => LvnPrefs.TextSpeed = v));
            scroll.Add(ToggleRow(L("auto_advance", "Auto-advance"), LvnPrefs.AutoAdvance, v => LvnPrefs.AutoAdvance = v));
            scroll.Add(SliderRow(L("auto_delay", "Auto delay"), 0.5f, 2.5f, LvnPrefs.AutoDelayScale, v => LvnPrefs.AutoDelayScale = v));
            scroll.Add(SliderRow(L("music", "Music"), 0f, 1f, LvnPrefs.VolMusic, v => LvnPrefs.VolMusic = v));
            scroll.Add(SliderRow(L("ambient", "Ambient"), 0f, 1f, LvnPrefs.VolAmbient, v => LvnPrefs.VolAmbient = v));
            scroll.Add(SliderRow(L("sfx", "Sound FX"), 0f, 1f, LvnPrefs.VolSfx, v => LvnPrefs.VolSfx = v));
            scroll.Add(SliderRow(L("voice", "Voice"), 0f, 1f, LvnPrefs.VolVoice, v => LvnPrefs.VolVoice = v));
            scroll.Add(SliderRow(L("window_opacity", "Window opacity"), 0.2f, 1f, LvnPrefs.DialogOpacity, v => LvnPrefs.DialogOpacity = v));
            scroll.Add(ToggleRow(L("skip_read_only", "Skip read text only"), LvnPrefs.SkipReadOnly, v => LvnPrefs.SkipReadOnly = v));
            scroll.Add(ToggleRow(L("reduce_motion", "Reduce motion"), LvnPrefs.ReduceMotion, v => LvnPrefs.ReduceMotion = v));

            // Language — only when the content ships catalogs (manifest.languages).
            // Tapping cycles Original → each language → Original.
            if (LvnPrefs.AvailableLocales.Count > 0)
                scroll.Add(LanguageRow());
        }

        private VisualElement LanguageRow()
        {
            var row = new VisualElement();
            row.style.marginBottom = 10;
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.alignItems = Align.Center;
            row.Add(Text(L("language", "Language"), 14, FontStyle.Normal));

            string Caption(string code) =>
                string.IsNullOrEmpty(code) ? L("language_original", "Original") : code.ToUpperInvariant();

            var btn = new Button { text = Caption(LvnPrefs.Locale) };
            btn.style.minWidth = 110;
            btn.style.height = 30;
            btn.style.color = _theme.MenuTextColor;
            var tint = _theme.MenuTextColor;
            btn.style.backgroundColor = new Color(tint.r, tint.g, tint.b, 0.08f);
            ClearBorder(btn);
            Round(btn, 6f);
            btn.clicked += () =>
            {
                LvnPrefs.Locale = LvnPrefs.NextLocale(LvnPrefs.Locale, LvnPrefs.AvailableLocales);
                btn.text = Caption(LvnPrefs.Locale);
            };
            row.Add(btn);
            return row;
        }

        private VisualElement SliderRow(string label, float min, float max, float value, Action<float> onChange)
        {
            var row = new VisualElement();
            row.style.marginBottom = 10;
            row.Add(Text(label, 14, FontStyle.Normal));
            var s = new Slider(min, max) { value = value };
            s.RegisterValueChangedCallback(e => onChange(e.newValue));
            row.Add(s);
            return row;
        }

        private VisualElement ToggleRow(string label, bool value, Action<bool> onChange)
        {
            var row = new VisualElement();
            row.style.marginBottom = 10;
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.Add(Text(label, 14, FontStyle.Normal));
            var t = new Toggle { value = value };
            t.RegisterValueChangedCallback(e => onChange(e.newValue));
            row.Add(t);
            return row;
        }

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
            b.style.fontSize = 24;
            b.style.width = 34; b.style.height = 30;
            ClearBorder(b);
            if (_theme.Font != null) b.style.unityFont = new StyleFont(_theme.Font);
        }

        private static void Round(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r; el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r; el.style.borderBottomRightRadius = r;
        }

        private static void ClearBorder(VisualElement el)
        {
            el.style.borderTopWidth = 0; el.style.borderBottomWidth = 0;
            el.style.borderLeftWidth = 0; el.style.borderRightWidth = 0;
        }

        private static string Trunc(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
