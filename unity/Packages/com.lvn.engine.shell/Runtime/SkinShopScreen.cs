using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;   // кошелёк: витрина берёт у него валюты игрока
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The premium <b>skin shop</b> overlay — a wardrobe that stores outfits/skins
    /// for characters (actors) across novels. The player picks a character, browses
    /// skin categories (hair / dress / accessories / background) and buys skins with
    /// currency, then equips the ones they own. Full-screen scrim + sheet, themed
    /// entirely from <see cref="LvnTokens"/> ("Полночь"): a big character preview on
    /// top, a circular avatar selector, category pill tabs, and a gacha-style item
    /// grid where each tile reads its state at a glance — equipped, owned or on sale.
    ///
    /// This build ships with hardcoded demo data so the screen looks complete now;
    /// the real catalog + wallet wiring lands later (see the wardrobe sheet
    /// for the live, manifest-driven equivalent).
    /// </summary>
    public sealed class SkinShopScreen : LvnOverlayScreen, Lvn.UI.ILvnRedress
    {
        private readonly ILvnAssets _assets;

        private readonly Label _balanceAmount;
        private readonly VisualElement _previewBox;
        private readonly VisualElement _previewImage;
        private readonly Label _previewName;
        private readonly Label _previewWearing;
        private readonly VisualElement _avatars;
        private readonly VisualElement _tabs;
        private readonly ScrollView _grid;

        private readonly List<Character> _chars = new List<Character>();
        private readonly string[] _categories = { "Причёска", "Платье", "Аксессуары", "Фон" };
        // demo skins keyed by "charIndex:catIndex" so equip/buy state persists.
        private readonly Dictionary<string, List<Skin>> _skins = new Dictionary<string, List<Skin>>();

        private int _char;
        private int _cat;
        private int _gold = 1240;
        // Какой валютой платит демо-кошелёк витрины (первая у игрока).
        private string _demoCurrency;


        private enum SkinState { Equipped, Owned, ForSale }

        private sealed class Character
        {
            public string Name;
            public string Preview; // background url
        }

        private sealed class Skin
        {
            public string Name;
            public string Thumb;
            public SkinState State;
            public int Price;
            /// <summary>Чем платят. Раньше стоял булев «золото или энергия»
            /// — то есть витрина знала ровно две валюты в лицо, а облик им
            /// рисовала сама, мимо Ценника.</summary>
            public string Currency;
        }

        public SkinShopScreen(ILvnAssets assets)
        {
            _assets = assets;

            ScreenUi.Stretch(this);
            style.backgroundColor = LvnTokens.Scrim;
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            // tap the scrim (not the sheet) to close
            RegisterCallback<ClickEvent>(evt => { if (evt.target == this) Close(); });

            // ── the sheet ────────────────────────────────────────────────────
            // Витрина скинов шире прочих: её содержимое — сетка карточек, и
            // лишний процент по краю режет колонку.
            var sheet = Sheet(sideInset: 4f, topInset: 5f);
            sheet.style.paddingTop = 18;
            sheet.style.paddingBottom = 16;
            sheet.style.paddingLeft = 18;
            sheet.style.paddingRight = 18;

            // ── top bar: back ‹ + title + currency pill ─────────────────────
            var topBar = new VisualElement();
            topBar.style.flexDirection = FlexDirection.Row;
            topBar.style.alignItems = Align.Center;
            topBar.style.justifyContent = Justify.SpaceBetween;
            topBar.style.marginBottom = 14;
            sheet.Add(topBar);

            var left = new VisualElement();
            left.style.flexDirection = FlexDirection.Row;
            left.style.alignItems = Align.Center;
            topBar.Add(left);

            var back = new Label("‹");
            back.style.color = LvnTokens.Text;
            back.style.fontSize = 44;
            back.style.marginRight = 12;
            back.style.width = 40;
            back.style.unityTextAlign = TextAnchor.MiddleCenter;
            back.AddManipulator(new Clickable(Close));
            left.Add(back);

            var title = SectionTitle(LvnWords.Of("skinshop.title", "Wardrobe"));
            left.Add(title);

            var balancePill = new VisualElement();
            balancePill.style.flexDirection = FlexDirection.Row;
            balancePill.style.alignItems = Align.Center;
            balancePill.style.paddingLeft = 14; balancePill.style.paddingRight = 14;
            balancePill.style.paddingTop = 7; balancePill.style.paddingBottom = 7;
            balancePill.style.backgroundColor = new Color(0f, 0f, 0f, 0.4f);
            LvnChrome.Round(balancePill, 16f);
            LvnChrome.Border(balancePill, new Color(LvnTokens.Gold.r, LvnTokens.Gold.g, LvnTokens.Gold.b, 0.4f), 1f);
            topBar.Add(balancePill);

            var diamond = LvnIcons.Make(LvnIcon.Gem, 22f, LvnTokens.Gold, 0f, LvnTheme.Current.IconGlow);
            diamond.style.marginRight = 8;
            balancePill.Add(diamond);

            _balanceAmount = new Label(LvnPriceTag.Amount(_gold));
            _balanceAmount.style.color = LvnTokens.Gold;
            _balanceAmount.style.fontSize = 24;
            _balanceAmount.style.unityFontStyleAndWeight = FontStyle.Bold;
            balancePill.Add(_balanceAmount);

            // ── character preview (~38% height) ─────────────────────────────
            _previewBox = new VisualElement();
            _previewBox.style.height = Length.Percent(38f);
            _previewBox.style.backgroundColor = LvnTokens.Bg;
            LvnChrome.Round(_previewBox, LvnTokens.Radius);
            LvnChrome.Border(_previewBox, LvnTokens.Border, 1f);
            _previewBox.style.marginBottom = 12;
            _previewBox.style.overflow = Overflow.Hidden;
            sheet.Add(_previewBox);

            _previewImage = new VisualElement { pickingMode = PickingMode.Ignore };
            ScreenUi.Stretch(_previewImage);
            _previewImage.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            _previewImage.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _previewImage.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _previewImage.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            _previewBox.Add(_previewImage);

            // vignette: a dark band at the bottom so the caption always reads
            var vignette = new VisualElement { pickingMode = PickingMode.Ignore };
            vignette.style.position = Position.Absolute;
            vignette.style.left = 0; vignette.style.right = 0; vignette.style.bottom = 0;
            vignette.style.height = Length.Percent(42f);
            vignette.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            _previewBox.Add(vignette);

            var caption = new VisualElement { pickingMode = PickingMode.Ignore };
            caption.style.position = Position.Absolute;
            caption.style.left = 18; caption.style.bottom = 16;
            _previewBox.Add(caption);

            _previewName = new Label();
            _previewName.style.color = LvnTokens.Text;
            _previewName.style.fontSize = 34;
            _previewName.style.unityFontStyleAndWeight = FontStyle.Bold;
            caption.Add(_previewName);

            _previewWearing = new Label();
            _previewWearing.style.color = LvnTokens.TextDim;
            _previewWearing.style.fontSize = 18;
            _previewWearing.style.marginTop = 2;
            caption.Add(_previewWearing);

            // ── character selector: circular avatar chips ───────────────────
            _avatars = new VisualElement();
            _avatars.style.flexDirection = FlexDirection.Row;
            _avatars.style.alignItems = Align.Center;
            _avatars.style.marginBottom = 12;
            sheet.Add(_avatars);

            // ── category tabs ───────────────────────────────────────────────
            _tabs = new VisualElement();
            _tabs.style.flexDirection = FlexDirection.Row;
            _tabs.style.flexWrap = Wrap.Wrap;
            _tabs.style.marginBottom = 12;
            sheet.Add(_tabs);

            // ── item grid ───────────────────────────────────────────────────
            _grid = new ScrollView(ScrollViewMode.Vertical);
            _grid.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            _grid.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _grid.style.flexGrow = 1;
            _grid.contentContainer.style.flexDirection = FlexDirection.Row;
            _grid.contentContainer.style.flexWrap = Wrap.Wrap;
            _grid.contentContainer.style.justifyContent = Justify.SpaceBetween;
            sheet.Add(_grid);

            var close = new Button(Close) { text = LvnWords.Of("common.close", "Close") };
            close.style.fontSize = 26;
            close.style.marginTop = 12;
            close.style.paddingTop = 12;
            close.style.paddingBottom = 12;
            close.style.color = LvnTokens.Text;
            close.style.backgroundColor = LvnTokens.Faint;
            LvnChrome.ClearBorder(close);
            LvnChrome.Round(close, LvnTokens.RadiusSm);
            sheet.Add(close);

            SeedDemo();
            Rebuild();
        }

        // ── demo catalog ────────────────────────────────────────────────────
        private void SeedDemo()
        {
            _chars.Clear();
            _chars.Add(new Character { Name = "Алиса", Preview = "/content/cards/card0.png" });
            _chars.Add(new Character { Name = "Алина", Preview = "/content/cards/card1.png" });
            _chars.Add(new Character { Name = "Ева", Preview = "/content/cards/card2.png" });
            _chars.Add(new Character { Name = "Мира", Preview = "/content/cards/card3.png" });

            _skins.Clear();
            // per category: a themed set of six skins, mixed states (1 equipped,
            // 2 owned, 3 for sale) so the shop reads like a gacha wardrobe.
            string[][] namesByCat =
            {
                new[] { "Локоны", "Каре", "Высокий пучок", "Длинные волны", "Пикси", "Косы короны" },
                new[] { "Бальное платье", "Сарафан", "Готический наряд", "Летний костюм", "Вечернее платье", "Мантия звёзд" },
                new[] { "Жемчуг", "Серьги-кольца", "Диадема", "Чокер", "Веер", "Крылья феи" },
                new[] { "Бальный зал", "Сад роз", "Ночной город", "Библиотека", "Пляж на закате", "Звёздный дворец" },
            };
            var states = new[]
            {
                SkinState.Equipped, SkinState.Owned, SkinState.Owned,
                SkinState.ForSale, SkinState.ForSale, SkinState.ForSale,
            };
            int[] prices = { 0, 0, 0, 250, 480, 3 };
            // Валюты берём у кошелька игрока: витрина показывает НАСТОЯЩИЕ
            // деньги этой новеллы, а не пару, зашитую в движок.
            var purse = new List<string>(LvnWallet.Balances.Keys);
            string soft = purse.Count > 0 ? purse[0] : null;
            _demoCurrency = soft;
            string hard = purse.Count > 1 ? purse[1] : soft;

            for (int c = 0; c < _chars.Count; c++)
            {
                for (int k = 0; k < _categories.Length; k++)
                {
                    var list = new List<Skin>();
                    var names = namesByCat[k];
                    for (int i = 0; i < 6; i++)
                    {
                        list.Add(new Skin
                        {
                            Name = names[i],
                            Thumb = $"/content/cards/card{i % 4}.png",
                            State = states[i],
                            Price = prices[i],
                            Currency = i == 5 ? hard : soft, // последний — за вторую валюту
                        });
                    }
                    _skins[c + ":" + k] = list;
                }
            }
        }

        private List<Skin> Current() =>
            _skins.TryGetValue(_char + ":" + _cat, out var l) ? l : new List<Skin>();

        private string EquippedName(int charIndex, int catIndex)
        {
            if (_skins.TryGetValue(charIndex + ":" + catIndex, out var l))
                foreach (var s in l)
                    if (s.State == SkinState.Equipped) return s.Name;
            return null;
        }

        // ── public API ──────────────────────────────────────────────────────


        /// <summary>(Re)build every dynamic section from the current selection.</summary>
        /// <summary>Слова, шрифт или размеры сменились — перечитать их.</summary>
        public void Redress() => Rebuild();

        public void Rebuild()
        {
            _balanceAmount.text = LvnPriceTag.Amount(_gold);
            RebuildPreview();
            RebuildAvatars();
            RebuildTabs();
            RebuildGrid();
        }

        // ── preview ─────────────────────────────────────────────────────────
        private void RebuildPreview()
        {
            if (_char < 0 || _char >= _chars.Count) return;
            var ch = _chars[_char];
            _previewName.text = ch.Name;
            var worn = EquippedName(_char, _cat);
            _previewWearing.text = worn != null
                ? LvnWords.Of("skinshop.worn", "worn: {0}", worn)
                : LvnWords.Of("skinshop.worn_none", "nothing worn");
            LvnAsync.Fire(ScreenUi.AssignBgAsync(_previewImage, ch.Preview, _assets), "AssignBg");
        }

        // ── character selector ──────────────────────────────────────────────
        private void RebuildAvatars()
        {
            _avatars.Clear();
            for (int i = 0; i < _chars.Count; i++)
            {
                int idx = i;
                bool active = idx == _char;

                var chip = new VisualElement();
                chip.style.width = 64; chip.style.height = 64;
                chip.style.marginRight = 12;
                chip.style.overflow = Overflow.Hidden;
                LvnChrome.Round(chip, 32f);
                chip.style.backgroundColor = LvnTokens.Surface;
                LvnChrome.Edge(chip);
                LvnChrome.Border(chip, active ? LvnTokens.Accent : LvnTokens.Border, active ? 3f : 1f);

                var img = new VisualElement { pickingMode = PickingMode.Ignore };
                ScreenUi.Stretch(img);
                img.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                img.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                img.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                img.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                chip.Add(img);
                LvnAsync.Fire(ScreenUi.AssignBgAsync(img, _chars[idx].Preview, _assets), "AssignBg");
                chip.AddManipulator(new Clickable(() =>
                {
                    if (_char == idx) return;
                    _char = idx;
                    Rebuild();
                }));
                _avatars.Add(chip);
            }
        }

        // ── category tabs ───────────────────────────────────────────────────
        private void RebuildTabs()
        {
            _tabs.Clear();
            for (int i = 0; i < _categories.Length; i++)
            {
                int idx = i;
                bool active = idx == _cat;

                var tab = new Label(_categories[i]);
                tab.style.fontSize = 24;
                tab.style.marginRight = 8;
                tab.style.marginBottom = 6;
                tab.style.paddingLeft = 18; tab.style.paddingRight = 18;
                tab.style.paddingTop = 9; tab.style.paddingBottom = 9;
                tab.style.unityTextAlign = TextAnchor.MiddleCenter;
                LvnStyler.Choice(tab, active, 20f);
                if (active) tab.style.unityFontStyleAndWeight = FontStyle.Bold;

                tab.AddManipulator(new Clickable(() =>
                {
                    if (_cat == idx) return;
                    _cat = idx;
                    Rebuild();
                }));
                _tabs.Add(tab);
            }
        }

        // ── item grid ───────────────────────────────────────────────────────
        // Сетка СВЕРЯЕТСЯ, а не пересобирается: покупка и надевание меняют
        // состояние одной плитки, а прежний Clear() сносил все — вместе с
        // позицией прокрутки и уже загруженными миниатюрами, которые тут же
        // запрашивались снова. Именно этот случай Монтажёр и описывает.
        private void RebuildGrid()
        {
            LvnMontage.Sync(_grid, Current(),
                s => _char + ":" + _cat + ":" + s.Name,
                TileShell,
                DressTile);
        }

        // КАРКАС плитки — то, что не зависит от состояния скина и потому
        // создаётся один раз: рамка, миниатюра (одна загрузка на жизнь плитки),
        // имя и пустая строка состояния.
        private VisualElement TileShell(Skin skin)
        {
            var tile = new VisualElement();
            tile.style.width = Length.Percent(48f);
            tile.style.marginBottom = 14;
            tile.style.backgroundColor = LvnTokens.Surface;
            LvnChrome.Edge(tile);
            LvnChrome.Round(tile, LvnTokens.Radius);
            tile.style.overflow = Overflow.Hidden;
            tile.style.paddingBottom = 12;

            // thumbnail
            var thumbWrap = new VisualElement { viewDataKey = "thumbwrap" };
            thumbWrap.style.height = 170;
            thumbWrap.style.overflow = Overflow.Hidden;
            tile.Add(thumbWrap);

            var thumb = new VisualElement { pickingMode = PickingMode.Ignore };
            ScreenUi.Stretch(thumb);
            thumb.style.backgroundColor = LvnTokens.Bg;
            thumb.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            thumb.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            thumb.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            thumb.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            thumbWrap.Add(thumb);
            LvnAsync.Fire(ScreenUi.AssignBgAsync(thumb, skin.Thumb, _assets), "AssignBg");

            // Лента «Надето» появляется и исчезает при обновлении — держим её
            // место готовым, чтобы не трогать миниатюру под ней.
            var ribbon = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("skinshop.equipped", "Equipped"));
            ribbon.viewDataKey = "ribbon";
            ribbon.style.position = Position.Absolute;
            ribbon.style.top = 10; ribbon.style.left = 10;
            ribbon.style.fontSize = 18;
            ribbon.style.unityFontStyleAndWeight = FontStyle.Bold;
            ribbon.style.color = LvnTokens.OnAccent;
            ribbon.style.backgroundColor = LvnTokens.Accent;
            ribbon.style.paddingLeft = 10; ribbon.style.paddingRight = 10;
            ribbon.style.paddingTop = 4; ribbon.style.paddingBottom = 4;
            LvnChrome.Round(ribbon, 12f);
            thumbWrap.Add(ribbon);

            // name
            var name = new Label(skin.Name) { viewDataKey = "skinname" };
            name.style.color = LvnTokens.Text;
            name.style.fontSize = 22;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.marginTop = 10;
            name.style.marginLeft = 12; name.style.marginRight = 12;
            name.style.whiteSpace = WhiteSpace.Normal;
            tile.Add(name);

            // state row (badge / action)
            var row = new VisualElement { viewDataKey = "staterow" };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 8;
            row.style.marginLeft = 12; row.style.marginRight = 12;
            tile.Add(row);

            DressTile(tile, skin);
            return tile;
        }

        // СОСТОЯНИЕ плитки — всё, что меняется покупкой, надеванием и сменой
        // баланса. Миниатюры это не касается: она уже на месте.
        private void DressTile(VisualElement tile, Skin skin)
        {
            bool forSale = skin.State == SkinState.ForSale;
            bool equipped = skin.State == SkinState.Equipped;

            LvnChrome.Border(tile, equipped ? LvnTokens.Accent : LvnTokens.Border, equipped ? 2f : 1f);
            tile.style.opacity = forSale ? 0.82f : 1f;

            var ribbon = Find(tile, "ribbon");
            if (ribbon != null) ribbon.style.display = equipped ? DisplayStyle.Flex : DisplayStyle.None;

            var name = Find(tile, "skinname") as Label;
            if (name != null) name.text = skin.Name;

            var row = Find(tile, "staterow");
            if (row == null) return;
            row.Clear(); // подписи и кнопки без картинок — их пересборка не видна

            if (equipped)
            {
                var state = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("skinshop.active", "Active"));
                state.style.color = LvnTokens.Accent;
                state.style.fontSize = 18;
                state.style.unityFontStyleAndWeight = FontStyle.Bold;
                row.Add(state);
            }
            else if (skin.State == SkinState.Owned)
            {
                var equip = new Button(() => Equip(skin)) { text = LvnWords.Of("skinshop.equip", "Equip") };
                equip.style.flexGrow = 1;
                equip.style.fontSize = 20;
                equip.style.paddingTop = 8; equip.style.paddingBottom = 8;
                equip.style.color = LvnTokens.Text;
                equip.style.backgroundColor = LvnTokens.Faint;
                LvnChrome.ClearBorder(equip);
                LvnChrome.Round(equip, LvnTokens.RadiusSm);
                row.Add(equip);
            }
            else // for sale
            {
                var chip = new VisualElement();
                chip.style.flexDirection = FlexDirection.Row;
                chip.style.alignItems = Align.Center;
                chip.style.flexGrow = 1;
                chip.style.justifyContent = Justify.Center;
                chip.style.paddingTop = 8; chip.style.paddingBottom = 8;
                chip.style.paddingLeft = 12; chip.style.paddingRight = 12;
                LvnChrome.Round(chip, LvnTokens.RadiusSm);
                // Цвет и значок — у Ценника: он один знает облик валюты.
                var priceColor = LvnPriceTag.Of(skin.Currency).Tint;
                chip.style.backgroundColor = new Color(priceColor.r, priceColor.g, priceColor.b, 0.14f);
                LvnChrome.Border(chip, new Color(priceColor.r, priceColor.g, priceColor.b, 0.5f), 1f);

                chip.Add(LvnPriceTag.Tag(skin.Currency, skin.Price,
                    new LvnPriceTag.Row { FontSize = 20f, IconSize = 19f, Gap = 6f }));

                chip.AddManipulator(new Clickable(() => Buy(skin, tile)));
                row.Add(chip);
            }
        }

        // Поиск части плитки по опознавательному ключу: имя элемента занято
        // Монтажёром под ключ сверки, поэтому части метятся viewDataKey.
        private static VisualElement Find(VisualElement root, string key)
        {
            foreach (var child in root.Children())
            {
                if (child.viewDataKey == key) return child;
                var deep = Find(child, key);
                if (deep != null) return deep;
            }
            return null;
        }

        // ── demo actions ────────────────────────────────────────────────────
        private void Equip(Skin skin)
        {
            foreach (var s in Current())
                if (s.State == SkinState.Equipped) s.State = SkinState.Owned;
            skin.State = SkinState.Equipped;
            Rebuild();
        }

        private void Buy(Skin skin, VisualElement tile)
        {
            // Демо-кошелёк держит только первую валюту; за вторую здесь не
            // покупают — витрина показывает вид, а не ведёт расчёты.
            if (skin.Currency == _demoCurrency && _gold >= skin.Price)
            {
                _gold -= skin.Price;
                skin.State = SkinState.Owned; // bought → now equippable
                Rebuild();
                return;
            }
            // insufficient funds / not buyable: brief nudge on the tile
            tile.style.opacity = 1f;
            tile.schedule.Execute(() => { tile.style.opacity = 0.82f; }).ExecuteLater(180);
        }
    }
}
