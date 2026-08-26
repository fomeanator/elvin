using System.Collections.Generic;
using Lvn.Content;
using Lvn.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ВКЛАДКА ГАРДЕРОБА (концепция Ильи 26.08): героиня общего слоя меню
    /// стоит в центре внимания, а эта страница рисует только UI ВОКРУГ неё —
    /// оси и варианты из манифеста. ГАРДЕРОБ ОДИН: данные и надевание — тот же
    /// LvnWardrobe, что и у сценического шита в игре; здесь лишь меню-вид.
    /// Покупки платных вариантов остаются в сценическом шите (этап 2 —
    /// перенести цены и сюда).
    /// </summary>
    public sealed class WardrobeTabScreen : LvnOverlayScreen
    {
        private readonly LvnManifest _manifest;
        private readonly ILvnAssets _assets;
        private readonly VisualElement _panel;

        // ── Покупка (решение Ильи 27.08: «в меню-гардеробе прям листинг
        // скинов, и там можно купить») ── семантика владения — как в сюжетном
        // шите: платный предмет твой, когда его sku лежит в серверном кошельке.
        // Тап по чужому предмету ВЗВОДИТ карточку («Купить · цена»), второй
        // тап — покупает и надевает. Нехватка средств ведёт в быстрый магазин.
        private string _armedSku;
        private bool _buying;
        /// <summary>Открыть быстрый магазин (модаль) — вешает NovelShell.</summary>
        public System.Func<System.Threading.Tasks.Task> OpenStore;
        /// <summary>Подтверждение «не хватает — в магазин?» — вешает NovelShell.</summary>
        public System.Func<string, string, System.Threading.Tasks.Task<bool>> ConfirmTopUp;

        private bool IsOwned(string axis, LvnWardrobeItem item) =>
            item == null || item.price <= 0
            || LvnWallet.Inventory.ContainsKey(LvnWardrobe.Sku(Entity, axis, item.value));

        // Текущий персонаж вкладки: фаворит меню, иначе героиня по умолчанию.
        private string Entity
        {
            get
            {
                var fav = LvnPrefs.MenuFavorite;
                if (!string.IsNullOrEmpty(fav) && _manifest?.sprites != null
                    && _manifest.sprites.ContainsKey(fav)) return fav;
                return _manifest?.ui?.wardrobe?.entity;
            }
        }

        // Ростер фаворитов: явный ui.wardrobe.characters, иначе одна героиня.
        private List<string> Roster()
        {
            var list = new List<string>();
            var explicitRoster = _manifest?.ui?.wardrobe?.characters;
            if (explicitRoster != null)
                foreach (var id in explicitRoster)
                    if (!string.IsNullOrEmpty(id) && _manifest.sprites != null
                        && _manifest.sprites.ContainsKey(id)) list.Add(id);
            if (list.Count == 0 && !string.IsNullOrEmpty(Entity)) list.Add(Entity);
            return list;
        }

        public WardrobeTabScreen(LvnManifest manifest, ILvnAssets assets)
        {
            _manifest = manifest;
            _assets = assets;
            ScreenUi.Stretch(this);
            style.backgroundColor = Color.clear;
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            pickingMode = PickingMode.Ignore;

            var title = new Label(manifest?.ui?.wardrobe?.title ?? "Гардероб");
            LvnChrome.Heading(title);
            title.pickingMode = PickingMode.Ignore;
            title.style.position = Position.Absolute;
            title.style.top = 108; title.style.left = 20;
            title.style.color = LvnTokens.Text;
            title.style.fontSize = 40;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            Add(title);

            // «Во весь рост» — прячет панель, оставляя героиню (фича шита).
            var peek = new Button { text = _manifest?.ui?.wardrobe?.peek_text ?? "Во весь рост" };
            peek.style.position = Position.Absolute;
            peek.style.top = 108; peek.style.right = 20;
            peek.style.height = 46; peek.style.fontSize = 20;
            peek.style.paddingLeft = 16; peek.style.paddingRight = 16;
            peek.style.color = LvnTokens.Text;
            peek.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.ClearBorder(peek); LvnChrome.Round(peek, 14f);
            peek.clicked += () =>
            {
                bool hidden = _panel.style.display == DisplayStyle.None;
                _panel.style.display = hidden ? DisplayStyle.Flex : DisplayStyle.None;
                peek.text = hidden ? (_manifest?.ui?.wardrobe?.peek_text ?? "Во весь рост") : "Наряды";
            };
            Add(peek);

            // Панель осей — снизу, над нижним меню; героиня видна за ней.
            _panel = new VisualElement();
            _panel.style.position = Position.Absolute;
            _panel.style.left = 0; _panel.style.right = 0;
            _panel.style.bottom = 132;
            _panel.style.paddingLeft = 20; _panel.style.paddingRight = 20;
            Add(_panel);
        }

        protected override void OnOpening() => Rebuild();

        // Карточка предмета — СТАРЫЙ вид шита (просьба Ильи): превью-иконка
        // наряда, имя, цена у платных; выбранная — акцентная рамка; чужой
        // взведённый — золотая рамка и «Купить».
        private VisualElement ItemCard(string axis, LvnWardrobeItem item, bool on)
        {
            bool owned = IsOwned(axis, item);
            string sku = LvnWardrobe.Sku(Entity, axis, item.value);
            bool armed = !owned && sku == _armedSku;

            var card = new VisualElement();
            card.style.width = 132;
            card.style.marginRight = 10;
            card.style.alignItems = Align.Center;
            card.style.paddingTop = 8; card.style.paddingBottom = 8;
            var bg = LvnTokens.PanelBg;
            card.style.backgroundColor = on ? LvnTokens.SurfaceHi : new Color(bg.r, bg.g, bg.b, 0.6f);
            LvnChrome.Round(card, 12f);
            card.style.borderTopWidth = card.style.borderBottomWidth = 2f;
            card.style.borderLeftWidth = card.style.borderRightWidth = 2f;
            var edge = armed ? LvnTokens.Gold : on ? LvnTokens.Accent : LvnTokens.Border;
            card.style.borderTopColor = card.style.borderBottomColor = edge;
            card.style.borderLeftColor = card.style.borderRightColor = edge;

            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.style.width = 96; icon.style.height = 116;
            icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            if (!string.IsNullOrEmpty(item.icon))
                LvnAsync.Fire(ScreenUi.AssignBgAsync(icon, item.icon, _assets), "WardrobeIcon");
            card.Add(icon);

            var name = new Label(item.name ?? item.value) { pickingMode = PickingMode.Ignore };
            name.style.color = on ? LvnTokens.Text : LvnTokens.TextDim;
            name.style.fontSize = 19;
            name.style.marginTop = 5;
            card.Add(name);

            if (item.price > 0 && !owned)
            {
                var price = new Label(armed ? $"Купить · ◆ {item.price}" : $"◆ {item.price}")
                { pickingMode = PickingMode.Ignore };
                price.style.color = LvnTokens.Gold;
                price.style.fontSize = 18;
                price.style.marginTop = 2;
                if (armed) price.style.unityFontStyleAndWeight = FontStyle.Bold;
                card.Add(price);
            }

            var value = item.value;
            card.RegisterCallback<ClickEvent>(_ =>
            {
                if (_buying) return;
                if (IsOwned(axis, item))
                {
                    _armedSku = null;
                    LvnWardrobe.Equip(Entity, axis, value); // кукла обновится сама
                    Rebuild();
                }
                else if (sku != _armedSku) { _armedSku = sku; Rebuild(); }
                else LvnAsync.Fire(BuyAsync(axis, item), "WardrobeBuy");
            });
            return card;
        }

        // Покупка = списание кошелька с этим sku (владение приезжает в
        // Inventory тем же ответом) → надеть. Нехватка — предложить быстрый
        // магазин и после него попробовать ровно ещё раз, как сюжетный шит.
        private async System.Threading.Tasks.Task BuyAsync(string axis, LvnWardrobeItem item)
        {
            _buying = true;
            try
            {
                string sku = LvnWardrobe.Sku(Entity, axis, item.value);
                bool ok = await LvnWallet.SpendAsync(item.currency, item.price, "wardrobe", sku);
                if (!ok && ConfirmTopUp != null && OpenStore != null)
                {
                    string title = item.name ?? item.value;
                    if (await ConfirmTopUp("Не хватает кристаллов",
                            $"{title}: ◆ {item.price:N0}"))
                    {
                        await OpenStore();
                        ok = await LvnWallet.SpendAsync(item.currency, item.price, "wardrobe", sku);
                    }
                }
                _armedSku = null;
                if (ok) LvnWardrobe.Equip(Entity, axis, item.value);
                Rebuild();
            }
            finally { _buying = false; }
        }

        public void Rebuild()
        {
            _panel.Clear();
            var entity = Entity;
            if (string.IsNullOrEmpty(entity) || _manifest?.sprites == null
                || !_manifest.sprites.TryGetValue(entity, out var def)) return;

            // ФАВОРИТЫ (прикол Ильи 26.08): выбери, кто стоит на главной —
            // выбранный тут же встаёт на передний план всего меню.
            var roster = Roster();
            if (roster.Count > 1)
            {
                var favRow = new VisualElement();
                favRow.style.flexDirection = FlexDirection.Row;
                favRow.style.marginTop = 10;
                _panel.Add(favRow);
                foreach (var id in roster)
                {
                    bool on = id == entity;
                    var b = new Button
                    { text = _manifest.sprites[id]?.name ?? id };
                    b.style.height = 48;
                    b.style.fontSize = 21;
                    b.style.marginRight = 8;
                    b.style.paddingLeft = 16; b.style.paddingRight = 16;
                    b.style.color = on ? LvnTokens.OnAccent : LvnTokens.Text;
                    b.style.backgroundColor = on ? LvnTokens.Accent : LvnTokens.Faint;
                    LvnChrome.ClearBorder(b);
                    LvnChrome.Round(b, 14f);
                    var captured = id;
                    b.clicked += () =>
                    {
                        LvnPrefs.MenuFavorite = captured; // кукла меню сменится сама
                        Rebuild();
                    };
                    favRow.Add(b);
                }
            }
            if (def?.wardrobe == null) return;

            foreach (var kv in def.wardrobe)
            {
                string axis = kv.Key;
                var slot = kv.Value;
                if (slot?.items == null || slot.items.Count == 0) continue;

                var card = new VisualElement();
                var bg = LvnTokens.PanelBg;
                card.style.backgroundColor = new Color(bg.r, bg.g, bg.b, 0.82f);
                LvnChrome.Edge(card);
                LvnChrome.Round(card, 14f);
                card.style.paddingTop = 10; card.style.paddingBottom = 12;
                card.style.paddingLeft = 14; card.style.paddingRight = 14;
                card.style.marginTop = 10;
                _panel.Add(card);

                var name = new Label(slot.name ?? axis);
                name.pickingMode = PickingMode.Ignore;
                name.style.color = LvnTokens.TextDim;
                name.style.fontSize = 19;
                name.style.marginBottom = 8;
                card.Add(name);

                var row = new ScrollView(ScrollViewMode.Horizontal);
                row.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                row.style.flexDirection = FlexDirection.Row;
                card.Add(row);

                var eq = LvnWardrobe.Equipped(entity);
                string current = eq != null && eq.TryGetValue(axis, out var cv) ? cv : null;
                if (string.IsNullOrEmpty(current)
                    && def.defaults != null && def.defaults.TryGetValue(axis, out var dv)) current = dv;

                foreach (var item in slot.items)
                {
                    if (item == null || string.IsNullOrEmpty(item.value)) continue;
                    row.Add(ItemCard(axis, item, item.value == current));
                }
            }
        }
    }
}
