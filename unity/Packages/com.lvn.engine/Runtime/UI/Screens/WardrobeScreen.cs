using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The wardrobe overlay, themed from a <see cref="WardrobeConfig"/>
    /// (manifest <c>ui.wardrobe</c>): a LIVE layered preview of the character
    /// on top, slot tabs (one per wardrobe axis) and item cards below. Items
    /// come from the character itself (<c>sprites.&lt;id&gt;.wardrobe</c>);
    /// tapping a card tries it on in the preview, the card's button buys
    /// (wallet sku inventory — server-authoritative), equips
    /// (<see cref="LvnWardrobe"/> → the stage re-applies the actor live) or
    /// takes off. Free items are owned from the start.
    /// </summary>
    public sealed class WardrobeScreen : VisualElement
    {
        private readonly WardrobeConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly Color _text, _dim, _accent, _accentText;
        private readonly float _radius;

        private readonly Label _title;
        private readonly VisualElement _balances;
        private readonly VisualElement _previewBox;
        private readonly VisualElement _preview;
        private readonly VisualElement _tabs;
        private readonly ScrollView _list;
        private readonly Label _note;

        private LvnManifest _manifest;
        private SpriteCatalog _catalog;
        private string _entity;
        private LvnSpriteEntity _def;
        private string _tab;                       // active axis
        private readonly Dictionary<string, string> _tryOn = new Dictionary<string, string>(); // preview-only fit

        private TaskCompletionSource<bool> _tcs;
        private bool _open;
        private bool _buying;
        private int _previewGen;

        public WardrobeScreen(WardrobeConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new WardrobeConfig();
            _assets = assets;
            _text = UiColor.Parse(_cfg.text_color, new Color(0.95f, 0.93f, 0.88f));
            _dim = UiColor.Parse(_cfg.dim_text_color, new Color(0.60f, 0.58f, 0.54f));
            _accent = UiColor.Parse(_cfg.accent_color, new Color(0.78f, 0.63f, 0.31f));
            _accentText = UiColor.Parse(_cfg.accent_text_color, new Color(0.08f, 0.08f, 0.10f));
            _radius = _cfg.corner_radius ?? 12f;

            ScreenUi.Stretch(this);
            style.backgroundColor = UiColor.Parse(_cfg.scrim_color, new Color(0f, 0f, 0f, 0.7f));
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            RegisterCallback<ClickEvent>(e => { if (e.target == this) Close(); });

            var sheet = new VisualElement();
            sheet.style.position = Position.Absolute;
            sheet.style.left = Length.Percent(5f);
            sheet.style.right = Length.Percent(5f);
            sheet.style.top = Length.Percent(6f);
            sheet.style.bottom = Length.Percent(6f);
            sheet.style.backgroundColor = UiColor.Parse(_cfg.panel_color, new Color(0.078f, 0.078f, 0.10f, 0.97f));
            Round(sheet, _radius + 4f);
            sheet.style.paddingTop = 20;
            sheet.style.paddingBottom = 16;
            sheet.style.paddingLeft = 18;
            sheet.style.paddingRight = 18;
            Add(sheet);

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.marginBottom = 10;
            sheet.Add(header);

            _title = new Label(_cfg.title ?? "Wardrobe");
            _title.style.color = UiColor.Parse(_cfg.title_color, new Color(0.96f, 0.93f, 0.85f));
            _title.style.fontSize = 34;
            header.Add(_title);

            _balances = new VisualElement();
            _balances.style.flexDirection = FlexDirection.Row;
            _balances.style.alignItems = Align.Center;
            header.Add(_balances);

            // ── live preview: a fixed-aspect box, layers stacked inside ──
            _previewBox = new VisualElement();
            _previewBox.style.height = Length.Percent(60f); // taller heroine preview
            _previewBox.style.backgroundColor = UiColor.Parse(_cfg.preview_bg_color, new Color(0.06f, 0.06f, 0.08f));
            // Optional configurable scene behind the heroine (manifest ui.wardrobe.preview_bg_image).
            if (!string.IsNullOrEmpty(_cfg.preview_bg_image))
            {
                _previewBox.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                _previewBox.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                _previewBox.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                _previewBox.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                _ = ScreenUi.AssignBgAsync(_previewBox, _cfg.preview_bg_image, _assets);
            }
            Round(_previewBox, _radius);
            _previewBox.style.alignItems = Align.Center;
            _previewBox.style.justifyContent = Justify.Center;
            _previewBox.style.marginBottom = 10;
            _previewBox.style.overflow = Overflow.Hidden;
            sheet.Add(_previewBox);

            _preview = new VisualElement();
            _preview.style.position = Position.Relative;
            _previewBox.Add(_preview);
            // UITK has no aspect-ratio style — size the preview to the art's
            // authored aspect whenever the box lays out.
            _previewBox.RegisterCallback<GeometryChangedEvent>(_ => FitPreview());

            _tabs = new VisualElement();
            _tabs.style.flexDirection = FlexDirection.Row;
            _tabs.style.marginBottom = 8;
            sheet.Add(_tabs);

            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _list.style.flexGrow = 1;
            sheet.Add(_list);

            _note = new Label("");
            _note.style.color = _dim;
            _note.style.fontSize = 22;
            _note.style.unityTextAlign = TextAnchor.MiddleCenter;
            _note.style.marginTop = 8;
            _note.style.display = DisplayStyle.None;
            sheet.Add(_note);

            var close = new Button(Close) { text = _cfg.close_text ?? "Close" };
            close.style.fontSize = 26;
            close.style.marginTop = 10;
            close.style.paddingTop = 12;
            close.style.paddingBottom = 12;
            close.style.color = _text;
            close.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
            Round(close, _radius);
            sheet.Add(close);
        }

        /// <summary>Point the screen at the live manifest (entities + their
        /// wardrobes). Called on Build and on every live content update.</summary>
        public void SetManifest(LvnManifest manifest)
        {
            _manifest = manifest;
            _catalog = new SpriteCatalog(manifest?.sprites);
        }

        /// <summary>Entities that actually have a wardrobe, in manifest order.</summary>
        public List<string> Entities()
        {
            var list = new List<string>();
            if (_manifest?.sprites != null)
                foreach (var kv in _manifest.sprites)
                    if (kv.Value?.wardrobe != null && kv.Value.wardrobe.Count > 0)
                        list.Add(kv.Key);
            return list;
        }

        /// <summary>Open the wardrobe for a character (null → the config's
        /// default, else the first entity with a wardrobe). Resolves when the
        /// player closes the screen.</summary>
        public async Task ShowAsync(string entityId = null, CancellationToken ct = default)
        {
            if (_open) return;
            var id = entityId ?? _cfg.entity;
            if (string.IsNullOrEmpty(id) || _manifest?.sprites == null || !_manifest.sprites.ContainsKey(id))
            {
                var all = Entities();
                id = all.Count > 0 ? all[0] : null;
            }
            _open = true;
            BuildFor(id);
            style.display = DisplayStyle.Flex;
            LvnWallet.Changed += OnWalletChanged;
            _ = LvnWallet.RefreshAsync(); // ownership lives in the wallet inventory
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.25f, ct);
            // Hide() during the fade-in cancels the open (see StoreScreen).
            if (!_open) { LvnWallet.Changed -= OnWalletChanged; return; }

            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            try { await _tcs.Task; }
            finally
            {
                LvnWallet.Changed -= OnWalletChanged;
                await ScreenFx.FadeAsync(this, 1f, 0f, 0.25f, CancellationToken.None);
                style.display = DisplayStyle.None;
                _open = false;
            }
        }

        public void Hide()
        {
            LvnWallet.Changed -= OnWalletChanged;
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _open = false;
            _tcs?.TrySetResult(false);
        }

        private void Close() => _tcs?.TrySetResult(true);

        private void OnWalletChanged()
        {
            RefreshBalances();
            RebuildTab(); // a purchase just landed → cards flip to "equip"
        }

        /// <summary>Build the whole screen for one character. Public so tests
        /// can render without opening/awaiting.</summary>
        public void BuildFor(string entityId)
        {
            _entity = entityId;
            _def = _entity != null && _manifest?.sprites != null && _manifest.sprites.TryGetValue(_entity, out var d) ? d : null;
            _tryOn.Clear();
            foreach (var kv in LvnWardrobe.Equipped(_entity)) _tryOn[kv.Key] = kv.Value; // start from what's worn

            _title.text = (_cfg.title ?? "Wardrobe")
                + (string.IsNullOrEmpty(_def?.name) ? "" : " — " + _def.name);
            RefreshBalances();

            _tabs.Clear();
            _list.Clear();
            _tab = null;
            if (_def?.wardrobe == null || _def.wardrobe.Count == 0)
            {
                SetNote(_cfg.empty_text ?? "The wardrobe is empty");
                RebuildPreview();
                return;
            }
            SetNote(null);

            foreach (var kv in _def.wardrobe)
            {
                var axis = kv.Key;
                if (_tab == null) _tab = axis;
                var b = new Button(() => { _tab = axis; StyleTabs(); RebuildTab(); })
                { text = kv.Value?.name ?? axis };
                b.style.fontSize = 24;
                b.style.marginRight = 8;
                b.style.paddingTop = 8; b.style.paddingBottom = 8;
                b.style.paddingLeft = 16; b.style.paddingRight = 16;
                Round(b, _radius);
                b.userData = axis;
                _tabs.Add(b);
            }
            StyleTabs();
            RebuildTab();
            RebuildPreview();
        }

        private void StyleTabs()
        {
            foreach (var c in _tabs.Children())
            {
                var b = c as Button;
                if (b == null) continue;
                bool active = (string)b.userData == _tab;
                b.style.backgroundColor = active ? _accent : new Color(1f, 1f, 1f, 0.07f);
                b.style.color = active ? _accentText : _text;
            }
        }

        private void RebuildTab()
        {
            _list.Clear();
            if (_def?.wardrobe == null || _tab == null || !_def.wardrobe.TryGetValue(_tab, out var slot) || slot?.items == null)
                return;

            var equipped = LvnWardrobe.Equipped(_entity);
            equipped.TryGetValue(_tab, out var worn);

            foreach (var item in slot.items)
            {
                if (item == null || string.IsNullOrEmpty(item.value)) continue;
                _list.Add(Card(slot, item, worn == item.value));
            }
        }

        private VisualElement Card(LvnWardrobeSlot slot, LvnWardrobeItem item, bool isWorn)
        {
            bool owned = item.price <= 0
                || LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(_entity, _tab, item.value));
            _tryOn.TryGetValue(_tab, out var trying);

            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems = Align.Center;
            card.style.backgroundColor = UiColor.Parse(_cfg.card_color, new Color(0.11f, 0.11f, 0.13f));
            Round(card, _radius);
            card.style.marginBottom = 8;
            card.style.paddingTop = 10; card.style.paddingBottom = 10;
            card.style.paddingLeft = 12; card.style.paddingRight = 12;
            // ring: accent when worn, rarity tint otherwise, focus when trying on
            var ring = isWorn ? _accent
                : (trying == item.value ? new Color(_accent.r, _accent.g, _accent.b, 0.45f)
                    : RarityColor(item.rarity));
            if (ring.a > 0f) Border(card, ring, 2f);

            if (!string.IsNullOrEmpty(item.icon))
            {
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.style.width = 72; icon.style.height = 72; icon.style.marginRight = 12;
                icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                icon.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                icon.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                icon.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                card.Add(icon);
                _ = ScreenUi.AssignBgAsync(icon, item.icon, _assets);
            }

            var col = new VisualElement();
            col.style.flexGrow = 1;
            card.Add(col);

            var name = new Label(item.name ?? item.value);
            name.style.color = _text;
            name.style.fontSize = 26;
            col.Add(name);

            if (isWorn)
            {
                var state = new Label(_cfg.equipped_text ?? "Equipped");
                state.style.color = _accent;
                state.style.fontSize = 20;
                state.style.marginTop = 2;
                col.Add(state);
            }

            // tap anywhere on the card = try it on in the preview
            card.RegisterCallback<ClickEvent>(e =>
            {
                if (e.target is Button) return; // the action button handles itself
                _tryOn[_tab] = item.value;
                RebuildPreview();
                RebuildTab(); // move the focus ring
            });

            // the action button: buy → equip → take off
            var action = new Button();
            action.style.fontSize = 24;
            action.style.minWidth = 130;
            action.style.paddingTop = 10; action.style.paddingBottom = 10;
            action.style.paddingLeft = 14; action.style.paddingRight = 14;
            Round(action, _radius);
            if (!owned)
            {
                action.text = $"{item.price:N0} {item.currency}";
                action.style.color = _accentText;
                action.style.backgroundColor = _accent;
                action.clicked += () => _ = BuyAsync(item, action);
            }
            else if (isWorn)
            {
                bool removable = slot.removable ?? true;
                action.text = removable ? (_cfg.remove_text ?? "Take off") : (_cfg.equipped_text ?? "Equipped");
                action.style.color = _text;
                action.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
                action.SetEnabled(removable);
                if (removable) action.clicked += () =>
                {
                    LvnWardrobe.Equip(_entity, _tab, null);
                    _tryOn.Remove(_tab);
                    RebuildPreview();
                    RebuildTab();
                };
            }
            else
            {
                action.text = _cfg.equip_text ?? "Equip";
                action.style.color = _accentText;
                action.style.backgroundColor = _accent;
                action.clicked += () => Equip(item.value);
            }
            card.Add(action);
            return card;
        }

        private void Equip(string value)
        {
            LvnWardrobe.Equip(_entity, _tab, value);
            _tryOn[_tab] = value;
            RebuildPreview();
            RebuildTab();
        }

        private async Task BuyAsync(LvnWardrobeItem item, Button b)
        {
            if (_buying) return;
            _buying = true;
            var label = b.text;
            b.SetEnabled(false);
            b.text = "…";
            bool ok = false;
            try
            {
                var sku = LvnWardrobe.Sku(_entity, _tab, item.value);
                // Fresh inventory first — never charge twice for an owned sku.
                await LvnWallet.RefreshAsync();
                if (LvnWallet.Inventory.ContainsKey(sku))
                {
                    Debug.Log($"[lvn-wardrobe] screen: {sku} already owned — equipping without charge");
                    ok = true;
                }
                else
                {
                    Debug.Log($"[lvn-wardrobe] screen buying {sku}: {item.price} {item.currency ?? "(null currency!)"}");
                    ok = await LvnWallet.SpendAsync(item.currency, item.price, "wardrobe", sku);
                    Debug.Log($"[lvn-wardrobe] screen buy {sku} → {(ok ? "OK" : "FAILED")}");
                    LvnAnalytics.Track(ok ? "wardrobe_buy" : "wardrobe_buy_fail",
                        ("entity", _entity), ("sku", sku));
                }
            }
            catch { }
            finally { _buying = false; }
            if (ok)
            {
                Equip(item.value); // bought → straight onto the character
            }
            else
            {
                b.text = "✕"; // insufficient funds / offline
                b.schedule.Execute(() => { b.text = label; b.SetEnabled(true); }).ExecuteLater(1200);
            }
        }

        // ── the live preview ─────────────────────────────────────────────────
        private async void RebuildPreview()
        {
            int gen = ++_previewGen;
            _preview.Clear();
            if (_catalog == null || string.IsNullOrEmpty(_entity) || !_catalog.Has(_entity)) return;
            FitPreview();

            // preview axes: what's being tried on; entity defaults fill the rest
            var rls = _catalog.ResolveLayers(_entity, _tryOn, _ => true);
            foreach (var rl in rls)
            {
                var el = new VisualElement { pickingMode = PickingMode.Ignore };
                el.style.position = Position.Absolute;
                bool hasRect = rl.W > 0f && rl.H > 0f;
                el.style.left = Length.Percent(hasRect ? rl.X * 100f : 0f);
                el.style.top = Length.Percent(hasRect ? rl.Y * 100f : 0f);
                el.style.width = Length.Percent(hasRect ? rl.W * 100f : 100f);
                el.style.height = Length.Percent(hasRect ? rl.H * 100f : 100f);
                el.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                el.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                el.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                el.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                _preview.Add(el);

                Sprite s = null;
                try { s = await _assets.LoadSpriteAsync(rl.Url, CancellationToken.None); }
                catch { }
                if (gen != _previewGen) return; // a newer fit superseded this one
                if (s != null) el.style.backgroundImage = new StyleBackground(s);
            }
        }

        private void FitPreview()
        {
            float boxH = _previewBox.resolvedStyle.height;
            if (float.IsNaN(boxH) || boxH <= 0f) return;
            float aspect = _def != null && _def.aspect > 0f ? _def.aspect : 0.6f;
            float h = boxH * 1.10f; // heroine ~15% bigger inside the box (crops slightly)
            _preview.style.height = h;
            _preview.style.width = h * aspect;
        }

        private void RefreshBalances()
        {
            _balances.Clear();
            foreach (var kv in LvnWallet.Balances)
            {
                var pill = new Label(kv.Value.ToString("N0") + " " + kv.Key);
                pill.style.color = _text;
                pill.style.fontSize = 22;
                pill.style.marginLeft = 10;
                pill.style.paddingLeft = 12; pill.style.paddingRight = 12;
                pill.style.paddingTop = 5; pill.style.paddingBottom = 5;
                pill.style.backgroundColor = new Color(0f, 0f, 0f, 0.4f);
                Round(pill, 14f);
                _balances.Add(pill);
            }
        }

        private Color RarityColor(string rarity)
        {
            if (string.IsNullOrEmpty(rarity)) return Color.clear;
            if (_cfg.rarity_colors != null && _cfg.rarity_colors.TryGetValue(rarity, out var hex))
                return UiColor.Parse(hex, Color.clear);
            return Color.clear;
        }

        private void SetNote(string text)
        {
            _note.text = text ?? "";
            _note.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private static void Round(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r;
            el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r;
            el.style.borderBottomRightRadius = r;
        }

        private static void Border(VisualElement el, Color c, float w)
        {
            el.style.borderTopColor = c; el.style.borderBottomColor = c;
            el.style.borderLeftColor = c; el.style.borderRightColor = c;
            el.style.borderTopWidth = w; el.style.borderBottomWidth = w;
            el.style.borderLeftWidth = w; el.style.borderRightWidth = w;
        }
    }
}
