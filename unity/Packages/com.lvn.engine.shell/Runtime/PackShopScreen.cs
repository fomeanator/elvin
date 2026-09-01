using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
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
    public sealed partial class PackShopScreen : LvnOverlayScreen, ILvnContentAware
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

        /// <summary>ЧТО МЫ ЗНАЕМ О КАТАЛОГЕ. Пустой список — это ответ, а не
        /// молчание: до ответа сервера экран рисовал «магазин закрыт», хотя не
        /// знал ещё ничего, и первым кадром сообщал игроку неправду. Три
        /// состояния вместо одного: не спрашивали, не смогли, знаем.</summary>
        private enum CatalogState { Unknown, Failed, Known }
        private CatalogState _catalogState = CatalogState.Unknown;

        private readonly ILvnAssets _assets;
        private readonly VisualElement _tabsRow;
        private readonly ScrollView _list;
        private readonly List<Button> _tabButtons = new List<Button>();
        private readonly Dictionary<string, List<Pack>> _catalog;

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


            var sheet = new VisualElement();
            sheet.style.position = Position.Absolute;
            if (modal)
            {
                // Скрим ловит тап мимо листа = закрыть (как попап загрузок).
                style.backgroundColor = LvnTokens.Veil(0.55f);
                pickingMode = PickingMode.Position;
                RegisterCallback<ClickEvent>(e => { if (e.target == this) Cancel(); });
                sheet.style.left = 16; sheet.style.right = 16;
                sheet.style.top = Length.Percent(12f);
                sheet.style.bottom = Length.Percent(5f);
                LvnAir.PadX(sheet, LvnTokens.Space3);
                sheet.style.paddingBottom = LvnTokens.Space2;
                sheet.style.paddingTop = LvnTokens.Space3;
                AdoptSheet(sheet); // общий вид листа: фон, окантовка, подъезд
            }
            else
            {
                // ВКЛАДКА, точь-в-точь как главная (решение Ильи 26.08): никакого
                // враппера-листа и скрима — контент прямо на общей атмосфере;
                // сверху навбар, снизу дырка под нижнее меню хаба (оно живёт и
                // кликается — root не ловит тапы).
                ScreenUi.HubTabSheet(this, sheet);
            }
            Add(sheet);

            // ── Top bar: back ‹ · title · balances ────────────────────────────
            var top = ScreenUi.Row();
            top.style.marginBottom = LvnTokens.Space3;
            sheet.Add(top);

            var titleBlock = new VisualElement();
            titleBlock.style.flexGrow = 1;
            titleBlock.Add(ScreenUi.Eyebrow(() => LvnWords.Of("shop.eyebrow", "TOP UP")));
            var title = SectionTitle(() => LvnWords.Of("shop.title", "Store"));
            titleBlock.Add(title);
            top.Add(titleBlock);

            if (modal)
            {
                var close = new Button(Cancel) { text = "×" };
                close.style.width = 52; close.style.height = 52;
                close.style.fontSize = LvnTokens.TextLg;
                LvnStyler.Plate(close, LvnTokens.Faint, LvnTokens.TextDim, 26f);
                top.Add(close);
            }

            // Балансы в шапке удалены — валюты несёт единый навбар (дубль).
            // Пустой скрытый контейнер и пересборка пилюль в него жили здесь
            // ещё месяц после удаления: каждое начисление честно собирало две
            // плашки внутрь того, чего не видно. Хуже расхода то, что код
            // выглядел рабочим балансом магазина и уводил починку не туда.

            // ── Category tabs ─────────────────────────────────────────────────
            _tabsRow = new VisualElement();
            LvnFlow.Wrap(_tabsRow);
            _tabsRow.style.marginBottom = LvnTokens.Space2;
            sheet.Add(_tabsRow);
            BuildTabs();

            // ── Pack grid ─────────────────────────────────────────────────────
            _list = Lvn.UI.LvnScroll.Vertical();
            _list.style.flexGrow = 1;
            sheet.Add(_list);

            // Пилюли следуют за кошельком, а не за своими вызовами. Раньше
            Rebuild();
            LvnAsync.Fire(LoadCatalogAsync(), "PackShopCatalog");
        }

        // Живой каталог: сервер — единственный источник паков и цен.
        private async Task LoadCatalogAsync()
        {
            var packs = await Lvn.Services.LvnWallet.GetCatalogAsync();
            _catalog.Clear();
            _tabIds.Clear();
            // Сервер не ответил — это «не смогли узнать», а не «товаров нет».
            _catalogState = packs != null ? CatalogState.Known : CatalogState.Failed;
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

        // Названия валют держит ЦЕННИК — они приходят из манифеста новеллы.
        // Здесь стояли два switch с русскими словами: «Кристаллы», «энергии».
        // Движку они не принадлежат (docs/language-policy.md) — любая другая
        // игра получала чужую валюту насильно. Осталась только вкладка
        // «Наборы»: это раздел витрины, а не деньги.
        private string TabTitle(string tab)
        {
            // «Наборы» — раздел витрины, а не валюта: он остаётся подписью
            // движка, пока у магазина нет своего блока в манифесте.
            if (tab == "bundles") return LvnWords.Of("shop.bundles", "Bundles");
            var name = Lvn.UI.LvnPriceTag.Of(tab).Name;
            return string.IsNullOrEmpty(name)
                ? char.ToUpperInvariant(tab[0]) + tab.Substring(1) : name;
        }

        private static string UnitOf(string currency)
        {
            var look = Lvn.UI.LvnPriceTag.Of(currency);
            return !string.IsNullOrEmpty(look.Unit) ? look.Unit
                 : !string.IsNullOrEmpty(look.Name) ? look.Name : currency;
        }

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
                    parts.Add($"{LvnPriceTag.Amount(kv.Value)} {UnitOf(kv.Key)}");
                sub = string.Join(" · ", parts);
            }
            return new Pack
            {
                Sku = p.Sku,
                Currency = p.Currency,
                Grants = p.Grants,
                Headline = bundle ? (string.IsNullOrEmpty(p.Title) ? LvnWords.Of("shop.bundle", "Bundle") : p.Title) : null,
                SubLine = sub,
                Amount = p.Amount,
                Unit = UnitOf(p.Currency),
                Price = p.Price,
                Bonus = p.Bonus,
                Card = string.IsNullOrEmpty(p.Icon) ? null : p.Icon,
                // Значок и цвет — у Ценника: раньше «энергия или самоцвет»
                // решалось сравнением с зашитым идентификатором.
                Emblem = bundle ? LvnIcon.Gift : Lvn.UI.LvnPriceTag.Of(p.Currency).Icon,
                Tint = bundle ? bun : Lvn.UI.LvnPriceTag.Of(p.Currency).Tint,
            };
        }

        /// <summary>Re-render the pack grid for the active tab and re-style the tab
        /// pills. Cheap to call after any state change.</summary>
        /// <summary>Слова, шрифт или размеры сменились — перечитать их.</summary>

        public override void Rebuild()
        {
            for (int i = 0; i < _tabButtons.Count; i++) StyleTab(_tabButtons[i], i == _tab);

            _list.Clear();
            if (_tabIds.Count == 0)
            {
                string word =
                    _catalogState == CatalogState.Unknown ? LvnWords.Of("shop.loading", "Loading…") :
                    _catalogState == CatalogState.Failed ? LvnWords.Of("shop.offline", "The store is unavailable") :
                    LvnWords.Of("shop.closed", "The store is closed");
                var empty = new Label(word);
                empty.style.color = LvnTokens.TextDim;
                empty.style.fontSize = LvnTokens.TextSm;
                empty.style.marginTop = LvnTokens.Space5;
                empty.style.unityTextAlign = TextAnchor.MiddleCenter;
                _list.Add(empty);
                return;
            }
            // БЕСПЛАТНЫЙ СПОСОБ — ПЕРВЫМ. Игрок, у которого нет денег, пришёл
            // сюда именно за ним; спрятать рекламу под платными паками значит
            // показать ему только то, что он купить не может.
            var free = AdCard();
            if (free != null) _list.Add(free);
            if (_tab >= _tabIds.Count || !_catalog.TryGetValue(_tabIds[_tab], out var packs)) return;
            // Витрина, а не таблица: обычные паки — сеткой в две колонки,
            // «герой» вкладки и наборы — широкими карточками.
            var grid = new VisualElement();
            LvnFlow.Wrap(grid, Justify.SpaceBetween);
            _list.Add(grid);
            foreach (var p in packs) grid.Add(Card(p));
        }



        /// <summary>Какую площадку показывать бесплатной карточкой. Пусто —
        /// карточки нет: движок не выбирает за игру, чем она торгует.</summary>
        public string AdPlacement;

        /// <inheritdoc cref="ILvnContentAware.SetContent"/>
        /// <remarks>Чем торгует «бесплатная» карточка магазина, игра решает
        /// манифестом — и решение обязано доезжать вместе с ним.</remarks>
        public void SetContent(LvnManifest manifest) =>
            AdPlacement = manifest?.ui?.store?.ad_placement;

        private void BuildTabs()
        {
            _tabsRow.Clear();
            _tabButtons.Clear();
            for (int i = 0; i < _tabIds.Count; i++)
            {
                int idx = i;
                // Подпись вкладки спрашивается заново: список вкладок собирает
                // каталог (он приходит с сервера), а переодевание его не
                // перезапрашивает — застывшая строка пережила бы смену языка.
                var pill = new Button(() => { _tab = idx; Rebuild(); });
                Lvn.UI.LvnRedress.Bind(pill, () => idx < _tabIds.Count ? TabTitle(_tabIds[idx]) : string.Empty);
                pill.style.fontSize = LvnTokens.TextSm;
                LvnAir.PadX(pill, LvnTokens.Space3);
                LvnAir.PadY(pill, LvnTokens.Space2);
                pill.style.marginBottom = LvnTokens.Space1;
                pill.style.marginRight = LvnTokens.Space2;
                LvnChrome.Round(pill, LvnTokens.Radius);
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
            LvnStyler.Tab(b, active, LvnTokens.Radius);
        }


        // ЗАНЯТОСТЬ ВЕДЁТ ДОМ, а не своё поле. Здесь стоял `_buying`, снимаемый
        // в трёх местах руками, и комментарий обещал `finally`, которого не
        // было: оборванная сеть посреди покупки оставляла экран мёртвым до
        // выхода с него. Дом (LvnBusy) держит занятость самой кнопкой и
        // отпускает её при любом исходе; releaseOnSuccess: false — потому что
        // успех экран доигрывает сам («Готово» на секунду, потом прежняя
        // подпись).
        private void Buy(Button b, Pack pack)
        {
            string label = b.text;   // подпись запоминаем ДО занятости
            LvnAsync.Fire(LvnBusy.RunAsync(b, () => BuyAsync(b, pack, label), "…",
                                           releaseOnSuccess: false, what: "Buy"), "Buy");
        }

        private async Task BuyAsync(Button b, Pack pack, string label)
        {
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
            // Баланс тут обновлять некому и незачем: его показывает единый
            // навбар, а он слушает Changed сам.
            if (!ok)
            {
                b.text = label;
                b.SetEnabled(true);
                return;
            }
            b.schedule.Execute(() =>
            {
                b.text = LvnWords.Of("common.done", "Done");
                b.schedule.Execute(() =>
                {
                    b.text = label;
                    b.SetEnabled(true);
                }).ExecuteLater(1100);
            }).ExecuteLater(650);
        }


        // ── Hardcoded demo catalog: five tiered packs per tab ─────────────────
    }
}
