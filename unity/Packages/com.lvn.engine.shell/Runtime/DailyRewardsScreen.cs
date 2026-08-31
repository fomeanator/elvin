using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The daily-login rewards calendar — a centred modal (scrim + a capped-width
    /// card, not full-bleed) that every free-to-play game ships: seven day-cells in
    /// a wrapping grid, each showing its reward (⚡ energy / ◆ gold, alternating) and
    /// amount. Past days read as CLAIMED (dim + ✓), today is the highlighted,
    /// CLAIMABLE cell (an Accent border + a slightly bigger tile), and the rest are
    /// plain Surface FUTURE cells. Day 7 is an oversized "premium" tile. A single
    /// primary "Забрать" call to action sits at the bottom, live only for today.
    ///
    /// Fully self-contained: the streak state and the reward ladder are hardcoded
    /// demo data (<see cref="Day"/> ladder + <see cref="_currentDay"/>), so the
    /// screen renders and resolves without any server or wallet wired in. It mirrors
    /// <see cref="StoreScreen"/>'s fade/TCS lifecycle: <see cref="ShowAsync"/> fades
    /// in, waits on a <see cref="TaskCompletionSource{TResult}"/> that Close (✕) or
    /// the claim button resolves, then fades out. All colours come from
    /// <see cref="LvnTokens"/> ("Полночь").
    /// </summary>
    public sealed class DailyRewardsScreen : LvnOverlayScreen
    {
        /// <summary>Called when the player taps "Забрать" for the live day.
        /// Argument: the day number (1-based). Hosts wire this to their wallet;
        /// the demo just flips the cell to CLAIMED.</summary>
        public Action<int> OnClaim;

        private readonly ILvnAssets _assets;
        private readonly VisualElement _grid;
        private readonly Label _subtitle;
        private readonly Button _claim;


        // ── Hardcoded demo data ────────────────────────────────────────────────
        // The reward ladder: seven days, alternating energy/gold, day 7 premium.
        private readonly struct Day
        {
            public readonly int Amount;
            public readonly bool Gold;   // true → ◆ gold, false → ⚡ energy
            public Day(int amount, bool gold) { Amount = amount; Gold = gold; }
        }

        private static readonly Day[] Ladder =
        {
            new Day(50,  false), // День 1 — ⚡
            new Day(80,  true),  // День 2 — ◆
            new Day(120, false), // День 3 — ⚡
            new Day(150, true),  // День 4 — ◆
            new Day(200, false), // День 5 — ⚡
            new Day(300, true),  // День 6 — ◆
            new Day(1000, true), // День 7 — ◆ premium
        };

        // Какой сегодня день серии (с единицы). Дни до него — ЗАБРАНЫ, этот —
        // сегодняшний, остальные впереди.
        //
        // ЗДЕСЬ СТОЯЛА ПЯТЁРКА. Экран рисовал «День 5» всем и всегда: четыре
        // ячейки с галочками, которых игрок не забирал, и премию на седьмой день
        // через двое суток после установки. Живой стрик сервис знал
        // (LvnDaily.GetAsync), но до экрана его никто не доносил — хост подключил
        // только начисление. Умолчание теперь честное: первый день, ничего не
        // забрано; настоящее состояние приходит через SetStatus.
        private int _currentDay = 1;
        private bool _claimed;

        /// <summary>
        /// СОСТОЯНИЕ СЕРИИ ОТ СЕРВИСА: сколько дней подряд и забрано ли сегодня.
        ///
        /// <para>Экран не решает, какой сегодня день, — он его показывает.
        /// Серия считается на сервере (там же, где начисление), потому что иначе
        /// её сбрасывал бы переустановленный клиент и подкручивали бы часы
        /// устройства.</para>
        /// </summary>
        public void SetStatus(int streak, bool claimedToday)
        {
            // Забрано сегодня — стоим на этом дне; не забрано — сегодняшний день
            // следующий за серией. Ниже нуля и выше лестницы не уходим: сервер
            // вправе вести серию дальше семи дней, лестница на этом кончается.
            int day = claimedToday ? streak : streak + 1;
            _currentDay = Mathf.Clamp(day <= 0 ? 1 : day, 1, Ladder.Length);
            _claimed = claimedToday;
            Rebuild();
        }

        public DailyRewardsScreen(ILvnAssets assets)
        {
            _assets = assets;

            style.backgroundColor = LvnTokens.Scrim;
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            // tap the scrim (not the card) to close
            RegisterCallback<ClickEvent>(evt => { if (evt.target == this) Close(); });

            // ── The centred modal card (capped width, auto height) ─────────────
            var card = new VisualElement();
            card.style.width = Length.Percent(90f);
            card.style.maxWidth = 760;
            card.style.backgroundColor = LvnTokens.PanelBg;
            LvnChrome.Round(card, LvnTokens.Radius + 4f);
            LvnChrome.Border(card, LvnTokens.Border, 1f);
            card.style.paddingTop = 26;
            card.style.paddingBottom = 22;
            card.style.paddingLeft = 24;
            card.style.paddingRight = 24;
            Add(card);
            AdoptSheet(card); // единый враппер попапа: стекло, окантовка, подъезд

            // ── Header: title + subtitle on the left, Close (✕) top-right ───────
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.FlexStart;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.marginBottom = 20;
            card.Add(header);

            var titleCol = new VisualElement();
            titleCol.style.flexGrow = 1;
            header.Add(titleCol);

            var title = SectionTitle(() => LvnWords.Of("daily.title", "Daily reward"));
            titleCol.Add(title);

            _subtitle = new Label();
            _subtitle.style.color = LvnTokens.TextDim;
            _subtitle.style.fontSize = Lvn.UI.LvnFonts.Size(26f);
            _subtitle.style.marginTop = 4;
            titleCol.Add(_subtitle);

            var close = new Button(Close) { text = "" };
            LvnStyler.IconSlot(close, 44f);
            close.style.marginLeft = 12;
            header.Add(close);

            // ── The 7-day grid (wraps: 4 + 3) ──────────────────────────────────
            _grid = new VisualElement();
            _grid.style.flexDirection = FlexDirection.Row;
            _grid.style.flexWrap = Wrap.Wrap;
            _grid.style.justifyContent = Justify.Center;
            card.Add(_grid);

            // ── The primary claim call to action ───────────────────────────────
            // Надпись читает СОСТОЯНИЕ: назначь её руками — и смена языка на
            // открытом экране вернула бы «Забрать» уже забранной награде.
            _claim = Lvn.UI.LvnRedress.Bind(new Button(ClaimToday), () => _claimed
                ? LvnWords.Of("daily.claimed", "Claimed")
                : LvnWords.Of("daily.claim", "Claim"));
            _claim.style.fontSize = Lvn.UI.LvnFonts.Size(28f);
            _claim.style.unityFontStyleAndWeight = FontStyle.Bold;
            _claim.style.marginTop = 22;
            _claim.style.paddingTop = 16;
            _claim.style.paddingBottom = 16;
            LvnStyler.Primary(_claim, LvnTokens.RadiusSm);
            card.Add(_claim);

            Rebuild();
        }


        private void ClaimToday()
        {
            if (_claimed) return;
            _claimed = true;
            OnClaim?.Invoke(_currentDay);
            // Advance the streak: today becomes claimed, the calendar rolls forward.
            if (_currentDay < Ladder.Length) _currentDay++;
            Rebuild();
            Close();
        }

        /// <summary>Re-render the subtitle, the seven cells, and the claim button
        /// from the current streak state. Safe to call any number of times.</summary>
        /// <summary>Слова, шрифт или размеры сменились — перечитать их.</summary>

        public override void Rebuild()
        {
            _subtitle.text = LvnWords.Of("daily.day", "Day {0}", _currentDay);

            _grid.Clear();
            for (int i = 0; i < Ladder.Length; i++)
            {
                int day = i + 1;
                State state = _claimed && day == _currentDay ? State.Claimed
                    : day < _currentDay ? State.Claimed
                    : day == _currentDay ? State.Today
                    : State.Future;
                _grid.Add(Cell(day, Ladder[i], state, premium: day == Ladder.Length));
            }

            bool canClaim = !_claimed;
            Lvn.UI.LvnRedress.Refresh(_claim);
            _claim.SetEnabled(canClaim);
            _claim.style.opacity = canClaim ? 1f : 0.5f;
        }

        private enum State { Claimed, Today, Future }

        private VisualElement Cell(int day, Day reward, State state, bool premium)
        {
            var cell = new VisualElement();
            cell.style.width = premium ? 172 : 148;
            cell.style.height = state == State.Today ? 176 : 160;
            cell.style.marginLeft = 6;
            cell.style.marginRight = 6;
            cell.style.marginTop = 6;
            cell.style.marginBottom = 6;
            cell.style.alignItems = Align.Center;
            cell.style.justifyContent = Justify.Center;
            cell.style.paddingTop = 12;
            cell.style.paddingBottom = 12;
            cell.style.paddingLeft = 10;
            cell.style.paddingRight = 10;
            LvnChrome.Round(cell, LvnTokens.RadiusSm);

            // Fills & borders per state.
            switch (state)
            {
                case State.Today:
                    cell.style.backgroundColor = LvnTokens.SurfaceHi;
                    LvnChrome.Edge(cell);
                    cell.style.borderLeftWidth = 2;
                    cell.style.borderRightWidth = 2;
                    cell.style.borderTopWidth = 2;
                    cell.style.borderBottomWidth = 2;
                    LvnChrome.Tint(cell, LvnTokens.Accent);
                    break;
                case State.Claimed:
                    cell.style.backgroundColor = LvnTokens.Surface;
                    LvnChrome.Edge(cell);
                    cell.style.opacity = 0.5f;
                    LvnChrome.ClearBorder(cell);
                    break;
                default: // Future
                    cell.style.backgroundColor = LvnTokens.Surface;
                    LvnChrome.Edge(cell);
                    cell.style.borderLeftWidth = 1;
                    cell.style.borderRightWidth = 1;
                    cell.style.borderTopWidth = 1;
                    cell.style.borderBottomWidth = 1;
                    LvnChrome.Tint(cell, LvnTokens.Border);
                    break;
            }

            // Day label.
            // Подпись дня, а у премиального — ещё и звезда РЯДОМ, отдельным
            // элементом: приписывать её к строке значило бы снова полагаться на
            // то, что нужный символ найдётся в шрифте телефона.
            var labelRow = new VisualElement();
            labelRow.style.flexDirection = FlexDirection.Row;
            labelRow.style.alignItems = Align.Center;
            labelRow.style.justifyContent = Justify.Center;
            labelRow.style.marginBottom = 8;
            var label = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("daily.day", "Day {0}", day));
            label.style.color = state == State.Today ? LvnTokens.Text : LvnTokens.TextDim;
            label.style.fontSize = Lvn.UI.LvnFonts.Size(20f);
            label.style.unityFontStyleAndWeight = premium ? FontStyle.Bold : FontStyle.Normal;
            labelRow.Add(label);
            if (premium)
            {
                var pin = LvnIcons.Make(LvnIcon.Star, 16f, LvnTokens.Gold);
                pin.style.marginLeft = 5;
                labelRow.Add(pin);
            }
            cell.Add(labelRow);

            // Reward icon (⚡ energy / ◆ gold).
            var icon = LvnIcons.Make(reward.Gold ? LvnIcon.Gem : LvnIcon.Energy,
                                     premium ? 48f : 40f,
                                     reward.Gold ? LvnTokens.Gold : LvnTokens.Accent,
                                     0f, LvnTheme.Current.IconGlow);
            icon.style.alignSelf = Align.Center;
            cell.Add(icon);

            // Amount.
            var amount = new Label("+" + LvnPriceTag.Amount(reward.Amount));
            amount.style.color = reward.Gold ? LvnTokens.Gold : LvnTokens.Text;
            amount.style.fontSize = premium ? 26 : 24;
            amount.style.unityFontStyleAndWeight = FontStyle.Bold;
            amount.style.marginTop = 6;
            cell.Add(amount);

            // CLAIMED tick badge, TODAY "сегодня" pill.
            if (state == State.Claimed)
            {
                var tick = LvnIcons.Make(LvnIcon.Check, 22f, LvnTokens.Accent);
                tick.style.position = Position.Absolute;
                tick.style.top = 6;
                tick.style.right = 10;
                cell.Add(tick);
            }
            else if (state == State.Today)
            {
                var badge = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("daily.today", "today"));
                badge.style.position = Position.Absolute;
                badge.style.top = 6;
                badge.style.right = 8;
                badge.style.fontSize = Lvn.UI.LvnFonts.Size(18f);
                badge.style.color = LvnTokens.OnAccent;
                badge.style.backgroundColor = LvnTokens.Accent;
                badge.style.paddingLeft = 8;
                badge.style.paddingRight = 8;
                badge.style.paddingTop = 2;
                badge.style.paddingBottom = 2;
                LvnChrome.Round(badge, LvnTokens.RadiusSm - 4f);
                cell.Add(badge);
            }

            return cell;
        }

    }
}
