using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The choice layer (z-order 4): a centered stack of option buttons, each a
    /// caption with an optional narrative-cost line beneath. Raises
    /// <see cref="OnSelected"/> with the picked <see cref="LvnOption.Index"/>.
    /// Options gated out by the player never reach here.
    /// </summary>
    public sealed class ChoiceList : VisualElement
    {
        private readonly VnTheme _theme;

        /// <summary>Fires with the chosen option's <see cref="LvnOption.Index"/>.</summary>
        public event Action<int> OnSelected;

        /// <summary>Fires when the options appear (true) / are dismissed (false).
        /// The shell listens to surface reading-mode chrome (e.g. a HUD that hides
        /// during plain reading but must be visible while a priced choice is up).</summary>
        public event Action<bool> VisibleChanged;

        public ChoiceList(VnTheme theme)
        {
            _theme = theme ?? new VnTheme();
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.top = 0;
            style.bottom = 0;
            style.paddingLeft = _theme.EdgePadding;
            style.paddingRight = _theme.EdgePadding;
            style.paddingBottom = _theme.BottomPadding;

            // Horizontal placement of the button stack across the screen.
            string al = string.IsNullOrEmpty(_theme.ChoiceAlign) ? "center" : _theme.ChoiceAlign;
            style.alignItems = al == "left" ? Align.FlexStart
                : al == "right" ? Align.FlexEnd
                : Align.Center;

            // Vertical placement: a free ChoiceYPercent puts the top of the stack at
            // that screen % (e.g. 70 = lower third); otherwise ChoiceVAlign docks it
            // top / centre / bottom.
            if (_theme.ChoiceYPercent >= 0f)
            {
                style.justifyContent = Justify.FlexStart;
                style.paddingTop = Length.Percent(Mathf.Clamp(_theme.ChoiceYPercent, 0f, 100f));
            }
            else
            {
                string v = string.IsNullOrEmpty(_theme.ChoiceVAlign) ? "center" : _theme.ChoiceVAlign;
                style.justifyContent = v == "top" ? Justify.FlexStart
                    : v == "bottom" ? Justify.FlexEnd
                    : Justify.Center;
            }

            pickingMode = PickingMode.Ignore; // only the buttons are interactive
            style.display = DisplayStyle.None;
        }

        /// <summary>Show the options. Replaces any currently shown.</summary>
        public void Present(IReadOnlyList<LvnOption> options)
        {
            _timerFill = null; // cleared with the children below
            Clear();
            if (options != null)
            {
                foreach (var o in options)
                    Add(BuildOption(o));
            }
            style.display = DisplayStyle.Flex;
            VisibleChanged?.Invoke(true);
        }

        /// <summary>Hide and clear the options.</summary>
        public void Dismiss()
        {
            _timerFill = null;
            Clear();
            style.display = DisplayStyle.None;
            VisibleChanged?.Invoke(false);
        }

        // ── countdown bar (timed choices) ────────────────────────────────────
        private VisualElement _timerFill;

        /// <summary>Show the countdown bar above the options (call after
        /// <see cref="Present"/>) and set its remaining fraction (1 → 0).</summary>
        public void SetTimer(float remaining01)
        {
            if (_timerFill == null)
            {
                var track = new VisualElement();
                track.style.width = Length.Percent(_theme.ChoiceMinWidthPercent);
                track.style.height = 6;
                track.style.marginBottom = _theme.ChoiceSpacing;
                track.style.backgroundColor = new Color(1f, 1f, 1f, 0.15f);
                track.style.borderTopLeftRadius = 3; track.style.borderTopRightRadius = 3;
                track.style.borderBottomLeftRadius = 3; track.style.borderBottomRightRadius = 3;
                _timerFill = new VisualElement();
                _timerFill.style.height = Length.Percent(100);
                _timerFill.style.backgroundColor = _theme.ChoiceCostColor;
                track.Add(_timerFill);
                Insert(0, track);
            }
            _timerFill.style.width = Length.Percent(Mathf.Clamp01(remaining01) * 100f);
        }

        private VisualElement BuildOption(LvnOption option)
        {
            int index = option.Index;
            var btn = new Button(() => OnSelected?.Invoke(index)) { text = string.Empty };
            btn.style.backgroundColor = _theme.ChoiceColor;
            btn.style.minWidth = Length.Percent(_theme.ChoiceMinWidthPercent);
            btn.style.maxWidth = Length.Percent(_theme.ChoiceMaxWidthPercent);
            // Readable on tablets: percent widths of a landscape viewport are
            // enormous — cap to the portrait story frame and centre.
            btn.style.maxWidth = new StyleLength(new Length(900, LengthUnit.Pixel));
            btn.style.alignSelf = Align.Center;
            btn.style.minHeight = _theme.ChoiceMinHeight; // thumb-sized (market norm ~6.5% H)
            btn.style.justifyContent = Justify.Center;
            btn.style.marginBottom = _theme.ChoiceSpacing;
            btn.style.paddingTop = _theme.ChoicePaddingY;
            btn.style.paddingBottom = _theme.ChoicePaddingY;
            btn.style.paddingLeft = _theme.ChoicePaddingX;
            btn.style.paddingRight = _theme.ChoicePaddingX;
            btn.style.borderTopLeftRadius = _theme.ChoiceCornerRadius;
            btn.style.borderTopRightRadius = _theme.ChoiceCornerRadius;
            btn.style.borderBottomLeftRadius = _theme.ChoiceCornerRadius;
            btn.style.borderBottomRightRadius = _theme.ChoiceCornerRadius;
            btn.style.flexDirection = FlexDirection.Column;
            btn.style.alignItems = Align.Center;

            var caption = new Label(option.Text ?? string.Empty);
            caption.style.color = _theme.ChoiceTextColor;
            caption.style.fontSize = _theme.ChoiceFontSize;
            caption.style.whiteSpace = WhiteSpace.Normal;
            caption.style.unityTextAlign = TextAnchor.MiddleCenter;
            LvnFonts.Apply(caption, _theme.Font); // SDF path (unityFontDefinition), legacy fallback inside
            btn.Add(caption);

            if (!string.IsNullOrEmpty(option.Cost))
            {
                var cost = new Label(option.Cost);
                cost.style.color = _theme.ChoiceCostColor;
                cost.style.fontSize = Mathf.RoundToInt(_theme.ChoiceFontSize * 0.72f);
                cost.style.marginTop = 4;
                btn.Add(cost);
            }

            if (_theme.ChoiceSprite != null)
            {
                UiStyle.ApplyBackground(btn, _theme.ChoiceSprite, _theme.ChoiceSlice);
                var hover = _theme.ChoiceHoverSprite != null ? _theme.ChoiceHoverSprite : _theme.ChoiceSprite;
                btn.RegisterCallback<MouseEnterEvent>(_ => btn.style.backgroundImage = new StyleBackground(hover));
                btn.RegisterCallback<MouseLeaveEvent>(_ => btn.style.backgroundImage = new StyleBackground(_theme.ChoiceSprite));
            }
            else
            {
                btn.RegisterCallback<MouseEnterEvent>(_ => btn.style.backgroundColor = _theme.ChoiceHoverColor);
                btn.RegisterCallback<MouseLeaveEvent>(_ => btn.style.backgroundColor = _theme.ChoiceColor);
            }
            return btn;
        }
    }
}
