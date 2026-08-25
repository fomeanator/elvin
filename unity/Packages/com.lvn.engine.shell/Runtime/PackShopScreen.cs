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
    public sealed class PackShopScreen : LvnOverlayScreen
    {
        private enum Ribbon { None, Popular, Value, BestPrice }

        private struct Pack
        {
            public string Sku;
            public string Currency;                     // валюта одиночного пака
            public Dictionary<string, long> Grants;     // набор: валюта → количество
            public string Headline; // заголовок карточки набора (title из каталога)
            public string SubLine;  // состав набора («550 кристаллов · 5 энергии»)
            public long Amount;
            public string Unit;   // "кристаллов", "энергии", …
            public string Price;  // "$4.99"
            public long Bonus;
            public Ribbon Badge;
            public bool Best;     // biggest / highlighted card
            public string Card;   // illustration url, "/content/cards/cardN.png"
            public LvnIcon Emblem;  // эмблема поверх плашки, пока не приехал арт
            public Color Tint;    // illustration block fill
        }

        // Вкладки строятся ИЗ ЖИВОГО КАТАЛОГА (/v1/iap/catalog): какие валюты
        // продаются — такие и таблетки; «Наборы» появляются, когда у пака есть
        // grants. Демо-хардкод с золотом ушёл (живой репорт «3 валюты, а надо
        // 2, и реальные наборы»).
        private readonly List<string> _tabIds = new List<string>();
        private readonly List<string> _tabNames = new List<string>();

        private readonly ILvnAssets _assets;
        private readonly VisualElement _balances;
        private readonly VisualElement _tabsRow;
        private readonly ScrollView _list;
        private readonly List<Button> _tabButtons = new List<Button>();
        private readonly Dictionary<string, List<Pack>> _catalog;

        private bool _buying;
        private int _tab;

        public PackShopScreen(ILvnAssets assets)
        {
            _assets = assets;
            _catalog = new Dictionary<string, List<Pack>>();

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
            LvnChrome.Round(sheet, LvnTokens.Radius + 4f);
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
            LvnChrome.Round(back, LvnTokens.RadiusSm);
            LvnChrome.ClearBorder(back);
            top.Add(back);

            var title = new Label("Магазин");
            LvnChrome.Heading(title);
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
            LvnAsync.Fire(LoadCatalogAsync(), "PackShopCatalog");
        }

        // Живой каталог: сервер — единственный источник паков и цен.
        private async Task LoadCatalogAsync()
        {
            var packs = await Lvn.Services.LvnWallet.GetCatalogAsync();
            _catalog.Clear();
            _tabIds.Clear();
            _tabNames.Clear();
            if (packs != null)
            {
                foreach (var p in packs)
                {
                    bool bundle = p.Grants != null && p.Grants.Count > 0;
                    string tab = bundle ? "bundles" : p.Currency;
                    if (string.IsNullOrEmpty(tab)) continue;
                    if (!_catalog.TryGetValue(tab, out var list))
                    {
                        _catalog[tab] = list = new List<Pack>();
                        _tabIds.Add(tab);
                        _tabNames.Add(TabTitle(tab));
                    }
                    list.Add(ToCard(p, bundle));
                }
                // Витринные акценты: самый крупный пак вкладки — «герой» с
                // лучшей ценой, серединный — «популярный».
                foreach (var list in _catalog.Values)
                {
                    if (list.Count >= 3)
                    {
                        var mid = list[list.Count / 2];
                        mid.Badge = Ribbon.Popular;
                        list[list.Count / 2] = mid;
                    }
                    if (list.Count >= 2)
                    {
                        var last = list[list.Count - 1];
                        last.Badge = Ribbon.BestPrice;
                        last.Best = true;
                        list[list.Count - 1] = last;
                    }
                }
            }
            _tab = 0;
            BuildTabs();
            Rebuild();
        }

        private static string TabTitle(string tab) => tab switch
        {
            "crystals" => "Кристаллы",
            "energy" => "Энергия",
            "bundles" => "Наборы",
            _ => char.ToUpperInvariant(tab[0]) + tab.Substring(1),
        };

        private static string UnitOf(string currency) => currency switch
        {
            "crystals" => "кристаллов",
            "energy" => "энергии",
            _ => currency,
        };

        private Pack ToCard(Lvn.Services.LvnWallet.IapPack p, bool bundle)
        {
            var gem = new Color(0.42f, 0.28f, 0.62f);
            var en = new Color(0.16f, 0.34f, 0.52f);
            var bun = new Color(0.44f, 0.20f, 0.34f);
            string sub = null;
            if (bundle)
            {
                var parts = new List<string>();
                foreach (var kv in p.Grants)
                    parts.Add($"{kv.Value:N0} {UnitOf(kv.Key)}");
                sub = string.Join(" · ", parts);
            }
            return new Pack
            {
                Sku = p.Sku,
                Currency = p.Currency,
                Grants = p.Grants,
                Headline = bundle ? (string.IsNullOrEmpty(p.Title) ? "Набор" : p.Title) : null,
                SubLine = sub,
                Amount = p.Amount,
                Unit = UnitOf(p.Currency),
                Price = p.Price,
                Bonus = p.Bonus,
                Card = string.IsNullOrEmpty(p.Icon) ? null : p.Icon,
                Emblem = bundle ? LvnIcon.Gift : p.Currency == "energy" ? LvnIcon.Energy : LvnIcon.Gem,
                Tint = bundle ? bun : p.Currency == "energy" ? en : gem,
            };
        }

        /// <summary>Re-render the pack grid for the active tab and re-style the tab
        /// pills. Cheap to call after any state change.</summary>
        public void Rebuild()
        {
            for (int i = 0; i < _tabButtons.Count; i++) StyleTab(_tabButtons[i], i == _tab);

            _list.Clear();
            if (_tabIds.Count == 0)
            {
                var empty = new Label("Магазин сейчас закрыт");
                empty.style.color = LvnTokens.TextDim;
                empty.style.fontSize = 26;
                empty.style.marginTop = 40;
                empty.style.unityTextAlign = TextAnchor.MiddleCenter;
                _list.Add(empty);
                return;
            }
            if (_tab >= _tabIds.Count || !_catalog.TryGetValue(_tabIds[_tab], out var packs)) return;
            foreach (var p in packs) _list.Add(Card(p));
        }


        private void BuildTabs()
        {
            _tabsRow.Clear();
            _tabButtons.Clear();
            for (int i = 0; i < _tabNames.Count; i++)
            {
                int idx = i;
                var pill = new Button(() => { _tab = idx; Rebuild(); }) { text = _tabNames[i] };
                pill.style.fontSize = 24;
                pill.style.marginRight = 10;
                pill.style.marginBottom = 8;
                pill.style.paddingTop = 10; pill.style.paddingBottom = 10;
                pill.style.paddingLeft = 20; pill.style.paddingRight = 20;
                LvnChrome.Round(pill, LvnTokens.RadiusSm + 4f);
                LvnChrome.ClearBorder(pill);
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
            LvnChrome.Round(card, LvnTokens.Radius);
            LvnChrome.Edge(card);
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
                LvnChrome.Round(glow, LvnTokens.Radius + 3f);
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
            LvnChrome.Round(art, LvnTokens.RadiusSm);
            var glyph = LvnIcons.Make(pack.Emblem, pack.Best ? 52f : 40f, LvnTokens.Text,
                                      0f, LvnTheme.Current.IconGlow);
            glyph.style.alignSelf = Align.Center;
            art.Add(glyph);
            card.Add(art);
            LvnAsync.Fire(ScreenUi.AssignBgAsync(art, pack.Card, _assets), "AssignBg");
            // Middle column: amount headline + gold bonus line.
            var col = new VisualElement();
            col.style.flexGrow = 1;
            card.Add(col);

            var amount = new Label(pack.Headline ?? $"{pack.Amount:N0} {pack.Unit}");
            amount.style.color = LvnTokens.Text;
            amount.style.fontSize = pack.Best ? 32 : 28;
            amount.style.unityFontStyleAndWeight = FontStyle.Bold;
            amount.style.whiteSpace = WhiteSpace.Normal;
            col.Add(amount);

            if (!string.IsNullOrEmpty(pack.SubLine))
            {
                var sub = new Label(pack.SubLine);
                sub.style.color = LvnTokens.TextDim;
                sub.style.fontSize = 22;
                sub.style.marginTop = 4;
                col.Add(sub);
            }
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
            LvnChrome.Round(buy, LvnTokens.RadiusSm);
            LvnChrome.ClearBorder(buy);
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
                LvnChrome.Round(ribbon, LvnTokens.RadiusSm - 4f);
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
            bool ok;
            if (pack.Grants != null && pack.Grants.Count > 0)
            {
                ok = true;
                foreach (var kv in pack.Grants)
                    ok &= await Lvn.Services.LvnWallet.EarnAsync(
                        kv.Key, kv.Value, "packshop_test:" + pack.Sku);
            }
            else
            {
                long total = pack.Amount + pack.Bonus;
                ok = await Lvn.Services.LvnWallet.EarnAsync(
                    string.IsNullOrEmpty(pack.Currency) ? "crystals" : pack.Currency,
                    total, "packshop_test:" + pack.Sku);
            }
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
                b.text = "Готово";
                b.schedule.Execute(() =>
                {
                    b.text = label;
                    b.SetEnabled(true);
                    _buying = false;
                }).ExecuteLater(1100);
            }).ExecuteLater(650);
        }

        // ── Balances: the REAL wallet ────────────────────────────────────────
        private void RefreshBalances()
        {
            _balances.Clear();
            var bal = Lvn.Services.LvnWallet.Balances;
            long crystals = bal.TryGetValue("crystals", out var c) ? c : 0;
            long energy = bal.TryGetValue("energy", out var e) ? e : 0;
            _balances.Add(BalancePill(LvnIcon.Gem, crystals.ToString("N0"), LvnTokens.Gold));
            _balances.Add(BalancePill(LvnIcon.Energy, energy.ToString("N0"), LvnTokens.Accent));
        }

        private static VisualElement BalancePill(LvnIcon glyph, string value, Color glyphColor)
        {
            var pill = new VisualElement();
            pill.style.flexDirection = FlexDirection.Row;
            pill.style.alignItems = Align.Center;
            pill.style.marginLeft = 10;
            pill.style.paddingLeft = 12; pill.style.paddingRight = 12;
            pill.style.paddingTop = 6; pill.style.paddingBottom = 6;
            pill.style.backgroundColor = new Color(0f, 0f, 0f, 0.4f);
            LvnChrome.Round(pill, 16f);

            var icon = LvnIcons.Make(glyph, 22f, glyphColor, 0f, LvnTheme.Current.IconGlow);
            icon.style.marginRight = 6;
            pill.Add(icon);

            var amount = new Label(value);
            amount.style.color = LvnTokens.Text;
            amount.style.fontSize = 24;
            amount.style.unityFontStyleAndWeight = FontStyle.Bold;
            pill.Add(amount);
            return pill;
        }

        // ── Hardcoded demo catalog: five tiered packs per tab ─────────────────
    }
}
