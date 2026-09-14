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
        private readonly List<LvnGacha.Sector> _cells = new List<LvnGacha.Sector>();
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
        /// <summary>Дверь в магазин: при нехватке валюты вместо «Крутить» —
        /// «Пополнить» (TR-107). Вешает хозяин витрины.</summary>
        public Func<Task> OpenStore;
        private bool _needTopUp;
        private const int MaxLanes = 3;
        private const float FastSpinSeconds = 0.5f;
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
            content.contentContainer.style.justifyContent = Justify.Center;
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
            var needle = new VisualElement { pickingMode = PickingMode.Ignore };
            needle.style.position = Position.Absolute;
            needle.style.top = 0; needle.style.bottom = 0;
            needle.style.left = Length.Percent(50f);
            needle.style.width = 2f;
            needle.style.backgroundColor = LvnTokens.Gold;
            _window.Add(needle);
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

        private void BuildStrip()
        {
            _strip.Clear(); _cells.Clear();
            var sectors = _state?.Sectors;
            if (sectors == null || sectors.Count == 0) return;
            for (int i = 0; i < sectors.Count * RenderLaps; i++)
            {
                var sector = sectors[i % sectors.Count];
                _cells.Add(sector);
                _strip.Add(Cell(sector));
            }
            _sectorsDirty = false;
            Render(_main);
        }

        private VisualElement Cell(LvnGacha.Sector sector)
        {
            var cell = new VisualElement { pickingMode = PickingMode.Ignore };
            cell.style.width = CellWidth;
            cell.style.flexShrink = 0;
            cell.style.marginRight = LvnTokens.Space1;
            cell.style.alignItems = Align.Center;
            cell.style.justifyContent = Justify.Center;
            cell.style.backgroundColor = LvnTokens.Surface;
            LvnChrome.Round(cell, LvnTokens.RadiusSm);
            if (sector.Super) LvnChrome.Frame(cell, LvnTokens.RadiusSm, LvnTokens.Gold, 2f);
            cell.Add(sector.Super ? LvnIcons.Make(LvnIcon.Gift, LvnStageKit.D(32f), LvnTokens.Gold)
                : LvnIcons.MakeCurrency(sector.Currency, LvnStageKit.D(32f)));
            var label = new Label(sector.Super ? LvnWords.Of("gacha.super", "Rare") : LvnPriceTag.Amount(sector.Amount));
            label.style.color = sector.Super ? LvnTokens.Gold : LvnTokens.Text;
            label.style.fontSize = LvnTokens.TextLg;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.marginTop = LvnTokens.Space1;
            cell.Add(label);
            return cell;
        }

        private float WindowWidth => float.IsNaN(_window.resolvedStyle.width) || _window.resolvedStyle.width <= 0
            ? 640f : _window.resolvedStyle.width;
        private float CellWidth => Mathf.Max(1f, (WindowWidth - LvnTokens.Space1 * Visible) / Visible);
        private int SectorCount => Mathf.Max(1, _state?.Sectors?.Count ?? 1);

        /// <summary>Положить ленту по позиции барабана: под стрелкой — ячейка
        /// среднего круга с номером (позиция mod N).</summary>
        private void Render(Lane lane)
        {
            if (lane?.Strip == null) return;
            int n = SectorCount;
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
            await OpenStore();
            if (_closed) return;
            await LvnWallet.RefreshAsync();    // вернулись из магазина — кошелёк мог вырасти
            if (!_closed) PaintIdle();
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
            var needle = new VisualElement { pickingMode = PickingMode.Ignore };
            needle.style.position = Position.Absolute;
            needle.style.top = 0; needle.style.bottom = 0;
            needle.style.left = Length.Percent(50f);
            needle.style.width = 2f;
            needle.style.backgroundColor = LvnTokens.Gold;
            lane.Window.Add(needle);
            var sectors = _state?.Sectors;
            if (sectors != null && sectors.Count > 0)
                for (int i = 0; i < sectors.Count * RenderLaps; i++)
                    lane.Strip.Add(Cell(sectors[i % sectors.Count]));
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
                        if (lane != null) rolls.Add(RollLaneAsync(lane, SectorIndex(ok[i].SectorId), 1, FastSpinSeconds));
                    }
                    await Task.WhenAll(rolls);
                    if (_closed) return;
                    LvnGacha.Spin rare = null;
                    long sum = 0; string cur = null;
                    foreach (var sp in ok)
                    {
                        if (sp.Super) { rare ??= sp; continue; }
                        sum += sp.Amount; cur ??= sp.Currency;
                    }
                    if (rare != null)
                    {
                        _auto = false; _spinning = false;
                        await RevealPrizeAsync(rare);        // редкое — церемония и стоп
                        return;
                    }
                    _status.text = LvnWords.Of("gacha.won_currency", "You got: {0}", LvnPriceTag.Full(cur, sum));
                    await Task.Delay(200);
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
                await RollLaneAsync(_main, SectorIndex(spin.SectorId), SpinLaps, SpinSeconds);
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

        private int SectorIndex(string id)
        {
            var sectors = _state?.Sectors;
            if (sectors == null) return 0;
            for (int i = 0; i < sectors.Count; i++) if (sectors[i].Id == id) return i;
            return 0;
        }

        /// <summary>Прокрутить барабан вперёд на <paramref name="laps"/> кругов до
        /// сектора <paramref name="target"/>: позиция только растёт, лента не
        /// перестраивается.</summary>
        private async Task RollLaneAsync(Lane lane, int target, int laps, float seconds)
        {
            int n = SectorCount;
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

        /// <summary>Ячейка под стрелкой коротко «вспыхивает» — момент остановки читается как открытие.</summary>
        private async Task StopFlashAsync(Lane lane)
        {
            if (LvnPrefs.ReduceMotion || lane?.Strip == null) return;
            int n = SectorCount;
            int idx = n + (int)((((long)System.Math.Round(lane.Pos)) % n + n) % n);
            if (idx < 0 || idx >= lane.Strip.childCount) return;
            var cell = lane.Strip[idx];
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
