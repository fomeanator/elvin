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
        private int _landing = -1;
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
            content.Add(_status);
            _actions = new VisualElement { name = "gacha-actions" };
            _actions.style.flexShrink = 0;
            _actions.style.marginTop = LvnTokens.Space3;
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
            _strip.Clear(); _cells.Clear(); _landing = -1;
            _strip.style.left = 0;
            var sectors = _state?.Sectors;
            if (sectors == null || sectors.Count == 0) return;
            for (int i = 0; i < sectors.Count * (SpinLaps + 2); i++)
            {
                var sector = sectors[i % sectors.Count];
                _cells.Add(sector);
                _strip.Add(Cell(sector));
            }
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
        private float LandingOffset => _landing * (CellWidth + LvnTokens.Space1) - WindowWidth * 0.5f + CellWidth * 0.5f;
        private void LayoutStrip()
        {
            foreach (var cell in _strip.Children()) cell.style.width = CellWidth;
            if (!_spinning && _landing >= 0) _strip.style.left = -LandingOffset;
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

        private void PaintIdle()
        {
            DismissCeremony();
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
            var button = ActionButton("gacha-spin", () => _state.FreeToday ? LvnWords.Of("gacha.spin", "Spin") : "",
                () => LvnAsync.Fire(SpinAsync(), "GachaSpin"));
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

        internal async Task SpinAsync()
        {
            if (_spinning || _closed || _state == null) return;
            _spinning = true; _skipAsked = false;
            _actions.Clear();
            _reward.style.display = DisplayStyle.None;
            _status.text = LvnWords.Of("gacha.spinning", "Spinning…");
            try
            {
                var spin = await LvnGacha.SpinAsync();
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
                _landing = LandingCell(spin.SectorId);
                await RollAsync();
                if (_closed) return;
                _state.FreeToday = spin.FreeToday;
                _state.PrizesLeft = spin.PrizesLeft;
                _state.Spins++;
                // Keep the landed rare cell until the player acknowledges it.
                if (spin.PrizesLeft.Count == 0) _state.Sectors.RemoveAll(s => s.Super);
                await RevealPrizeAsync(spin);
            }
            finally { _spinning = false; }
        }

        private int LandingCell(string id)
        {
            int from = Mathf.Max(0, _cells.Count - (_state?.Sectors?.Count ?? 1) * 2);
            for (int i = from; i < _cells.Count; i++) if (_cells[i].Id == id) return i;
            return Mathf.Max(0, _cells.Count - 1);
        }

        private async Task RollAsync()
        {
            float start = Time.realtimeSinceStartup;
            while (!_closed && !_skipAsked && !LvnPrefs.ReduceMotion)
            {
                float progress = Mathf.Clamp01((Time.realtimeSinceStartup - start) / SpinSeconds);
                _strip.style.left = -LandingOffset * LvnMotion.Settle(progress);
                if (progress >= 1f) break;
                await Task.Yield();
            }
            _strip.style.left = -LandingOffset;
        }
    }
}
