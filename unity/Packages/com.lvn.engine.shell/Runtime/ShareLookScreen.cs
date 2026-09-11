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
    /// ВИТРИНА ПРОХОЖДЕНИЯ (TR-18) — чужой образ как товар.
    ///
    /// <para>Замысел Ильи: блогерка кидает ссылку на свой акт, подписчицы видят
    /// не абстрактный магазин с паками, а конкретный образ конкретного
    /// человека, которого только что видели в истории, — и покупают «то же, что
    /// у неё». Распространение и продажа одним движением.</para>
    ///
    /// <para>ПРАВИЛО, записанное в самой задаче: показывать чужой образ можно,
    /// ВЫДАВАТЬ его нельзя — только продавать. Иначе один купил, десять
    /// получили даром. Поэтому экран показывает вещи и цену, а прохождение
    /// кладётся в сохранения БЕЗ них: игрок продолжит с того же места, но в
    /// своей одежде, пока не купит.</para>
    /// </summary>
    public sealed class ShareLookScreen : LvnOverlayScreen, ILvnContentAware
    {
        private readonly ILvnAssets _assets;
        private readonly VisualElement _sheet;
        private readonly Label _title;
        private readonly Label _note;
        private readonly VisualElement _list;
        private readonly VisualElement _actions;
        private LvnManifest _manifest;
        private string _skin;
        private bool _stageGlass;
        private LvnLookOffer.Offer _offer;
        private bool StageDressed => !string.IsNullOrEmpty(_skin);

        public ShareLookScreen(ILvnAssets assets)
        {
            _assets = assets;
            var sheet = _sheet = Sheet(sideInset: 8f, topInset: 18f);
            AdoptSheet(sheet);

            _title = Lvn.UI.LvnRedress.Bind(new Label(),
                () => LvnWords.Of("look.title", "Her look"));
            LvnChrome.Heading(_title);
            _title.style.color = LvnTokens.Text;
            _title.style.fontSize = LvnTokens.TextLg;
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            sheet.Add(_title);

            _note = new Label();
            _note.style.color = LvnTokens.TextDim;
            _note.style.fontSize = LvnTokens.TextSm;
            _note.style.whiteSpace = WhiteSpace.Normal;
            _note.style.marginTop = LvnTokens.Hair;
            sheet.Add(_note);

            _list = new VisualElement();
            _list.style.marginTop = LvnTokens.Space3;
            sheet.Add(_list);

            _actions = new VisualElement();
            _actions.style.marginTop = LvnTokens.Space4;
            sheet.Add(_actions);
        }

        /// <inheritdoc cref="ILvnContentAware.SetContent"/>
        public void SetContent(LvnManifest manifest)
        {
            _manifest = manifest;
            LvnStageKit.TakeSkin(manifest, ref _skin, StageDress);
        }

        private void StageDress()
            => _stageGlass = LvnStageKit.DressSheet(_sheet, _skin, _assets, _stageGlass, _title);

        /// <summary>Показать образ из чужого прохождения и довести до решения:
        /// купить набор, продолжить так или уйти.</summary>
        public async Task RunAsync(string entity, IReadOnlyDictionary<string, string> worn, string note)
        {
            _entity = entity;
            _worn = worn;
            _offer = Recount();
            _note.text = string.IsNullOrEmpty(note)
                ? LvnWords.Of("look.note", "This is where she stopped")
                : "«" + note + "»";
            Paint();
            await ShowAsync();
        }

        private void Paint()
        {
            _list.Clear();
            _actions.Clear();
            if (_offer == null) return;
            foreach (var piece in _offer.Pieces) _list.Add(Row(piece));

            if (_offer.Missing > 0 && _offer.Price > 0)
            {
                _actions.Add(Primary(
                    () => LvnWords.Of("look.buy_set", "Buy the whole look — {0}",
                                      _offer.Price + " " + LvnPriceTag.Of(_offer.Currency).Unit),
                    () => LvnAsync.Fire(BuyAllAsync(), "BuyLook")));
            }
            else if (_offer.Missing == 0 && _offer.Pieces.Count > 0)
            {
                var all = Lvn.UI.LvnRedress.Bind(new Label(),
                    () => LvnWords.Of("look.have_all", "You already have this look"));
                all.style.color = LvnTokens.Gold;
                all.style.fontSize = LvnTokens.TextSm;
                all.style.marginBottom = LvnTokens.Space2;
                _actions.Add(all);
            }
            if (_offer.HasGachaOnly)
            {
                var only = Lvn.UI.LvnRedress.Bind(new Label(),
                    () => LvnWords.Of("look.gacha_only", "Something here only comes from the wheel"));
                only.style.color = LvnTokens.TextDim;
                only.style.fontSize = LvnTokens.TextXs;
                only.style.whiteSpace = WhiteSpace.Normal;
                only.style.marginBottom = LvnTokens.Space2;
                _actions.Add(only);
            }
            // Вход в саму историю остаётся штатным: прохождение уже лежит в
            // сохранениях, и «Загрузить» — то место, где игрок его ждёт.
            var where = Lvn.UI.LvnRedress.Bind(new Label(),
                () => LvnWords.Of("share.taken", "The playthrough is in your saves — open «Load»."));
            where.style.color = LvnTokens.TextDim;
            where.style.fontSize = LvnTokens.TextXs;
            where.style.whiteSpace = WhiteSpace.Normal;
            where.style.marginBottom = LvnTokens.Space2;
            _actions.Add(where);
            _actions.Add(Quiet(() => LvnWords.Of("common.close", "Close"), Cancel));
        }

        /// <summary>Строка вещи: что это, почём и есть ли она уже. Своё
        /// помечаем словом, а не отсутствием цены: «бесплатно» и «уже куплено»
        /// — разные вещи.</summary>
        private VisualElement Row(LvnLookOffer.Piece piece)
        {
            var row = ScreenUi.Row(spread: true);
            row.style.marginBottom = LvnTokens.Space2;

            var name = new Label(piece.Name);
            name.style.color = LvnTokens.Text;
            name.style.fontSize = LvnTokens.TextSm;
            name.style.flexGrow = 1;
            row.Add(name);

            var right = Lvn.UI.LvnRedress.Bind(new Label(), () =>
                piece.Owned ? LvnWords.Of("look.owned", "yours")
                : piece.Gacha ? LvnWords.Of("look.from_wheel", "from the wheel")
                : piece.Price + " " + LvnPriceTag.Of(piece.Currency).Unit);
            right.style.color = piece.Owned ? LvnTokens.TextDim : LvnTokens.Gold;
            right.style.fontSize = LvnTokens.TextSm;
            row.Add(right);
            return row;
        }

        /// <summary>
        /// КУПИТЬ НАБОР ОДНОЙ КНОПКОЙ. Вещи покупаются по очереди — у кошелька
        /// нет «купить всё», и заводить его ради витрины значило бы держать две
        /// правды о покупке.
        ///
        /// <para>Отказ на середине НЕ откатываем: купленное остаётся у игрока,
        /// а экран пересобирается — оставшееся видно ценой и докупается. Возврат
        /// части денег выглядел бы честнее, но означал бы, что кошелёк умеет
        /// отменять покупки, а он не умеет.</para>
        /// </summary>
        private async Task BuyAllAsync()
        {
            if (_offer == null) return;
            foreach (var piece in _offer.Pieces)
            {
                if (piece.Owned || piece.Gacha || piece.Price <= 0) continue;
                bool ok = await Lvn.Services.LvnWallet.SpendAsync(
                    piece.Currency, piece.Price, "share_look", piece.Sku);
                if (!ok) break;   // не хватило — остальное покажем ценой
            }
            if (!IsOpen) return;
            _offer = Recount();
            Paint();
        }

        /// <summary>Пересчитать предложение по ТЕКУЩЕМУ владению: после покупки
        /// часть вещей стала своей, и цена набора обязана это учесть.</summary>
        private LvnLookOffer.Offer Recount()
            => LvnLookOffer.Build(_entity, _worn, _manifest, sku => Lvn.Services.LvnWallet.Has(sku));

        private string _entity;
        private IReadOnlyDictionary<string, string> _worn;

        private VisualElement Primary(Func<string> text, Action onTap)
            => LvnStageKit.SheetPrimary(text, onTap, _skin, _assets);

        private static VisualElement Quiet(Func<string> text, Action onTap)
            => LvnStageKit.SheetQuiet(text, onTap);
    }
}
