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
        private const int MaxLanes = 5;   // Илья 15.09: «можно 5 лент максимально»
        /// <summary>Такт автокрутки (Илья 15.09: «0,6 крутка, 0,4 показываем,
        /// чтобы секунда была»): лента едет 0,6 с, выпавшая клетка держится
        /// подсвеченной 0,4 с — и только потом следующий ход.</summary>
        private const float FastSpinSeconds = 0.6f;
        private const int AutoShowMs = 400;
        private const int AutoTakeMs = 2400, AutoResumeMs = 400;
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
            // В ленте — ВЕСЬ набор, и выбитое тоже (Илья: «почему только 3?»):
            // лента — витрина, садится она всё равно на то, что выдал сервер.
            var prizes = new List<LvnGacha.Prize>();
            if (supers.Count > 0 && _state.Prizes != null)
                foreach (var p in _state.Prizes) prizes.Add(DescribePrize(p));
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
        private bool _poolHidden, _poolDirty;
        private int _poolMotion;

        /// <summary>ПАУЗА, КОТОРУЮ МОЖНО ПРОТАПАТЬ (Илья 15.09: «тапы ускоряют любую
        /// часть, чтобы можно было протапать всё, если нет терпения»): ждём срок
        /// или тап по листу — что раньше. Каждая пауза берёт свой тап.</summary>
        private async Task WaitOrTapAsync(int ms)
        {
            _skipAsked = false;
            float until = Time.realtimeSinceStartup + ms / 1000f;
            while (!_closed && !_skipAsked && Time.realtimeSinceStartup < until) await Task.Yield();
            _skipAsked = false;
        }

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
            var left = new HashSet<string>();
            double weights = 0;
            foreach (var p in _state.PrizesLeft ?? new List<LvnGacha.Prize>()) { left.Add(p.Sku); weights += p.Weight > 0 ? p.Weight : 1; }
            var prizes = new List<LvnGacha.Prize>();
            foreach (var p in _state.Prizes ?? new List<LvnGacha.Prize>()) prizes.Add(DescribePrize(p));
            prizes.Sort((a, b) => LvnRarity.Rank(b.Rarity).CompareTo(LvnRarity.Rank(a.Rarity)));
            var grid = new VisualElement { name = "gacha-pool-grid" };
            LvnFlow.Wrap(grid, Justify.Center);
            foreach (var p in prizes)
            {
                bool inPool = left.Contains(p.Sku);
                double share = inPool && total > 0 && weights > 0 ? superW / total * ((p.Weight > 0 ? p.Weight : 1) / weights) * 100.0 : 0;
                var card = new LvnSkinCard();
                card.style.marginRight = LvnTokens.Space1; card.style.marginBottom = LvnTokens.Space1;
                card.Bind(InfoFor(p, palette, inPool ? share : (double?)null, owned: !inPool || LvnWallet.Has(p.Sku)), _assets);
                if (!inPool) card.Art.style.opacity = 0.75f;
                grid.Add(card);   // своего действия нет — тап и долгое нажатие открывают подробности
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

        /// <summary>Сведения о призе для общей плитки: кадр по разделу из SKU,
        /// ступень, цена, шанс в углу малозаметно, способ получения словами.</summary>
        private LvnSkinCard.Info InfoFor(LvnGacha.Prize prize, IReadOnlyDictionary<string, string> palette, double? chance, bool owned)
        {
            var parts = prize.Sku?.Split(':');
            string axis = parts != null && parts.Length == 4 ? parts[2] : null;
            bool backdrop = axis == WardrobeSheet.BackdropAxis;
            var (zoom, ay) = backdrop ? (1f, 0.5f) : LvnWardrobeStage.Framing(axis);
            int rank = LvnRarity.Rank(prize.Rarity);
            string chanceText = chance.HasValue ? LvnWords.Of("skin.get_chance", "chance {0} %", chance.Value.ToString("0.##")) : null;
            string obtain = owned ? LvnWords.Of("skin.get_owned", "Already yours")
                : LvnWords.Of("skin.get_gacha", "Drops from spins") + (chanceText != null ? " · " + chanceText : "")
                  + (prize.Price > 0 ? " · " + LvnWords.Of("skin.get_buy", "Buy: {0}", LvnPriceTag.Full(prize.Currency ?? _state?.SpinCurrency, prize.Price)) : "");
            return new LvnSkinCard.Info
            {
                Title = prize.Label ?? prize.Sku, Art = prize.Art, SharpArt = zoom >= 3f,
                Frame = zoom, FrameY = ay, Cover = backdrop,
                Rarity = rank >= 0 ? LvnRarity.ColorOf(prize.Rarity, palette) : (Color?)null,
                RarityWord = rank >= 0 ? LvnRarity.Word(prize.Rarity) : null,
                Price = prize.Price, Currency = prize.Currency ?? _state?.SpinCurrency,
                Gift = prize.Price <= 0, Owned = owned,
                Corner = owned ? LvnWords.Of("gacha.owned", "owned") : chance.HasValue ? chance.Value.ToString("0.##") + " %" : null,
                Obtain = obtain,
            };
        }

        /// <summary>Одеть клетку призом — той же плиткой, что в гардеробе (Илья
        /// 15.09: «в рулетке так же, платиновый задник со скруглёнными краями,
        /// как в гардеробе точь-в-точь»): плитка заполняет клетку и сама
        /// подбирает размер шрифтов под высоту ленты.</summary>
        private void DressPrizeCell(VisualElement cell, LvnGacha.Prize raw)
        {
            cell.Clear();
            cell.style.backgroundColor = Color.clear;
            LvnChrome.ClearBorder(cell);
            var prize = raw != null ? DescribePrize(raw) : null;
            if (prize == null)
            {
                // Пул пуст, а сектор есть: безликий подарок в золоте.
                cell.style.backgroundColor = UiColor.WithAlpha(LvnTokens.Gold, 0.22f);
                LvnChrome.Frame(cell, LvnTokens.RadiusSm, LvnTokens.Gold, 2f);
                cell.Add(LvnIcons.Make(LvnIcon.Gift, LvnStageKit.D(28f), LvnTokens.Gold));
                return;
            }
            var card = new LvnSkinCard { pickingMode = PickingMode.Ignore }.Fill();
            card.Bind(InfoFor(prize, _manifest?.ui?.wardrobe?.rarity_colors, null, owned: false), _assets);
            cell.Add(card);
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
            else if (_poolDirty) BuildPool();  // приз выбит — в пуле он теперь «есть», лента не трогается
            _poolDirty = false;
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
            // Одна 144, две по 112, три по 92, четыре по 76, пять по 64 dp —
            // ряд кнопок остаётся на экране при любом числе лент.
            float h = LvnStageKit.D(lanes >= 5 ? 64f : lanes == 4 ? 76f : lanes == 3 ? 92f : lanes == 2 ? 112f : 144f);
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
                        if (ok[i].Super) _poolDirty = true;   // в пуле приз станет «есть»; лента не сбрасывается
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
                    // НЕСКОЛЬКО РЕДКИХ ЗА ХОД (Илья: «что будет, если несколько
                    // редких?»): церемонии идут по очереди. В АВТОКРУТКЕ (Илья
                    // 15.09) приз принимается сам: после «Забрать» ждём 2,4 с
                    // (тап — раньше), закрываем, через 0,4 с лента едет дальше;
                    // ленты и их положение не сбрасываются.
                    foreach (var rare in rares)
                    {
                        _taken = new TaskCompletionSource<bool>();
                        await RevealPrizeAsync(rare);
                        if (_closed) return;
                        await Task.WhenAny(_taken.Task, WaitOrTapAsync(AutoTakeMs));
                        if (_closed) return;
                        if (!_taken.Task.IsCompleted) TakeNow(rare);
                        await WaitOrTapAsync(AutoResumeMs);
                        if (_closed) return;
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
                if (spin.Super) _poolDirty = true;   // в пуле приз станет «есть»; лента не сбрасывается
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
            if (cell == null) { await WaitOrTapAsync(ms); return; }
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
            try { await WaitOrTapAsync(ms); }
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
