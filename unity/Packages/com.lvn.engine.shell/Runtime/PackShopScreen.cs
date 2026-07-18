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
    /// A premium, dedicated currency-pack store overlay — the F2P "coin shop"
    /// step up from <see cref="StoreScreen"/>'s flat list. A scrim + sheet with
    /// a top bar (back, title, live-ish balances), a row of category tabs
    /// (Кристаллы / Золото / Энергия / Наборы), and a scrolling grid of big,
    /// enticing pack cards per tab: tiered amounts, gold bonus lines, badges
    /// ("ПОПУЛЯРНЫЙ" / "ВЫГОДНО" / "ЛУЧШАЯ ЦЕНА"), and a highlighted best-value
    /// pack. Colours all come from <see cref="LvnTokens"/> ("Полночь" palette).
    ///
    /// Self-contained by design: it ships hardcoded demo packs so it looks
    /// complete without a live catalog, and the buy button drives a harmless
    /// "…" → "✓" demo state rather than a real purchase. A host that wants real
    /// billing can wire it to the same <see cref="LvnWallet.VerifyPurchaseAsync"/>
    /// pattern <see cref="StoreScreen"/> uses.
    /// </summary>
    public sealed class PackShopScreen : VisualElement
    {
        private enum Ribbon { None, Popular, Value, BestPrice }

        private struct Pack
        {
            public long Amount;
            public string Unit;   // "золота", "кристаллов", "энергии", …
            public string Price;  // "$4.99"
            public long Bonus;
            public Ribbon Badge;
            public bool Best;     // biggest / highlighted card
            public string Card;   // illustration url, "/content/cards/cardN.png"
            public string Glyph;  // fallback emblem drawn over the tint block
            public Color Tint;    // illustration block fill
        }

        private static readonly string[] TabIds = { "crystals", "gold", "energy", "bundles" };
        private static readonly string[] TabNames = { "Кристаллы", "Золото", "Энергия", "Наборы" };

        private readonly ILvnAssets _assets;
        private readonly VisualElement _balances;
        private readonly VisualElement _tabsRow;
        private readonly ScrollView _list;
        private readonly List<Button> _tabButtons = new List<Button>();
        private readonly Dictionary<string, List<Pack>> _catalog;

        private TaskCompletionSource<bool> _tcs;
        private bool _open;
        private bool _buying;
        private int _tab;

        public PackShopScreen(ILvnAssets assets)
        {
            _assets = assets;
            _catalog = BuildDemoCatalog();

            ScreenUi.Stretch(this);
            style.backgroundColor = LvnTokens.Scrim;
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            // tap the scrim (not the sheet) to close
            RegisterCallback<ClickEvent>(evt => { if (evt.target == this) Close(); });

            var sheet = new VisualElement();
            sheet.style.position = Position.Absolute;
            sheet.style.left = Length.Percent(5f);
            sheet.style.right = Length.Percent(5f);
            sheet.style.top = Length.Percent(7f);
            sheet.style.bottom = Length.Percent(7f);
            sheet.style.backgroundColor = LvnTokens.PanelBg;
            Round(sheet, LvnTokens.Radius + 4f);
            sheet.style.paddingTop = 20;
            sheet.style.paddingBottom = 18;
            sheet.style.paddingLeft = 20;
            sheet.style.paddingRight = 20;
            Add(sheet);

            // ── Top bar: back ‹ · title · balances ────────────────────────────
            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            top.style.marginBottom = 16;
            sheet.Add(top);

            var back = new Button(Close) { text = "‹" };
            back.style.fontSize = 34;
            back.style.width = 52;
            back.style.height = 52;
            back.style.marginRight = 6;
            back.style.paddingTop = 0; back.style.paddingBottom = 0;
            back.style.paddingLeft = 0; back.style.paddingRight = 0;
            back.style.color = LvnTokens.Text;
            back.style.backgroundColor = LvnTokens.Faint;
            back.style.unityFontStyleAndWeight = FontStyle.Bold;
            Round(back, LvnTokens.RadiusSm);
            ClearBorder(back);
            top.Add(back);

            var title = new Label("Магазин");
            title.style.color = LvnTokens.Text;
            title.style.fontSize = 40;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginLeft = 6;
            title.style.flexGrow = 1;
            top.Add(title);

            _balances = new VisualElement();
            _balances.style.flexDirection = FlexDirection.Row;
            _balances.style.alignItems = Align.Center;
            top.Add(_balances);

            // ── Category tabs ─────────────────────────────────────────────────
            _tabsRow = new VisualElement();
            _tabsRow.style.flexDirection = FlexDirection.Row;
            _tabsRow.style.flexWrap = Wrap.Wrap;
            _tabsRow.style.marginBottom = 14;
            sheet.Add(_tabsRow);
            BuildTabs();

            // ── Pack grid ─────────────────────────────────────────────────────
            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.style.flexGrow = 1;
            _list.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _list.horizontalScrollerVisibility = ScrollerVisibility.Hidden; // kill the stray horizontal bar
            sheet.Add(_list);

            RefreshBalances();
            Rebuild();
        }

        /// <summary>Open the shop, fade it in, and resolve when the player closes it.</summary>
        public async Task ShowAsync(CancellationToken ct = default)
        {
            if (_open) return;
            _open = true;
            style.display = DisplayStyle.Flex;
            RefreshBalances();
            Lvn.Services.LvnWallet.Changed += RefreshBalances; // live pills while open
            _ = Lvn.Services.LvnWallet.RefreshAsync();         // server truth on entry
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.25f, ct);
            // A Hide() during the fade-in must cancel the open, not leave this
            // await parked on a _tcs nobody will ever resolve.
            if (!_open) return;

            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            try { await _tcs.Task; }
            finally
            {
                Lvn.Services.LvnWallet.Changed -= RefreshBalances;
                await ScreenFx.FadeAsync(this, 1f, 0f, 0.25f, CancellationToken.None);
                style.display = DisplayStyle.None;
                _open = false;
            }
        }

        public void Hide()
        {
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _open = false;
            _tcs?.TrySetResult(false);
        }

        /// <summary>Re-render the pack grid for the active tab and re-style the tab
        /// pills. Cheap to call after any state change.</summary>
        public void Rebuild()
        {
            for (int i = 0; i < _tabButtons.Count; i++) StyleTab(_tabButtons[i], i == _tab);

            _list.Clear();
            if (!_catalog.TryGetValue(TabIds[_tab], out var packs)) return;
            foreach (var p in packs) _list.Add(Card(p));
        }

        private void Close() => _tcs?.TrySetResult(true);

        private void BuildTabs()
        {
            _tabsRow.Clear();
            _tabButtons.Clear();
            for (int i = 0; i < TabNames.Length; i++)
            {
                int idx = i;
                var pill = new Button(() => { _tab = idx; Rebuild(); }) { text = TabNames[i] };
                pill.style.fontSize = 24;
                pill.style.marginRight = 10;
                pill.style.marginBottom = 8;
                pill.style.paddingTop = 10; pill.style.paddingBottom = 10;
                pill.style.paddingLeft = 20; pill.style.paddingRight = 20;
                Round(pill, LvnTokens.RadiusSm + 4f);
                ClearBorder(pill);
                StyleTab(pill, i == _tab);
                _tabsRow.Add(pill);
                _tabButtons.Add(pill);
            }
        }

        private static void StyleTab(Button b, bool active)
        {
            b.style.color = active ? LvnTokens.OnAccent : LvnTokens.TextDim;
            b.style.backgroundColor = active ? LvnTokens.Accent : LvnTokens.Faint;
            b.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
        }

        // ── One pack card ─────────────────────────────────────────────────────
        private VisualElement Card(Pack pack)
        {
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems = Align.Center;
            card.style.backgroundColor = pack.Best ? LvnTokens.SurfaceHi : LvnTokens.Surface;
            Round(card, LvnTokens.Radius);
            card.style.marginBottom = pack.Best ? 12 : 9;
            card.style.paddingTop = pack.Best ? 14 : 11;
            card.style.paddingBottom = pack.Best ? 14 : 11;
            card.style.paddingLeft = 14;
            card.style.paddingRight = 14;
            card.style.overflow = Overflow.Visible;
            if (pack.Best)
            {
                // Accent border + a faint glow ring approximates the "hero" pack.
                card.style.borderTopWidth = 2; card.style.borderBottomWidth = 2;
                card.style.borderLeftWidth = 2; card.style.borderRightWidth = 2;
                card.style.borderTopColor = LvnTokens.Accent;
                card.style.borderBottomColor = LvnTokens.Accent;
                card.style.borderLeftColor = LvnTokens.Accent;
                card.style.borderRightColor = LvnTokens.Accent;
                var glow = new VisualElement { pickingMode = PickingMode.Ignore };
                glow.style.position = Position.Absolute;
                glow.style.left = -3; glow.style.right = -3; glow.style.top = -3; glow.style.bottom = -3;
                glow.style.backgroundColor = new Color(LvnTokens.Accent.r, LvnTokens.Accent.g, LvnTokens.Accent.b, 0.14f);
                Round(glow, LvnTokens.Radius + 3f);
                card.Add(glow);
                glow.SendToBack();
            }

            // Illustration block (tinted, rounded) with a glyph + art overlay.
            float box = pack.Best ? 78 : 62;
            var art = new VisualElement();
            art.style.width = box;
            art.style.height = box;
            art.style.marginRight = 14;
            art.style.alignItems = Align.Center;
            art.style.justifyContent = Justify.Center;
            art.style.backgroundColor = pack.Tint;
            art.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            art.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            art.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            art.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            Round(art, LvnTokens.RadiusSm);
            var glyph = new Label(pack.Glyph) { pickingMode = PickingMode.Ignore };
            glyph.style.fontSize = pack.Best ? 52 : 40;
            glyph.style.color = LvnTokens.Text;
            art.Add(glyph);
            card.Add(art);
            _ = ScreenUi.AssignBgAsync(art, pack.Card, _assets);

            // Middle column: amount headline + gold bonus line.
            var col = new VisualElement();
            col.style.flexGrow = 1;
            card.Add(col);

            var amount = new Label($"{pack.Amount:N0} {pack.Unit}");
            amount.style.color = LvnTokens.Text;
            amount.style.fontSize = pack.Best ? 32 : 28;
            amount.style.unityFontStyleAndWeight = FontStyle.Bold;
            amount.style.whiteSpace = WhiteSpace.Normal;
            col.Add(amount);

            if (pack.Bonus > 0)
            {
                var bonus = new Label($"+{pack.Bonus:N0} бонус");
                bonus.style.color = LvnTokens.Gold;
                bonus.style.fontSize = 24;
                bonus.style.marginTop = 4;
                bonus.style.unityFontStyleAndWeight = FontStyle.Bold;
                col.Add(bonus);
            }

            // Price button (Accent) → harmless demo buying state.
            var buy = new Button { text = pack.Price };
            buy.style.fontSize = 26;
            buy.style.minWidth = 132;
            buy.style.paddingTop = 14; buy.style.paddingBottom = 14;
            buy.style.paddingLeft = 18; buy.style.paddingRight = 18;
            buy.style.color = LvnTokens.OnAccent;
            buy.style.backgroundColor = LvnTokens.Accent;
            buy.style.unityFontStyleAndWeight = FontStyle.Bold;
            Round(buy, LvnTokens.RadiusSm);
            ClearBorder(buy);
            buy.clicked += () => Buy(buy, pack);
            card.Add(buy);

            // Ribbon badge (top-left), for popular / best-value packs.
            if (pack.Badge != Ribbon.None)
            {
                bool gold = pack.Badge == Ribbon.Value || pack.Badge == Ribbon.BestPrice;
                string txt = pack.Badge == Ribbon.Popular ? "ПОПУЛЯРНЫЙ"
                           : pack.Badge == Ribbon.Value ? "ВЫГОДНО"
                           : "ЛУЧШАЯ ЦЕНА";
                var ribbon = new Label(txt) { pickingMode = PickingMode.Ignore };
                ribbon.style.position = Position.Absolute;
                ribbon.style.top = -10;
                ribbon.style.left = 14;
                ribbon.style.fontSize = 18;
                ribbon.style.unityFontStyleAndWeight = FontStyle.Bold;
                ribbon.style.color = gold ? LvnTokens.Bg : LvnTokens.OnAccent;
                ribbon.style.backgroundColor = gold ? LvnTokens.Gold : LvnTokens.Accent;
                ribbon.style.paddingTop = 3; ribbon.style.paddingBottom = 3;
                ribbon.style.paddingLeft = 10; ribbon.style.paddingRight = 10;
                Round(ribbon, LvnTokens.RadiusSm - 4f);
                card.Add(ribbon);
            }

            return card;
        }

        private async void Buy(Button b, Pack pack)
        {
            if (_buying) return;
            _buying = true;
            var label = b.text;
            b.SetEnabled(false);
            b.text = "…";
            // TEST-mode purchase: no store billing yet, but the CREDIT is real —
            // it lands in the server wallet (idempotent op), so bought crystals
            // exist everywhere: the wardrobe, the HUD pills, the next session.
            // Real IAP swaps only this call for the receipt flow.
            long total = pack.Amount + pack.Bonus;
            bool ok = await Lvn.Services.LvnWallet.EarnAsync(
                CurrencyOf(pack), total, "packshop_test:" + pack.Amount + pack.Unit);
            RefreshBalances();
            if (!ok)
            {
                b.text = label;
                b.SetEnabled(true);
                _buying = false;
                return;
            }
            b.schedule.Execute(() =>
            {
                b.text = "✓";
                b.schedule.Execute(() =>
                {
                    b.text = label;
                    b.SetEnabled(true);
                    _buying = false;
                }).ExecuteLater(1100);
            }).ExecuteLater(650);
        }

        // The wallet currency a pack credits — from its display unit.
        private static string CurrencyOf(Pack p)
        {
            var u = (p.Unit ?? "").ToLowerInvariant();
            if (u.Contains("крист")) return "crystals";
            if (u.Contains("энерг")) return "energy";
            if (u.Contains("золот")) return "gold";
            return "crystals";
        }

        // ── Balances: the REAL wallet ────────────────────────────────────────
        private void RefreshBalances()
        {
            _balances.Clear();
            var bal = Lvn.Services.LvnWallet.Balances;
            long crystals = bal.TryGetValue("crystals", out var c) ? c : 0;
            long energy = bal.TryGetValue("energy", out var e) ? e : 0;
            _balances.Add(BalancePill("◆", crystals.ToString("N0"), LvnTokens.Gold));
            _balances.Add(BalancePill("⚡", energy.ToString("N0"), LvnTokens.Accent));
        }

        private static VisualElement BalancePill(string glyph, string value, Color glyphColor)
        {
            var pill = new VisualElement();
            pill.style.flexDirection = FlexDirection.Row;
            pill.style.alignItems = Align.Center;
            pill.style.marginLeft = 10;
            pill.style.paddingLeft = 12; pill.style.paddingRight = 12;
            pill.style.paddingTop = 6; pill.style.paddingBottom = 6;
            pill.style.backgroundColor = new Color(0f, 0f, 0f, 0.4f);
            Round(pill, 16f);

            var icon = new Label(glyph);
            icon.style.color = glyphColor;
            icon.style.fontSize = 24;
            icon.style.marginRight = 6;
            icon.style.unityFontStyleAndWeight = FontStyle.Bold;
            pill.Add(icon);

            var amount = new Label(value);
            amount.style.color = LvnTokens.Text;
            amount.style.fontSize = 24;
            amount.style.unityFontStyleAndWeight = FontStyle.Bold;
            pill.Add(amount);
            return pill;
        }

        // ── Hardcoded demo catalog: five tiered packs per tab ─────────────────
        private static Dictionary<string, List<Pack>> BuildDemoCatalog()
        {
            var gem = new Color(0.42f, 0.28f, 0.62f);
            var au = new Color(0.55f, 0.42f, 0.16f);
            var en = new Color(0.16f, 0.34f, 0.52f);
            var bun = new Color(0.44f, 0.20f, 0.34f);

            return new Dictionary<string, List<Pack>>
            {
                ["crystals"] = new List<Pack>
                {
                    new Pack { Amount = 80,   Unit = "кристаллов", Price = "$0.99",  Bonus = 0,    Glyph = "◆", Tint = gem, Card = "/content/cards/card1.png" },
                    new Pack { Amount = 250,  Unit = "кристаллов", Price = "$2.99",  Bonus = 20,   Glyph = "◆", Tint = gem, Card = "/content/cards/card2.png" },
                    new Pack { Amount = 550,  Unit = "кристаллов", Price = "$4.99",  Bonus = 60,   Badge = Ribbon.Popular, Glyph = "◆", Tint = gem, Card = "/content/cards/card3.png" },
                    new Pack { Amount = 1200, Unit = "кристаллов", Price = "$9.99",  Bonus = 200,  Glyph = "◆", Tint = gem, Card = "/content/cards/card4.png" },
                    new Pack { Amount = 2800, Unit = "кристаллов", Price = "$19.99", Bonus = 700,  Badge = Ribbon.Value, Best = true, Glyph = "◆", Tint = gem, Card = "/content/cards/card5.png" },
                },
                ["gold"] = new List<Pack>
                {
                    new Pack { Amount = 500,    Unit = "золота", Price = "$0.99",  Bonus = 0,     Glyph = "◆", Tint = au, Card = "/content/cards/card1.png" },
                    new Pack { Amount = 1500,   Unit = "золота", Price = "$2.99",  Bonus = 150,   Glyph = "◆", Tint = au, Card = "/content/cards/card2.png" },
                    new Pack { Amount = 3500,   Unit = "золота", Price = "$4.99",  Bonus = 500,   Badge = Ribbon.Popular, Glyph = "◆", Tint = au, Card = "/content/cards/card3.png" },
                    new Pack { Amount = 8000,   Unit = "золота", Price = "$9.99",  Bonus = 1500,  Glyph = "◆", Tint = au, Card = "/content/cards/card4.png" },
                    new Pack { Amount = 20000,  Unit = "золота", Price = "$19.99", Bonus = 6000,  Badge = Ribbon.BestPrice, Best = true, Glyph = "◆", Tint = au, Card = "/content/cards/card5.png" },
                },
                ["energy"] = new List<Pack>
                {
                    new Pack { Amount = 30,   Unit = "энергии", Price = "$0.99",  Bonus = 0,   Glyph = "⚡", Tint = en, Card = "/content/cards/card1.png" },
                    new Pack { Amount = 100,  Unit = "энергии", Price = "$2.99",  Bonus = 10,  Glyph = "⚡", Tint = en, Card = "/content/cards/card2.png" },
                    new Pack { Amount = 250,  Unit = "энергии", Price = "$4.99",  Bonus = 35,  Badge = Ribbon.Popular, Glyph = "⚡", Tint = en, Card = "/content/cards/card3.png" },
                    new Pack { Amount = 600,  Unit = "энергии", Price = "$9.99",  Bonus = 120, Glyph = "⚡", Tint = en, Card = "/content/cards/card4.png" },
                    new Pack { Amount = 1500, Unit = "энергии", Price = "$19.99", Bonus = 400, Badge = Ribbon.Value, Best = true, Glyph = "⚡", Tint = en, Card = "/content/cards/card5.png" },
                },
                ["bundles"] = new List<Pack>
                {
                    new Pack { Amount = 1,  Unit = "Набор новичка",   Price = "$1.99",  Bonus = 0,  Glyph = "🎁", Tint = bun, Card = "/content/cards/card1.png" },
                    new Pack { Amount = 1,  Unit = "Недельный набор", Price = "$4.99",  Bonus = 0,  Badge = Ribbon.Popular, Glyph = "🎁", Tint = bun, Card = "/content/cards/card2.png" },
                    new Pack { Amount = 1,  Unit = "Набор героя",     Price = "$9.99",  Bonus = 0,  Glyph = "🎁", Tint = bun, Card = "/content/cards/card3.png" },
                    new Pack { Amount = 1,  Unit = "Королевский набор",Price = "$24.99", Bonus = 0, Badge = Ribbon.Value, Best = true, Glyph = "👑", Tint = bun, Card = "/content/cards/card4.png" },
                    new Pack { Amount = 1,  Unit = "Легендарный набор",Price = "$49.99", Bonus = 0, Badge = Ribbon.BestPrice, Glyph = "👑", Tint = bun, Card = "/content/cards/card5.png" },
                },
            };
        }

        private static void Round(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r;
            el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r;
            el.style.borderBottomRightRadius = r;
        }

        private static void ClearBorder(VisualElement el)
        {
            el.style.borderTopWidth = 0;
            el.style.borderBottomWidth = 0;
            el.style.borderLeftWidth = 0;
            el.style.borderRightWidth = 0;
        }
    }
}
