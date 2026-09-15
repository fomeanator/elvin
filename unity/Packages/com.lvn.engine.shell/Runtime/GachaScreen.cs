using System;
using System.Collections.Generic;
using System.Threading;
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
        /// <summary>До пяти лент (Илья 15.09: «как мне поток в 5 раз увеличить?»):
        /// каждая лента — своя крутка за свою цену, все едут разом.</summary>
        private const int MaxLanes = 5;
        /// <summary>Такт автокрутки (Илья 15.09: «0,6 крутка, 0,4 показываем,
        /// чтобы секунда была»): лента едет 0,6 с, выпавшая клетка держится
        /// подсвеченной 0,4 с — и только потом следующий ход.</summary>
        private const float FastSpinSeconds = 0.6f;
        private const int AutoShowMs = 400;
        private const int AutoTakeMs = 2400, AutoResumeMs = 400;

        private VisualElement _cases;
        /// <summary>Какой набор крутим: последний выбранный (помнится), иначе первый.</summary>
        private string CaseId => string.IsNullOrEmpty(LvnPrefs.GachaCase) ? null : LvnPrefs.GachaCase;

        /// <summary>Ряд наборов: обложка, имя, цена; текущий подсвечен. Один набор — ряда нет.</summary>
        private void PaintCases()
        {
            if (_cases == null) return;
            _cases.Clear();
            var list = _state?.Cases;
            if (list == null || list.Count <= 1) { _cases.style.display = DisplayStyle.None; return; }
            _cases.style.display = DisplayStyle.Flex;
            foreach (LvnGacha.Case c in list)
            {
                var id = c.Id;
                bool on = id == _state.CaseId;
                var card = new LvnSkinCard();
                card.SetSize(LvnStageKit.D(96f), LvnStageKit.D(120f));
                LvnAir.Margin(card, LvnTokens.Space1);
                card.Bind(new LvnSkinCard.Info
                {
                    Title = c.Name ?? id, Art = c.Cover, Cover = true, Frame = 1f, FrameY = 0.5f,
                    Owned = true, Description = c.Description,
                    Corner = c.SpinPrice > 0 ? LvnPriceTag.Amount(c.SpinPrice) : null,
                }, _assets);
                card.SetChosen(on, LvnTokens.Gold);
                card.Tapped += () => LvnAsync.Fire(SwitchCaseAsync(id), "GachaCase");
                _cases.Add(card);
            }
        }

        /// <summary>Сменить набор: помним выбор, перечитываем состояние, ленты — заново.</summary>
        private async Task SwitchCaseAsync(string id)
        {
            if (_spinning || _closed || id == _state?.CaseId) return;
            LvnPrefs.GachaCase = id;
            Say(LvnWords.Of("boot.loading_data", "loading data…"));
            var state = await LvnGacha.GetAsync(id);
            if (_closed || state == null) return;
            _sectorsDirty = true;
            Present(state);
        }

        /// <summary>РЕЖИМ АВТО — переключатель над кнопкой, как качество в
        /// настройках (Илья 15.09): «Выкл» — обычная крутка и кнопка снова;
        /// «Плавно» — авто с обычной лентой; «Быстро» — авто 0,6 с + 0,4 с.
        /// Ленты — в любом режиме, авто лишь дополнение. Помнится на устройстве.</summary>
        private enum AutoMode { Off, Smooth, Fast }
        private static AutoMode Mode
        {
            get => LvnPrefs.GachaAuto == "fast" ? AutoMode.Fast : LvnPrefs.GachaAuto == "smooth" ? AutoMode.Smooth : AutoMode.Off;
            set => LvnPrefs.GachaAuto = value == AutoMode.Fast ? "fast" : value == AutoMode.Smooth ? "smooth" : "off";
        }
        /// <summary>«Крутим…» не должно висеть вечно (TR-106): ответ дольше —
        /// считается неудачей, кнопка возвращается.</summary>
        private const int SpinTimeoutMs = 15000;
        private sealed class Lane
        {
            public VisualElement Window, Strip;
            public double Pos;   // позиция барабана в ячейках, только растёт
            public bool[] Shown; // какие клетки круга сейчас в окне (включены)
        }
        private readonly List<Lane> _extraLanes = new List<Lane>();
        /// <summary>БЕСКОНЕЧНЫЙ БАРАБАН (TR-108, Илья 15.09): позиция ленты —
        /// непрерывное число ячеек, только растёт; рисуется по модулю круга
        /// секторов, в окне три круга ячеек. Лента не пересобирается ни при
        /// открытии, ни после приза — каждая крутка едет дальше вперёд и
        /// останавливается на нужном секторе. Остановка и есть открытие.</summary>
        private readonly Lane _main = new Lane();
        private bool _sectorsDirty;
        /// <summary>ОДИН КРУГ В ДЕРЕВЕ, В КАДРЕ — ТОЛЬКО ОКНО (Илья 15.09: «лагает
        /// до жути»): было три копии круга на ленту (сотни плиток на пяти
        /// лентах), и UITK тянул их все каждый кадр. Теперь клетки круга стоят
        /// в дереве по одной, а Render ставит на место лишь те ~7, что попадают
        /// в окно, остальные выключены.</summary>
        private const int RenderLaps = 1;
        private const float PrizeShare = 0.3f;   // доля призовых клеток в круге ленты
        private const int Visible = 4;
        private const float SpinSeconds = 7f;
        private const int SpinLaps = 6;

        /// <summary>Вкладкой: страница живёт долго, показывается и прячется
        /// (ShowAsTab/HideAsTab), «назад» ведёт на главную, панель встаёт над
        /// нижней лентой, фон прозрачнее — сквозь него купленный фон меню.</summary>
        private readonly bool _tabMode;
        public Action GoHome;
        public Func<float> NavHeight;

        public GachaScreen(ILvnAssets assets, bool tab = false)
        {
            _assets = assets;
            _tabMode = tab;
            name = "gacha-screen";
            BuySpins = TestBuySpinsAsync;
            // ВО ВЕСЬ ЭКРАН под навбаром, как магазин и профиль (Илья 15.09:
            // «крутку на полный экран»): раздел, а не окно — лентам и пулу
            // нужно всё место.
            _sheet = LvnChrome.Sheet(new VisualElement(), 0f);
            Add(_sheet);
            AdoptSheet(_sheet, fullscreen: true);
            if (_tabMode) _sheet.style.backgroundColor = LvnTokens.Veil(0.42f);   // фон меню виден сквозь
            _title = LvnRedress.Bind(new Label(), () => LvnWords.Of("gacha.title", "Spin"));
            var header = ScreenUi.GalleryHeader(_tabMode ? (Action)(() => GoHome?.Invoke()) : Cancel, _title, out var counter);
            _sheet.Add(header);
            // В комнате шапка лишняя — где ты, говорит нижняя лента; высота уходит
            // лентам и пулу (Илья 15.09: «модалку круток больше по высоте»).
            if (_tabMode) header.style.display = DisplayStyle.None;
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
            // ВЫБОР НАБОРА (Илья и партнёр 15.09: «крутки на наборы — в крутке
            // выбрать, какой кейс крутить; пока один, потом второй»): ряд над
            // лентой, виден, когда наборов больше одного.
            _cases = new VisualElement { name = "gacha-cases" };
            LvnFlow.Wrap(_cases, Justify.FlexStart);
            _cases.style.marginBottom = LvnTokens.Space2;
            _cases.style.display = DisplayStyle.None;
            content.Add(_cases);
            content.Add(_window);

            _strip = new VisualElement { name = "gacha-strip", pickingMode = PickingMode.Ignore };
            // ЛЕНТА ДВИЖЕТСЯ КАЖДЫЙ КАДР: без этой подсказки UITK перетесселирует
            // всё поддерево (сотни плиток) на каждый сдвиг — «лагает до жути»
            // (Илья 15.09). С ней сдвиг — только матрица.
            _strip.usageHints = UsageHints.DynamicTransform;
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
            _status.style.marginTop = LvnTokens.Space2;
            _status.style.display = DisplayStyle.None;   // говорит только об ошибке
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
            Say(LvnWords.Of("boot.loading_data", "loading data…"));
            var shown = ShowAsync();
            if (!LvnSkins.Loaded) await LvnSkins.RefreshAsync();   // описания призов — из каталога
            _state = await LvnGacha.GetAsync(CaseId);
            if (!_closed) Present(_state);
            await shown;
        }

        internal void Present(LvnGacha.Status state)
        {
            _state = state;
            Say(null);   // «загрузка данных…» снята: дальше говорит только ошибка (скрин Ильи 15.09)
            PaintCases();
            BuildStrip();
            PaintIdle();
        }

        protected override void OnClosed() => StopPresentation();

        /// <summary>Вкладка показывается снова: экран оживает и перечитывает
        /// состояние — пул, кошелёк, шансы могли смениться, пока его не было.</summary>
        protected override void OnOpening()
        {
            if (!_tabMode) return;
            _closed = false; _skipAsked = false; _auto = false; _spinning = false;
            _artCancel = new CancellationTokenSource();
            DismissCeremony();
            LvnAsync.Fire(ReloadAsync(), "GachaTab");
        }

        private async Task ReloadAsync()
        {
            Say(LvnWords.Of("boot.loading_data", "loading data…"));
            if (!LvnSkins.Loaded) await LvnSkins.RefreshAsync();
            var state = await LvnGacha.GetAsync(CaseId);
            if (_closed) return;
            Present(state);
        }

        public override void Settled()
        {
            if (_tabMode && NavHeight != null) _sheet.style.bottom = NavHeight();   // панель над нижней лентой
        }
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
            // ПРИЗОВ — ТРЕТЬ ЛЕНТЫ (Илья 15.09: «слишком много фейк-редких, надо
            // 30 %»): валютных клеток столько, чтобы призы были долей PrizeShare,
            // и раздаются они секторам по весам — частый сектор чаще и в ленте.
            var filler = new List<LvnGacha.Sector>();
            if (plain.Count > 0)
            {
                int want = Mathf.Max(plain.Count, Mathf.RoundToInt(mixed.Count * (1f - PrizeShare) / PrizeShare));
                double weight = 0; foreach (var p in plain) weight += p.Weight > 0 ? p.Weight : 1;
                var quota = new int[plain.Count]; int given = 0;
                for (int i = 0; i < plain.Count; i++) { quota[i] = Mathf.Max(1, (int)((plain[i].Weight > 0 ? plain[i].Weight : 1) / weight * want)); given += quota[i]; }
                for (int i = 0; given < want; i = (i + 1) % plain.Count) { quota[i]++; given++; }
                // Круговая раздача: соседние валютные клетки — разные сектора.
                for (int round = 0; filler.Count < want; round++)
                    for (int i = 0; i < plain.Count && filler.Count < want; i++)
                        if (quota[i] > round) filler.Add(plain[i]);
            }
            int a = filler.Count, b = mixed.Count, ia = 0, ib = 0;
            while (ia < a || ib < b)
            {
                bool takePlain = ib >= b || (ia < a && (long)ia * b <= (long)ib * a);
                if (takePlain) _cells.Add(new ReelCell { Sector = filler[ia++] });
                else _cells.Add(new ReelCell { Sector = supers[ib % supers.Count], Prize = mixed[ib++] });
            }
            if (_cells.Count == 0) foreach (var s in sectors) _cells.Add(new ReelCell { Sector = s });
        }

        private void FillStrip(VisualElement strip)
        {
            strip.Clear();
            for (int i = 0; i < _cells.Count * RenderLaps; i++) strip.Add(Cell(_cells[i % _cells.Count]));
            var lane = strip == _strip ? _main : _extraLanes.Find(l => l.Strip == strip);
            if (lane != null) lane.Shown = new bool[strip.childCount];
        }

        private VisualElement Cell(ReelCell info)
        {
            var sector = info.Sector;
            var cell = new VisualElement { pickingMode = PickingMode.Ignore };
            cell.usageHints = UsageHints.DynamicTransform;   // едет каждый кадр — только матрица
            cell.style.position = Position.Absolute;
            cell.style.top = 0; cell.style.bottom = 0; cell.style.left = 0;
            cell.style.width = CellWidth;
            cell.style.display = DisplayStyle.None;   // включит Render, когда клетка войдёт в окно
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
            cell.Add(LvnPriceTag.Icon(sector.Currency, LvnStageKit.D(32f)));
            var label = new Label(LvnPriceTag.Amount(sector.Amount));
            label.style.color = LvnTokens.Text;
            label.style.fontSize = LvnTokens.TextLg;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.marginTop = LvnTokens.Space1;
            cell.Add(label);
            return cell;
        }

        /// <summary>Выигрыш с начала последнего запуска по валютам — показывается
        /// в кнопке суммой со значком (Илья 15.09: «статус в кнопку, только
        /// сумма и значок»); новый запуск обнуляет.</summary>
        private readonly Dictionary<string, long> _win = new Dictionary<string, long>();

        private void Say(string text)
        {
            _status.text = text ?? "";
            _status.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>СТРОКА ВЫИГРЫША НАД КНОПКОЙ (Илья 15.09: «кристаллы и энергию
        /// показывать над кнопкой, в кнопку не надо»): сумма и значок по валютам.</summary>
        private VisualElement WinRow()
        {
            var row = ScreenUi.Row();
            row.pickingMode = PickingMode.Ignore;
            row.style.justifyContent = Justify.Center;
            row.style.marginBottom = LvnTokens.Space1;
            foreach (var kv in _win)
            {
                if (kv.Value <= 0) continue;
                var amount = new Label("+" + LvnPriceTag.Amount(kv.Value)) { pickingMode = PickingMode.Ignore };
                amount.style.color = LvnTokens.Gold;
                amount.style.fontSize = LvnTokens.TextLg;
                LvnAir.MarginX(amount, LvnTokens.Space1);
                row.Add(amount);
                row.Add(LvnPriceTag.Icon(kv.Key, LvnStageKit.D(22f)));
            }
            return row;
        }

        /// <summary>Правая часть кнопки: цена хода на всех лентах или «бесплатно».</summary>
        private VisualElement PriceTag()
        {
            var row = ScreenUi.Row();
            row.pickingMode = PickingMode.Ignore;
            if (_state.FreeToday)
            {
                var free = LvnRedress.Bind(new Label { pickingMode = PickingMode.Ignore }, () => LvnWords.Of("gacha.free", "free"));
                free.style.color = LvnTokens.Gold; free.style.marginLeft = LvnTokens.Space2;
                row.Add(free);
                return row;
            }
            var cost = new Label(LvnPriceTag.Amount(_state.SpinPrice * (1 + _extraLanes.Count))) { pickingMode = PickingMode.Ignore };
            cost.style.color = LvnTokens.Gold;
            cost.style.marginLeft = LvnTokens.Space2;
            row.Add(cost);
            row.Add(LvnPriceTag.Icon(_state.SpinCurrency, LvnStageKit.D(20f)));
            return row;
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
            // Набор не пустеет: шанс есть у КАЖДОГО приза, выбитое падает копией
            // и продаётся за цену скина; у имеющихся — «есть» и число копий.
            var left = new HashSet<string>();
            foreach (var p in _state.PrizesLeft ?? new List<LvnGacha.Prize>()) left.Add(p.Sku);
            double weights = 0;
            var prizes = new List<LvnGacha.Prize>();
            foreach (var p in _state.Prizes ?? new List<LvnGacha.Prize>()) { var d = DescribePrize(p); prizes.Add(d); weights += d.Weight > 0 ? d.Weight : 1; }
            prizes.Sort((a, b) => LvnRarity.Rank(b.Rarity).CompareTo(LvnRarity.Rank(a.Rarity)));
            // СЕТКА — КАК ТАБЛИЦА (Илья 15.09): заполняется слева направо, без
            // центрирования; валюта — теми же плитками после призов.
            var grid = new VisualElement { name = "gacha-pool-grid" };
            LvnFlow.Wrap(grid, Justify.FlexStart);
            foreach (var p in prizes)
            {
                bool owned = !left.Contains(p.Sku) || LvnWallet.Has(p.Sku);
                double share = total > 0 && weights > 0 ? superW / total * ((p.Weight > 0 ? p.Weight : 1) / weights) * 100.0 : 0;
                bool won = !left.Contains(p.Sku);   // выбит здесь, а не куплен в гардеробе
                var card = new LvnSkinCard();
                card.style.marginRight = LvnTokens.Space1; card.style.marginBottom = LvnTokens.Space1;
                card.Bind(InfoFor(p, palette, share, owned, won), _assets);
                if (owned) card.Art.style.opacity = 0.75f;
                grid.Add(card);   // своего действия нет — тап и долгое нажатие открывают подробности
            }
            foreach (var s in _state.Sectors)
            {
                if (s.Super) continue;
                var card = new LvnSkinCard { pickingMode = PickingMode.Ignore };
                card.style.marginRight = LvnTokens.Space1; card.style.marginBottom = LvnTokens.Space1;
                card.Bind(new LvnSkinCard.Info
                {
                    Title = "+" + LvnPriceTag.Amount(s.Amount),
                    CurrencyIcon = s.Currency,
                    Corner = (total > 0 ? s.Weight / total * 100.0 : 0).ToString("0.#") + " %",
                }, _assets);
                grid.Add(card);
            }
            _pool.Add(grid);
            // ПЯТЬ В РЯД (Илья): ширина плитки — от ширины сетки, высота — по
            // пропорции плитки гардероба; пересчёт при смене геометрии.
            grid.RegisterCallback<GeometryChangedEvent>(_ => FitPoolCards(grid));
            FitPoolCards(grid);
        }

        private const int PoolColumns = 5;

        private static void FitPoolCards(VisualElement grid)
        {
            float width = grid.resolvedStyle.width;
            if (float.IsNaN(width) || width <= 1f) return;
            float gap = LvnTokens.Space1;
            // У КАЖДОЙ плитки правое поле, включая последнюю в ряду: считать зазоры
            // «на один меньше» значило получить четыре в ряд вместо пяти (скрин Ильи).
            float w = Mathf.Floor((width - gap * PoolColumns) / PoolColumns) - 0.5f;
            float h = w * LvnSkinCard.BaseHeight / LvnSkinCard.BaseWidth;
            foreach (var child in grid.Children())
                if (child is LvnSkinCard card && Mathf.Abs(card.resolvedStyle.width - w) > 0.6f) card.SetSize(w, h);
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
        private LvnSkinCard.Info InfoFor(LvnGacha.Prize prize, IReadOnlyDictionary<string, string> palette, double? chance, bool owned, bool won = false)
        {
            var parts = prize.Sku?.Split(':');
            string axis = parts != null && parts.Length == 4 ? parts[2] : null;
            bool backdrop = axis == WardrobeSheet.BackdropAxis || prize.Kind == "backdrop";
            bool avatar = prize.Kind == "avatar" || (prize.Sku != null && prize.Sku.StartsWith("avatar."));
            var (zoom, ay) = backdrop || avatar ? (1f, 0.5f) : LvnWardrobeStage.Framing(axis);
            int rank = LvnRarity.Rank(prize.Rarity);
            string chanceText = chance.HasValue ? LvnWords.Of("skin.get_chance", "chance {0} %", chance.Value.ToString("0.##")) : null;
            string drops = LvnWords.Of("skin.get_gacha", "Drops from spins") + (chanceText != null ? " · " + chanceText : "");
            bool sellable = prize.Price > 0 && !prize.GachaOnly;   // «только из крутки» не покупается и при цене
            // КОПИЙ В ИНВЕНТАРЕ НЕТ (Илья 15.09: «в инвентаре только одна копия,
            // дубль продаётся автоматом»): у имеющегося приза — способ получения
            // и за сколько уйдёт повтор; счётчик копий не показываем.
            // Имеющийся приз: выбит здесь — «выпало в крутке», иначе куплен в гардеробе.
            string how = won ? LvnWords.Of("skin.got_gacha", "Won in spins")
                : prize.Price > 0 ? LvnWords.Of("skin.got_buy", "Bought for {0}", LvnPriceTag.Full(prize.Currency ?? _state?.SpinCurrency, prize.Price))
                : LvnWords.Of("skin.get_free", "Free");
            string obtain = owned
                ? LvnWords.Of("skin.get_owned", "Yours") + " · " + how
                  + ((prize.SellPrice > 0 ? prize.SellPrice : prize.Price) > 0 ? " · " + LvnWords.Of("gacha.copy_worth", "a copy sells for {0}", LvnPriceTag.Full(prize.Currency ?? _state?.SpinCurrency, prize.SellPrice > 0 ? prize.SellPrice : prize.Price)) : "")
                : drops + (sellable ? " · " + LvnWords.Of("skin.get_buy", "Buy: {0}", LvnPriceTag.Full(prize.Currency ?? _state?.SpinCurrency, prize.Price)) : "");
            return new LvnSkinCard.Info
            {
                Title = prize.Label ?? prize.Sku, Art = prize.Art, SharpArt = zoom >= 3f,
                Frame = zoom, FrameY = ay, Cover = backdrop || avatar,   // фон и аватарка — заливкой
                Rarity = rank >= 0 ? LvnRarity.ColorOf(prize.Rarity, palette) : (Color?)null,
                RarityWord = rank >= 0 ? LvnRarity.Word(prize.Rarity) : null,
                Price = prize.Price, Currency = prize.Currency ?? _state?.SpinCurrency,
                Gift = !sellable, Owned = owned, PriceAlways = sellable,   // «есть» не прячет цену (Илья); только из крутки — подарок
                Corner = owned ? LvnWords.Of("gacha.owned", "owned")
                    : chance.HasValue ? chance.Value.ToString("0.##") + " %" : null,
                Obtain = obtain,
                Description = prize.Description,
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
                if (fallback < lane.Strip.childCount) DressPrizeCell(lane.Strip[fallback], spin.Prize);
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
            int n = lane.Strip.childCount;
            if (n == 0) return;
            if (lane.Shown == null || lane.Shown.Length != n) lane.Shown = new bool[n];
            float step = CellWidth + LvnTokens.Space1;
            float centre = WindowWidth * 0.5f - CellWidth * 0.5f;
            float reach = WindowWidth * 0.5f + CellWidth;   // дальше этого клетка за окном
            for (int i = 0; i < n; i++)
            {
                // Кратчайшее расстояние по кругу от позиции барабана до клетки.
                double d = i - lane.Pos;
                d -= System.Math.Round(d / n) * n;
                float x = (float)(d * step);
                var cell = lane.Strip[i];
                bool inWindow = Mathf.Abs(x) <= reach;
                if (inWindow)
                {
                    cell.style.translate = new Translate(centre + x, 0f);
                    if (!lane.Shown[i]) { cell.style.display = DisplayStyle.Flex; lane.Shown[i] = true; }
                }
                else if (lane.Shown[i]) { cell.style.display = DisplayStyle.None; lane.Shown[i] = false; }
            }
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
            if (_sectorsDirty) BuildStrip();   // секторы изменились (кончились редкие) — только тогда
            else if (_poolDirty) BuildPool();  // приз выбит — в пуле он теперь «есть», лента не трогается
            _poolDirty = false;
            _actions.Clear();
            _reward.style.display = DisplayStyle.None;
            _window.style.display = DisplayStyle.Flex;
            ShowPool();
            if (_state == null)
            {
                Say(LvnWords.Of("gacha.offline", "Spins need a connection."));
                return;
            }
            if (_state.Sectors.Count == 0 || (!_state.FreeToday && _state.SpinPrice <= 0))
            {
                Say(LvnWords.Of("gacha.come_back", "Come back tomorrow for a free spin"));
                return;
            }
            // НЕ ХВАТАЕТ — «ПОПОЛНИТЬ» (TR-107): кнопка ведёт в магазин, а не
            // предлагает крутку, которая упрётся в кошелёк.
            bool broke = !_state.FreeToday && _state.SpinPrice > 0
                && (_needTopUp || LvnWallet.Balance(_state.SpinCurrency) < _state.SpinPrice);
            if (broke)
            {
                _needTopUp = false;
                PaintTopUp();
                return;
            }
            _actions.Add(ModeRow());
            if (_win.Count > 0) _actions.Add(WinRow());
            bool autoMode = Mode != AutoMode.Off;
            var button = ActionButton("gacha-spin", () => "", () => LvnAsync.Fire(autoMode ? AutoAsync() : SpinAsync(), "GachaSpin"));
            button.RemoveFromHierarchy();
            button.text = "";
            LvnFlow.Wrap(ScreenUi.Row(button), Justify.Center);
            var label = LvnRedress.Bind(new Label { pickingMode = PickingMode.Ignore },
                () => autoMode ? LvnWords.Of("gacha.auto", "Auto") : LvnWords.Of("gacha.spin", "Spin"));
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 0;
            label.style.maxWidth = Length.Percent(100f);
            label.style.color = LvnTokens.Gold;
            button.Add(label);
            // Справа — цена хода на всех лентах (лент три — цена втрое) или «бесплатно».
            button.Add(PriceTag());
            var row = ScreenUi.Row();
            button.style.flexGrow = 1; button.style.flexShrink = 1;
            row.Add(button);
            row.Add(LaneStepper(enabled: true));
            _actions.Add(row);
        }

        /// <summary>БЫСТРОЕ ПОПОЛНЕНИЕ В КРУТКАХ (Илья 15.09: «5 круток, 10 круток,
        /// 50 круток»): три пакета ровно на N ходов по текущей цене — покупка тем
        /// же путём, что в магазине (сейчас тестовое зачисление, реальный
        /// биллинг сменит только транспорт); ниже — дверь в магазин.</summary>
        internal static readonly int[] QuickPacks = { 5, 10, 50 };
        internal Func<int, long, Task<bool>> BuySpins;

        private void PaintTopUp()
        {
            _actions.Add(ModeRow());
            var row = ScreenUi.Row();
            foreach (var n in QuickPacks)
            {
                int spins = n;
                var b = ActionButton("gacha-pack-" + n, () => "", () => LvnAsync.Fire(BuyPackAsync(spins), "GachaPack"));
                b.RemoveFromHierarchy();
                b.text = "";
                b.style.flexGrow = 1; b.style.flexShrink = 1;
                b.style.fontSize = LvnTokens.TextBase;
                LvnAir.Pad(b, LvnTokens.Space2, LvnTokens.Space1);
                var col = new VisualElement { pickingMode = PickingMode.Ignore };
                col.style.alignItems = Align.Center;
                var title = LvnRedress.Bind(new Label { pickingMode = PickingMode.Ignore }, () => LvnWords.Of("gacha.spins_pack", "{0} spins", spins));
                title.style.color = LvnTokens.Gold;
                col.Add(title);
                var cost = ScreenUi.Row();
                cost.pickingMode = PickingMode.Ignore;
                var amount = new Label(LvnPriceTag.Amount(_state.SpinPrice * spins)) { pickingMode = PickingMode.Ignore };
                amount.style.color = LvnTokens.TextDim;
                amount.style.fontSize = LvnTokens.TextSm;
                cost.Add(amount);
                cost.Add(LvnPriceTag.Icon(_state.SpinCurrency, LvnStageKit.D(16f)));
                col.Add(cost);
                b.Add(col);
                if (row.childCount > 0) b.style.marginLeft = LvnTokens.Space1;
                row.Add(b);
            }
            _actions.Add(row);
            var store = ActionButton("gacha-topup", () => LvnWords.Of("gacha.top_up", "Top up"), () => LvnAsync.Fire(TopUpAsync(), "GachaTopUp"));
            LvnStageKit.PlateButton(store, primary: false);
            store.SetEnabled(OpenStore != null);
            store.style.marginTop = LvnTokens.Space1;
        }

        private async Task BuyPackAsync(int spins)
        {
            if (_state == null || _spinning || BuySpins == null) return;
            long amount = _state.SpinPrice * spins;
            _actions.SetEnabled(false);
            bool ok = false;
            try { ok = await BuySpins(spins, amount); }
            finally { _actions.SetEnabled(true); }
            if (_closed) return;
            if (ok) await LvnWallet.RefreshAsync();
            if (_closed) return;
            if (!ok) Say(LvnWords.Of("gacha.pack_failed", "The purchase did not go through. Try again."));
            PaintIdle();
        }

        /// <summary>Тестовое зачисление — тот же путь, что у магазина без биллинга:
        /// кристаллы реально ложатся в серверный кошелёк.</summary>
        private Task<bool> TestBuySpinsAsync(int spins, long amount)
            => LvnWallet.EarnAsync(_state.SpinCurrency, amount, "gacha_pack_test:" + spins);

        private async Task TopUpAsync()
        {
            if (OpenStore == null) return;
            if (_tabMode) { await OpenStore(); return; }   // вкладка остаётся, магазин — модалью поверх
            // МАГАЗИН ПОД КРУТКАМИ (Илья 15.09: «конфликт модалок»): магазин —
            // модалка оболочки, а крутки — оверлей поверх корня, и магазин
            // открывался под ними. Крутки закрываются, магазин выходит на свет;
            // назад — кнопкой «Крутка» на главной.
            Close();
            await Task.Yield();
            await OpenStore();
        }

        /// <summary>Ряд режима: подпись «Авто» и три ступени.</summary>
        private VisualElement ModeRow()
        {
            var row = ScreenUi.Row(spread: true);
            row.style.marginBottom = LvnTokens.Space1;
            var caption = LvnRedress.Bind(new Label { pickingMode = PickingMode.Ignore }, () => LvnWords.Of("gacha.auto", "Auto"));
            caption.style.color = LvnTokens.TextDim;
            caption.style.fontSize = LvnTokens.TextSm;
            row.Add(caption);
            row.Add(LvnSegment.Of(new[] { AutoMode.Off, AutoMode.Smooth, AutoMode.Fast },
                m => m == AutoMode.Off ? LvnWords.Of("gacha.mode_off", "Off")
                   : m == AutoMode.Smooth ? LvnWords.Of("gacha.mode_smooth", "Smooth")
                   : LvnWords.Of("gacha.mode_fast", "Fast"),
                m => Mode == m,
                m =>
                {
                    Mode = m;
                    // На ходу: «Выкл» останавливает авто после этого хода, смена
                    // темпа берётся со следующего; на покое — кнопка меняет смысл.
                    if (_spinning) { if (m == AutoMode.Off) _auto = false; }
                    else PaintIdle();
                },
                StyleSegment, alignEnd: true));
            return row;
        }

        private static void StyleSegment(Button b, bool on)
        {
            b.style.fontSize = LvnTokens.TextSm;
            LvnAir.Pad(b, LvnTokens.Space2, LvnTokens.Hair);
            LvnStyler.Plate(b, on ? LvnTokens.Gold : LvnTokens.Faint, on ? LvnTokens.OnAccent : LvnTokens.Text, LvnTokens.RadiusSm);
        }

        /// <summary>Ленты — в любом режиме (Илья: «многоленточность только для
        /// авто — неверно»): «−», число, «+» до пяти; в авто можно добавлять на
        /// ходу — новая лента вступает со следующего хода.</summary>
        private VisualElement LaneStepper(bool enabled)
        {
            var row = ScreenUi.Row();
            row.style.flexShrink = 0;
            row.style.marginLeft = LvnTokens.Space2;
            var minus = new Button(() => { RemoveLane(); Repaint(); }) { text = "−", name = "gacha-lane-minus" };
            var count = LvnRedress.Bind(new Label { pickingMode = PickingMode.Ignore },
                () => LvnWords.Of("gacha.lanes", "Lanes: {0}", 1 + _extraLanes.Count));
            var plus = new Button(() => { AddLane(); Repaint(); }) { text = "+", name = "gacha-lane-plus" };
            foreach (var b in new[] { minus, plus })
            {
                LvnStageKit.PlateButton(b, primary: false);
                b.style.fontSize = LvnTokens.TextLg;
                LvnAir.Pad(b, LvnTokens.Space2, LvnTokens.Hair);
                b.RegisterCallback<ClickEvent>(e => e.StopPropagation());
            }
            minus.SetEnabled(enabled && _extraLanes.Count > 0);
            plus.SetEnabled(enabled && _extraLanes.Count + 1 < MaxLanes);
            count.style.color = LvnTokens.TextDim;
            count.style.fontSize = LvnTokens.TextSm;
            LvnAir.MarginX(count, LvnTokens.Space1);
            row.Add(minus); row.Add(count); row.Add(plus);
            return row;
        }

        private void Repaint()
        {
            if (_spinning) PaintRunning(_auto); else PaintIdle();
        }

        /// <summary>Кнопки на время хода: ряд режима, «Стоп» в авто, ленты
        /// (в авто — живые, вручную — до конца хода).</summary>
        private void PaintRunning(bool auto)
        {
            _actions.Clear();
            _actions.Add(ModeRow());
            if (_win.Count > 0) _actions.Add(WinRow());
            var row = ScreenUi.Row();
            if (auto)
            {
                var stop = ActionButton("gacha-auto-stop", () => LvnWords.Of("gacha.auto_stop", "Stop"), () => _auto = false);
                LvnStageKit.PlateButton(stop, primary: false);
                stop.RemoveFromHierarchy();
                stop.style.flexGrow = 1;
                row.Add(stop);
            }
            else
            {
                var filler = new VisualElement();
                filler.style.flexGrow = 1;
                row.Add(filler);
            }
            row.Add(LaneStepper(enabled: auto));
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
            lane.Strip.usageHints = UsageHints.DynamicTransform;   // см. главную ленту
            lane.Strip.style.position = Position.Absolute;
            lane.Strip.style.left = 0; lane.Strip.style.top = 0; lane.Strip.style.bottom = 0;
            lane.Strip.style.flexDirection = FlexDirection.Row;
            lane.Window.Add(lane.Strip);
            AddNeedle(lane.Window);
            FillStrip(lane.Strip);
            // ЛЕНТЫ НЕ ХОДЯТ СТРОЕМ: у каждой своя фаза круга (пятая доля на
            // ленту) — иначе пять дорожек показывают одну и ту же клетку в
            // клетку («ну сам посмотри» — Илья 15.09 со скрином пяти лент).
            lane.Pos = _main.Pos + LapLength * (double)(_extraLanes.Count + 1) / MaxLanes;
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
            // Одна 144, две по 120, три по 104, четыре по 90, пять по 80 dp
            // (Илья: «не сжимай, дай место пяти лентам») — статус ушёл в кнопку,
            // и место под лентами появилось.
            float h = LvnStageKit.D(lanes >= 5 ? 80f : lanes == 4 ? 90f : lanes == 3 ? 104f : lanes == 2 ? 120f : 144f);
            _window.style.height = h;
            foreach (var lane in _extraLanes) lane.Window.style.height = h;
        }

        /// <summary>Снять последнюю добавленную ленту.</summary>
        private void RemoveLane()
        {
            if (_extraLanes.Count == 0) return;
            var lane = _extraLanes[_extraLanes.Count - 1];
            lane.Window.RemoveFromHierarchy();
            _extraLanes.RemoveAt(_extraLanes.Count - 1);
            ApplyLaneHeights();
        }

        /// <summary>Ответ сервера с пределом ожидания: висящий запрос — неудача, а не вечное «Крутим…».</summary>
        private async Task<LvnGacha.Spin> SpinWithTimeoutAsync()
        {
            var spin = LvnGacha.SpinAsync(_state?.CaseId);
            var done = await Task.WhenAny(spin, Task.Delay(SpinTimeoutMs));
            if (done != spin) return new LvnGacha.Spin { Error = "timeout" };
            return await spin;
        }

        private void Tip(string currency, long amount)
        {
            _win.TryGetValue(currency, out var had);
            _win[currency] = had + amount;
        }

        private Lane LaneAt(int i) => i == 0 ? _main : (i - 1 < _extraLanes.Count ? _extraLanes[i - 1] : null);

        /// <summary>ОДИН ХОД НА ВСЕХ ЛЕНТАХ: столько круток, сколько лент (и
        /// сколько по карману), все едут разом — быстро (0,6 с и 0,4 с показ)
        /// или как обычная крутка (шесть кругов за семь секунд и вспышка).
        /// Валюта — суммой в подпись; редкое — церемонией по очереди: в авто
        /// приз принимается сам через 2,4 с после «Забрать» (тап — раньше) и
        /// через 0,4 с лента едет дальше, вручную ждём «Забрать». Ленты и их
        /// положение ход не трогает. Возвращает ошибку сервера, если ход не
        /// состоялся или состоялся не на всех лентах.</summary>
        private async Task<string> RoundAsync(bool fast, bool auto)
        {
            _skipAsked = false;
            Say(null);
            int n = 1 + _extraLanes.Count;
            if (!_state.FreeToday && _state.SpinPrice > 0)
                n = Mathf.Clamp((int)(LvnWallet.Balance(_state.SpinCurrency) / _state.SpinPrice), 1, n);
            var tasks = new List<Task<LvnGacha.Spin>>(n);
            for (int i = 0; i < n; i++) tasks.Add(SpinWithTimeoutAsync());
            var spins = await Task.WhenAll(tasks);
            if (_closed) return "closed";
            var ok = new List<LvnGacha.Spin>();
            string error = null;
            foreach (var sp in spins) { if (string.IsNullOrEmpty(sp.Error)) ok.Add(sp); else error = sp.Error; }
            if (ok.Count == 0)
            {
                if (error == "insufficient_funds") _needTopUp = true;
                Say(error == "insufficient_funds"
                    ? LvnWords.Of("gacha.no_funds", "Not enough for a spin")
                    : LvnWords.Of("gacha.failed", "The spin did not go through. Try again."));
                return error ?? "failed";
            }
            var last = ok[ok.Count - 1];
            _state.FreeToday = last.FreeToday;
            _state.PrizesLeft = last.PrizesLeft;
            _state.Spins += ok.Count;
            bool walletStale = false;
            var rolls = new List<Task>();
            for (int i = 0; i < ok.Count; i++)
            {
                var lane = LaneAt(i);
                if (lane == null) continue;
                if (ok[i].Super) _poolDirty = true;   // в пуле приз станет «есть»; лента не сбрасывается
                if (!ok[i].WalletSynced) walletStale = true;
                rolls.Add(fast
                    ? RollLaneAsync(lane, LandingIndex(ok[i], lane), 1, FastSpinSeconds)
                    : RollLaneAsync(lane, LandingIndex(ok[i], lane), SpinLaps, SpinSeconds));
            }
            await Task.WhenAll(rolls);
            if (_closed) return "closed";
            if (walletStale) LvnAsync.Fire(LvnWallet.RefreshAsync(), "GachaWalletRetry");
            var rares = new List<LvnGacha.Spin>();
            foreach (var sp in ok)
            {
                if (sp.Super) { rares.Add(sp); if (sp.SoldAmount > 0 && !string.IsNullOrEmpty(sp.SoldCurrency)) Tip(sp.SoldCurrency, sp.SoldAmount); continue; }
                if (sp.Amount > 0 && !string.IsNullOrEmpty(sp.Currency)) Tip(sp.Currency, sp.Amount);
            }
            if (_spinning && _auto) PaintRunning(auto: true);   // строка выигрыша над кнопкой растёт с каждым ходом
            // ОСТАНОВКА И ЕСТЬ ОТКРЫТИЕ: быстро — клетка горит 0,4 с, обычно —
            // вспышка; редкое тоже показывается в ленте, а уже потом церемония.
            var shows = new List<Task>();
            for (int i = 0; i < ok.Count; i++)
            {
                var lane = LaneAt(i);
                if (lane == null) continue;
                shows.Add(fast ? ShowLandingAsync(lane, AutoShowMs, ok[i].Super ? ok[i].Prize : null) : StopFlashAsync(lane));
            }
            await Task.WhenAll(shows);
            if (_closed) return "closed";
            // НЕСКОЛЬКО РЕДКИХ ЗА ХОД: церемонии по очереди.
            foreach (var rare in rares)
            {
                _taken = new TaskCompletionSource<bool>();
                await RevealPrizeAsync(rare);
                if (_closed) return "closed";
                if (auto)
                {
                    await Task.WhenAny(_taken.Task, WaitOrTapAsync(AutoTakeMs));
                    if (_closed) return "closed";
                    // Игрок нажал «Продать» — авто не закрывает показ у него под рукой (TR-124).
                    while (_sellingPrize && !_closed) await Task.Yield();
                    if (_closed) return "closed";
                    if (!_taken.Task.IsCompleted) TakeNow(rare);
                    await WaitOrTapAsync(AutoResumeMs);
                }
                else await _taken.Task;   // вручную — ждём «Забрать»
                if (_closed) return "closed";
            }
            return error;
        }

        /// <summary>Обычная крутка: один ход на всех лентах, потом кнопка снова.</summary>
        internal async Task SpinAsync()
        {
            if (_spinning || _closed || _state == null) return;
            _spinning = true; _win.Clear();
            PaintRunning(auto: false);
            _reward.style.display = DisplayStyle.None;
            LvnAsync.Fire(HidePoolAsync(), "GachaPoolHide");
            try { await RoundAsync(fast: false, auto: false); }
            finally
            {
                _spinning = false;
                if (!_closed && _blackout == null) PaintIdle();
            }
        }

        /// <summary>Авто: ход за ходом, пока не «Стоп», не «Выкл», не кончились
        /// деньги и не закрыли экран.</summary>
        private async Task AutoAsync()
        {
            if (_spinning || _closed || _state == null) return;
            _auto = true; _spinning = true; _win.Clear();
            PaintRunning(auto: true);
            _reward.style.display = DisplayStyle.None;
            LvnAsync.Fire(HidePoolAsync(), "GachaPoolHide");
            try
            {
                while (_auto && !_closed && _state != null && Mode != AutoMode.Off && (_state.FreeToday || _state.SpinPrice > 0))
                {
                    var error = await RoundAsync(fast: Mode == AutoMode.Fast, auto: true);
                    if (error != null) break;
                }
            }
            finally
            {
                _auto = false; _spinning = false;
                if (!_closed && _blackout == null) PaintIdle();
            }
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
            int n = lane.Strip.childCount;
            if (n == 0) return null;
            int idx = (int)((((long)System.Math.Round(lane.Pos)) % n + n) % n);
            return lane.Strip[idx];
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
