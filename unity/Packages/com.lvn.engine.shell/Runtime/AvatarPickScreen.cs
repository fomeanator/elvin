using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ВЫБОР АВАТАРКИ (TR-79) — небольшой экран поверх профиля.
    ///
    /// <para>Сетка лиц: бесплатные выбираются сразу, платные сперва покупаются
    /// — механика та же, что у фонов меню в гардеробе, и цена написана на самой
    /// плитке, а не открывается вторым нажатием.</para>
    ///
    /// <para>Выбор применяется немедленно и виден в шапке: аватар — это то,
    /// чем игрок себя показывает, и ждать «Сохранить» здесь не за чем.</para>
    /// </summary>
    public sealed class AvatarPickScreen : LvnOverlayScreen, ILvnContentAware
    {
        private readonly ILvnAssets _assets;
        private readonly VisualElement _sheet;
        private readonly Label _title;
        private readonly ScrollView _grid;
        private LvnManifest _manifest;
        private string _skin;
        private bool _stageGlass;
        private bool StageDressed => !string.IsNullOrEmpty(_skin);

        /// <summary>Игрок сменил аватар — хозяин перерисовывает шапку.</summary>
        public Action Changed;

        public AvatarPickScreen(ILvnAssets assets)
        {
            _assets = assets;
            var sheet = _sheet = Sheet(sideInset: 7f, topInset: 16f);
            AdoptSheet(sheet);

            _title = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("avatar.title", "Your picture"));
            sheet.Add(ScreenUi.GalleryHeader(Cancel, _title, out var counter));
            counter.style.display = DisplayStyle.None;

            _grid = Lvn.UI.LvnScroll.Vertical();
            _grid.style.flexGrow = 1;
            LvnFlow.Wrap(_grid.contentContainer, Justify.FlexStart);
            sheet.Add(_grid);
        }

        /// <inheritdoc cref="ILvnContentAware.SetContent"/>
        public void SetContent(LvnManifest manifest)
        {
            _manifest = manifest;
            LvnStageKit.TakeSkin(manifest, ref _skin, StageDress);
            Rebuild();
        }

        private void StageDress()
            => _stageGlass = LvnStageKit.DressSheet(_sheet, _skin, _assets, _stageGlass, _title);

        public override void Rebuild()
        {
            if (_grid == null) return;
            _grid.Clear();
            // СВОЁ ЛИЦО ПЕРВЫМ (TR-68): герой, которого игрок собрал сам,
            // важнее готовых картинок — его и предлагаем раньше.
            _grid.Add(SelfTile());
            foreach (var choice in LvnAvatars.Offered(_manifest)) _grid.Add(Tile(choice));
        }

        /// <summary>Плитка «мой облик» — живой портрет героя. Пока портрет не
        /// снят, плитка объясняет это словом, а не показывает пустоту: снимок
        /// делается в гардеробе, и игрока туда надо позвать.</summary>
        private VisualElement SelfTile()
        {
            bool picked = LvnAvatars.Picked == LvnAvatars.SelfId;
            var cell = Cell(picked);

            var art = ScreenUi.Stretch(new VisualElement());
            art.pickingMode = PickingMode.Ignore;
            cell.Add(art);
            // На плитке лицо показываем ВСЕГДА, даже когда выбрана картинка из
            // набора: иначе игрок не видит, на что меняет.
            if (!LvnPortraitFace.Wear(art, _manifest, _assets, force: true))
            {
                art.style.backgroundColor = LvnTokens.SurfaceHi;
                var hint = Lvn.UI.LvnRedress.Bind(new Label(),
                    () => LvnWords.Of("avatar.self_hint", "This novel has no hero to dress"));
                hint.style.whiteSpace = WhiteSpace.Normal;
                hint.style.color = LvnTokens.TextDim;
                hint.style.fontSize = LvnTokens.TextXs;
                hint.style.unityTextAlign = TextAnchor.MiddleCenter;
                hint.style.marginTop = LvnTokens.Space4;
                hint.pickingMode = PickingMode.Ignore;
                cell.Add(hint);
            }

            var caption = Lvn.UI.LvnRedress.Bind(new Label(),
                () => LvnWords.Of("avatar.self", "My look"));
            caption.style.position = Position.Absolute;
            caption.style.left = 0; caption.style.right = 0; caption.style.bottom = 0;
            caption.style.unityTextAlign = TextAnchor.MiddleCenter;
            caption.style.color = LvnTokens.Text;
            caption.style.fontSize = LvnTokens.TextXs;
            caption.style.backgroundColor = LvnTokens.Veil(0.6f);
            LvnAir.PadY(caption, LvnTokens.Hair);
            caption.pickingMode = PickingMode.Ignore;
            cell.Add(caption);

            cell.AddManipulator(new Clickable(() =>
            {
                LvnAvatars.Picked = LvnAvatars.SelfId;
                Changed?.Invoke();
                Rebuild();
            }));
            LvnMotion.Tappable(cell);
            return cell;
        }

        /// <summary>Пустая плитка набора: размер, углы и отметка выбранной.
        /// Общая у своего лица и у картинок — иначе они разъедутся видом.</summary>
        private VisualElement Cell(bool picked)
        {
            var cell = new VisualElement();
            cell.style.width = Length.Percent(31f);
            cell.style.marginRight = Length.Percent(2f);
            cell.style.marginBottom = LvnTokens.Space2;
            float last = 0f;
            cell.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                float w = evt.newRect.width;
                if (w <= 0f || Mathf.Approximately(w, last)) return;
                last = w;
                cell.style.height = w * 1.25f;   // портрет, а не квадрат
            });
            cell.style.backgroundColor = LvnTokens.Surface;
            cell.style.overflow = Overflow.Hidden;
            LvnChrome.Round(cell, LvnTokens.RadiusSm);
            LvnStyler.Chosen(cell, picked, StageDressed ? LvnTokens.Gold : LvnTokens.Accent);
            return cell;
        }

        /// <summary>Плитка лица: картинка, отметка выбранной и цена у платной.</summary>
        private VisualElement Tile(LvnAvatars.Choice c)
        {
            bool owned = LvnAvatars.Owned(c);
            bool picked = LvnAvatars.Picked == c.Id;

            var cell = Cell(picked);

            var art = ScreenUi.Stretch(new VisualElement());
            art.pickingMode = PickingMode.Ignore;
            LvnPicture.Photo(art, c.Url, _assets, cover: true);
            // Некупленное показываем приглушённым: видно, что есть, и видно,
            // что пока не твоё.
            art.style.opacity = owned ? 1f : 0.55f;
            cell.Add(art);

            if (!owned)
            {
                var price = new Label(c.Price + " " + LvnPriceTag.Of(c.Currency).Unit);
                price.style.position = Position.Absolute;
                price.style.left = 0; price.style.right = 0; price.style.bottom = 0;
                price.style.unityTextAlign = TextAnchor.MiddleCenter;
                price.style.color = LvnTokens.Gold;
                price.style.fontSize = LvnTokens.TextXs;
                price.style.backgroundColor = LvnTokens.Veil(0.6f);
                LvnAir.PadY(price, LvnTokens.Hair);
                price.pickingMode = PickingMode.Ignore;
                cell.Add(price);
            }

            cell.AddManipulator(new Clickable(() => LvnAsync.Fire(PickAsync(c), "AvatarPick")));
            LvnMotion.Tappable(cell);
            return cell;
        }

        /// <summary>Выбрать лицо: бесплатное — сразу, платное — после покупки.
        /// Отказ кошелька ничего не меняет: сказать «не хватает» должен он, а
        /// не молчаливо не сработавшая плитка.</summary>
        private async Task PickAsync(LvnAvatars.Choice c)
        {
            if (c == null) return;
            if (!LvnAvatars.Owned(c))
            {
                bool bought = await Lvn.Services.LvnWallet.SpendAsync(
                    c.Currency, c.Price, "avatar", c.Item);
                if (!bought) return;
            }
            LvnAvatars.Picked = c.Id;
            Changed?.Invoke();
            Rebuild();
        }
    }
}
