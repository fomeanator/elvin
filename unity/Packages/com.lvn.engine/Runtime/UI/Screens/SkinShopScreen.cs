using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
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
    /// the real catalog + wallet wiring lands later (see <see cref="WardrobeScreen"/>
    /// for the live, manifest-driven equivalent).
    /// </summary>
    public sealed class SkinShopScreen : VisualElement
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

        private TaskCompletionSource<bool> _tcs;
        private bool _open;

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
            public bool Energy; // false → gold (◆), true → energy (⚡)
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
            var sheet = new VisualElement();
            sheet.style.position = Position.Absolute;
            sheet.style.left = Length.Percent(4f);
            sheet.style.right = Length.Percent(4f);
            sheet.style.top = Length.Percent(5f);
            sheet.style.bottom = Length.Percent(5f);
            sheet.style.backgroundColor = LvnTokens.PanelBg;
            Round(sheet, LvnTokens.Radius + 4f);
            Border(sheet, LvnTokens.Border, 1f);
            sheet.style.paddingTop = 18;
            sheet.style.paddingBottom = 16;
            sheet.style.paddingLeft = 18;
            sheet.style.paddingRight = 18;
            Add(sheet);

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

            var title = new Label("Гардероб");
            title.style.color = LvnTokens.Text;
            title.style.fontSize = 40;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            left.Add(title);

            var balancePill = new VisualElement();
            balancePill.style.flexDirection = FlexDirection.Row;
            balancePill.style.alignItems = Align.Center;
            balancePill.style.paddingLeft = 14; balancePill.style.paddingRight = 14;
            balancePill.style.paddingTop = 7; balancePill.style.paddingBottom = 7;
            balancePill.style.backgroundColor = new Color(0f, 0f, 0f, 0.4f);
            Round(balancePill, 16f);
            Border(balancePill, new Color(LvnTokens.Gold.r, LvnTokens.Gold.g, LvnTokens.Gold.b, 0.4f), 1f);
            topBar.Add(balancePill);

            var diamond = new Label("◆");
            diamond.style.color = LvnTokens.Gold;
            diamond.style.fontSize = 24;
            diamond.style.marginRight = 8;
            balancePill.Add(diamond);

            _balanceAmount = new Label(_gold.ToString("N0"));
            _balanceAmount.style.color = LvnTokens.Gold;
            _balanceAmount.style.fontSize = 24;
            _balanceAmount.style.unityFontStyleAndWeight = FontStyle.Bold;
            balancePill.Add(_balanceAmount);

            // ── character preview (~38% height) ─────────────────────────────
            _previewBox = new VisualElement();
            _previewBox.style.height = Length.Percent(38f);
            _previewBox.style.backgroundColor = LvnTokens.Bg;
            Round(_previewBox, LvnTokens.Radius);
            Border(_previewBox, LvnTokens.Border, 1f);
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
            _grid.style.flexGrow = 1;
            _grid.contentContainer.style.flexDirection = FlexDirection.Row;
            _grid.contentContainer.style.flexWrap = Wrap.Wrap;
            _grid.contentContainer.style.justifyContent = Justify.SpaceBetween;
            sheet.Add(_grid);

            var close = new Button(Close) { text = "Закрыть" };
            close.style.fontSize = 26;
            close.style.marginTop = 12;
            close.style.paddingTop = 12;
            close.style.paddingBottom = 12;
            close.style.color = LvnTokens.Text;
            close.style.backgroundColor = LvnTokens.Faint;
            ClearBorder(close);
            Round(close, LvnTokens.RadiusSm);
            sheet.Add(close);

            SeedDemo();
            Rebuild();
        }

        // ── demo catalog ────────────────────────────────────────────────────
        private void SeedDemo()
        {
            _chars.Clear();
            _chars.Add(new Character { Name = "Виктория", Preview = "/content/cards/card0.png" });
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
                            Energy = i == 5, // the last for-sale item is priced in energy
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

        /// <summary>Open the shop: fade in and resolve when the player closes it.
        /// Mirrors <see cref="StoreScreen.ShowAsync"/> — the fade-in can be
        /// cancelled by <see cref="Hide"/>.</summary>
        public async Task ShowAsync(CancellationToken ct = default)
        {
            if (_open) return;
            _open = true;
            Rebuild();
            style.display = DisplayStyle.Flex;
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.25f, ct);
            // Hide() during the fade-in must cancel the open, not park this await
            // on a _tcs nobody will ever resolve.
            if (!_open) return;

            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            try { await _tcs.Task; }
            finally
            {
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

        private void Close() => _tcs?.TrySetResult(true);

        /// <summary>(Re)build every dynamic section from the current selection.</summary>
        public void Rebuild()
        {
            _balanceAmount.text = _gold.ToString("N0");
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
            _previewWearing.text = worn != null ? "надето: " + worn : "ничего не надето";
            _ = ScreenUi.AssignBgAsync(_previewImage, ch.Preview, _assets);
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
                Round(chip, 32f);
                chip.style.backgroundColor = LvnTokens.Surface;
                Border(chip, active ? LvnTokens.Accent : LvnTokens.Border, active ? 3f : 1f);

                var img = new VisualElement { pickingMode = PickingMode.Ignore };
                ScreenUi.Stretch(img);
                img.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                img.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                img.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                img.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                chip.Add(img);
                _ = ScreenUi.AssignBgAsync(img, _chars[idx].Preview, _assets);

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
                Round(tab, 20f);
                tab.style.backgroundColor = active ? LvnTokens.Accent : LvnTokens.Faint;
                tab.style.color = active ? LvnTokens.OnAccent : LvnTokens.Text;
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
        private void RebuildGrid()
        {
            _grid.Clear();
            var skins = Current();
            foreach (var s in skins) _grid.Add(Tile(s));
        }

        private VisualElement Tile(Skin skin)
        {
            bool forSale = skin.State == SkinState.ForSale;
            bool equipped = skin.State == SkinState.Equipped;

            var tile = new VisualElement();
            tile.style.width = Length.Percent(48f);
            tile.style.marginBottom = 14;
            tile.style.backgroundColor = LvnTokens.Surface;
            Round(tile, LvnTokens.Radius);
            Border(tile, equipped ? LvnTokens.Accent : LvnTokens.Border, equipped ? 2f : 1f);
            tile.style.overflow = Overflow.Hidden;
            tile.style.paddingBottom = 12;
            if (forSale) tile.style.opacity = 0.82f;

            // thumbnail
            var thumbWrap = new VisualElement();
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
            _ = ScreenUi.AssignBgAsync(thumb, skin.Thumb, _assets);

            // "✓ Надето" ribbon over the thumbnail
            if (equipped)
            {
                var ribbon = new Label("✓ Надето");
                ribbon.style.position = Position.Absolute;
                ribbon.style.top = 10; ribbon.style.left = 10;
                ribbon.style.fontSize = 18;
                ribbon.style.unityFontStyleAndWeight = FontStyle.Bold;
                ribbon.style.color = LvnTokens.OnAccent;
                ribbon.style.backgroundColor = LvnTokens.Accent;
                ribbon.style.paddingLeft = 10; ribbon.style.paddingRight = 10;
                ribbon.style.paddingTop = 4; ribbon.style.paddingBottom = 4;
                Round(ribbon, 12f);
                thumbWrap.Add(ribbon);
            }

            // name
            var name = new Label(skin.Name);
            name.style.color = LvnTokens.Text;
            name.style.fontSize = 22;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.marginTop = 10;
            name.style.marginLeft = 12; name.style.marginRight = 12;
            name.style.whiteSpace = WhiteSpace.Normal;
            tile.Add(name);

            // state row (badge / action)
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 8;
            row.style.marginLeft = 12; row.style.marginRight = 12;
            tile.Add(row);

            if (equipped)
            {
                var state = new Label("Активно");
                state.style.color = LvnTokens.Accent;
                state.style.fontSize = 18;
                state.style.unityFontStyleAndWeight = FontStyle.Bold;
                row.Add(state);
            }
            else if (skin.State == SkinState.Owned)
            {
                var equip = new Button(() => Equip(skin)) { text = "Надеть" };
                equip.style.flexGrow = 1;
                equip.style.fontSize = 20;
                equip.style.paddingTop = 8; equip.style.paddingBottom = 8;
                equip.style.color = LvnTokens.Text;
                equip.style.backgroundColor = LvnTokens.Faint;
                ClearBorder(equip);
                Round(equip, LvnTokens.RadiusSm);
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
                Round(chip, LvnTokens.RadiusSm);
                var priceColor = skin.Energy ? LvnTokens.Accent : LvnTokens.Gold;
                chip.style.backgroundColor = new Color(priceColor.r, priceColor.g, priceColor.b, 0.14f);
                Border(chip, new Color(priceColor.r, priceColor.g, priceColor.b, 0.5f), 1f);

                var glyph = new Label(skin.Energy ? "⚡" : "◆");
                glyph.style.color = priceColor;
                glyph.style.fontSize = 20;
                glyph.style.marginRight = 6;
                chip.Add(glyph);

                var price = new Label(skin.Price.ToString("N0"));
                price.style.color = priceColor;
                price.style.fontSize = 20;
                price.style.unityFontStyleAndWeight = FontStyle.Bold;
                chip.Add(price);

                chip.AddManipulator(new Clickable(() => Buy(skin, tile)));
                row.Add(chip);
            }

            return tile;
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
            // Energy items aren't demo-purchasable here; gold ones spend the mirror.
            if (!skin.Energy && _gold >= skin.Price)
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

        // ── helpers (mirrors StoreScreen) ───────────────────────────────────
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

        private static void ClearBorder(VisualElement el)
        {
            el.style.borderTopWidth = 0; el.style.borderBottomWidth = 0;
            el.style.borderLeftWidth = 0; el.style.borderRightWidth = 0;
        }
    }
}
