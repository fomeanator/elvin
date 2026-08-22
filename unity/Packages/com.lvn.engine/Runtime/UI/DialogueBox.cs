using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    public enum DialogueSpeakerSide
    {
        Unanchored,
        Left,
        Center,
        Right,
    }

    /// <summary>
    /// The dialogue panel: a nameplate above a body panel that reveals text with
    /// a fast whole-word fade (driven by <see cref="RichTextTypewriter"/> +
    /// <see cref="TypewriterClock"/>). A props-driven <see cref="VisualElement"/>
    /// — no networking, no asset loader, no game-specific ornament. Anchor it to
    /// the bottom of a UIDocument root; the host taps to advance and calls
    /// <see cref="Complete"/> / <see cref="Reveal"/>.
    /// </summary>
    public sealed class DialogueBox : VisualElement
    {
        private readonly VnTheme _theme;
        private readonly VisualElement _box;
        private readonly VisualElement _plate;
        private readonly VisualElement _panelShell;
        private readonly VisualElement _panel;
        private readonly VisualElement _speakerPointer;
        private readonly bool _hasSpeakerPointer;
        private readonly Label _speaker;
        private readonly Label _body;
        private readonly RichTextTypewriter _tw = new RichTextTypewriter();

        private Label _advanceHint;
        private IVisualElementScheduledItem _hintPulse;
        private bool _hintSuppressed;

        /// <summary>Hide the ▼ "tap to continue" marker while a choice is up (a
        /// tap shouldn't be invited when the player must pick).</summary>
        public void SuppressAdvanceHint(bool suppressed)
        {
            _hintSuppressed = suppressed;
            RefreshAdvanceHint();
        }

        private void RefreshAdvanceHint()
        {
            bool show = !_hintSuppressed && !IsRevealing && _tw.VisibleCount > 0;
            _advanceHint.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show)
            {
                if (_hintPulse == null)
                {
                    bool dim = false;
                    _hintPulse = schedule.Execute(() =>
                    {
                        dim = !dim;
                        _advanceHint.style.opacity = dim ? 0.35f : 0.95f;
                    }).Every(600);
                }
                _hintPulse.Resume();
            }
            else _hintPulse?.Pause();
        }

        /// <summary>Peel the visible card from the screen and let it fall down.</summary>
        public void DropOut(int ms, System.Action done = null) =>
            LvnAppear.DetachDrop(this, _box, ms, done);

        /// <summary>Slide a replacement card up and settle it onto the screen.</summary>
        public void SlideIn(int ms, System.Action done = null) =>
            LvnAppear.CardArrive(this, _box, ms, done);

        /// <summary>Clear the previous exit transform before the next entrance.</summary>
        public void ResetCardVisual()
        {
            LvnAppear.Reset(this);
            // Do not reset _box.translate: free-popup placement stores its
            // anchor translation there. Card choreography never owns it.
            _box.style.scale = new Scale(Vector2.one);
            _box.style.rotate = new Rotate(new Angle(0f, AngleUnit.Degree));
        }

        /// <summary>Kept for playback API compatibility. Text is now installed
        /// whole and revealed by the card fade, so this is always false.</summary>
        public bool IsRevealing { get; private set; }

        public DialogueBox(VnTheme theme)
        {
            _theme = theme ?? new VnTheme();

            var align = string.IsNullOrEmpty(_theme.BoxAlign) ? "stretch" : _theme.BoxAlign;
            // The box is a universal popup. Three placement modes, decided by theme:
            //  • free  — any x/y given: positioned absolutely anywhere on screen, the
            //            given anchor point of the box landing on (x,y). Full control.
            //  • NVL   — a tall full-width reading surface from a top inset.
            //  • dock  — bottom-docked; BoxAlign sets the horizontal placement
            //            (stretch bar / centre / left / right), hugging the text.
            bool free = !_theme.Nvl && (_theme.BoxXPercent >= 0f || _theme.BoxYPercent >= 0f);
            bool stretch = _theme.Nvl || (!free && align == "stretch");

            // The root is a full-screen, click-through canvas; the box lives inside it.
            style.position = Position.Absolute;

            _box = new VisualElement { name = "vn-box" };
            _box.style.flexDirection = FlexDirection.Column;

            if (free)
            {
                style.left = 0; style.right = 0; style.top = 0; style.bottom = 0;
                _box.style.position = Position.Absolute;
                _box.style.left = Length.Percent(Mathf.Clamp(_theme.BoxXPercent >= 0f ? _theme.BoxXPercent : 50f, 0f, 100f));
                _box.style.top = Length.Percent(Mathf.Clamp(_theme.BoxYPercent >= 0f ? _theme.BoxYPercent : 50f, 0f, 100f));
                var (tx, ty) = AnchorTranslate(_theme.BoxAnchor);
                _box.style.translate = new Translate(Length.Percent(tx), Length.Percent(ty));
            }
            else if (_theme.Nvl)
            {
                // NVL: stretch from a top inset to the bottom as a tall reading surface.
                style.left = 0; style.right = 0; style.bottom = 0;
                style.top = Length.Percent(Mathf.Clamp01(_theme.NvlTop) * 100f);
                style.paddingLeft = _theme.EdgePadding;
                style.paddingRight = _theme.EdgePadding;
                style.paddingTop = _theme.EdgePadding;
                style.paddingBottom = _theme.BottomPadding;
                _box.style.flexGrow = 1;
            }
            else
            {
                style.left = 0; style.right = 0;
                if (_theme.DockTopPercent >= 0f)
                {
                    // Anchor the box by its TOP → it GROWS DOWNWARD as the text gets
                    // longer (instead of pushing its top up). BottomPadding unused here.
                    style.top = Length.Percent(Mathf.Clamp(_theme.DockTopPercent, 0f, 100f));
                }
                else
                {
                    // Bottom-anchored: BottomLiftPercent floats the box up from the
                    // screen edge; BottomPadding is the small inner gap. Grows UP.
                    style.bottom = Length.Percent(Mathf.Max(0f, _theme.BottomLiftPercent));
                    style.paddingBottom = _theme.BottomPadding;
                }
                style.paddingLeft = _theme.EdgePadding;
                style.paddingRight = _theme.EdgePadding;
                // alignItems places the box across the screen width.
                style.alignItems = stretch ? Align.Stretch
                    : align == "center" ? Align.Center
                    : align == "right" ? Align.FlexEnd
                    : Align.FlexStart;
                if (stretch) _box.style.flexGrow = 1;
            }

            // Box width/height (skipped for stretch & NVL, which fill their region):
            // The box has a FIXED width (it does NOT shrink to the text) — width =
            // BoxWidthPercent, else BoxMaxWidthPercent, else a sensible default. The
            // HEIGHT grows with the text (flex content height over PanelMinHeight),
            // so a long line makes the box taller, not wider. BoxMaxHeightPercent caps
            // that growth (the body clamps/scrolls beyond it).
            if (!stretch && !_theme.Nvl)
            {
                float w = _theme.BoxWidthPercent > 0f ? _theme.BoxWidthPercent
                        : _theme.BoxMaxWidthPercent > 0f ? _theme.BoxMaxWidthPercent
                        : 80f;
                _box.style.width = Length.Percent(Mathf.Clamp(w, 5f, 100f));
                // Tablets/landscape: the box caps at a readable line length and
                // centres — a 1900px-wide dialogue is a teleprompter, not a novel.
                _box.style.maxWidth = 1000;
                _box.style.alignSelf = Align.Center;
                if (_theme.BoxMaxHeightPercent > 0f)
                    _box.style.maxHeight = Length.Percent(Mathf.Clamp(_theme.BoxMaxHeightPercent, 5f, 100f));
            }
            Add(_box);

            // Nameplate (hidden for narration).
            _plate = new VisualElement { name = "vn-plate" };
            _plate.style.alignSelf = Align.FlexStart;
            _plate.style.flexShrink = 0; // never squeezed out of the column when space is tight
            _plate.style.backgroundColor = _theme.PanelColor;
            _plate.style.paddingLeft = _theme.NamePaddingX;
            _plate.style.paddingRight = _theme.NamePaddingX;
            _plate.style.paddingTop = _theme.NamePaddingY;
            _plate.style.paddingBottom = _theme.NamePaddingY;
            _plate.style.marginBottom = -2;
            SetCorner(_plate, _theme.PanelCornerRadius * 0.6f, top: true, bottom: false);
            UiStyle.ApplyBackground(_plate, _theme.PlateSprite, _theme.PanelSlice);
            if (_theme.PlateSprite == null && LvnChrome.Bubble(_plate))
            {
                // Плашка ПОДНЫРИВАЕТ под окно: её нижняя кромка прямая и
                // нарисована так, чтобы прятаться за верхней кромкой рамки.
                // Встык они читаются как две отдельные коробки, внахлёст — как
                // один прибор.
                _plate.style.marginBottom = -5;
                _plate.SendToBack();   // и по слоям тоже под окном, а не над ним
            }
            _speaker = new Label { name = "vn-speaker" };
            _speaker.style.color = _theme.SpeakerColor;
            _speaker.style.fontSize = _theme.SpeakerFontSize;
            _speaker.style.unityFontStyleAndWeight = FontStyle.Bold;
            LvnFonts.Apply(_speaker, _theme.Font); // SDF path (unityFontDefinition), legacy fallback inside
            _plate.Add(_speaker);
            _box.Add(_plate);

            // Body panel.
            // The shell stays overflow-visible for ornaments that rise over the
            // border. The actual panel may be clipped by UiGlass to preserve its
            // rounded corners; keeping the pointer inside that clipped element
            // made it permanently invisible whenever glass was enabled.
            _panelShell = new VisualElement { name = "vn-panel-shell" };
            _panelShell.style.position = Position.Relative;
            _panelShell.style.overflow = Overflow.Visible;
            _panelShell.style.flexShrink = 0;
            _panel = new VisualElement { name = "vn-panel" };
            _panel.style.paddingLeft = _theme.PanelPaddingX;
            _panel.style.paddingRight = _theme.PanelPaddingX;
            _panel.style.paddingTop = _theme.PanelPaddingY;
            _panel.style.paddingBottom = _theme.PanelPaddingY;
            _panel.style.minHeight = _theme.PanelMinHeight;
            // The speaker pointer deliberately rises above the top border.
            // Keep it outside the panel's content clip.
            _panel.style.overflow = Overflow.Visible;
            if (_theme.Nvl)
            {
                _panelShell.style.flexGrow = 1;
                _panel.style.flexGrow = 1; // fill the tall NVL region
            }
            SetCorner(_panel, _theme.PanelCornerRadius, top: true, bottom: true);
            UiStyle.ApplyBackground(_panel, _theme.PanelSprite, _theme.PanelSlice);
            // Рамка темы — только если новелла не принесла свою: авторский
            // спрайт сильнее оформления оболочки, иначе тема затирала бы
            // сознательно нарисованное окно.
            if (_theme.PanelSprite == null) LvnChrome.Frame(_panel);

            // Reference grammar: the name sits below the speaker, while a small
            // folded pointer on the OPPOSITE top edge aims back across the card.
            // A tiny shared raster is more reliable here than Painter2D: the
            // pointer must remain visible outside a sliced/rounded panel on every
            // UI Toolkit backend.
            var pointerTexture = Resources.Load<Texture2D>("ui/dialogue_pointer_cyan");
            _hasSpeakerPointer = pointerTexture != null;
            _speakerPointer = new VisualElement
            {
                name = "vn-speaker-pointer",
                pickingMode = PickingMode.Ignore,
            };
            _speakerPointer.style.position = Position.Absolute;
            // The source is a broad folded flourish, not a one-pixel caret. The
            // former 63x21 size reduced it to a cyan speck on a phone; four times
            // that visual size restores the intentional 3:1 silhouette.
            _speakerPointer.style.width = 252;
            _speakerPointer.style.height = 84;
            _speakerPointer.style.top = -72;
            _speakerPointer.style.backgroundColor = Color.clear;
            if (pointerTexture != null)
                _speakerPointer.style.backgroundImage = new StyleBackground(pointerTexture);
            _speakerPointer.style.display = DisplayStyle.None;
            _panelShell.Add(_panel);
            _panelShell.Add(_speakerPointer); // above the clipped glass surface
            _body = new Label { name = "vn-body" };
            _body.style.color = _theme.TextColor;
            _body.style.fontSize = _theme.BodyFontSize;
            _body.style.whiteSpace = WhiteSpace.Normal;
            LvnFonts.Apply(_body, _theme.Font); // SDF path (unityFontDefinition), legacy fallback inside
            _panel.Add(_body);

            // The genre's "line finished — tap" marker: a small pulsing ▼ in the
            // panel's bottom-right corner. Shown when the reveal is done (and no
            // choice is up — the host suppresses it then).
            _advanceHint = new Label("▼") { name = "vn-advance-hint", pickingMode = PickingMode.Ignore };
            _advanceHint.style.position = Position.Absolute;
            _advanceHint.style.right = 10;
            _advanceHint.style.bottom = 4;
            _advanceHint.style.fontSize = Mathf.RoundToInt(_theme.BodyFontSize * 0.55f);
            _advanceHint.style.color = _theme.SpeakerColor;
            _advanceHint.style.display = DisplayStyle.None;
            LvnFonts.Apply(_advanceHint, _theme.Font); // SDF path (unityFontDefinition), legacy fallback inside
            _panel.Add(_advanceHint);

            _box.Add(_panelShell);
            // Фон окна ставится ОДНИМ методом — тем же, что потом меняет его по
            // настройке прозрачности и по стилю реплики. Раньше конструктор
            // красил панель сам, и стекло, добавленное в общий метод, на первом
            // кадре не появлялось: окно ждало, пока игрок тронет настройки.
            ApplyPanelBackground();

            pickingMode = PickingMode.Ignore; // the host root owns tap-to-advance
        }

        /// <summary>Translate fractions for an anchor keyword so the box's (x,y)
        /// positions <em>that</em> point of the box: <c>center</c> → -50%,
        /// <c>right</c>/<c>bottom</c> → -100%, <c>left</c>/<c>top</c> → 0. Accepts
        /// combos like <c>"bottom-center"</c>, <c>"top-left"</c>, <c>"center"</c>.</summary>
        private static (float tx, float ty) AnchorTranslate(string anchor)
        {
            string a = string.IsNullOrEmpty(anchor) ? "center" : anchor.ToLowerInvariant();
            float tx = a.Contains("left") ? 0f : a.Contains("right") ? -100f : -50f;
            float ty = a.Contains("top") ? 0f : a.Contains("bottom") ? -100f : -50f;
            return (tx, ty);
        }

        /// <summary>Set the speaker name; empty/null hides the nameplate.</summary>
        public void SetSpeaker(string who, DialogueSpeakerSide side = DialogueSpeakerSide.Unanchored)
        {
            bool show = !string.IsNullOrEmpty(who);
            _plate.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            _speaker.text = show ? who : "";

            float offset = Mathf.Max(0f, LvnTheme.Current.SpeakerBubbleOffsetX);
            switch (side)
            {
                case DialogueSpeakerSide.Right:
                    _plate.style.alignSelf = Align.FlexEnd;
                    _plate.style.marginLeft = 0;
                    _plate.style.marginRight = offset;
                    // Name right → pointer left, leaning back toward the actor.
                    PlaceSpeakerPointer(left: true, pointsRight: true, show: show);
                    break;
                case DialogueSpeakerSide.Center:
                    _plate.style.alignSelf = Align.Center;
                    _plate.style.marginLeft = 0;
                    _plate.style.marginRight = 0;
                    // A centred staged actor still owns a spatial dialogue card.
                    // Keep the ornament on the opposite/right edge of the plate
                    // instead of silently dropping it.
                    PlaceSpeakerPointer(left: false, pointsRight: false, show: show);
                    break;
                case DialogueSpeakerSide.Left:
                    _plate.style.alignSelf = Align.FlexStart;
                    _plate.style.marginLeft = offset;
                    _plate.style.marginRight = 0;
                    // Name left → pointer right, leaning back toward the actor.
                    PlaceSpeakerPointer(left: false, pointsRight: false, show: show);
                    break;
                default:
                    // Narrator / voice without a staged actor keeps the stable
                    // historical left plate and has no false spatial pointer.
                    _plate.style.alignSelf = Align.FlexStart;
                    _plate.style.marginLeft = offset;
                    _plate.style.marginRight = 0;
                    PlaceSpeakerPointer(left: false, pointsRight: false, show: false);
                    break;
            }
        }

        private void PlaceSpeakerPointer(bool left, bool pointsRight, bool show)
        {
            _speakerPointer.style.display = show && _hasSpeakerPointer
                ? DisplayStyle.Flex
                : DisplayStyle.None;
            if (left)
            {
                _speakerPointer.style.left = 22;
                _speakerPointer.style.right = StyleKeyword.Auto;
            }
            else
            {
                _speakerPointer.style.left = StyleKeyword.Auto;
                _speakerPointer.style.right = 22;
            }
            _speakerPointer.style.scale = new Scale(new Vector2(pointsRight ? 1f : -1f, 1f));
        }

        /// <summary>
        /// Install the complete line. Its appearance belongs to the dialogue
        /// card's fixed fade, not to a length-dependent typewriter.
        /// </summary>
        public void Reveal(string text, float? cps = null)
        {
            _tw.SetText(text ?? "");
            _body.text = _tw.Full();
            IsRevealing = false;
            _body.MarkDirtyRepaint();
            RefreshAdvanceHint();
        }

        /// <summary>How long until the card fade makes this already-complete line
        /// fully visible. Independent of text length and never longer than the
        /// actor transition.</summary>
        public float EstimateRevealSeconds(string text, float? cps = null)
        {
            float card = Mathf.Max(0f, _theme.BoxAppearDuration) * VnTheme.MotionDurationScale;
            float actor = Mathf.Max(0f, _theme.ActorTransition) * VnTheme.MotionDurationScale;
            return actor > 0.001f ? Mathf.Min(card, actor) : card;
        }

        /// <summary>Snap to the full line immediately for engine-controlled fast
        /// forward/restore. Normal player taps advance the card instead.</summary>
        public void Complete()
        {
            IsRevealing = false;
            _body.MarkDirtyRepaint();
            RefreshAdvanceHint();
        }

        /// <summary>Show a complete line with no reveal (resume / backlog).</summary>
        public void SetText(string text)
        {
            _tw.SetText(text ?? "");
            _body.text = _tw.Full();
            IsRevealing = false;
            _body.MarkDirtyRepaint();
            RefreshAdvanceHint();
        }

        // The player's window-opacity preference and the current style's own panel
        // scale compose multiplicatively onto the PANEL BACKGROUND only — element
        // opacity would dim the text with it (and "narration"'s old opacity=0
        // silently hid the line, since the body label is a child of the panel).
        private float _userOpacity = 1f;
        private float _styleBgScale = 1f;

        /// <summary>Scale the dialogue window's background opacity (0.2–1) — the
        /// player's comfort setting. Text stays fully opaque.</summary>
        public void SetUserOpacity(float value)
        {
            _userOpacity = Mathf.Clamp(value, 0.2f, 1f);
            ApplyPanelBackground();
        }

        private void ApplyPanelBackground()
        {
            float a = _styleBgScale * _userOpacity;
            var c = _theme.PanelColor;
            c.a *= a;

            // Матовое стекло вместо плоской заливки. Цвет панели при этом не
            // пропадает — он уходит в тонировку ПОВЕРХ размытия, а сама заливка
            // гасится: оставленная под стеклом, она приглушила бы его до того же
            // плоского пятна, ради ухода от которого стекло и заводили.
            // Авторский спрайт сильнее стекла: если новелла нарисовала окно, она
            // нарисовала и то, как оно пропускает свет.
            bool glass = _theme.PanelGlass > 0.004f && _theme.PanelSprite == null;
            UiGlass.Apply(_panel, glass ? _theme.PanelGlass : 0f, c);

            _panel.style.backgroundColor = (_theme.PanelSprite != null || glass) ? Color.clear : c;
            // Sprite-skinned panels dim via the image tint instead.
            if (_theme.PanelSprite != null)
                _panel.style.unityBackgroundImageTintColor = new Color(1f, 1f, 1f, a);
        }

        /// <summary>
        /// Apply a text style preset before <see cref="Reveal"/>: "thought"
        /// (italic), "shout" (bold, larger), "narration" (centered, no panel),
        /// "whisper" (italic, faint panel). Unknown styles reset to default.
        /// </summary>
        public void ApplyStyle(string style)
        {
            _body.style.unityFontStyleAndWeight = FontStyle.Normal;
            _body.style.fontSize = _theme.BodyFontSize;
            _body.style.unityTextAlign = TextAnchor.UpperLeft;
            _styleBgScale = 1f;

            switch (style)
            {
                case "thought":
                    _body.style.unityFontStyleAndWeight = FontStyle.Italic;
                    break;
                case "shout":
                    _body.style.unityFontStyleAndWeight = FontStyle.Bold;
                    _body.style.fontSize = Mathf.RoundToInt(_theme.BodyFontSize * 1.2f);
                    break;
                case "narration":
                    _body.style.fontSize = Mathf.RoundToInt(_theme.BodyFontSize * 1.15f);
                    _body.style.unityTextAlign = TextAnchor.MiddleCenter;
                    _styleBgScale = 0f; // no panel behind pure narration — text stays visible
                    break;
                case "whisper":
                    _body.style.unityFontStyleAndWeight = FontStyle.Italic;
                    _styleBgScale = 0.5f;
                    break;
            }
            ApplyPanelBackground();
        }

        private static void SetCorner(VisualElement el, float r, bool top, bool bottom)
        {
            if (top)
            {
                el.style.borderTopLeftRadius = r;
                el.style.borderTopRightRadius = r;
            }
            if (bottom)
            {
                el.style.borderBottomLeftRadius = r;
                el.style.borderBottomRightRadius = r;
            }
        }

    }
}
