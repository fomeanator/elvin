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

        /// <summary>Peel the visible card from the screen: карточка бокового
        /// спикера уезжает в его сторону («табличка уезжает с героем»),
        /// рассказчик и центр — прежнее падение вниз.</summary>
        public void DropOut(int ms, System.Action done = null) =>
            LvnAppear.DetachDrop(this, _box, ms, done,
                _lastSide == DialogueSpeakerSide.Left ? -1
                : _lastSide == DialogueSpeakerSide.Right ? 1 : 0);

        /// <summary>Slide a replacement card in and settle it onto the screen.
        /// Направление — от последнего SetSpeaker: карточка спикера слева
        /// въезжает слева, справа — справа; рассказчик и центр — снизу
        /// («диалог принадлежит говорящему»).</summary>
        public void SlideIn(int ms, System.Action done = null) =>
            LvnAppear.CardArrive(this, _box, ms, done,
                _lastSide == DialogueSpeakerSide.Left ? -1
                : _lastSide == DialogueSpeakerSide.Right ? 1 : 0);

        /// <summary>Clear the previous exit transform before the next entrance.</summary>
        public void ResetCardVisual()
        {
            LvnAppear.Reset(this);
            // Do not reset _box.translate: free-popup placement stores its
            // anchor translation there. Card choreography never owns it.
            _box.style.scale = new Scale(Vector2.one);
            _box.style.rotate = new Rotate(new Angle(0f, AngleUnit.Degree));
        }

        private IVisualElementScheduledItem _tick;
        private float _startTime;
        private float _cps;
        private const float AverageCharactersPerWord = 6f;

        /// <summary>True while the typewriter is still revealing the line.</summary>
        public bool IsRevealing { get; private set; }

        /// <summary>Печать началась (true) / кончилась (false) — хост ведёт
        /// по этому луп звука клавиатуры, а не по-глифовый цокот.</summary>
        public event System.Action<bool> RevealingChanged;

        private void SetRevealing(bool on)
        {
            if (IsRevealing == on) return;
            IsRevealing = on;
            RevealingChanged?.Invoke(on);
        }

        /// <summary>Fires each time the reveal head visibly moves (word steps).</summary>
        public event System.Action RevealTicked;

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
            _speaker.style.fontSize = SpeakerSize;
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
            _body.style.fontSize = BodySize;
            _body.style.whiteSpace = WhiteSpace.Normal;
            LvnFonts.Apply(_body, _theme.Font); // SDF path (unityFontDefinition), legacy fallback inside
            // Typewriter = vertex post-processing: the FULL line is set once (so
            // word-wrap and box height are final from frame 0) and each repaint
            // ramps per-glyph tint alpha up to the reveal head. No per-tick string
            // rebuilds, no rich-text <alpha> hacks, no re-layout — the tick only
            // moves a float and calls MarkDirtyRepaint.
            _body.PostProcessTextVertices += OnPostProcessGlyphs;
            _panel.Add(_body);

            // The genre's "line finished — tap" marker: a small pulsing ▼ in the
            // panel's bottom-right corner. Shown when the reveal is done (and no
            // choice is up — the host suppresses it then).
            _advanceHint = new Label("▼") { name = "vn-advance-hint", pickingMode = PickingMode.Ignore };
            _advanceHint.style.position = Position.Absolute;
            _advanceHint.style.right = 10;
            _advanceHint.style.bottom = 4;
            _advanceHint.style.fontSize = Mathf.RoundToInt(BodySize * 0.55f);
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

        // Сторона последнего спикера — SlideIn въезжает карточкой с неё.
        private DialogueSpeakerSide _lastSide = DialogueSpeakerSide.Unanchored;

        /// <summary>Set the speaker name; empty/null hides the nameplate.</summary>
        public void SetSpeaker(string who, DialogueSpeakerSide side = DialogueSpeakerSide.Unanchored)
        {
            _lastSide = side;
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
        /// Begin revealing <paramref name="text"/> with the typewriter. Optional
        /// <paramref name="cps"/> overrides the theme speed for this line.
        /// Первые ~<see cref="VnTheme.InitialVisibleCharacters"/> символов (до
        /// конца слова) встают МГНОВЕННО — смысл ловится сразу, печатается
        /// только хвост.
        /// </summary>
        public void Reveal(string text, float? cps = null)
        {
            _tw.SetText(text ?? "");
            _cps = PaceFor(cps);
            _startTime = LvnClock.Now();
            _lastQuantum = -1;
            _tick?.Pause();

            // The budget is deliberately approximate: round it FORWARD to a word
            // boundary so the first readable block never opens as "предложе…".
            _initialReveal = _tw.WordEndAtOrAfter(InitialFor(_tw.VisibleCount));
            SetRevealing(_tw.VisibleCount > _initialReveal);
            RefreshAdvanceHint(); // hidden while revealing
            _revealProgress = _initialReveal;
            _wordCompleteChars = _initialReveal;
            _wordActiveEndChars = _initialReveal;
            _wordActiveAlpha = 0f;
            _body.text = _tw.Full();
            if (IsRevealing)
            {
                _body.MarkDirtyRepaint(); // same text as the last line? still restart at 0
                _tick = schedule.Execute(Tick).Every(16);
            }
        }

        /// <summary>How long this line's tail will take at the current reader
        /// pace. Used to let a newly entering actor settle with the text instead
        /// of finishing its animation in an unrelated rhythm.</summary>
        public float EstimateRevealSeconds(string text, float? cps = null)
        {
            var probe = new RichTextTypewriter();
            probe.SetText(text ?? "");
            int initial = probe.WordEndAtOrAfter(InitialFor(probe.VisibleCount));
            int words = probe.WordsAfter(initial);
            float pace = PaceFor(cps);
            float wordsPerSecond = TypewriterClock.Progress(1f, pace) / AverageCharactersPerWord;
            return words / Mathf.Max(0.01f, wordsPerSecond);
        }

        /// <summary>
        /// ТЕМП ЭТОЙ СТРОКИ: скорость из команды, если автор её задал и она
        /// осмысленна, иначе тема.
        ///
        /// <para>Правило стояло дважды — в самой печати и в её ОЦЕНКЕ, — а они
        /// обязаны совпадать: по оценке входящий актёр рассчитывает, когда
        /// осесть вместе с текстом. Разойдись они на строке с авторской
        /// скоростью, и герой заканчивал бы движение в чужом ритме. Дублировать
        /// правило, обе половины которого сверяются друг с другом, — верный
        /// способ однажды их рассинхронизировать.</para>
        /// </summary>
        private float PaceFor(float? cps)
            => cps.HasValue && cps.Value > TypewriterClock.MinCps ? cps.Value : _theme.CharsPerSecond;

        /// <summary>Сколько символов встаёт мгновенно — не больше, чем есть.</summary>
        private int InitialFor(int visibleCount)
            => Mathf.Min(Mathf.Max(0, _theme.InitialVisibleCharacters), visibleCount);

        /// <summary>Snap to the full line immediately (e.g. on the first tap).</summary>
        public void Complete()
        {
            _tick?.Pause();
            SetRevealing(false);
            _body.MarkDirtyRepaint(); // repaint with the reveal ramp inactive
            RefreshAdvanceHint();
        }

        /// <summary>Show a complete line with no reveal (resume / backlog).</summary>
        public void SetText(string text)
        {
            _tick?.Pause();
            _tw.SetText(text ?? "");
            _body.text = _tw.Full();
            SetRevealing(false);
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
        /// <summary>ПЕРЕЧИТАТЬ НАСТРОЙКИ ТЕКСТА НА ЖИВОМ ЭКРАНЕ. Кегль и
        /// гарнитура ставятся при сборке реплики, и без этого вызова выбор в
        /// настройках доезжал бы только со следующей строкой — то есть игрок,
        /// который для того их и открыл, видел бы неизменившийся текст и решил,
        /// что настройка сломана.</summary>
        public void RefreshTextStyle()
        {
            if (_theme == null) return;
            // Проступает САМ ТЕКСТ реплики, не плашка под ним: мигающая плашка
            // читается как вспышка, а меняется только текст. Фейд короткий —
            // игрок крутит ползунок и ждёт ОТВЕТА, а не представления.
            if (_body != null) LvnMotion.FadeIn(_body, delayMs: 0, ms: LvnMotion.Quick);
            if (_speaker != null)
            {
                _speaker.style.fontSize = SpeakerSize;
                LvnFonts.Apply(_speaker, _theme.Font);
            }
            if (_body != null)
            {
                _body.style.fontSize = BodySize;
                // ВЕС ПРИМЕНЯЕТСЯ ОДИН РАЗ — раздутием контура глифа
                // (LvnFonts.ApplyWeight, _FaceDilate). Здесь стояло второе,
                // прежнее применение — FontStyle.Bold, которым UITK ЭМУЛИРУЕТ
                // жир, растягивая контур. Складываясь, они давали заметно
                // более широкую букву, чем просил ползунок: «ширина шрифта в
                // диалоговом окне больше, чем надо» (Илья 29.08).
                //
                // Реплика — самый крупный текст в игре, поэтому лишний вес
                // виден в ней первым, хотя механизм общий для всех надписей.
                LvnFonts.Apply(_body, _theme.Font);
            }
            if (_advanceHint != null)
            {
                _advanceHint.style.fontSize = Mathf.RoundToInt(BodySize * 0.55f);
                LvnFonts.Apply(_advanceHint, _theme.Font);
            }
        }

        /// <summary>Кегль реплики С УЧЁТОМ ВЫБОРА ИГРОКА. Авторский размер —
        /// постановка новеллы, множитель — его глаза и его телефон; спорить им
        /// не о чем, поэтому размер один и считается здесь, а не в пяти местах
        /// показа.</summary>
        private int BodySize => LvnFonts.Size(_theme.BodyFontSize * LvnPrefs.TextScale);

        /// <summary>Имя говорящего растёт вместе с репликой: разъехавшись, они
        /// читались бы как разные экраны.</summary>
        private int SpeakerSize => LvnFonts.Size(_theme.SpeakerFontSize * LvnPrefs.TextScale);

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
            _body.style.fontSize = BodySize;
            _body.style.unityTextAlign = TextAnchor.UpperLeft;
            _styleBgScale = 1f;

            switch (style)
            {
                case "thought":
                    _body.style.unityFontStyleAndWeight = FontStyle.Italic;
                    break;
                case "shout":
                    _body.style.unityFontStyleAndWeight = FontStyle.Bold;
                    _body.style.fontSize = Mathf.RoundToInt(BodySize * 1.2f);
                    break;
                case "narration":
                    _body.style.fontSize = Mathf.RoundToInt(BodySize * 1.15f);
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

        // Whole-word reveal state, measured in visible characters. Every glyph
        // of the active word shares one opacity, so the eye reads a word rather
        // than watching individual letters crawl in.
        private float _revealProgress;
        private int _initialReveal;
        private int _wordCompleteChars;
        private int _wordActiveEndChars;
        private float _wordActiveAlpha;

        // Progress quantum of the last RevealTicked — one step per word.
        private int _lastQuantum = -1;

        private void Tick()
        {
            if (!IsRevealing) { _tick?.Pause(); return; }
            float elapsed = LvnClock.Since(_startTime);
            float wordProgress = TypewriterClock.Progress(elapsed, _cps) / AverageCharactersPerWord;
            _tw.WordReveal(_initialReveal, wordProgress,
                out _wordCompleteChars, out _wordActiveEndChars, out _wordActiveAlpha);
            if (_wordCompleteChars >= _tw.VisibleCount)
            {
                Complete();
                return;
            }
            _revealProgress = _wordCompleteChars
                + (_wordActiveEndChars - _wordCompleteChars) * _wordActiveAlpha;
            _body.MarkDirtyRepaint(); // vertex-tint pass only — no layout, no strings
            int q = Mathf.FloorToInt(wordProgress);
            if (q == _lastQuantum) return;
            _lastQuantum = q;
            RevealTicked?.Invoke();
        }

        // Per-word alpha before the text mesh renders. Vertices are
        // regenerated fresh for every repaint, so this only ever writes the
        // CURRENT frame's fade — nothing accumulates. Inactive (IsRevealing
        // false) it leaves the mesh untouched: the full line renders as-is.
        private void OnPostProcessGlyphs(TextElement.GlyphsEnumerable glyphs)
        {
            if (!IsRevealing) return;
            int count = glyphs.Count;
            if (count <= 0) return;

            // Boundaries are in CHARS (steps include spaces); glyphs are only
            // rendered quads. Rescale both complete and active word ends.
            int chars = _tw.VisibleCount;
            float completeGlyph = chars > 0 ? _wordCompleteChars * count / (float)chars : count;
            float activeGlyph = chars > 0 ? _wordActiveEndChars * count / (float)chars : count;

            int i = 0;
            foreach (TextElement.Glyph glyph in glyphs)
            {
                float midpoint = i + 0.5f;
                i++;
                if (midpoint <= completeGlyph) continue;
                byte b = midpoint <= activeGlyph
                    ? (byte)(_wordActiveAlpha * 255f + 0.5f)
                    : (byte)0;
                var verts = glyph.vertices;
                for (int v = 0; v < verts.Length; v++)
                {
                    var vert = verts[v];
                    var tint = vert.tint;
                    tint.a = b == 0 ? (byte)0 : (byte)(tint.a * b / 255);
                    vert.tint = tint;
                    verts[v] = vert;
                }
            }
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
