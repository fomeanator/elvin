using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
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
    public sealed class DailyRewardsScreen : VisualElement
    {
        /// <summary>Called when the player taps "Забрать" for the live day.
        /// Argument: the day number (1-based). Hosts wire this to their wallet;
        /// the demo just flips the cell to CLAIMED.</summary>
        public Action<int> OnClaim;

        private readonly ILvnAssets _assets;
        private readonly VisualElement _grid;
        private readonly Label _subtitle;
        private readonly Button _claim;

        private TaskCompletionSource<bool> _tcs;
        private bool _open;

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

        // Which day the player is on (1-based). Days below this are CLAIMED, this
        // one is CLAIMABLE (today), the rest are FUTURE.
        private int _currentDay = 5;
        private bool _claimed;

        public DailyRewardsScreen(ILvnAssets assets)
        {
            _assets = assets;

            ScreenUi.Stretch(this);
            style.backgroundColor = LvnTokens.Scrim;
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            // tap the scrim (not the card) to close
            RegisterCallback<ClickEvent>(evt => { if (evt.target == this) Close(); });

            // ── The centred modal card (capped width, auto height) ─────────────
            var card = new VisualElement();
            card.style.width = Length.Percent(90f);
            card.style.maxWidth = 760;
            card.style.backgroundColor = LvnTokens.PanelBg;
            Round(card, LvnTokens.Radius + 4f);
            card.style.borderLeftWidth = 1;
            card.style.borderRightWidth = 1;
            card.style.borderTopWidth = 1;
            card.style.borderBottomWidth = 1;
            SetBorderColor(card, LvnTokens.Border);
            card.style.paddingTop = 26;
            card.style.paddingBottom = 22;
            card.style.paddingLeft = 24;
            card.style.paddingRight = 24;
            Add(card);

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

            var title = new Label("Ежедневная награда");
            title.style.color = LvnTokens.Text;
            title.style.fontSize = 42;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleCol.Add(title);

            _subtitle = new Label();
            _subtitle.style.color = LvnTokens.TextDim;
            _subtitle.style.fontSize = 26;
            _subtitle.style.marginTop = 4;
            titleCol.Add(_subtitle);

            var close = new Button(Close) { text = "✕" };
            close.style.fontSize = 28;
            close.style.width = 44;
            close.style.height = 44;
            close.style.marginLeft = 12;
            close.style.paddingTop = 0;
            close.style.paddingBottom = 0;
            close.style.paddingLeft = 0;
            close.style.paddingRight = 0;
            close.style.color = LvnTokens.Text;
            close.style.backgroundColor = LvnTokens.Faint;
            ClearBorder(close);
            Round(close, LvnTokens.RadiusSm);
            header.Add(close);

            // ── The 7-day grid (wraps: 4 + 3) ──────────────────────────────────
            _grid = new VisualElement();
            _grid.style.flexDirection = FlexDirection.Row;
            _grid.style.flexWrap = Wrap.Wrap;
            _grid.style.justifyContent = Justify.Center;
            card.Add(_grid);

            // ── The primary claim call to action ───────────────────────────────
            _claim = new Button(ClaimToday) { text = "Забрать" };
            _claim.style.fontSize = 28;
            _claim.style.unityFontStyleAndWeight = FontStyle.Bold;
            _claim.style.marginTop = 22;
            _claim.style.paddingTop = 16;
            _claim.style.paddingBottom = 16;
            _claim.style.color = LvnTokens.OnAccent;
            _claim.style.backgroundColor = LvnTokens.Accent;
            ClearBorder(_claim);
            Round(_claim, LvnTokens.RadiusSm);
            card.Add(_claim);

            Rebuild();
        }

        /// <summary>Open the calendar: fade in, wait until the player claims or
        /// closes it, then fade out. Mirrors <see cref="StoreScreen.ShowAsync"/>.</summary>
        public async Task ShowAsync(CancellationToken ct = default)
        {
            if (_open) return;
            _open = true;
            style.display = DisplayStyle.Flex;
            Rebuild();
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.25f, ct);
            // Hide() during the fade-in must cancel the open, not leave this await
            // parked on a _tcs nobody will ever resolve.
            if (!_open) return;

            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            try { await _tcs.Task; }
            finally
            {
                await ScreenFx.FadeAsync(this, 1f, 0f, 0.25f, CancellationToken.None);
                style.display = DisplayStyle.None;
                _open = false;
            }
        }

        public void Hide()
        {
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _open = false;
            _tcs?.TrySetResult(false);
        }

        private void Close() => _tcs?.TrySetResult(true);

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
        public void Rebuild()
        {
            _subtitle.text = $"День {_currentDay}";

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
            _claim.text = _claimed ? "Награда получена" : "Забрать";
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
            Round(cell, LvnTokens.RadiusSm);

            // Fills & borders per state.
            switch (state)
            {
                case State.Today:
                    cell.style.backgroundColor = LvnTokens.SurfaceHi;
                    cell.style.borderLeftWidth = 2;
                    cell.style.borderRightWidth = 2;
                    cell.style.borderTopWidth = 2;
                    cell.style.borderBottomWidth = 2;
                    SetBorderColor(cell, LvnTokens.Accent);
                    break;
                case State.Claimed:
                    cell.style.backgroundColor = LvnTokens.Surface;
                    cell.style.opacity = 0.5f;
                    ClearBorder(cell);
                    break;
                default: // Future
                    cell.style.backgroundColor = LvnTokens.Surface;
                    cell.style.borderLeftWidth = 1;
                    cell.style.borderRightWidth = 1;
                    cell.style.borderTopWidth = 1;
                    cell.style.borderBottomWidth = 1;
                    SetBorderColor(cell, LvnTokens.Border);
                    break;
            }

            // Day label.
            var label = new Label(premium ? $"День {day} ★" : $"День {day}");
            label.style.color = state == State.Today ? LvnTokens.Text : LvnTokens.TextDim;
            label.style.fontSize = 20;
            label.style.unityFontStyleAndWeight = premium ? FontStyle.Bold : FontStyle.Normal;
            label.style.marginBottom = 8;
            cell.Add(label);

            // Reward icon (⚡ energy / ◆ gold).
            var icon = new Label(reward.Gold ? "◆" : "⚡");
            icon.style.fontSize = premium ? 48 : 40;
            icon.style.color = reward.Gold ? LvnTokens.Gold : LvnTokens.Accent;
            icon.style.unityTextAlign = TextAnchor.MiddleCenter;
            cell.Add(icon);

            // Amount.
            var amount = new Label($"+{reward.Amount:N0}");
            amount.style.color = reward.Gold ? LvnTokens.Gold : LvnTokens.Text;
            amount.style.fontSize = premium ? 26 : 24;
            amount.style.unityFontStyleAndWeight = FontStyle.Bold;
            amount.style.marginTop = 6;
            cell.Add(amount);

            // CLAIMED tick badge, TODAY "сегодня" pill.
            if (state == State.Claimed)
            {
                var tick = new Label("✓");
                tick.style.position = Position.Absolute;
                tick.style.top = 6;
                tick.style.right = 10;
                tick.style.fontSize = 24;
                tick.style.color = LvnTokens.Accent;
                cell.Add(tick);
            }
            else if (state == State.Today)
            {
                var badge = new Label("сегодня");
                badge.style.position = Position.Absolute;
                badge.style.top = 6;
                badge.style.right = 8;
                badge.style.fontSize = 18;
                badge.style.color = LvnTokens.OnAccent;
                badge.style.backgroundColor = LvnTokens.Accent;
                badge.style.paddingLeft = 8;
                badge.style.paddingRight = 8;
                badge.style.paddingTop = 2;
                badge.style.paddingBottom = 2;
                Round(badge, LvnTokens.RadiusSm - 4f);
                cell.Add(badge);
            }

            return cell;
        }

        private static void SetBorderColor(VisualElement el, Color c)
        {
            el.style.borderLeftColor = c;
            el.style.borderRightColor = c;
            el.style.borderTopColor = c;
            el.style.borderBottomColor = c;
        }

        private static void Round(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r;
            el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r;
            el.style.borderBottomRightRadius = r;
        }

        private static void ClearBorder(VisualElement el)
        {
            el.style.borderLeftWidth = 0;
            el.style.borderRightWidth = 0;
            el.style.borderTopWidth = 0;
            el.style.borderBottomWidth = 0;
        }
    }
}
