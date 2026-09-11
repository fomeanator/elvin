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
    /// КРУТКИ (TR-47) — лента призов, которая едет мимо указателя.
    ///
    /// <para>Устройство взято у оружейных кейсов: полоса секторов уезжает
    /// влево, замедляется и останавливается так, чтобы под указателем оказался
    /// тот сектор, который НАЗВАЛ СЕРВЕР. Анимация ничего не решает — она
    /// показывает уже случившееся; тап по экрану её пропускает.</para>
    ///
    /// <para>Поэтому и честно: приз начислен ещё до первого кадра прокрутки, и
    /// закрытая на середине игра не отнимает выигрыш.</para>
    /// </summary>
    public sealed class GachaScreen : LvnOverlayScreen, ILvnContentAware
    {
        private readonly ILvnAssets _assets;
        private readonly VisualElement _sheet;
        private readonly Label _title;
        private readonly VisualElement _window;    // окно ленты с указателем
        private readonly VisualElement _strip;     // сама лента секторов
        private readonly Label _status;
        private readonly VisualElement _actions;

        private Lvn.Services.LvnGacha.Status _state;
        private string _skin;
        private bool _stageGlass;
        private bool _spinning;
        private bool _skipAsked;
        private bool StageDressed => !string.IsNullOrEmpty(_skin);

        /// <summary>Сколько секторов видно в окне разом (по задаче — четыре).</summary>
        private const int Visible = 4;

        /// <summary>Сколько длится прокрутка и сколько секторов пролетает.
        /// Семь секунд — по задаче; оборотов столько, чтобы лента успела
        /// разогнаться и осесть, а не дёрнуться на место.</summary>
        private const float SpinSeconds = 7f;
        private const int SpinLaps = 6;

        public GachaScreen(ILvnAssets assets)
        {
            _assets = assets;
            var sheet = _sheet = Sheet(sideInset: 6f, topInset: 18f);
            AdoptSheet(sheet);

            _title = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("gacha.title", "Spin"));
            sheet.Add(ScreenUi.GalleryHeader(Cancel, _title, out var counter));
            counter.style.display = DisplayStyle.None;

            // ОКНО С УКАЗАТЕЛЕМ: лента едет внутри, указатель стоит.
            _window = new VisualElement { name = "gacha-window" };
            _window.style.overflow = Overflow.Hidden;
            _window.style.marginTop = LvnTokens.Space3;
            _window.style.height = 150f;
            LvnChrome.Round(_window, LvnTokens.RadiusSm);
            _window.style.backgroundColor = LvnTokens.Veil(0.35f);
            sheet.Add(_window);

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

            _status = new Label();
            _status.style.color = LvnTokens.TextDim;
            _status.style.fontSize = LvnTokens.TextSm;
            _status.style.unityTextAlign = TextAnchor.MiddleCenter;
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.marginTop = LvnTokens.Space3;
            sheet.Add(_status);

            _actions = new VisualElement();
            _actions.style.marginTop = LvnTokens.Space3;
            sheet.Add(_actions);

            // Тап по листу пропускает анимацию — обещание задачи «если юзер
            // тапает по экрану, то скип».
            sheet.RegisterCallback<ClickEvent>(_ => { if (_spinning) _skipAsked = true; });
        }

        /// <inheritdoc cref="ILvnContentAware.SetContent"/>
        public void SetContent(LvnManifest manifest)
            => LvnStageKit.TakeSkin(manifest, ref _skin, StageDress);

        private void StageDress()
            => _stageGlass = LvnStageKit.DressSheet(_sheet, _skin, _assets, _stageGlass, _title);

        /// <summary>Открыть экран: спросить состояние и ждать, пока игрок
        /// крутит или уходит.</summary>
        public async Task RunAsync()
        {
            _state = await Lvn.Services.LvnGacha.GetAsync();
            BuildStrip();
            PaintIdle();
            await ShowAsync();
        }

        // ── лента ────────────────────────────────────────────────────────────

        private readonly List<Lvn.Services.LvnGacha.Sector> _cells = new List<Lvn.Services.LvnGacha.Sector>();

        /// <summary>Набить ленту повторами секторов: она должна быть длиннее
        /// окна настолько, чтобы прокрутка выглядела дорогой, а не рывком.</summary>
        private void BuildStrip()
        {
            _strip.Clear();
            _cells.Clear();
            var sectors = _state?.Sectors;
            if (sectors == null || sectors.Count == 0) return;
            int need = sectors.Count * (SpinLaps + 2);
            for (int i = 0; i < need; i++)
            {
                var sector = sectors[i % sectors.Count];
                _cells.Add(sector);
                _strip.Add(Cell(sector));
            }
        }

        private VisualElement Cell(Lvn.Services.LvnGacha.Sector s)
        {
            var cell = new VisualElement { pickingMode = PickingMode.Ignore };
            cell.style.width = CellWidth;
            cell.style.marginRight = LvnTokens.Space1;
            cell.style.alignItems = Align.Center;
            cell.style.justifyContent = Justify.Center;
            cell.style.backgroundColor = s.Super ? LvnTokens.Veil(0.7f) : LvnTokens.Surface;
            LvnChrome.Round(cell, LvnTokens.RadiusSm);
            if (s.Super) LvnChrome.Frame(cell, LvnTokens.RadiusSm, LvnTokens.Gold, 2f);

            var label = new Label(s.Super
                ? LvnWords.Of("gacha.super", "Rare")
                : s.Amount + " " + LvnPriceTag.Of(s.Currency).Unit);
            label.style.color = s.Super ? LvnTokens.Gold : LvnTokens.Text;
            label.style.fontSize = LvnTokens.TextSm;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.pickingMode = PickingMode.Ignore;
            cell.Add(label);
            return cell;
        }

        private float CellWidth => Mathf.Max(64f, (_window.resolvedStyle.width - LvnTokens.Space1 * Visible) / Visible);

        // ── кнопки и подписи ─────────────────────────────────────────────────

        private void PaintIdle()
        {
            _actions.Clear();
            if (_state == null)
            {
                _status.text = LvnWords.Of("gacha.offline", "Spins need a connection.");
                return;
            }
            _status.text = _state.FreeToday
                ? LvnWords.Of("gacha.free_ready", "Today's free spin is waiting")
                : _state.SpinPrice > 0
                    ? LvnWords.Of("gacha.price", "Next spin: {0}",
                                  _state.SpinPrice + " " + LvnPriceTag.Of(_state.SpinCurrency).Unit)
                    : LvnWords.Of("gacha.come_back", "Come back tomorrow for a free spin");

            bool can = _state.FreeToday || _state.SpinPrice > 0;
            if (!can) return;
            var btn = Lvn.UI.LvnRedress.Bind(new Button(() => LvnAsync.Fire(SpinAsync(), "GachaSpin")),
                () => LvnWords.Of("gacha.spin", "Spin"));
            btn.style.fontSize = LvnTokens.TextBase;
            btn.style.unityFontStyleAndWeight = FontStyle.Bold;
            LvnAir.PadY(btn, LvnTokens.Space3);
            LvnStyler.Primary(btn, LvnTokens.RadiusSm);
            _actions.Add(btn);
        }

        // ── прокрутка ────────────────────────────────────────────────────────

        private async Task SpinAsync()
        {
            if (_spinning) return;
            _spinning = true;
            _skipAsked = false;
            _actions.Clear();
            _status.text = LvnWords.Of("gacha.spinning", "Spinning…");

            var spin = await Lvn.Services.LvnGacha.SpinAsync();
            if (!string.IsNullOrEmpty(spin.Error))
            {
                _spinning = false;
                _status.text = spin.Error == "insufficient_funds"
                    ? LvnWords.Of("gacha.no_funds", "Not enough for a spin")
                    : LvnWords.Of("gacha.failed", "The spin did not go through. Try again.");
                PaintIdle();
                return;
            }

            int landing = LandingCell(spin.SectorId);
            await RollAsync(landing);
            _spinning = false;
            ShowResult(spin);
        }

        /// <summary>Какая ячейка ленты остановится под указателем. Берём
        /// ПОСЛЕДНИЙ круг: лента к тому времени уже разогналась, и торможение
        /// видно целиком.</summary>
        private int LandingCell(string sectorId)
        {
            if (_cells.Count == 0) return 0;
            int from = Mathf.Max(0, _cells.Count - (_state?.Sectors?.Count ?? 1) * 2);
            for (int i = from; i < _cells.Count; i++)
                if (_cells[i].Id == sectorId) return i;
            return _cells.Count - 1;
        }

        private async Task RollAsync(int landing)
        {
            float step = CellWidth + LvnTokens.Space1;
            float target = landing * step - _window.resolvedStyle.width * 0.5f + step * 0.5f;
            float from = 0f;
            float t = 0f;
            while (t < SpinSeconds && !_skipAsked)
            {
                t += Time.unscaledDeltaTime;
                // Торможение к концу: быстрый разгон, долгая осадка — так
                // читается «повезло или нет» на последних сантиметрах. Кривая
                // берётся у движения оболочки, а не пишется числом: почерк у
                // всех приходов один, и своя копия однажды разошлась бы с ним.
                float k = Mathf.Clamp01(t / SpinSeconds);
                float eased = LvnMotion.Settle(k);
                _strip.style.left = Mathf.Lerp(from, -target, eased);
                await Task.Yield();
            }
            _strip.style.left = -target;   // пропуск ставит ленту ровно на место
        }

        private void ShowResult(Lvn.Services.LvnGacha.Spin spin)
        {
            if (_state != null)
            {
                _state.FreeToday = spin.FreeToday;
                _state.PrizesLeft = spin.PrizesLeft;
                // Опустевший супер-сектор уходит из рулетки: сервер его уже не
                // прислал бы, но лента живёт до перестройки.
                if (spin.PrizesLeft.Count == 0)
                    _state.Sectors.RemoveAll(s => s.Super);
                BuildStrip();
            }
            _status.text = spin.Super && spin.Prize != null
                ? LvnWords.Of("gacha.won_prize", "You got: {0}", spin.Prize.Label ?? spin.Prize.Sku)
                : LvnWords.Of("gacha.won_currency", "You got: {0}",
                              spin.Amount + " " + LvnPriceTag.Of(spin.Currency).Unit);

            _actions.Clear();
            var take = Lvn.UI.LvnRedress.Bind(new Button(() => { LvnAsync.Fire(Lvn.Services.LvnWallet.NudgeAsync(), "GachaWallet"); PaintIdle(); }),
                () => LvnWords.Of("gacha.take", "Take the prize"));
            take.style.fontSize = LvnTokens.TextBase;
            take.style.unityFontStyleAndWeight = FontStyle.Bold;
            LvnAir.PadY(take, LvnTokens.Space3);
            LvnStyler.Primary(take, LvnTokens.RadiusSm);
            _actions.Add(take);
        }
    }
}
