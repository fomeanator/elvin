using System;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>Preview a face, then explicitly buy/set it. The wallet owns
    /// purchases; LvnAvatars owns the durable selection for this account.</summary>
    public sealed class AvatarPickScreen : LvnOverlayScreen, ILvnContentAware
    {
        private readonly ILvnAssets _assets;
        private readonly VisualElement _sheet;
        private readonly Label _title, _message;
        private readonly ScrollView _grid;
        private readonly Button _apply;
        private LvnManifest _manifest;
        private string _skin, _selected;
        private bool _stageGlass, _busy;
        public Action Changed;

        public AvatarPickScreen(ILvnAssets assets)
        {
            _assets = assets;
            name = "avatar-screen";
            _sheet = Sheet(sideInset: 6f, topInset: 10f);
            AdoptSheet(_sheet);
            _title = LvnRedress.Bind(new Label(), () => LvnWords.Of("avatar.title", "Your picture"));
            var header = ScreenUi.GalleryHeader(Cancel, _title, out var counter);
            counter.style.display = DisplayStyle.None;
            LvnStyler.IconSlot(header.Q<Button>(), LvnStageKit.D(44f));
            _title.style.fontSize = LvnTokens.TextDisplay;
            _sheet.Add(header);
            _grid = LvnScroll.Vertical();
            _grid.style.flexGrow = 1;
            _grid.style.minHeight = 0;
            LvnFlow.Wrap(_grid.contentContainer, Justify.FlexStart);
            _sheet.Add(_grid);
            _message = new Label { name = "avatar-message", pickingMode = PickingMode.Ignore };
            _message.style.whiteSpace = WhiteSpace.Normal;
            _message.style.color = LvnTokens.Gold;
            _message.style.fontSize = LvnTokens.TextBase;
            _message.style.unityTextAlign = TextAnchor.MiddleCenter;
            _message.style.marginTop = LvnTokens.Space2;
            _sheet.Add(_message);
            _apply = new Button(() => LvnAsync.Fire(ApplyAsync(), "AvatarPick")) { name = "avatar-apply" };
            LvnStageKit.PlateButton(_apply, primary: true);
            _apply.style.minHeight = LvnStageKit.D(52f);
            _apply.style.flexShrink = 0;
            _apply.style.fontSize = LvnTokens.TextLg;
            _apply.style.whiteSpace = WhiteSpace.Normal;
            LvnAir.Pad(_apply, LvnTokens.Space3, LvnTokens.Space2);
            _sheet.Add(_apply);
            LvnLeash.WhileOnScreen(this, () => LvnWallet.Changed += WalletChanged,
                () => LvnWallet.Changed -= WalletChanged);
        }

        public void SetContent(LvnManifest manifest)
        {
            _manifest = manifest;
            _selected = Effective;
            LvnStageKit.TakeSkin(manifest, ref _skin, StageDress);
            Rebuild();
        }

        private void StageDress()
            => _stageGlass = LvnStageKit.DressSheet(_sheet, _skin, _assets, _stageGlass, _title);

        private void WalletChanged() { Rebuild(); Changed?.Invoke(); }

        public override void Rebuild()
        {
            if (_grid == null) return;
            var offset = _grid.scrollOffset;
            _grid.Clear();
            if (LvnHeroPortrait.Layers(_manifest) != null)
                _grid.Add(Tile(LvnAvatars.SelfId, null, null));
            foreach (var choice in LvnAvatars.Offered(_manifest))
                _grid.Add(Tile(choice.Id, choice.Url, choice));
            _grid.scrollOffset = offset;
            PaintAction();
        }

        private VisualElement Tile(string id, string url, LvnAvatars.Choice choice)
        {
            var cell = new Button(() => Select(id)) { name = "avatar-" + id };
            cell.style.width = Length.Percent(31f);
            cell.style.marginRight = Length.Percent(2f);
            cell.style.marginBottom = LvnTokens.Space2;
            LvnAir.Pad(cell, 0f, 0f);
            cell.style.flexShrink = 0;
            cell.style.overflow = Overflow.Hidden;
            float last = 0f;
            cell.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                float width = evt.newRect.width;
                if (width <= 0 || Mathf.Approximately(width, last)) return;
                last = width;
                cell.style.height = width + LvnStageKit.D(26f);
            });
            LvnChrome.Round(cell, LvnTokens.RadiusSm);
            LvnStyler.Chosen(cell, _selected == id, LvnTokens.Gold);
            var art = new VisualElement { name = "avatar-art", pickingMode = PickingMode.Ignore };
            art.style.flexGrow = 1;
            art.style.minHeight = 0;
            art.style.alignSelf = Align.Stretch;
            LvnPortraitFace.Show(art, url, _manifest, _assets,
                forceSelf: id == LvnAvatars.SelfId, staticOnly: choice != null);
            cell.Add(art);
            bool current = Effective == id && (choice == null || LvnAvatars.Owned(choice));
            string caption = current ? LvnWords.Of("avatar.current", "Selected")
                : choice == null ? LvnWords.Of("avatar.self", "My look")
                : LvnAvatars.Owned(choice) ? LvnWords.Of("avatar.owned", "Available")
                : LvnAvatars.GachaOnly(choice) ? LvnWords.Of("skin.get_gacha", "Drops from spins")
                : LvnPriceTag.Full(choice.Currency, choice.Price);
            var label = new Label(caption) { pickingMode = PickingMode.Ignore };
            label.style.color = LvnTokens.Gold;
            label.style.fontSize = LvnTokens.TextSm;
            label.style.height = LvnStageKit.D(26f);
            label.style.flexShrink = 0;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.backgroundColor = LvnTokens.Surface;
            cell.Add(label);
            cell.SetEnabled(!_busy);
            return cell;
        }

        private void Select(string id)
        {
            if (_busy) return;
            _selected = id;
            _message.text = "";
            foreach (var cell in _grid.Children())
                LvnStyler.Chosen(cell, cell.name == "avatar-" + id, LvnTokens.Gold);
            PaintAction();
        }

        /// <summary>Что показано сейчас: пустой выбор — это «Мой облик»
        /// (<see cref="LvnAvatars.ShowsSelf"/>), и набор обязан отмечать его,
        /// а не оставлять игрока без выбранной плитки.</summary>
        private static string Effective => LvnAvatars.ShowsSelf ? LvnAvatars.SelfId : LvnAvatars.Picked;

        private void PaintAction()
        {
            var choice = LvnAvatars.Offered(_manifest).Find(c => c.Id == _selected);
            bool valid = choice != null || (_selected == LvnAvatars.SelfId && LvnHeroPortrait.Layers(_manifest) != null);
            bool owned = choice == null || LvnAvatars.Owned(choice);
            bool gachaOnly = !owned && LvnAvatars.GachaOnly(choice);
            bool current = valid && owned && _selected == Effective;
            _apply.text = _busy ? LvnWords.Of("avatar.applying", "Applying…")
                : current ? LvnWords.Of("avatar.current", "Selected")
                : owned ? LvnWords.Of("avatar.apply", "Set picture")
                : gachaOnly ? LvnWords.Of("skin.get_gacha", "Drops from spins")
                : LvnWords.Of("avatar.buy", "Buy and set · {0}", LvnPriceTag.Full(choice.Currency, choice.Price));
            _apply.SetEnabled(valid && !current && !_busy && !gachaOnly);
        }

        internal async Task ApplyAsync()
        {
            if (_busy || string.IsNullOrEmpty(_selected)) return;
            _busy = true;
            Rebuild();
            try
            {
                bool applied = await LvnAvatars.ChooseAsync(_manifest, _selected);
                _message.text = applied ? ""
                    : LvnWords.Of("avatar.failed", "Picture not set. Check your balance and try again.");
                if (applied) Changed?.Invoke();
            }
            finally { _busy = false; Rebuild(); }
        }
    }
}
