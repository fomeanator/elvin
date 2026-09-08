using System.Globalization;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// МАГАЗИН В ОБЛИКЕ «СЦЕНА» — столбик из тех же деталей, что и главная.
    ///
    /// <para>Первый заход делал магазин «листом справа»: тёмная подложка,
    /// пилюли вкладок темы, карточки темы в рамке-по-проценту. Рядом с главной,
    /// собранной из рисованных панелей, это читалось как другое приложение,
    /// а карточки без ширины легли в ряд (скрин Ильи 08.09: «перенести
    /// элементы дизайна с главной, чтобы один стиль был, переделай
    /// полностью»). Здесь подложки нет вовсе — детали стоят на полотне, как
    /// на главной: панель облика на каждый пакет (плашка-заголовок, сумма,
    /// бонус, нарисованная кнопка с ценой), награда за рекламу той же
    /// кнопкой <c>adv.png</c>, вкладки — словами-плашками, без пилюль.</para>
    ///
    /// <para>Размеры — в dp макета через один множитель, как у главной:
    /// столбик 257 dp у правого края (15 dp), панель 200×124 dp, прижатая
    /// вправо. Проценты здесь запрещены: нарисованную рамку нельзя тянуть,
    /// а ширина столбика известна заранее.</para>
    /// </summary>
    public sealed partial class PackShopScreen
    {
        private static float D(float dp) => LvnStageKit.D(dp);
        private static T At<T>(T el, float x, float y, float w, float h) where T : VisualElement
            => LvnStageKit.At(el, x, y, w, h);
        private string SkinUrl(string file) => LvnStageKit.SkinUrl(_skin, file);

        private VisualElement _sheet, _header;
        private bool _dressedApplied;

        /// <summary>Домашняя полоса телефона в макете — как у главной
        /// (BrowseHub.Stage): низ столбика считается от неё.</summary>
        private const float HomeBarDp = 34f;

        /// <summary>Переодеть вкладку в столбик витрины: подложка снимается,
        /// шапка «пополнить кошелёк / Магазин» прячется (слово «Магазин»
        /// уже стоит на ленте), столбик встаёт на место панелей главной.</summary>
        private void DressAsStageColumn()
        {
            if (_sheet == null) return;
            style.backgroundColor = Color.clear;
            pickingMode = PickingMode.Ignore;
            var s = _sheet;
            s.style.position = Position.Absolute;
            s.style.left = StyleKeyword.Auto;
            s.style.right = D(15f);
            s.style.width = D(257f);
            s.style.top = D(70f);
            s.style.bottom = D(117f);
            s.style.paddingLeft = 0; s.style.paddingRight = 0;
            s.style.paddingTop = 0; s.style.paddingBottom = 0;
            s.style.backgroundColor = Color.clear;
            LvnChrome.ClearBorder(s);
            s.style.alignItems = Align.FlexEnd;
            // Вырезы устройства: шапка опускается под чёлку, лента растёт на
            // домашнюю полосу — столбик идёт за ними, как столбик главной.
            s.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                s.style.top = D(70f) + ScreenUi.SafeTop(s);
                s.style.bottom = D(117f - HomeBarDp) + Mathf.Max(LvnEdges.Bottom(s), D(HomeBarDp));
            });
            if (_header != null) _header.style.display = DisplayStyle.None;
            _tabsRow.style.flexDirection = FlexDirection.Row;
            _tabsRow.style.flexWrap = Wrap.NoWrap;
            _tabsRow.style.justifyContent = Justify.FlexEnd;
            _tabsRow.style.marginBottom = D(10f);
            _tabsRow.style.marginRight = D(2f);
            _list.contentContainer.style.alignItems = Align.FlexEnd;
        }

        /// <summary>Вкладки словами: выбранная — золотом, остальные приглушены.
        /// Пилюли темы на полотне были чужими; слово-плашка — та же типографика,
        /// что у заголовков панелей главной.</summary>
        private void BuildStageTabs()
        {
            _tabsRow.Clear();
            for (int i = 0; i < _tabIds.Count; i++)
            {
                int idx = i;
                bool active = i == _tab;
                var word = LvnStageKit.Text(
                    () => idx < _tabIds.Count ? TabTitle(_tabIds[idx]).ToUpperInvariant() : string.Empty,
                    LvnTokens.TextSm, active ? LvnTokens.Gold : LvnTokens.TextDim, medium: true);
                word.pickingMode = PickingMode.Position;
                word.style.marginLeft = D(14f);
                LvnAir.PadY(word, D(6f));
                word.AddManipulator(new Clickable(() => { _tab = idx; Rebuild(); }));
                LvnMotion.Tappable(word);
                _tabsRow.Add(word);
            }
        }

        /// <summary>Пакет — панель облика: плашка (лента «ПОПУЛЯРНЫЙ» или имя
        /// валюты), сумма со значком, бонус, нарисованная кнопка с ценой.
        /// Геометрия — ровно панели новостей главной (BrowseHub.StagePanel).</summary>
        private VisualElement StagePack(Pack pack)
        {
            var p = new VisualElement();
            p.style.width = D(200f); p.style.height = D(124f);
            p.style.marginRight = D(2f);
            p.style.marginBottom = D(10f);
            p.style.flexShrink = 0;
            p.Add(LvnStageKit.Art(SkinUrl("panel.png"), _assets, 0f, 0f, D(200f), D(124f)));

            string head = pack.Badge == Ribbon.Popular ? LvnWords.Of("shop.popular", "POPULAR")
                        : pack.Badge == Ribbon.Value ? LvnWords.Of("shop.value", "BEST VALUE")
                        : pack.Badge == Ribbon.BestPrice ? LvnWords.Of("shop.best_price", "BEST PRICE")
                        : pack.Grants != null ? LvnWords.Of("shop.story_bundle", "STORY BUNDLE")
                        : TabTitle(pack.Currency);
            var plaque = LvnStageKit.Plaque(() => head);
            At(plaque, 0f, 0f, D(200f), D(28f));
            p.Add(plaque);

            // Сумма по центру панели: набор — заголовком, валюта — числом со
            // значком; и то и другое золотом, как названия на главной.
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            At(row, 0f, D(34f), D(200f), D(28f));
            row.style.alignItems = Align.Center; row.style.justifyContent = Justify.Center;
            VisualElement amount = !string.IsNullOrEmpty(pack.Headline)
                ? LvnStageKit.Text(() => pack.Headline, LvnTokens.TextLg, LvnTokens.Gold, medium: true)
                : LvnPriceTag.Tag(pack.Currency, pack.Amount,
                    new LvnPriceTag.Row { FontSize = LvnTokens.TextXl, TextColor = LvnTokens.Gold, Gap = 8f });
            amount.pickingMode = PickingMode.Ignore;
            row.Add(amount);
            p.Add(row);

            if (!string.IsNullOrEmpty(pack.SubLine))
            {
                var sub = LvnStageKit.Text(() => pack.SubLine, LvnTokens.TextXs, LvnTokens.Silver);
                At(sub, 0f, D(62f), D(200f), D(18f));
                p.Add(sub);
            }
            else if (pack.Bonus > 0)
            {
                var bonus = LvnStageKit.Text(
                    () => LvnWords.Of("shop.bonus", "+{0} bonus", LvnPriceTag.Amount(pack.Bonus)),
                    LvnTokens.TextXs, LvnTokens.Silver);
                At(bonus, 0f, D(62f), D(200f), D(18f));
                p.Add(bonus);
            }

            var buy = StagePriceButton(pack);
            At(buy, D(25f), D(82f), D(150f), D(42f));
            p.Add(buy);
            return p;
        }

        /// <summary>Кнопка с ценой в НАРИСОВАННОЙ рамке панели: сама кнопка
        /// прозрачна и несёт только слово, как кнопка «Открыть» на главной.
        /// Настоящая <see cref="Button"/>, а не плашка: занятость покупки
        /// (LvnBusy) держит кнопку и меняет её подпись.</summary>
        private Button StagePriceButton(Pack pack)
        {
            var b = new Button { text = pack.Price };
            b.style.backgroundColor = Color.clear;
            LvnChrome.ClearBorder(b);
            b.style.marginLeft = 0; b.style.marginRight = 0; b.style.marginTop = 0; b.style.marginBottom = 0;
            b.style.paddingLeft = 0; b.style.paddingRight = 0; b.style.paddingTop = 0;
            b.style.paddingBottom = D(3f);   // подпись чуть выше центра: нижняя грань рамки толще
            b.style.color = LvnTokens.Gold;
            b.style.fontSize = LvnTokens.TextBase;
            b.style.unityTextAlign = TextAnchor.MiddleCenter;
            LvnFonts.Apply(b, LvnFonts.Display);
            b.clicked += () => Buy(b, pack);
            LvnMotion.Tappable(b);
            return b;
        }

        /// <summary>Награда за рекламу — та же кнопка, что на главной
        /// (<c>adv.png</c> с числом): показ через дом рекламы, число с
        /// сервера. Нет площадки или рекламы — кнопки нет.</summary>
        private VisualElement StageAdButton()
        {
            if (!Lvn.Services.LvnAds.Available || string.IsNullOrEmpty(AdPlacement)) return null;
            var st = Lvn.Services.LvnAds.StateOf(AdPlacement);
            if (st == null || st.Amount <= 0) return null;

            var b = new VisualElement();
            b.style.width = D(106f); b.style.height = D(39f);
            b.style.marginRight = D(2f);
            b.style.marginBottom = D(10f);
            b.style.flexShrink = 0;
            b.Add(LvnStageKit.Art(SkinUrl("adv.png"), _assets, 0f, 0f, D(106f), D(39f)));
            var amount = LvnStageKit.Text(null, LvnTokens.TextLg, LvnTokens.Gold, medium: true);
            At(amount, D(45f), D(9f), D(36f), D(22f));
            amount.style.unityTextAlign = TextAnchor.MiddleLeft;
            amount.text = "+" + st.Amount.ToString(CultureInfo.InvariantCulture);
            b.Add(amount);
            b.AddManipulator(new Clickable(() =>
                LvnAsync.Fire(Lvn.Services.LvnAds.WatchAndRewardAsync(AdPlacement), "StageAd")));
            LvnMotion.Tappable(b);
            return b;
        }
    }
}
