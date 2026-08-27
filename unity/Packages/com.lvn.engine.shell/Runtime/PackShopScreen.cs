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

        /// <summary>ДВА МАГАЗИНА ИЗ ОДНОГО КОНТЕНТА (решение Ильи 27.08):
        /// вкладка ленты — прозрачная страница на общей сцене меню; быстрый
        /// модальный (плюсик валют, гейт энергии, ext store_show) — лист-окно
        /// со СВОИМ фоном, открывается поверх ЛЮБОЙ страницы и в игре.
        /// Каталог, вкладки и карточки — общие; флаг меняет только обёртку.</summary>
        public PackShopScreen(ILvnAssets assets, bool modal = false)
        {
            _assets = assets;
            _catalog = new Dictionary<string, List<Pack>>();

            ScreenUi.Stretch(this);
            style.opacity = 0f;
            style.display = DisplayStyle.None;

            var sheet = new VisualElement();
            sheet.style.position = Position.Absolute;
            if (modal)
            {
                // Скрим ловит тап мимо листа = закрыть (как попап загрузок).
                style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
                pickingMode = PickingMode.Position;
                RegisterCallback<ClickEvent>(e => { if (e.target == this) Cancel(); });
                sheet.style.left = 16; sheet.style.right = 16;
                sheet.style.top = Length.Percent(12f);
                sheet.style.bottom = Length.Percent(5f);
                sheet.style.paddingTop = 18;
                sheet.style.paddingBottom = 14;
                sheet.style.paddingLeft = 20;
                sheet.style.paddingRight = 20;
                AdoptSheet(sheet); // общий вид листа: фон, окантовка, подъезд
            }
            else
            {
                // ВКЛАДКА, точь-в-точь как главная (решение Ильи 26.08): никакого
                // враппера-листа и скрима — контент прямо на общей атмосфере;
                // сверху навбар, снизу дырка под нижнее меню хаба (оно живёт и
                // кликается — root не ловит тапы).
                style.backgroundColor = Color.clear;
                pickingMode = PickingMode.Ignore;
                sheet.style.left = 10; sheet.style.right = 10;
                // Контент прижат ВНИЗ (решение Ильи 26.08, «как гардероб»):
                // верх экрана — воздух с героиней и полотном.
                sheet.style.top = Length.Percent(39f);    // лицо героини остаётся в чистой зоне
                sheet.style.bottom = 132; // дырка нижнего меню
                sheet.style.paddingTop = 18;
                sheet.style.paddingBottom = 14;
                sheet.style.paddingLeft = 18;
                sheet.style.paddingRight = 18;
                ScreenUi.SceneSheet(sheet, 0.92f);
            }
            Add(sheet);

            // ── Top bar: back ‹ · title · balances ────────────────────────────
            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            top.style.marginBottom = 16;
            sheet.Add(top);

            var titleBlock = new VisualElement();
            titleBlock.style.flexGrow = 1;
            var eyebrow = new Label("ПОПОЛНИТЬ КОШЕЛЁК");
            eyebrow.style.color = LvnTokens.Gold;
            eyebrow.style.fontSize = 18;
            eyebrow.style.letterSpacing = 2.2f;
            eyebrow.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleBlock.Add(eyebrow);
            var title = new Label("Магазин");
            LvnChrome.Heading(title);
            title.style.color = LvnTokens.Text;
            title.style.fontSize = 44;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleBlock.Add(title);
            top.Add(titleBlock);

            if (modal)
            {
                var close = new Button(Cancel) { text = "×" };
                close.style.width = 52; close.style.height = 52;
                close.style.fontSize = 34;
                close.style.color = LvnTokens.TextDim;
                close.style.backgroundColor = LvnTokens.Faint;
                LvnChrome.ClearBorder(close);
                LvnChrome.Round(close, 26f);
                top.Add(close);
            }

            // Балансы в шапке удалены — валюты несёт единый навбар (дубль).
            _balances = new VisualElement();
            _balances.style.display = DisplayStyle.None;
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
            // Витрина Time Romance живёт в ночном стекле и золоте, а не в
            // случайной фиолетовой палитре. Сливовый остаётся только у редких
            // наборов как у запечатанного личного дела.
            var gem = new Color(0.06f, 0.27f, 0.31f);
            var en = new Color(0.08f, 0.20f, 0.29f);
            var bun = new Color(0.19f, 0.12f, 0.23f);
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
            // Витрина, а не таблица: обычные паки — сеткой в две колонки,
            // «герой» вкладки и наборы — широкими карточками.
            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.justifyContent = Justify.SpaceBetween;
            _list.Add(grid);
            foreach (var p in packs) grid.Add(Card(p));
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
            // Скругление у вкладки своё (чуть круглее мелкого из темы) — роль
            // не имеет права его переопределять безымянным умолчанием.
            LvnStyler.Choice(b, active, LvnTokens.RadiusSm + 4f);
            b.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
        }

        // ── One pack card ─────────────────────────────────────────────────────
        private VisualElement Card(Pack pack)
        {
            bool wide = pack.Best || pack.Grants != null; // герой и наборы — во всю ширину
            var card = new VisualElement();
            card.style.width = Length.Percent(wide ? 100f : 48.5f);
            card.style.marginBottom = 14;
            card.style.backgroundColor = pack.Best ? LvnTokens.SurfaceHi : LvnTokens.Surface;
            LvnChrome.Round(card, LvnTokens.Radius);
            var quietEdge = LvnTokens.Border;
            LvnChrome.Border(card, new Color(quietEdge.r, quietEdge.g, quietEdge.b, quietEdge.a * 0.9f), 1f);
            card.style.overflow = Overflow.Hidden;
            if (pack.Best)
            {
                card.style.borderTopWidth = 2; card.style.borderBottomWidth = 1;
                card.style.borderLeftWidth = 1; card.style.borderRightWidth = 1;
                card.style.borderTopColor = LvnTokens.Accent;
                card.style.borderBottomColor = quietEdge;
                card.style.borderLeftColor = quietEdge;
                card.style.borderRightColor = quietEdge;
            }

            // Арт-сцена: не фиолетовая шапка, а тихий стол витрины. Реальная
            // иконка каталога может заполнить её целиком; без неё остаётся
            // аккуратный знак валюты и подпись категории.
            var art = new VisualElement();
            art.style.height = wide ? 112 : 82;
            art.style.alignItems = Align.Center;
            art.style.justifyContent = Justify.Center;
            art.style.backgroundColor = new Color(pack.Tint.r, pack.Tint.g, pack.Tint.b, 0.88f);
            art.style.borderTopLeftRadius = LvnTokens.Radius;
            art.style.borderTopRightRadius = LvnTokens.Radius;
            art.style.overflow = Overflow.Hidden;
            art.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            art.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            art.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            var halo = new VisualElement { pickingMode = PickingMode.Ignore };
            halo.style.position = Position.Absolute;
            halo.style.width = wide ? 78 : 60; halo.style.height = wide ? 78 : 60;
            halo.style.backgroundColor = new Color(1f, 1f, 1f, 0.07f);
            LvnChrome.Round(halo, wide ? 39f : 30f);
            art.Add(halo);
            var glyph = LvnIcons.Make(pack.Emblem, wide ? 46f : 36f, LvnTokens.Text, 0f, LvnTheme.Current.IconGlow * 0.55f);
            art.Add(glyph);
            var category = new Label(pack.Grants != null ? "НАБОР ДЛЯ ИСТОРИИ" : TabTitle(pack.Currency).ToUpperInvariant())
            { pickingMode = PickingMode.Ignore };
            category.style.position = Position.Absolute;
            category.style.left = 12; category.style.bottom = 9;
            category.style.color = new Color(LvnTokens.Text.r, LvnTokens.Text.g, LvnTokens.Text.b, 0.72f);
            category.style.fontSize = 15;
            category.style.letterSpacing = 1.4f;
            category.style.unityFontStyleAndWeight = FontStyle.Bold;
            art.Add(category);
            card.Add(art);
            if (!string.IsNullOrEmpty(pack.Card))
                LvnAsync.Fire(ScreenUi.AssignBgAsync(art, pack.Card, _assets), "AssignBg");

            // Текстовый этаж: количество/название, бонус и состав набора.
            var body = new VisualElement();
            body.style.paddingTop = 10;
            body.style.paddingBottom = 12;
            body.style.paddingLeft = 12;
            body.style.paddingRight = 12;
            body.style.alignItems = wide ? Align.FlexStart : Align.Center;
            card.Add(body);

            var amount = new Label(pack.Headline ?? $"{pack.Amount:N0} {pack.Unit}");
            amount.style.color = LvnTokens.Text;
            amount.style.fontSize = wide ? 30 : 25;
            amount.style.unityFontStyleAndWeight = FontStyle.Bold;
            amount.style.whiteSpace = WhiteSpace.Normal;
            if (!wide) amount.style.unityTextAlign = TextAnchor.MiddleCenter;
            body.Add(amount);

            if (pack.Grants != null && pack.Grants.Count > 0)
            {
                // Состав набора — пилюлями: читается с одного взгляда.
                var chips = new VisualElement();
                chips.style.flexDirection = FlexDirection.Row;
                chips.style.flexWrap = Wrap.Wrap;
                chips.style.marginTop = 8;
                foreach (var kv in pack.Grants)
                {
                    var chip = new VisualElement();
                    chip.style.flexDirection = FlexDirection.Row;
                    chip.style.alignItems = Align.Center;
                    chip.style.backgroundColor = LvnTokens.Faint;
                    LvnChrome.Round(chip, 12f);
                    chip.style.paddingTop = 5; chip.style.paddingBottom = 5;
                    chip.style.paddingLeft = 10; chip.style.paddingRight = 12;
                    chip.style.marginRight = 8; chip.style.marginBottom = 6;
                    var ic = LvnIcons.Make(kv.Key == "energy" ? LvnIcon.Energy : LvnIcon.Gem,
                        18f, LvnTokens.Accent, 0f, LvnTheme.Current.IconGlow);
                    ic.style.marginRight = 6;
                    chip.Add(ic);
                    var t = new Label($"{kv.Value:N0} {UnitOf(kv.Key)}");
                    t.style.color = LvnTokens.Text;
                    t.style.fontSize = 20;
                    chip.Add(t);
                    chips.Add(chip);
                }
                body.Add(chips);
            }
            else if (pack.Bonus > 0)
            {
                var bonus = new Label($"+{pack.Bonus:N0} бонус");
                bonus.style.color = LvnTokens.Gold;
                bonus.style.fontSize = 21;
                bonus.style.marginTop = 4;
                bonus.style.unityFontStyleAndWeight = FontStyle.Bold;
                body.Add(bonus);
            }

            var buy = new Button { text = pack.Price };
            buy.style.fontSize = 24;
            buy.style.marginTop = 10;
            buy.style.alignSelf = Align.Stretch;
            buy.style.paddingTop = 12; buy.style.paddingBottom = 12;
            buy.style.color = pack.Best ? LvnTokens.OnAccent : LvnTokens.Text;
            buy.style.backgroundColor = pack.Best
                ? LvnTokens.Accent
                : new Color(LvnTokens.Accent.r, LvnTokens.Accent.g, LvnTokens.Accent.b, 0.15f);
            buy.style.unityFontStyleAndWeight = FontStyle.Bold;
            LvnChrome.Round(buy, LvnTokens.RadiusSm);
            if (pack.Best) LvnChrome.ClearBorder(buy);
            else LvnChrome.Border(buy, new Color(LvnTokens.Accent.r, LvnTokens.Accent.g, LvnTokens.Accent.b, 0.36f), 1f);
            buy.clicked += () => Buy(buy, pack);
            body.Add(buy);

            if (pack.Badge != Ribbon.None)
            {
                bool gold = pack.Badge == Ribbon.Value || pack.Badge == Ribbon.BestPrice;
                string txt = pack.Badge == Ribbon.Popular ? "ПОПУЛЯРНЫЙ"
                           : pack.Badge == Ribbon.Value ? "ВЫГОДНО"
                           : "ЛУЧШАЯ ЦЕНА";
                var ribbon = new Label(txt) { pickingMode = PickingMode.Ignore };
                ribbon.style.position = Position.Absolute;
                ribbon.style.top = 10;
                ribbon.style.left = 12;
                ribbon.style.fontSize = 17;
                ribbon.style.unityFontStyleAndWeight = FontStyle.Bold;
                ribbon.style.letterSpacing = 1.5f;
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
