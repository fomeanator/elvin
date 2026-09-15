using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>Server-authoritative reel: payment and inventory land before
    /// the animation. Tapping the reel skips directly to the awarded prize.</summary>
    public sealed partial class GachaScreen : LvnOverlayScreen, ILvnContentAware
    {
        private readonly ILvnAssets _assets;
        private readonly VisualElement _sheet, _window, _strip, _actions;
        private readonly Label _title, _status;
        private readonly ScrollView _content;
        /// <summary>КРУГ ЛЕНТЫ — витрина, а не шансы (Илья 15.09: «побольше
        /// редких разнообразных с разным цветом»): клетка на КАЖДЫЙ приз пула
        /// вперемешку с валютными секторами. Шансы считает сервер по весам
        /// секторов и ступеней; лента лишь садится на то, что выпало.</summary>
        private sealed class ReelCell
        {
            public LvnGacha.Sector Sector;
            public LvnGacha.Prize Prize;   // null у валютной клетки
        }
        private readonly List<ReelCell> _cells = new List<ReelCell>();
        private LvnGacha.Status _state;
        private LvnManifest _manifest;
        private string _skin;
        private bool _stageGlass, _spinning, _skipAsked, _closed;
        /// <summary>АВТОКРУТКА ЛЕНТАМИ (TR-105, Илья 15.09): «Авто» крутит
        /// подряд — лента докручивается за 0,5 с, валюта показывается 0,2 с и
        /// идёт следующий ход; «Ещё лента» добавляет дорожку (до трёх), крутки
        /// уходят на сервер разом и каждая лента едет к своему результату;
        /// редкое на любой ленте — церемония и стоп.</summary>
        private bool _auto;
        private TaskCompletionSource<bool> _taken;   // «Забрать» нажали — очередь церемоний идёт дальше
        /// <summary>Дверь в магазин: при нехватке валюты вместо «Крутить» —
        /// «Пополнить» (TR-107). Вешает хозяин витрины.</summary>
        public Func<Task> OpenStore;
        private bool _needTopUp;
        private const int MaxLanes = 3;
        /// <summary>Такт автокрутки (Илья 15.09: «0,6 крутка, 0,4 показываем,
        /// чтобы секунда была»): лента едет 0,6 с, выпавшая клетка держится
        /// подсвеченной 0,4 с — и только потом следующий ход.</summary>
        private const float FastSpinSeconds = 0.6f;
        private const int AutoShowMs = 400;
        /// <summary>«Крутим…» не должно висеть вечно (TR-106): ответ дольше —
        /// считается неудачей, кнопка возвращается.</summary>
        private const int SpinTimeoutMs = 15000;
        private sealed class Lane
        {
            public VisualElement Window, Strip;
            public double Pos;   // позиция барабана в ячейках, только растёт
        }
        private readonly List<Lane> _extraLanes = new List<Lane>();
        /// <summary>БЕСКОНЕЧНЫЙ БАРАБАН (TR-108, Илья 15.09): позиция ленты —
        /// непрерывное число ячеек, только растёт; рисуется по модулю круга
        /// секторов, в окне три круга ячеек. Лента не пересобирается ни при
        /// открытии, ни после приза — каждая крутка едет дальше вперёд и
        /// останавливается на нужном секторе. Остановка и есть открытие.</summary>
        private readonly Lane _main = new Lane();
        private bool _sectorsDirty;
        private const int RenderLaps = 3;
        private const int Visible = 4;
        private const float SpinSeconds = 7f;
        private const int SpinLaps = 6;

        public GachaScreen(ILvnAssets assets)
        {
            _assets = assets;
            name = "gacha-screen";
            _sheet = Sheet(sideInset: 6f, topInset: 10f);
            _title = LvnRedress.Bind(new Label(), () => LvnWords.Of("gacha.title", "Spin"));
            var header = ScreenUi.GalleryHeader(Cancel, _title, out var counter);
            _sheet.Add(header);
            var back = header.Q<Button>();
            LvnStyler.IconSlot(back, LvnStageKit.D(44f));
            back.style.fontSize = LvnTokens.TextXl;
            counter.style.display = DisplayStyle.None;
            _title.style.fontSize = LvnTokens.TextDisplay;
            _title.style.whiteSpace = WhiteSpace.Normal;
            _title.style.minWidth = 0;

            var content = _content = LvnScroll.Vertical();
            content.style.flexGrow = 1;
            content.style.minHeight = 0;
            content.contentContainer.style.flexGrow = 1;
            // Содержимое не центруется по свободному месту: под лентой живёт
            // пул призов, и его уход не должен двигать ленту.
            content.contentContainer.style.justifyContent = Justify.FlexStart;
            _sheet.Add(content);
            _window = new VisualElement { name = "gacha-window" };
            _window.style.overflow = Overflow.Hidden;
            _window.style.height = LvnStageKit.D(144f);
            _window.style.flexShrink = 0;
            LvnChrome.Round(_window, LvnTokens.RadiusSm);
            _window.style.backgroundColor = LvnTokens.Veil(0.35f);
            content.Add(_window);

            _strip = new VisualElement { name = "gacha-strip", pickingMode = PickingMode.Ignore };
            _strip.style.position = Position.Absolute;
            _strip.style.left = 0; _strip.style.top = 0; _strip.style.bottom = 0;
            _strip.style.flexDirection = FlexDirection.Row;
            _window.Add(_strip);
            _main.Strip = _strip;
            AddNeedle(_window);
            _window.RegisterCallback<GeometryChangedEvent>(_ => LayoutStrip());
            _sheet.RegisterCallback<ClickEvent>(_ => { if (_spinning) _skipAsked = true; });

            BuildReward(content);
            _status = new Label { name = "gacha-status" };
            _status.style.color = LvnTokens.TextDim;
            _status.style.fontSize = LvnTokens.TextBase;
            _status.style.unityTextAlign = TextAnchor.MiddleCenter;
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.marginTop = LvnTokens.Space3;
            _status.style.minHeight = LvnTokens.TextBase * 2.9f;
            content.Add(_status);
            // ПУЛ ПРИЗОВ — ПРЯМО В МОДАЛКЕ (Илья 15.09: «призы надо в модалке
            // показывать, без кнопок; когда крутка включается, она вниз уезжает
            // плавно»): плитки гардероба под лентой. На покое видны, на крутке
            // уезжают вниз и гаснут, после — возвращаются.
            _pool = new VisualElement { name = "gacha-pool" };
            _pool.style.flexShrink = 0;
            _pool.style.marginTop = LvnTokens.Space3;
            content.Add(_pool);
            _actions = new VisualElement { name = "gacha-actions" };
            _actions.style.flexShrink = 0;
            _actions.style.marginTop = LvnTokens.Space3;
            // РЯД КНОПОК ДЕРЖИТ ВЫСОТУ ВСЕГДА (TR-103, Илья 15.09): лента в окне
            // отцентрована по свободному месту, и пустой ряд (во время крутки и
            // показа приза) отдавал ей своё место — лента прыгала вниз и
            // обратно. Пусто — место остаётся; строка статуса тоже держит две
            // строки, чтобы длинная подпись не сдвигала ленту.
            _actions.style.minHeight = LvnStageKit.D(52f);
            // Поля кнопки высоту ряда не считают — «на 3–4 пикселя всё равно
            // едет» (Илья). Ряд калибруется сам: с кнопкой запоминает свою
            // фактическую высоту и держит её пустым.
            _actions.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (_actions.childCount == 0) return;
                float h = _actions.resolvedStyle.height;
                if (float.IsNaN(h) || h <= _actionsHeight + 0.5f) return;
                _actionsHeight = h;
                _actions.style.minHeight = h;
            });
            _sheet.Add(_actions);
            RegisterCallback<DetachFromPanelEvent>(_ => StopPresentation());
        }

        public void SetContent(LvnManifest manifest)
        {
            _manifest = manifest;
            LvnStageKit.TakeSkin(manifest, ref _skin, StageDress);
        }
        private void StageDress()
            => _stageGlass = LvnStageKit.DressSheet(_sheet, _skin, _assets, _stageGlass, _title);

        public async Task RunAsync()
        {
            _status.text = LvnWords.Of("boot.loading_data", "loading data…");
            var shown = ShowAsync();
            _state = await LvnGacha.GetAsync();
            if (!_closed) Present(_state);
            await shown;
        }

        internal void Present(LvnGacha.Status state)
        {
            _state = state;
            BuildStrip();
            PaintIdle();
        }

        protected override void OnClosed() => StopPresentation();
        public override void Hide() { StopPresentation(); base.Hide(); }
        private void StopPresentation()
        {
            if (_closed) return;
            _closed = true; _skipAsked = true;
            LvnCancel.Retire(_artCancel);
        }

        // The home entry is a plate with its own size, not a full card frame
        // squeezed around an unconstrained word.
        internal static Button LaunchButton(Action open)
        {
            var button = new Button(open) { name = "stage-spin" };
            LvnStageKit.PlateButton(button, primary: true);
            button.style.width = LvnStageKit.D(146f);
            button.style.minHeight = LvnStageKit.D(44f);
            button.style.flexShrink = 0;
            ScreenUi.Row(button);
            button.style.justifyContent = Justify.Center;
            LvnAir.Pad(button, LvnStageKit.D(12f), LvnStageKit.D(8f));
            var gift = LvnIcons.Make(LvnIcon.Gift, LvnStageKit.D(20f), LvnTokens.Gold);
            gift.style.flexShrink = 0;
            gift.style.marginRight = LvnStageKit.D(8f);
            button.Add(gift);
            var label = LvnRedress.Bind(new Label { name = "gacha-launch-label", pickingMode = PickingMode.Ignore },
                () => LvnWords.Of("gacha.title", "Spin"));
            label.style.fontSize = LvnTokens.TextLg;
            label.style.color = LvnTokens.Gold;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 1;
            label.style.minWidth = 0;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.Add(label);
            LvnMotion.Tappable(button);
            return button;
        }

        /// <summary>Указатель ленты — две стрелки, сверху и снизу, к клетке
        /// между ними (Илья 15.09: «вместо линии стрелочки — так моднее»).</summary>
        private static void AddNeedle(VisualElement window)
        {
            foreach (var down in new[] { true, false })
            {
                var arrow = new LvnTriangle { Tint = LvnTokens.Gold, Points = down ? LvnTriangle.Point.Down : LvnTriangle.Point.Up };
                arrow.style.position = Position.Absolute;
                arrow.style.left = Length.Percent(50f);
                arrow.style.width = LvnStageKit.D(16f);
                arrow.style.height = LvnStageKit.D(10f);
                arrow.style.translate = new Translate(Length.Percent(-50f), 0f);
                if (down) arrow.style.top = 0; else arrow.style.bottom = 0;
                window.Add(arrow);
            }
        }

        private void BuildStrip()
        {
            BuildLap();
            FillStrip(_strip);
            foreach (var lane in _extraLanes) FillStrip(lane.Strip);
            BuildPool();
            _sectorsDirty = false;
            LayoutStrip();
        }

        /// <summary>Собрать круг: призы от бессмертных к обычным, перетасованы
        /// «верх-низ», чтобы соседние клетки были разных ступеней и лента
        /// переливалась; валюта рассыпана между ними поровну по всей длине.</summary>
        private void BuildLap()
        {
            _cells.Clear();
            var sectors = _state?.Sectors;
            if (sectors == null || sectors.Count == 0) return;
            var supers = new List<LvnGacha.Sector>(); var plain = new List<LvnGacha.Sector>();
            foreach (var s in sectors) (s.Super ? supers : plain).Add(s);
            var prizes = new List<LvnGacha.Prize>();
            if (supers.Count > 0 && _state.PrizesLeft != null)
                foreach (var p in _state.PrizesLeft) prizes.Add(DescribePrize(p));
            prizes.Sort((a, b) => LvnRarity.Rank(b.Rarity).CompareTo(LvnRarity.Rank(a.Rarity)));
            var mixed = new List<LvnGacha.Prize>(prizes.Count);
            for (int lo = 0, hi = prizes.Count - 1; lo <= hi; lo++, hi--)
            {
                mixed.Add(prizes[lo]);
                if (lo != hi) mixed.Add(prizes[hi]);
            }
            int a = plain.Count, b = mixed.Count, ia = 0, ib = 0;
            while (ia < a || ib < b)
            {
                bool takePlain = ib >= b || (ia < a && (long)ia * b <= (long)ib * a);
                if (takePlain) _cells.Add(new ReelCell { Sector = plain[ia++] });
                else _cells.Add(new ReelCell { Sector = supers[ib % supers.Count], Prize = mixed[ib++] });
            }
            if (_cells.Count == 0) foreach (var s in sectors) _cells.Add(new ReelCell { Sector = s });
        }

        private void FillStrip(VisualElement strip)
        {
            strip.Clear();
            for (int i = 0; i < _cells.Count * RenderLaps; i++) strip.Add(Cell(_cells[i % _cells.Count]));
        }

        private VisualElement Cell(ReelCell info)
        {
            var sector = info.Sector;
            var cell = new VisualElement { pickingMode = PickingMode.Ignore };
            cell.style.width = CellWidth;
            cell.style.flexShrink = 0;
            cell.style.marginRight = LvnTokens.Space1;
            cell.style.alignItems = Align.Center;
            cell.style.justifyContent = Justify.Center;
            cell.style.backgroundColor = LvnTokens.Surface;
            LvnChrome.Round(cell, LvnTokens.RadiusSm);
            if (sector.Super) LvnChrome.Frame(cell, LvnTokens.RadiusSm, LvnTokens.Gold, 2f);
            if (info.Prize != null || sector.Super)
            {
                // КЛЕТКА-ПРИЗ «КАК НА РУЛЕТКАХ» (TR-109): конкретный приз из пула
                // в цвете своей редкости. В круге есть клетка на каждый приз,
                // все копии круга одинаковы — лента садится ровно на выпавший.
                DressPrizeCell(cell, info.Prize);
                return cell;
            }
            cell.Add(LvnIcons.MakeCurrency(sector.Currency, LvnStageKit.D(32f)));
            var label = new Label(LvnPriceTag.Amount(sector.Amount));
            label.style.color = LvnTokens.Text;
            label.style.fontSize = LvnTokens.TextLg;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.marginTop = LvnTokens.Space1;
            cell.Add(label);
            return cell;
        }

        private VisualElement _pool;
        private bool _poolHidden;
        private int _poolMotion;

        /// <summary>Собрать пул: сетка плиток призов от бессмертных к обычным —
        /// тем же обликом, что гардероб, — и чипы валюты со своими шансами.
        /// Шанс приза — доля «Редкого» × вес его ступени к сумме весов (так
        /// делит сервер).</summary>
        private void BuildPool()
        {
            if (_pool == null) return;
            _pool.Clear();
            if (_state == null) return;
            double total = 0, superW = 0;
            foreach (var s in _state.Sectors) { total += s.Weight; if (s.Super) superW += s.Weight; }
            var palette = _manifest?.ui?.wardrobe?.rarity_colors;
            var left = _state.PrizesLeft ?? new List<LvnGacha.Prize>();
            var prizes = new List<LvnGacha.Prize>(left.Count);
            double weights = 0;
            foreach (var p in left) { var d = DescribePrize(p); prizes.Add(d); weights += d.Weight > 0 ? d.Weight : 1; }
            prizes.Sort((a, b) => LvnRarity.Rank(b.Rarity).CompareTo(LvnRarity.Rank(a.Rarity)));
            var grid = new VisualElement { name = "gacha-pool-grid" };
            LvnFlow.Wrap(grid, Justify.Center);
            foreach (var p in prizes)
            {
                double share = total > 0 && weights > 0 ? superW / total * ((p.Weight > 0 ? p.Weight : 1) / weights) * 100.0 : 0;
                grid.Add(PrizeCard(p, share, palette));
            }
            _pool.Add(grid);
            // Валюта — чипами: значок, сумма, шанс сектора.
            var chips = new VisualElement();
            LvnFlow.Wrap(chips, Justify.Center);
            chips.style.marginTop = LvnTokens.Space2;
            foreach (var s in _state.Sectors)
            {
                if (s.Super) continue;
                var chip = ScreenUi.Row();
                chip.style.backgroundColor = LvnTokens.Surface;
                LvnChrome.Round(chip, LvnTokens.RadiusSm);
                LvnAir.Pad(chip, LvnTokens.Space2, LvnTokens.Space1);
                chip.style.marginRight = LvnTokens.Space1; chip.style.marginBottom = LvnTokens.Space1;
                chip.Add(LvnIcons.MakeCurrency(s.Currency, LvnStageKit.D(18f)));
                var amount = new Label("+" + LvnPriceTag.Amount(s.Amount));
                amount.style.color = LvnTokens.Text; amount.style.marginLeft = LvnTokens.Space1;
                chip.Add(amount);
                var chance = new Label((total > 0 ? s.Weight / total * 100.0 : 0).ToString("0.#") + " %");
                chance.style.color = LvnTokens.TextDim; chance.style.fontSize = LvnTokens.TextSm; chance.style.marginLeft = LvnTokens.Space1;
                chip.Add(chance);
                chips.Add(chip);
            }
            _pool.Add(chips);
        }

        /// <summary>Пул уезжает вниз и гаснет — лента крутится без него.</summary>
        private async Task HidePoolAsync()
        {
            if (_pool == null || _poolHidden) return;
            _poolHidden = true;
            int v = ++_poolMotion;
            float dy = LvnStageKit.D(160f);
            if (!LvnPrefs.ReduceMotion)
                await LvnMotion.PlayAsync(_pool, 280, (el, p) =>
                {
                    if (v != _poolMotion) return;
                    float k = LvnMotion.Settle(p);
                    el.style.opacity = 1f - k;
                    el.style.translate = new Translate(0f, dy * k);
                });
            if (v != _poolMotion) return;
            _pool.style.display = DisplayStyle.None;
        }

        /// <summary>Пул возвращается снизу — на покое призы снова перед глазами.</summary>
        private void ShowPool()
        {
            if (_pool == null || !_poolHidden) return;
            _poolHidden = false;
            int v = ++_poolMotion;
            _pool.style.display = DisplayStyle.Flex;
            float dy = LvnStageKit.D(160f);
            if (LvnPrefs.ReduceMotion)
            {
                _pool.style.opacity = 1f; _pool.style.translate = new Translate(0f, 0f);
                return;
            }
            LvnAsync.Fire(LvnMotion.PlayAsync(_pool, 280, (el, p) =>
            {
                if (v != _poolMotion) return;
                float k = LvnMotion.Settle(p);
                el.style.opacity = k;
                el.style.translate = new Translate(0f, dy * (1f - k));
            }), "GachaPoolShow");
        }

        /// <summary>Плитка приза — та же, что в гардеробе (LvnSkinCard), поверх
        /// неё шанс слева, ценник справа, редкость обликом; тап — крупный показ.</summary>
        private VisualElement PrizeCard(LvnGacha.Prize prize, double chance, IReadOnlyDictionary<string, string> palette)
        {
            var parts = prize.Sku?.Split(':');
            string axis = parts != null && parts.Length == 4 ? parts[2] : null;
            bool backdrop = axis == WardrobeSheet.BackdropAxis;
            var (zoom, ay) = backdrop ? (1f, 0.5f) : LvnWardrobeStage.Framing(axis);
            var card = LvnSkinCard.Make(LvnTokens.RadiusSm, zoom, ay, false, () => prize.Label ?? prize.Sku, LvnTokens.Text);
            if (backdrop) LvnPicture.Fit(card.Art, cover: true);
            card.Card.style.marginRight = LvnTokens.Space1; card.Card.style.marginBottom = LvnTokens.Space1;
            int rank = LvnRarity.Rank(prize.Rarity);
            var color = LvnRarity.ColorOf(prize.Rarity, palette);
            LvnSkinCard.DressRarity(card.Card, rank >= 0 ? color : (Color?)null, LvnTokens.Text);
            if (prize.Price > 0) card.Card.Add(LvnSkinCard.PriceBadge(prize.Currency ?? _state?.SpinCurrency, prize.Price));
            bool owned = LvnWallet.Has(prize.Sku);
            card.Card.Add(LvnSkinCard.Mark(owned ? LvnWords.Of("gacha.owned", "owned") : chance.ToString("0.##") + " %",
                owned ? LvnTokens.Gold : rank >= 0 ? Color.Lerp(color, Color.white, 0.3f) : LvnTokens.Text));
            if (owned) card.Art.style.opacity = 0.75f;
            if (!string.IsNullOrEmpty(prize.Art)) LvnAsync.Fire(PaintCardArtAsync(card, prize.Art), "GachaContentsArt");
            card.Card.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); ShowPrize(prize, chance, palette); });
            LvnMotion.Tappable(card.Card);
            return card.Card;
        }

        /// <summary>Арт плитки: сначала мини-вариант (как в гардеробе), иначе
        /// полный; вешалка уходит, когда картинка легла.</summary>
        private async Task PaintCardArtAsync(LvnSkinCard.Parts card, string url)
        {
            if (_assets == null) return;
            Sprite sprite = null;
            var mini = Lvn.Content.DownloadPolicy.MiniVariant(url);
            try { if (!string.IsNullOrEmpty(mini)) sprite = await _assets.LoadSpriteAsync(mini, _artCancel.Token); }
            catch (System.OperationCanceledException) { return; }
            catch (System.Exception) { /* мини нет — ниже полный */ }
            if (sprite == null)
            {
                try { sprite = await _assets.LoadSpriteAsync(url, _artCancel.Token); }
                catch (System.OperationCanceledException) { return; }
                catch (System.Exception ex) { LvnLog.Warn("[lvn-gacha] арт плитки: " + ex.Message); return; }
            }
            if (_closed || sprite == null) return;
            card.Placeholder.style.display = DisplayStyle.None;
            LvnPicture.Paint(card.Art, sprite, slice: 0);
        }

        /// <summary>КРУПНЫЙ ПОКАЗ ПРИЗА (Илья: «когда кликаешь на элемент, надо
        /// показывать его»): чёрный экран, арт во всю ширину, имя цветом
        /// ступени, под ним редкость, цена и шанс; тап где угодно закрывает.</summary>
        private void ShowPrize(LvnGacha.Prize prize, double chance, IReadOnlyDictionary<string, string> palette)
        {
            var veil = new VisualElement { name = "gacha-prize-view" };
            LvnChrome.Stretch(veil);
            veil.style.backgroundColor = UiColor.WithAlpha(Color.black, 0.96f);
            veil.style.alignItems = Align.Center; veil.style.justifyContent = Justify.Center;
            veil.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            veil.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); veil.RemoveFromHierarchy(); });
            var column = new VisualElement();
            column.style.width = Length.Percent(88f);
            column.style.alignItems = Align.Center;
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.style.alignSelf = Align.Stretch;   // ширина своя — иначе процент от «ничего» (урок TR-102)
            art.style.height = LvnStageKit.D(320f);
            art.style.alignItems = Align.Center; art.style.justifyContent = Justify.Center;
            LvnPicture.Fit(art, cover: false);
            int rank = LvnRarity.Rank(prize.Rarity);
            var color = rank >= 0 ? LvnRarity.ColorOf(prize.Rarity, palette) : LvnTokens.Gold;
            art.Add(LvnIcons.Make(LvnIcon.Gift, LvnStageKit.D(96f), color));
            column.Add(art);
            var name = new Label(prize.Label ?? prize.Sku) { pickingMode = PickingMode.Ignore };
            LvnFonts.Apply(name, LvnFonts.Display);
            name.style.fontSize = LvnTokens.TextXl; name.style.color = color;
            name.style.whiteSpace = WhiteSpace.Normal; name.style.unityTextAlign = TextAnchor.MiddleCenter;
            name.style.alignSelf = Align.Stretch; name.style.marginTop = LvnTokens.Space2;
            column.Add(name);
            var line = new List<string>();
            if (rank >= 0) line.Add(LvnRarity.Word(prize.Rarity));
            if (prize.Price > 0) line.Add(LvnPriceTag.Full(prize.Currency ?? _state?.SpinCurrency, prize.Price));
            line.Add(LvnWords.Of("gacha.chance", "Chance") + " " + chance.ToString("0.##") + " %");
            var meta = new Label(string.Join(" · ", line)) { pickingMode = PickingMode.Ignore };
            meta.style.fontSize = LvnTokens.TextBase; meta.style.color = color;
            meta.style.whiteSpace = WhiteSpace.Normal; meta.style.unityTextAlign = TextAnchor.MiddleCenter;
            meta.style.alignSelf = Align.Stretch; meta.style.marginTop = LvnTokens.Hair;
            column.Add(meta);
            veil.Add(column);
            Add(veil);
            veil.BringToFront();
            if (!string.IsNullOrEmpty(prize.Art) && _assets != null)
                LvnAsync.Fire(PaintArtAsync(art, prize.Art), "GachaPrizeView");
        }

        /// <summary>Одеть клетку призом: фон и рамка цветом редкости, картинка
        /// приза (приезжает), название и цена, если продаётся.</summary>
        private void DressPrizeCell(VisualElement cell, LvnGacha.Prize raw)
        {
            cell.Clear();
            var prize = raw != null ? DescribePrize(raw) : null;
            var palette = _manifest?.ui?.wardrobe?.rarity_colors;
            var color = prize != null && LvnRarity.Rank(prize.Rarity) >= 0 ? LvnRarity.ColorOf(prize.Rarity, palette) : LvnTokens.Gold;
            cell.style.backgroundColor = UiColor.WithAlpha(color, 0.22f);
            LvnChrome.Frame(cell, LvnTokens.RadiusSm, color, 2f);
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.style.width = Length.Percent(100f); art.style.flexGrow = 1; art.style.minHeight = LvnStageKit.D(40f);
            art.style.alignItems = Align.Center; art.style.justifyContent = Justify.Center;
            LvnPicture.Fit(art, cover: false);
            art.Add(LvnIcons.Make(LvnIcon.Gift, LvnStageKit.D(28f), color));
            cell.Add(art);
            var label = new Label(prize?.Label ?? LvnWords.Of("gacha.super", "Rare"));
            label.style.color = color;
            label.style.fontSize = LvnTokens.TextSm;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.marginTop = LvnTokens.Hair;
            cell.Add(label);
            if (prize != null && prize.Price > 0)
            {
                var price = new Label(LvnPriceTag.Amount(prize.Price)) { pickingMode = PickingMode.Ignore };
                price.style.color = LvnTokens.TextDim; price.style.fontSize = LvnTokens.TextMicro;
                price.style.unityTextAlign = TextAnchor.MiddleCenter;
                cell.Add(price);
            }
            if (prize != null && !string.IsNullOrEmpty(prize.Art) && _assets != null)
                LvnAsync.Fire(PaintArtAsync(art, prize.Art), "GachaCellArt");
        }

        /// <summary>Арт в элемент — клетке ленты и крупному показу одинаково:
        /// значок остаётся, если картинка не приехала.</summary>
        private async Task PaintArtAsync(VisualElement art, string url)
        {
            try
            {
                var sprite = await _assets.LoadSpriteAsync(url, _artCancel.Token);
                if (_closed || sprite == null) return;
                art.Clear();
                LvnPicture.Paint(art, sprite, slice: 0);
            }
            catch (System.OperationCanceledException) { /* экран закрыт — рисовать некуда */ }
            catch (System.Exception) { /* клетка останется со значком */ }
        }

        /// <summary>Клетка круга, на которую сядет лента: приз — его клетка,
        /// валюта — ближайшая впереди клетка этого сектора (их в круге
        /// несколько). Приза нет в круге (пул сменился под ногами) — садимся на
        /// ближайшую призовую и одеваем её выпавшим во всех копиях.</summary>
        private int LandingIndex(LvnGacha.Spin spin, Lane lane)
        {
            int n = _cells.Count;
            if (n == 0 || lane == null) return 0;
            int cur = (int)((((long)System.Math.Round(lane.Pos)) % n + n) % n);
            int fallback = -1;
            for (int k = 1; k <= n; k++)
            {
                int i = (cur + k) % n; var c = _cells[i];
                if (spin.Super)
                {
                    if (c.Prize != null && c.Prize.Sku == spin.Prize?.Sku) return i;
                    if (c.Prize != null && fallback < 0) fallback = i;
                }
                else if (c.Sector?.Id == spin.SectorId) return i;
            }
            if (spin.Super && fallback >= 0 && spin.Prize != null)
            {
                for (int lap = 0; lap < RenderLaps; lap++)
                {
                    int idx = lap * n + fallback;
                    if (idx < lane.Strip.childCount) DressPrizeCell(lane.Strip[idx], spin.Prize);
                }
                return fallback;
            }
            return fallback >= 0 ? fallback : cur;
        }

        private float WindowWidth => float.IsNaN(_window.resolvedStyle.width) || _window.resolvedStyle.width <= 0
            ? 640f : _window.resolvedStyle.width;
        private float CellWidth => Mathf.Max(1f, (WindowWidth - LvnTokens.Space1 * Visible) / Visible);
        private int LapLength => Mathf.Max(1, _cells.Count);

        /// <summary>Положить ленту по позиции барабана: под стрелкой — ячейка
        /// среднего круга с номером (позиция mod N).</summary>
        private void Render(Lane lane)
        {
            if (lane?.Strip == null) return;
            int n = LapLength;
            float step = CellWidth + LvnTokens.Space1;
            double o = ((lane.Pos % n) + n) % n * step;
            lane.Strip.style.left = (float)(-(o + n * step) + WindowWidth * 0.5f - CellWidth * 0.5f);
        }

        private void LayoutStrip()
        {
            foreach (var cell in _strip.Children()) cell.style.width = CellWidth;
            Render(_main);
            foreach (var lane in _extraLanes)
            {
                foreach (var cell in lane.Strip.Children()) cell.style.width = CellWidth;
                Render(lane);
            }
        }

        private Button ActionButton(string name, Func<string> caption, Action action)
        {
            var button = LvnRedress.Bind(new Button(action) { name = name }, caption);
            LvnStageKit.PlateButton(button, primary: true);
            button.style.fontSize = LvnTokens.TextXl;
            button.style.whiteSpace = WhiteSpace.Normal;
            button.style.minHeight = LvnStageKit.D(52f);
            button.style.flexShrink = 0;
            LvnAir.Pad(button, LvnTokens.Space3, LvnTokens.Space2);
            // The initiating tap must not bubble into the sheet's skip handler.
            button.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            _actions.Add(button);
            return button;
        }

        private float _actionsHeight;

        private void PaintIdle()
        {
            DismissCeremony();
            RemoveLanes();
            if (_sectorsDirty) BuildStrip();   // секторы изменились (кончились редкие) — только тогда
            _actions.Clear();
            _reward.style.display = DisplayStyle.None;
            _window.style.display = DisplayStyle.Flex;
            ShowPool();
            if (_state == null)
            {
                _status.text = LvnWords.Of("gacha.offline", "Spins need a connection.");
                return;
            }
            _status.text = _state.FreeToday
                ? LvnWords.Of("gacha.free_ready", "Today's free spin is waiting")
                : _state.SpinPrice > 0
                    ? LvnWords.Of("gacha.price", "Next spin: {0}", LvnPriceTag.Full(_state.SpinCurrency, _state.SpinPrice))
                    : LvnWords.Of("gacha.come_back", "Come back tomorrow for a free spin");
            if (_state.Sectors.Count == 0 || (!_state.FreeToday && _state.SpinPrice <= 0)) return;
            // НЕ ХВАТАЕТ — «ПОПОЛНИТЬ» (TR-107): кнопка ведёт в магазин, а не
            // предлагает крутку, которая упрётся в кошелёк.
            bool broke = !_state.FreeToday && _state.SpinPrice > 0
                && (_needTopUp || LvnWallet.Balance(_state.SpinCurrency) < _state.SpinPrice);
            if (broke)
            {
                _needTopUp = false;
                var topUp = ActionButton("gacha-topup", () => LvnWords.Of("gacha.top_up", "Top up"), () =>
                    LvnAsync.Fire(TopUpAsync(), "GachaTopUp"));
                topUp.SetEnabled(OpenStore != null);
                return;
            }
            var button = ActionButton("gacha-spin", () => _state.FreeToday ? LvnWords.Of("gacha.spin", "Spin") : "",
                () => LvnAsync.Fire(SpinAsync(), "GachaSpin"));
            AddAutoButton(button);
            if (!_state.FreeToday)
            {
                // The currency icon carries the unit; long currency names no
                // longer compete with the action for the same line of text.
                button.text = "";
                LvnFlow.Wrap(ScreenUi.Row(button), Justify.Center);
                var label = LvnRedress.Bind(new Label { pickingMode = PickingMode.Ignore }, () => LvnWords.Of("gacha.spin", "Spin"));
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.flexShrink = 0;
                label.style.maxWidth = Length.Percent(100f);
                label.style.color = LvnTokens.Gold;
                label.style.marginRight = LvnTokens.Space3;
                button.Add(label);
                var price = ScreenUi.Row();
                price.pickingMode = PickingMode.Ignore;
                var amount = new Label(LvnPriceTag.Amount(_state.SpinPrice));
                amount.style.color = LvnTokens.Gold;
                price.Add(amount);
                price.Add(LvnIcons.MakeCurrency(_state.SpinCurrency, LvnStageKit.D(20f)));
                button.Add(price);
            }
        }

        private async Task TopUpAsync()
        {
            if (OpenStore == null) return;
            // МАГАЗИН ПОД КРУТКАМИ (Илья 15.09: «конфликт модалок»): магазин —
            // модалка оболочки, а крутки — оверлей поверх корня, и магазин
            // открывался под ними. Крутки закрываются, магазин выходит на свет;
            // назад — кнопкой «Крутка» на главной.
            Close();
            await Task.Yield();
            await OpenStore();
        }

        /// <summary>«Авто» рядом с основной кнопкой.</summary>
        private void AddAutoButton(Button main)
        {
            var auto = ActionButton("gacha-auto", () => LvnWords.Of("gacha.auto", "Auto"),
                () => LvnAsync.Fire(AutoAsync(), "GachaAuto"));
            LvnStageKit.PlateButton(auto, primary: false);
            auto.style.fontSize = LvnTokens.TextLg;
            main.RemoveFromHierarchy();
            auto.RemoveFromHierarchy();
            var row = ScreenUi.Row();
            main.style.flexGrow = 1;
            main.style.flexShrink = 1;
            auto.style.flexShrink = 0;
            auto.style.marginLeft = LvnTokens.Space2;
            row.Add(main);
            row.Add(auto);
            _actions.Add(row);
        }

        /// <summary>Кнопки на время авто: «Ещё лента» (до трёх) и «Стоп».</summary>
        private void PaintAuto()
        {
            _actions.Clear();
            var more = ActionButton("gacha-lane", () => LvnWords.Of("gacha.add_lane", "Add a lane"), () =>
            {
                if (_extraLanes.Count + 1 >= MaxLanes) return;
                AddLane();
                PaintAuto();
            });
            LvnStageKit.PlateButton(more, primary: false);
            more.SetEnabled(_extraLanes.Count + 1 < MaxLanes);
            more.style.opacity = more.enabledSelf ? 1f : 0.4f;
            var stop = ActionButton("gacha-auto-stop", () => LvnWords.Of("gacha.auto_stop", "Stop"), () => _auto = false);
            LvnStageKit.PlateButton(stop, primary: false);
            more.RemoveFromHierarchy(); stop.RemoveFromHierarchy();
            var row = ScreenUi.Row();
            more.style.flexGrow = 1; stop.style.flexShrink = 0; stop.style.marginLeft = LvnTokens.Space2;
            row.Add(more); row.Add(stop);
            _actions.Add(row);
        }

        /// <summary>Ещё одна дорожка под главной: то же окно, та же лента.</summary>
        private void AddLane()
        {
            var lane = new Lane();
            lane.Window = new VisualElement { name = "gacha-window-lane" };
            lane.Window.style.overflow = Overflow.Hidden;
            lane.Window.style.height = LvnStageKit.D(144f);
            lane.Window.style.flexShrink = 0;
            lane.Window.style.marginTop = LvnTokens.Space1;
            LvnChrome.Round(lane.Window, LvnTokens.RadiusSm);
            lane.Window.style.backgroundColor = LvnTokens.Veil(0.35f);
            lane.Strip = new VisualElement { name = "gacha-strip-lane", pickingMode = PickingMode.Ignore };
            lane.Strip.style.position = Position.Absolute;
            lane.Strip.style.left = 0; lane.Strip.style.top = 0; lane.Strip.style.bottom = 0;
            lane.Strip.style.flexDirection = FlexDirection.Row;
            lane.Window.Add(lane.Strip);
            AddNeedle(lane.Window);
            FillStrip(lane.Strip);
            lane.Pos = _main.Pos;
            var host = _content.contentContainer;
            host.Insert(host.IndexOf(_window) + 1 + _extraLanes.Count, lane.Window);
            _extraLanes.Add(lane);
            foreach (var cell in lane.Strip.Children()) cell.style.width = CellWidth;
            ApplyLaneHeights();
            Render(lane);
        }

        /// <summary>Три дорожки не влезали в окно (скрин Ильи 01:0x): с каждой
        /// добавленной лентой все дорожки становятся ниже — одна 144, две по
        /// 112, три по 92 dp, — чтобы ряд кнопок оставался на экране.</summary>
        private void ApplyLaneHeights()
        {
            int lanes = 1 + _extraLanes.Count;
            float h = LvnStageKit.D(lanes >= 3 ? 92f : lanes == 2 ? 112f : 144f);
            _window.style.height = h;
            foreach (var lane in _extraLanes) lane.Window.style.height = h;
        }

        private void RemoveLanes()
        {
            foreach (var lane in _extraLanes) lane.Window.RemoveFromHierarchy();
            _extraLanes.Clear();
            ApplyLaneHeights();
        }

        /// <summary>Ответ сервера с пределом ожидания: висящий запрос — неудача, а не вечное «Крутим…».</summary>
        private static async Task<LvnGacha.Spin> SpinWithTimeoutAsync()
        {
            var spin = LvnGacha.SpinAsync();
            var done = await Task.WhenAny(spin, Task.Delay(SpinTimeoutMs));
            if (done != spin) return new LvnGacha.Spin { Error = "timeout" };
            return await spin;
        }

        private async Task AutoAsync()
        {
            if (_spinning || _closed || _state == null) return;
            _auto = true; _spinning = true;
            PaintAuto();
            _reward.style.display = DisplayStyle.None;
            LvnAsync.Fire(HidePoolAsync(), "GachaPoolHide");
            try
            {
                while (_auto && !_closed && _state != null && (_state.FreeToday || _state.SpinPrice > 0))
                {
                    _skipAsked = false;
                    _status.text = LvnWords.Of("gacha.spinning", "Spinning…");
                    int n = 1 + _extraLanes.Count;
                    var tasks = new List<Task<LvnGacha.Spin>>(n);
                    for (int i = 0; i < n; i++) tasks.Add(SpinWithTimeoutAsync());
                    var spins = await Task.WhenAll(tasks);
                    if (_closed) return;
                    var ok = new List<LvnGacha.Spin>();
                    string error = null;
                    foreach (var sp in spins) { if (string.IsNullOrEmpty(sp.Error)) ok.Add(sp); else error = sp.Error; }
                    if (ok.Count == 0)
                    {
                        _status.text = error == "insufficient_funds"
                            ? LvnWords.Of("gacha.no_funds", "Not enough for a spin")
                            : LvnWords.Of("gacha.failed", "The spin did not go through. Try again.");
                        break;
                    }
                    var last = ok[ok.Count - 1];
                    _state.FreeToday = last.FreeToday;
                    _state.PrizesLeft = last.PrizesLeft;
                    _state.Spins += ok.Count;
                    if (last.PrizesLeft.Count == 0 && _state.Sectors.RemoveAll(s => s.Super) > 0) _sectorsDirty = true;
                    // Каждая лента едет к своему результату — все разом.
                    var rolls = new List<Task>();
                    for (int i = 0; i < ok.Count; i++)
                    {
                        var lane = i == 0 ? _main : (i - 1 < _extraLanes.Count ? _extraLanes[i - 1] : null);
                        if (lane == null) continue;
                        if (ok[i].Super) _sectorsDirty = true;   // приз ушёл из пула — круг пересобрать на покое
                        rolls.Add(RollLaneAsync(lane, LandingIndex(ok[i], lane), 1, FastSpinSeconds));
                    }
                    await Task.WhenAll(rolls);
                    if (_closed) return;
                    var rares = new List<LvnGacha.Spin>();
                    long sum = 0; string cur = null;
                    foreach (var sp in ok)
                    {
                        if (sp.Super) { rares.Add(sp); continue; }
                        sum += sp.Amount; cur ??= sp.Currency;
                    }
                    if (rares.Count == 0)
                        _status.text = LvnWords.Of("gacha.won_currency", "You got: {0}", LvnPriceTag.Full(cur, sum));
                    // ПОКАЗ ВЫПАВШЕГО: клетки под стрелками горят 0,4 с — в каждой
                    // ленте своя; редкое тоже показывается в ленте, а уже потом церемония.
                    var shows = new List<Task>();
                    for (int i = 0; i < ok.Count; i++)
                    {
                        var lane = i == 0 ? _main : (i - 1 < _extraLanes.Count ? _extraLanes[i - 1] : null);
                        if (lane != null) shows.Add(ShowLandingAsync(lane, AutoShowMs, ok[i].Super ? ok[i].Prize : null));
                    }
                    await Task.WhenAll(shows);
                    if (_closed) return;
                    if (rares.Count > 0)
                    {
                        // НЕСКОЛЬКО РЕДКИХ ЗА ХОД (Илья: «что будет, если несколько
                        // редких?»): церемонии идут по очереди, каждая ждёт «Забрать».
                        _auto = false; _spinning = false;
                        foreach (var rare in rares)
                        {
                            _taken = new TaskCompletionSource<bool>();
                            await RevealPrizeAsync(rare);
                            await _taken.Task;
                            if (_closed) return;
                        }
                        return;
                    }
                    if (error != null) break;                // часть лент упёрлась в кошелёк
                }
            }
            finally
            {
                _auto = false; _spinning = false;
                if (!_closed && _blackout == null) PaintIdle();
            }
        }

        internal async Task SpinAsync()
        {
            if (_spinning || _closed || _state == null) return;
            _spinning = true; _skipAsked = false;
            _actions.Clear();
            _reward.style.display = DisplayStyle.None;
            LvnAsync.Fire(HidePoolAsync(), "GachaPoolHide");
            _status.text = LvnWords.Of("gacha.spinning", "Spinning…");
            try
            {
                var spin = await SpinWithTimeoutAsync();
                if (_closed) return;
                if (!string.IsNullOrEmpty(spin.Error))
                {
                    PaintIdle();
                    // Paint buttons first so it cannot overwrite the failure.
                    _status.text = spin.Error == "insufficient_funds"
                        ? LvnWords.Of("gacha.no_funds", "Not enough for a spin")
                        : LvnWords.Of("gacha.failed", "The spin did not go through. Try again.");
                    return;
                }
                if (spin.Super) _sectorsDirty = true;   // приз ушёл из пула — круг пересобрать на покое
                await RollLaneAsync(_main, LandingIndex(spin, _main), SpinLaps, SpinSeconds);
                if (_closed) return;
                _state.FreeToday = spin.FreeToday;
                _state.PrizesLeft = spin.PrizesLeft;
                _state.Spins++;
                if (spin.PrizesLeft.Count == 0 && _state.Sectors.RemoveAll(s => s.Super) > 0) _sectorsDirty = true;
                if (spin.Super) { await RevealPrizeAsync(spin); return; }
                // ОСТАНОВКА И ЕСТЬ ОТКРЫТИЕ (Илья): валюта не ждёт «Забрать» —
                // ячейка под стрелкой мигает, подпись говорит выигрыш, и снова
                // можно крутить.
                await StopFlashAsync(_main);
                if (!spin.WalletSynced) LvnAsync.Fire(LvnWallet.RefreshAsync(), "GachaWalletRetry");
                _spinning = false;
                PaintIdle();
                _status.text = LvnWords.Of("gacha.won_currency", "You got: {0}", LvnPriceTag.Full(spin.Currency, spin.Amount));
            }
            finally { _spinning = false; }
        }

        /// <summary>Прокрутить барабан вперёд на <paramref name="laps"/> кругов до
        /// сектора <paramref name="target"/>: позиция только растёт, лента не
        /// перестраивается.</summary>
        private async Task RollLaneAsync(Lane lane, int target, int laps, float seconds)
        {
            int n = LapLength;
            double from = lane.Pos;
            long at = (long)System.Math.Round(from);
            int cur = (int)(((at % n) + n) % n);
            int delta = ((target - cur) % n + n) % n;
            double to = at + (long)laps * n + delta;
            float start = Time.realtimeSinceStartup;
            while (!_closed && !_skipAsked && !LvnPrefs.ReduceMotion)
            {
                float progress = Mathf.Clamp01((Time.realtimeSinceStartup - start) / seconds);
                lane.Pos = from + (to - from) * LvnMotion.Settle(progress);
                Render(lane);
                if (progress >= 1f) break;
                await Task.Yield();
            }
            lane.Pos = to;
            Render(lane);
        }

        /// <summary>Клетка, на которой стоит лента, — та, что под стрелкой.</summary>
        private VisualElement LandedCell(Lane lane)
        {
            if (lane?.Strip == null) return null;
            int n = LapLength;
            int idx = n + (int)((((long)System.Math.Round(lane.Pos)) % n + n) % n);
            return idx < 0 || idx >= lane.Strip.childCount ? null : lane.Strip[idx];
        }

        /// <summary>ПОКАЗ ВЫПАВШЕГО В ЛЕНТЕ (Илья 15.09): клетка под стрелкой
        /// приподнимается и загорается ободком — золотым, у приза цветом его
        /// редкости — и держится так всё время показа. Без паузы автокрутка
        /// сливалась в мелькание: что выпало, было не разглядеть.</summary>
        private async Task ShowLandingAsync(Lane lane, int ms, LvnGacha.Prize prize)
        {
            var cell = LandedCell(lane);
            if (cell == null) { await Task.Delay(ms); return; }
            var described = prize != null ? DescribePrize(prize) : null;
            var color = described != null && LvnRarity.Rank(described.Rarity) >= 0
                ? LvnRarity.ColorOf(described.Rarity, _manifest?.ui?.wardrobe?.rarity_colors) : LvnTokens.Gold;
            var glow = new VisualElement { name = "gacha-landing", pickingMode = PickingMode.Ignore };
            LvnChrome.Stretch(glow);
            LvnChrome.Frame(glow, LvnTokens.RadiusSm, color, 3f);
            glow.style.backgroundColor = UiColor.WithAlpha(color, 0.18f);
            cell.Add(glow);
            float lift = LvnPrefs.ReduceMotion ? 1f : 1.1f;
            cell.style.scale = new Scale(new Vector2(lift, lift));
            try { await Task.Delay(ms); }
            finally
            {
                glow.RemoveFromHierarchy();
                cell.style.scale = new Scale(Vector2.one);
            }
        }

        /// <summary>Ячейка под стрелкой коротко «вспыхивает» — момент остановки читается как открытие.</summary>
        private async Task StopFlashAsync(Lane lane)
        {
            if (LvnPrefs.ReduceMotion) return;
            var cell = LandedCell(lane);
            if (cell == null) return;
            await LvnMotion.PlayAsync(cell, 320, (el, p) =>
            {
                float k = Mathf.Sin(p * Mathf.PI);
                float s = 1f + 0.08f * k;
                el.style.scale = new Scale(new Vector2(s, s));
            });
            cell.style.scale = new Scale(Vector2.one);
        }
    }
}
