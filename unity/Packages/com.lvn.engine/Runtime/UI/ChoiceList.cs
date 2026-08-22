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
                // ПРОЦЕНТ ОТ ВЫСОТЫ, А НЕ ОТ ШИРИНЫ. Раньше высота стопки
                // задавалась через paddingTop — а проценты в отступах и в CSS, и
                // в UI Toolkit считаются от ШИРИНЫ родителя. На портретном
                // телефоне 1080×1920 «y=56%» превращалось в 605 px, то есть в
                // 31% высоты: выборы уезжали выше диалогового окна и налезали на
                // него. Написано одно, получалось другое, и никто не жаловался —
                // потому что на глаз это просто «выборы стоят не там».
                //
                // У top проценты берутся от ВЫСОТЫ, поэтому верх стопки надо
                // двигать им, отпустив низ.
                style.justifyContent = Justify.FlexStart;
                style.top = Length.Percent(Mathf.Clamp(_theme.ChoiceYPercent, 0f, 100f));
                style.bottom = StyleKeyword.Auto;
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

        /// <summary>
        /// Не дать стопке налезть на диалоговое окно. Статичный процент этого
        /// гарантировать не может: окно растёт вниз вместе с текстом, и любая
        /// заранее выбранная высота однажды встречает реплику длиннее себя.
        /// Поэтому сцена сообщает, где кончается окно, а стопка опускается до
        /// этой границы, если её собственный процент оказался выше.
        ///
        /// <para>Действует только в свободном режиме (ChoiceYPercent): доки
        /// top/center/bottom живут по своим правилам. <paramref name="yPx"/> —
        /// в координатах родителя; отрицательное значение возвращает чистый
        /// процент (окна на экране нет).</para>
        /// </summary>
        public void ClampBelow(float yPx)
        {
            if (_theme.ChoiceYPercent < 0f) return;
            float pct = Mathf.Clamp(_theme.ChoiceYPercent, 0f, 100f);
            float hostH = parent != null ? parent.resolvedStyle.height : 0f;
            float pctPx = hostH > 1f ? hostH * pct / 100f : -1f;
            if (yPx > 0f && pctPx >= 0f && yPx > pctPx)
                style.top = yPx;
            else
                style.top = Length.Percent(pct);
        }

        /// <summary>Show the options. Replaces any currently shown.</summary>
        public void Present(IReadOnlyList<LvnOption> options)
        {
            SetEnabled(true);
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
                ApplyChoiceWidth(track);
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
            ApplyChoiceWidth(btn);
            btn.style.minHeight = _theme.ChoiceMinHeight; // thumb-sized (market norm ~6.5% H)
            btn.style.justifyContent = Justify.Center;
            btn.style.marginBottom = _theme.ChoiceSpacing;
            btn.style.paddingTop = _theme.ChoicePaddingY;
            btn.style.paddingBottom = _theme.ChoicePaddingY;
            btn.style.paddingLeft = _theme.ChoicePaddingX;
            btn.style.paddingRight = _theme.ChoicePaddingX;
            // Кромка темы — до радиуса: у технической темы вариант выбора без
            // светящегося контура выпадает из экрана, на котором контур есть у
            // всего остального.
            LvnChrome.Edge(btn, 0.85f);
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
            // The label belongs to the button's content box. Without an explicit
            // stretch constraint a long caption may report its unwrapped desired
            // width to flex layout and make the whole option wider. Two lines must
            // add height only — the dialogue and choice columns stay aligned.
            caption.style.alignSelf = Align.Stretch;
            caption.style.flexShrink = 1f;
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

            // Stat award preview ("+2 Роман") — the importer's best-effort read
            // of what picking this option actually does, never executed here.
            if (option.Effects != null && option.Effects.Count > 0)
            {
                var effRow = new VisualElement();
                effRow.style.flexDirection = FlexDirection.Row;
                effRow.style.flexWrap = Wrap.Wrap;
                effRow.style.justifyContent = Justify.Center;
                effRow.style.marginTop = 4;
                foreach (var eff in option.Effects)
                {
                    var chip = new Label($"{(eff.Delta > 0 ? "+" : "")}{eff.Delta} {eff.Label}");
                    chip.style.color = _theme.ChoiceCostColor;
                    chip.style.fontSize = Mathf.RoundToInt(_theme.ChoiceFontSize * 0.6f);
                    chip.style.marginLeft = 6;
                    chip.style.marginRight = 6;
                    chip.style.opacity = 0.85f;
                    effRow.Add(chip);
                }
                btn.Add(effRow);
            }

            if (_theme.ChoiceSprite != null)
            {
                UiStyle.ApplyBackground(btn, _theme.ChoiceSprite, _theme.ChoiceSlice);
                var hover = _theme.ChoiceHoverSprite != null ? _theme.ChoiceHoverSprite : _theme.ChoiceSprite;
                btn.RegisterCallback<MouseEnterEvent>(_ => btn.style.backgroundImage = new StyleBackground(hover));
                btn.RegisterCallback<MouseLeaveEvent>(_ => btn.style.backgroundImage = new StyleBackground(_theme.ChoiceSprite));
            }
            else if (_theme.ChoiceGlass > 0.004f)
            {
                // Стекло: подсветка наведения меняет тонировку ПОВЕРХ размытия,
                // а не заливку под ним — иначе на касании кнопка на кадр
                // становится плоской.
                btn.style.backgroundColor = Color.clear;
                UiGlass.Apply(btn, _theme.ChoiceGlass, _theme.ChoiceColor);
                btn.RegisterCallback<MouseEnterEvent>(_ => UiGlass.Apply(btn, _theme.ChoiceGlass, _theme.ChoiceHoverColor));
                btn.RegisterCallback<MouseLeaveEvent>(_ => UiGlass.Apply(btn, _theme.ChoiceGlass, _theme.ChoiceColor));
            }
            else
            {
                btn.RegisterCallback<MouseEnterEvent>(_ => btn.style.backgroundColor = _theme.ChoiceHoverColor);
                btn.RegisterCallback<MouseLeaveEvent>(_ => btn.style.backgroundColor = _theme.ChoiceColor);
            }
            return btn;
        }

        /// <summary>Apply the authored choice column width without letting text
        /// content resize it. Equal min/max values mean a fixed column (the normal
        /// VN setup and the live title's 68%, matching its dialogue form). The old
        /// code assigned maxWidth twice, so the pixel tablet cap silently erased
        /// the authored percentage and multi-line captions could widen the card.</summary>
        private void ApplyChoiceWidth(VisualElement element)
        {
            if (element == null) return;
            float min = Mathf.Clamp(_theme.ChoiceMinWidthPercent, 5f, 100f);
            float max = Mathf.Clamp(_theme.ChoiceMaxWidthPercent, min, 100f);
            if (Mathf.Approximately(min, max))
            {
                element.style.width = Length.Percent(min);
                // Same readable-frame cap as DialogueBox: matching percentages
                // continue to match on tablets and landscape too.
                element.style.maxWidth = 1000f;
            }
            else
            {
                element.style.minWidth = Length.Percent(min);
                element.style.maxWidth = Length.Percent(max);
            }
            element.style.alignSelf = Align.Center;
        }
    }
}
