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
    /// The IN-STORY wardrobe: a bottom sheet over the running scene — the
    /// genre-standard "dress up mid-chapter" moment. No preview pane: the LIVE
    /// actor on stage is the mirror. Browsing writes try-on values into
    /// <see cref="LvnWardrobe.Preview"/>, which the stage picks up instantly;
    /// the confirm button buys whatever's previewed-but-unowned (wallet skus),
    /// commits every previewed slot and closes; the collapse arrow cancels and
    /// the actor snaps back. Layout mirrors the reference flow: icon tabs per
    /// slot, a ◀ item name ▶ carousel, one big confirm with the total price.
    /// Opened by <c>ext wardrobe_show char=id</c> (the story holds meanwhile).
    /// </summary>
    public sealed class WardrobeSheet : VisualElement
    {
        private readonly WardrobeConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly Color _text, _dim, _accent, _accentText;
        private readonly float _radius;

        private readonly Label _title;
        private readonly VisualElement _tabs;
        private readonly Label _itemName;
        private readonly Button _confirm;

        private LvnManifest _manifest;
        private string _entity;
        private LvnSpriteEntity _def;
        private string _tab;
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(); // axis → carousel pos

        private TaskCompletionSource<bool> _tcs;
        private bool _open;
        private bool _buying;

        public WardrobeSheet(WardrobeConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new WardrobeConfig();
            _assets = assets;
            _text = UiColor.Parse(_cfg.text_color, new Color(0.95f, 0.93f, 0.88f));
            _dim = UiColor.Parse(_cfg.dim_text_color, new Color(0.60f, 0.58f, 0.54f));
            _accent = UiColor.Parse(_cfg.accent_color, new Color(0.78f, 0.63f, 0.31f));
            _accentText = UiColor.Parse(_cfg.accent_text_color, new Color(0.08f, 0.08f, 0.10f));
            _radius = _cfg.corner_radius ?? 12f;

            // the sheet itself — bottom-docked, the scene stays visible above
            style.position = Position.Absolute;
            style.left = Length.Percent(4f);
            style.right = Length.Percent(4f);
            style.bottom = Length.Percent(2.5f);
            style.backgroundColor = UiColor.Parse(_cfg.panel_color, new Color(0.078f, 0.078f, 0.10f, 0.97f));
            Round(this, _radius + 6f);
            style.paddingTop = 14;
            style.paddingBottom = 16;
            style.paddingLeft = 16;
            style.paddingRight = 16;
            style.opacity = 0f;
            style.display = DisplayStyle.None;

            var headRow = new VisualElement();
            headRow.style.flexDirection = FlexDirection.Row;
            headRow.style.alignItems = Align.Center;
            headRow.style.justifyContent = Justify.Center;
            Add(headRow);

            _title = new Label(_cfg.title ?? "Wardrobe");
            _title.style.color = UiColor.Parse(_cfg.title_color, new Color(0.96f, 0.93f, 0.85f));
            _title.style.fontSize = 26;
            _title.style.unityTextAlign = TextAnchor.MiddleCenter;
            _title.style.paddingLeft = 24; _title.style.paddingRight = 24;
            _title.style.paddingTop = 4; _title.style.paddingBottom = 4;
            _title.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            Round(_title, _radius);
            headRow.Add(_title);

            var collapse = new Button(Cancel) { text = "▼" };
            collapse.style.position = Position.Absolute;
            collapse.style.right = 0;
            collapse.style.fontSize = 20;
            collapse.style.color = _dim;
            collapse.style.backgroundColor = new Color(1f, 1f, 1f, 0.07f);
            collapse.style.paddingLeft = 14; collapse.style.paddingRight = 14;
            collapse.style.paddingTop = 6; collapse.style.paddingBottom = 6;
            Round(collapse, _radius);
            headRow.Add(collapse);

            _tabs = new VisualElement();
            _tabs.style.flexDirection = FlexDirection.Row;
            _tabs.style.justifyContent = Justify.Center;
            _tabs.style.marginTop = 12;
            Add(_tabs);

            // ◀ item name ▶
            var carousel = new VisualElement();
            carousel.style.flexDirection = FlexDirection.Row;
            carousel.style.alignItems = Align.Center;
            carousel.style.marginTop = 12;
            Add(carousel);

            var prev = new Button(() => Step(-1)) { text = "◀" };
            var next = new Button(() => Step(+1)) { text = "▶" };
            foreach (var b in new[] { prev, next })
            {
                b.style.fontSize = 22;
                b.style.color = _text;
                b.style.backgroundColor = new Color(1f, 1f, 1f, 0.07f);
                b.style.paddingLeft = 16; b.style.paddingRight = 16;
                b.style.paddingTop = 10; b.style.paddingBottom = 10;
                Round(b, _radius);
            }
            carousel.Add(prev);

            _itemName = new Label("");
            _itemName.style.flexGrow = 1;
            _itemName.style.color = _text;
            _itemName.style.fontSize = 26;
            _itemName.style.unityTextAlign = TextAnchor.MiddleCenter;
            _itemName.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            _itemName.style.marginLeft = 10; _itemName.style.marginRight = 10;
            _itemName.style.paddingTop = 10; _itemName.style.paddingBottom = 10;
            Round(_itemName, _radius);
            carousel.Add(_itemName);
            carousel.Add(next);

            _confirm = new Button(() => _ = ConfirmAsync());
            _confirm.style.fontSize = 26;
            _confirm.style.marginTop = 12;
            _confirm.style.paddingTop = 14;
            _confirm.style.paddingBottom = 14;
            _confirm.style.color = _accentText;
            _confirm.style.backgroundColor = _accent;
            Round(_confirm, _radius);
            Add(_confirm);
        }

        public void SetManifest(LvnManifest manifest) => _manifest = manifest;

        /// <summary>Open the sheet for a character; resolves when the player
        /// confirms or collapses it. The story op awaits this.</summary>
        public async Task ShowAsync(string entityId, CancellationToken ct = default)
        {
            if (_open) return;
            _open = true;
            BuildFor(entityId);
            style.display = DisplayStyle.Flex;
            LvnWallet.Changed += OnWalletChanged;
            _ = LvnWallet.RefreshAsync();
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.25f, ct);

            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            try { await _tcs.Task; }
            finally
            {
                LvnWallet.Changed -= OnWalletChanged;
                LvnWardrobe.ClearPreview(_entity); // confirm already committed; cancel snaps back
                await ScreenFx.FadeAsync(this, 1f, 0f, 0.25f, CancellationToken.None);
                style.display = DisplayStyle.None;
                _open = false;
            }
        }

        public void Hide()
        {
            LvnWallet.Changed -= OnWalletChanged;
            if (!string.IsNullOrEmpty(_entity)) LvnWardrobe.ClearPreview(_entity);
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _open = false;
            _tcs?.TrySetResult(false);
        }

        private void Cancel() => _tcs?.TrySetResult(false);
        private void OnWalletChanged() => RefreshConfirm();

        /// <summary>Build tabs/carousel for a character. Public for tests.</summary>
        public void BuildFor(string entityId)
        {
            _entity = entityId;
            if ((string.IsNullOrEmpty(_entity) || _manifest?.sprites == null
                 || !_manifest.sprites.ContainsKey(_entity)) && _manifest?.sprites != null)
            {
                foreach (var kv in _manifest.sprites) // fallback: first with a wardrobe
                    if (kv.Value?.wardrobe != null && kv.Value.wardrobe.Count > 0) { _entity = kv.Key; break; }
            }
            _def = _entity != null && _manifest?.sprites != null
                   && _manifest.sprites.TryGetValue(_entity, out var d) ? d : null;
            _index.Clear();
            _tabs.Clear();
            _tab = null;
            _title.text = _cfg.title ?? "Wardrobe";

            if (_def?.wardrobe == null || _def.wardrobe.Count == 0)
            {
                _itemName.text = _cfg.empty_text ?? "The wardrobe is empty";
                RefreshConfirm();
                return;
            }

            foreach (var kv in _def.wardrobe)
            {
                var axis = kv.Key;
                if (_tab == null) _tab = axis;
                var slot = kv.Value;
                var b = new Button(() => SelectTab(axis));
                b.style.width = 92; b.style.height = 92;
                b.style.marginLeft = 6; b.style.marginRight = 6;
                Round(b, _radius);
                b.userData = axis;
                if (!string.IsNullOrEmpty(slot?.icon))
                {
                    b.text = "";
                    b.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                    b.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                    _ = ScreenUi.AssignBgAsync(b, slot.icon, _assets);
                }
                else
                {
                    b.text = slot?.name ?? axis;
                    b.style.fontSize = 18;
                    b.style.whiteSpace = WhiteSpace.Normal;
                }
                _tabs.Add(b);
            }
            SelectTab(_tab);
        }

        private void SelectTab(string axis)
        {
            _tab = axis;
            foreach (var c in _tabs.Children())
            {
                var b = c as Button;
                if (b == null) continue;
                bool active = (string)b.userData == _tab;
                b.style.backgroundColor = active ? _accent : new Color(1f, 1f, 1f, 0.07f);
                b.style.color = active ? _accentText : _text;
                Border(b, active ? _accent : new Color(1f, 1f, 1f, 0.15f), 2f);
            }

            var items = Items(_tab);
            if (items.Count == 0) { _itemName.text = ""; RefreshConfirm(); return; }
            if (!_index.ContainsKey(_tab))
            {
                // start the carousel on what's worn (previewed beats equipped)
                LvnWardrobe.Previewed(_entity).TryGetValue(_tab, out var current);
                if (current == null) LvnWardrobe.Equipped(_entity).TryGetValue(_tab, out current);
                int at = 0;
                for (int i = 0; i < items.Count; i++) if (items[i].value == current) { at = i; break; }
                _index[_tab] = at;
            }
            ShowItem(); // also previews it, so the carousel and the actor agree
        }

        private void Step(int dir)
        {
            var items = Items(_tab);
            if (items.Count == 0) return;
            _index[_tab] = ((_index.TryGetValue(_tab, out var i) ? i : 0) + dir + items.Count) % items.Count;
            ShowItem();
        }

        private void ShowItem()
        {
            var items = Items(_tab);
            if (items.Count == 0) return;
            var item = items[Mathf.Clamp(_index.TryGetValue(_tab, out var i) ? i : 0, 0, items.Count - 1)];
            _itemName.text = item.name ?? item.value;
            LvnWardrobe.Preview(_entity, _tab, item.value); // the live actor is the mirror
            RefreshConfirm();
        }

        // The confirm button carries the total price of everything previewed
        // but not yet owned, per currency — free/owned try-ons confirm at 0.
        private void RefreshConfirm()
        {
            var costs = UnownedPreviewCosts();
            var text = _cfg.confirm_text ?? "Choose";
            if (costs.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in costs) parts.Add($"{kv.Value:N0} {kv.Key}");
                text += ":  " + string.Join(" + ", parts);
            }
            _confirm.text = text;
        }

        private Dictionary<string, long> UnownedPreviewCosts()
        {
            var costs = new Dictionary<string, long>();
            if (_def?.wardrobe == null) return costs;
            foreach (var kv in LvnWardrobe.Previewed(_entity))
            {
                var item = Find(kv.Key, kv.Value);
                if (item == null || item.price <= 0) continue;
                if (LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(_entity, kv.Key, item.value))) continue;
                costs.TryGetValue(item.currency ?? "", out var sum);
                costs[item.currency ?? ""] = sum + item.price;
            }
            return costs;
        }

        private async Task ConfirmAsync()
        {
            if (_buying) return;
            _buying = true;
            var label = _confirm.text;
            _confirm.SetEnabled(false);
            _confirm.text = "…";
            try
            {
                // buy everything previewed-but-unowned, then commit the lot
                var previewed = new Dictionary<string, string>();
                foreach (var kv in LvnWardrobe.Previewed(_entity)) previewed[kv.Key] = kv.Value;
                foreach (var kv in previewed)
                {
                    var item = Find(kv.Key, kv.Value);
                    if (item == null) continue;
                    var sku = LvnWardrobe.Sku(_entity, kv.Key, item.value);
                    if (item.price > 0 && !LvnWallet.Inventory.ContainsKey(sku))
                    {
                        bool ok = await LvnWallet.SpendAsync(item.currency, item.price, "wardrobe", sku);
                        LvnAnalytics.Track(ok ? "wardrobe_buy" : "wardrobe_buy_fail",
                            ("entity", _entity), ("sku", sku));
                        if (!ok)
                        {
                            // insufficient funds / offline — keep the sheet open
                            _confirm.text = "✕";
                            _confirm.schedule.Execute(() =>
                            {
                                _confirm.SetEnabled(true);
                                RefreshConfirm();
                            }).ExecuteLater(1200);
                            return;
                        }
                    }
                    LvnWardrobe.Equip(_entity, kv.Key, kv.Value);
                }
                LvnWardrobe.ClearPreview(_entity); // equips now cover the look
                _tcs?.TrySetResult(true);
            }
            finally
            {
                _buying = false;
                if (_confirm.enabledSelf == false && _confirm.text == "…")
                { _confirm.SetEnabled(true); _confirm.text = label; }
            }
        }

        private List<LvnWardrobeItem> Items(string axis)
        {
            var list = new List<LvnWardrobeItem>();
            if (axis != null && _def?.wardrobe != null
                && _def.wardrobe.TryGetValue(axis, out var slot) && slot?.items != null)
                foreach (var it in slot.items)
                    if (it != null && !string.IsNullOrEmpty(it.value)) list.Add(it);
            return list;
        }

        private LvnWardrobeItem Find(string axis, string value)
        {
            foreach (var it in Items(axis)) if (it.value == value) return it;
            return null;
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
