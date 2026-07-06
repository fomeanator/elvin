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
    /// The currency store overlay, themed from a <see cref="StoreConfig"/>
    /// (manifest <c>ui.store</c>): a scrim, a sheet with the live balances on
    /// top, the server's IAP packs (<see cref="LvnWallet.GetCatalogAsync"/>)
    /// as buy cards, and a close button. Buying calls
    /// <see cref="LvnWallet.VerifyPurchaseAsync"/> — on real stores the host
    /// swaps the receipt in via <see cref="PurchaseFlow"/>; the default flow
    /// sends a dev receipt, which the server honours only under
    /// <c>-iap-dev</c>. Server-authoritative end to end: the card grants
    /// nothing, the wallet mirror updates from the verify response.
    /// </summary>
    public sealed class StoreScreen : VisualElement
    {
        /// <summary>The billing seam for production: (pack) → the receipt to
        /// verify, or null when the platform purchase failed/was cancelled.
        /// Default: a "dev" receipt straight to the server (test builds).</summary>
        public static System.Func<LvnWallet.IapPack, Task<(string platform, string receipt)?>> PurchaseFlow;

        private readonly StoreConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly VisualElement _balances;
        private readonly ScrollView _list;
        private readonly Label _note;
        private readonly Color _text;
        private readonly Color _dim;
        private readonly float _radius;

        private TaskCompletionSource<bool> _tcs;
        private bool _open;
        private bool _buying;

        public StoreScreen(StoreConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new StoreConfig();
            _assets = assets;
            _text = UiColor.Parse(_cfg.text_color, new Color(0.95f, 0.93f, 0.88f));
            _dim = UiColor.Parse(_cfg.dim_text_color, new Color(0.60f, 0.58f, 0.54f));
            _radius = _cfg.corner_radius ?? 12f;

            ScreenUi.Stretch(this);
            style.backgroundColor = UiColor.Parse(_cfg.scrim_color, new Color(0f, 0f, 0f, 0.7f));
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            // tap the scrim (not the sheet) to close
            RegisterCallback<ClickEvent>(e => { if (e.target == this) Close(); });

            var sheet = new VisualElement();
            sheet.style.position = Position.Absolute;
            sheet.style.left = Length.Percent(6f);
            sheet.style.right = Length.Percent(6f);
            sheet.style.top = Length.Percent(10f);
            sheet.style.bottom = Length.Percent(10f);
            sheet.style.backgroundColor = UiColor.Parse(_cfg.panel_color, new Color(0.078f, 0.078f, 0.10f, 0.97f));
            Round(sheet, _radius + 4f);
            sheet.style.paddingTop = 22;
            sheet.style.paddingBottom = 18;
            sheet.style.paddingLeft = 20;
            sheet.style.paddingRight = 20;
            Add(sheet);

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.marginBottom = 14;
            sheet.Add(header);

            var title = new Label(_cfg.title ?? "Store");
            title.style.color = UiColor.Parse(_cfg.title_color, new Color(0.96f, 0.93f, 0.85f));
            title.style.fontSize = 34;
            header.Add(title);

            _balances = new VisualElement();
            _balances.style.flexDirection = FlexDirection.Row;
            _balances.style.alignItems = Align.Center;
            header.Add(_balances);

            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.style.flexGrow = 1;
            sheet.Add(_list);

            _note = new Label("");
            _note.style.color = _dim;
            _note.style.fontSize = 20;
            _note.style.unityTextAlign = TextAnchor.MiddleCenter;
            _note.style.marginTop = 10;
            _note.style.display = DisplayStyle.None;
            sheet.Add(_note);

            var close = new Button(Close) { text = _cfg.close_text ?? "Close" };
            close.style.fontSize = 24;
            close.style.marginTop = 12;
            close.style.paddingTop = 12;
            close.style.paddingBottom = 12;
            close.style.color = _text;
            close.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
            Round(close, _radius);
            sheet.Add(close);
        }

        /// <summary>Open the store: refresh the wallet + catalog in parallel,
        /// render the packs, and resolve when the player closes it.</summary>
        public async Task ShowAsync(CancellationToken ct = default)
        {
            if (_open) return;
            _open = true;
            style.display = DisplayStyle.Flex;
            LvnWallet.Changed += RefreshBalances;
            RefreshBalances();
            _ = LvnWallet.RefreshAsync();
            _ = LoadCatalogAsync();
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.25f, ct);

            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            try { await _tcs.Task; }
            finally
            {
                LvnWallet.Changed -= RefreshBalances;
                await ScreenFx.FadeAsync(this, 1f, 0f, 0.25f, CancellationToken.None);
                style.display = DisplayStyle.None;
                _open = false;
            }
        }

        public void Hide()
        {
            LvnWallet.Changed -= RefreshBalances;
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _open = false;
            _tcs?.TrySetResult(false);
        }

        private void Close() => _tcs?.TrySetResult(true);

        private async Task LoadCatalogAsync()
        {
            SetNote("…");
            var packs = await LvnWallet.GetCatalogAsync();
            if (!_open) return;
            SetPacks(packs);
            // Free money row: rewarded-ad placements, only when the host
            // plugged an ad SDK (LvnAds.ShowRewarded) — server owns amounts.
            if (LvnAds.Available)
            {
                var ads = await LvnAds.GetCatalogAsync();
                if (!_open || ads == null) return;
                foreach (var p in ads) _list.Add(AdCard(p));
            }
        }

        private VisualElement AdCard(LvnAds.Placement p)
        {
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems = Align.Center;
            card.style.backgroundColor = UiColor.Parse(_cfg.card_color, new Color(0.11f, 0.11f, 0.13f));
            Round(card, _radius);
            card.style.marginBottom = 10;
            card.style.paddingTop = 14; card.style.paddingBottom = 14;
            card.style.paddingLeft = 16; card.style.paddingRight = 16;

            var col = new VisualElement();
            col.style.flexGrow = 1;
            card.Add(col);
            var name = new Label($"+{p.Amount:N0} {NameFor(p.Currency)}");
            name.style.color = _text;
            name.style.fontSize = 26;
            col.Add(name);
            if (p.DailyCap > 0)
            {
                var sub = new Label($"×{p.DailyCap}/day");
                sub.style.color = _dim;
                sub.style.fontSize = 20;
                sub.style.marginTop = 2;
                col.Add(sub);
            }

            var watch = new Button { text = _cfg.ad_text ?? "Watch ad" };
            watch.style.fontSize = 24;
            watch.style.minWidth = 130;
            watch.style.paddingTop = 12; watch.style.paddingBottom = 12;
            watch.style.paddingLeft = 18; watch.style.paddingRight = 18;
            watch.style.color = _text;
            watch.style.backgroundColor = new Color(1f, 1f, 1f, 0.10f);
            Round(watch, _radius);
            watch.clicked += async () =>
            {
                watch.SetEnabled(false);
                var label = watch.text;
                watch.text = "…";
                bool ok = await LvnAds.WatchAndRewardAsync(p.Id);
                watch.text = ok ? "✓" : "✕";
                watch.schedule.Execute(() => { watch.text = label; watch.SetEnabled(true); }).ExecuteLater(1400);
            };
            card.Add(watch);
            return card;
        }

        /// <summary>Render the pack cards (null/empty → the empty note). Split
        /// out from the fetch so tests and hosts can feed their own list.</summary>
        public void SetPacks(IReadOnlyList<LvnWallet.IapPack> packs)
        {
            _list.Clear();
            if (packs == null || packs.Count == 0)
            {
                SetNote(_cfg.empty_text ?? "The shop is closed right now");
                return;
            }
            SetNote(null);
            foreach (var p in packs) _list.Add(Card(p));
        }

        private VisualElement Card(LvnWallet.IapPack pack)
        {
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems = Align.Center;
            card.style.backgroundColor = UiColor.Parse(_cfg.card_color, new Color(0.11f, 0.11f, 0.13f));
            Round(card, _radius);
            card.style.marginBottom = 10;
            card.style.paddingTop = 14;
            card.style.paddingBottom = 14;
            card.style.paddingLeft = 16;
            card.style.paddingRight = 16;

            var iconUrl = !string.IsNullOrEmpty(pack.Icon) ? pack.Icon : IconFor(pack.Currency);
            if (!string.IsNullOrEmpty(iconUrl))
            {
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.style.width = 52; icon.style.height = 52; icon.style.marginRight = 14;
                icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                icon.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                icon.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                icon.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                card.Add(icon);
                _ = ScreenUi.AssignBgAsync(icon, iconUrl, _assets);
            }

            var col = new VisualElement();
            col.style.flexGrow = 1;
            card.Add(col);

            var name = new Label(!string.IsNullOrEmpty(pack.Title)
                ? pack.Title
                : $"{pack.Amount:N0} {NameFor(pack.Currency)}");
            name.style.color = _text;
            name.style.fontSize = 26;
            col.Add(name);

            var subText = !string.IsNullOrEmpty(pack.Title)
                ? $"{pack.Amount:N0} {NameFor(pack.Currency)}"
                : "";
            if (pack.Bonus > 0)
            {
                var bonus = string.Format(_cfg.bonus_text ?? "+{0} bonus", pack.Bonus.ToString("N0"));
                subText = string.IsNullOrEmpty(subText) ? bonus : subText + "  ·  " + bonus;
            }
            if (!string.IsNullOrEmpty(subText))
            {
                var sub = new Label(subText);
                sub.style.color = _dim;
                sub.style.fontSize = 20;
                sub.style.marginTop = 2;
                col.Add(sub);
            }

            var buy = new Button { text = !string.IsNullOrEmpty(pack.Price) ? pack.Price : (_cfg.buy_text ?? "Get") };
            buy.style.fontSize = 24;
            buy.style.minWidth = 130;
            buy.style.paddingTop = 12;
            buy.style.paddingBottom = 12;
            buy.style.paddingLeft = 18;
            buy.style.paddingRight = 18;
            buy.style.color = UiColor.Parse(_cfg.buy_text_color, new Color(0.08f, 0.08f, 0.10f));
            buy.style.backgroundColor = UiColor.Parse(_cfg.buy_color, new Color(0.78f, 0.63f, 0.31f));
            Round(buy, _radius);
            buy.clicked += () => _ = BuyAsync(pack, buy);
            card.Add(buy);

            return card;
        }

        private async Task BuyAsync(LvnWallet.IapPack pack, Button b)
        {
            if (_buying) return;
            _buying = true;
            var label = b.text;
            b.SetEnabled(false);
            b.text = "…";
            try
            {
                var flow = PurchaseFlow ?? DevPurchaseFlow;
                var bought = await flow(pack);
                bool ok = bought != null
                          && await LvnWallet.VerifyPurchaseAsync(bought.Value.platform, pack.Sku, bought.Value.receipt);
                b.text = ok ? "✓" : "✕";
                LvnAnalytics.Track(ok ? "iap_success" : "iap_fail", ("sku", pack.Sku));
            }
            catch { b.text = "✕"; }
            finally
            {
                _buying = false;
                b.schedule.Execute(() => { b.text = label; b.SetEnabled(true); }).ExecuteLater(1400);
            }
        }

        // Test-build billing: no platform store, the server (-iap-dev) trusts
        // the receipt. Production hosts install a real PurchaseFlow.
        private static Task<(string platform, string receipt)?> DevPurchaseFlow(LvnWallet.IapPack pack)
            => Task.FromResult<(string, string)?>(("dev", "dev-receipt"));

        private void RefreshBalances()
        {
            _balances.Clear();
            foreach (var kv in LvnWallet.Balances)
            {
                var pill = new VisualElement();
                pill.style.flexDirection = FlexDirection.Row;
                pill.style.alignItems = Align.Center;
                pill.style.marginLeft = 10;
                pill.style.paddingLeft = 12; pill.style.paddingRight = 12;
                pill.style.paddingTop = 5; pill.style.paddingBottom = 5;
                pill.style.backgroundColor = new Color(0f, 0f, 0f, 0.4f);
                Round(pill, 14f);

                var iconUrl = IconFor(kv.Key);
                if (!string.IsNullOrEmpty(iconUrl))
                {
                    var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                    icon.style.width = 24; icon.style.height = 24; icon.style.marginRight = 6;
                    icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                    icon.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                    pill.Add(icon);
                    _ = ScreenUi.AssignBgAsync(icon, iconUrl, _assets);
                }
                var amount = new Label(kv.Value.ToString("N0"));
                amount.style.color = _text;
                amount.style.fontSize = 22;
                pill.Add(amount);
                _balances.Add(pill);
            }
        }

        private void SetNote(string text)
        {
            _note.text = text ?? "";
            _note.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private string IconFor(string currency)
            => _cfg.currency_icons != null && currency != null
               && _cfg.currency_icons.TryGetValue(currency, out var url) ? url : null;

        private string NameFor(string currency)
            => _cfg.currency_names != null && currency != null
               && _cfg.currency_names.TryGetValue(currency, out var n) ? n : currency;

        private static void Round(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r;
            el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r;
            el.style.borderBottomRightRadius = r;
        }
    }
}
