using Lvn;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The two "player acts against the story" stops: timed choices (a countdown
    /// bar over the options — expiry takes the <c>timeout_goto</c> branch) and
    /// the <c>input</c> op's text-entry overlay (the typed string lands in a
    /// story variable). Both pause exactly like their untimed cousins, so
    /// save/rollback replay re-presents them naturally.
    /// </summary>
    public sealed partial class VnStage
    {
        // ── choice countdown ────────────────────────────────────────────────
        private IVisualElementScheduledItem _choiceTick;
        private float _choiceDeadline;
        private float _choiceTotal;

        private void StartChoiceTimer(float seconds)
        {
            StopChoiceTimer();
            if (seconds <= 0f || _uiRoot == null) return;
            _choiceTotal = seconds;
            _choiceDeadline = Time.unscaledTime + seconds;
            _choices?.SetTimer(1f);
            _choiceTick = _uiRoot.schedule.Execute(() =>
            {
                // An open menu or the art view freezes the clock — a timed choice
                // must race the player, not their settings screen.
                if (InputBlocked || _chromeHidden) { _choiceDeadline += 0.1f; return; }
                float left = _choiceDeadline - Time.unscaledTime;
                _choices?.SetTimer(left / _choiceTotal);
                if (left > 0f) return;
                StopChoiceTimer();
                _curChoices = null;
                _choices?.Dismiss();
                _dialogue?.SuppressAdvanceHint(false);
                // Stale after a load/rollback is a no-op inside the player.
                if (_player != null && _player.ResolveChoiceTimeout())
                    AutosaveNow(); // time picked the branch — same crash contract as a tap
            }).Every(100);
        }

        private void StopChoiceTimer()
        {
            _choiceTick?.Pause();
            _choiceTick = null;
        }

        // ── input op: text-entry overlay ────────────────────────────────────
        private bool _awaitingInput;
        private VisualElement _inputScrim;
        private string _inputVar;

        private void ApplyInput(JObject cmd)
        {
            CloseInput();
            _inputVar = (string)cmd["var"];
            if (string.IsNullOrEmpty(_inputVar) || _uiRoot == null)
            {
                // Malformed command (the validator flags it) — don't strand the story.
                _player?.Advance();
                return;
            }
            _awaitingInput = true;

            _inputScrim = new VisualElement();
            _inputScrim.style.position = Position.Absolute;
            _inputScrim.style.left = 0; _inputScrim.style.right = 0;
            _inputScrim.style.top = 0; _inputScrim.style.bottom = 0;
            _inputScrim.style.backgroundColor = new Color(0f, 0f, 0f, 0.62f);
            _inputScrim.style.justifyContent = Justify.Center;
            _inputScrim.style.alignItems = Align.Center;
            _inputScrim.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());

            // ФОРМА ВВОДА — ЭТО ТОЖЕ ИГРА. Она выглядела отладочной: серая
            // коробка, белое поле с системным выделением и серая кнопка посреди
            // нарисованной сцены. Одевается тем же, чем одето окно диалога —
            // цвет панели, скругление, рисованная рамка темы, — потому что
            // игрок в этот момент разговаривает с персонажем, а не заполняет
            // анкету.
            var panel = new VisualElement();
            panel.style.width = Length.Percent(82);
            panel.style.maxWidth = 900;
            panel.style.backgroundColor = Theme != null ? Theme.PanelColor : new Color(0.086f, 0.063f, 0.094f, 0.94f);
            panel.style.paddingLeft = Theme != null ? Theme.PanelPaddingX : 22f;
            panel.style.paddingRight = Theme != null ? Theme.PanelPaddingX : 22f;
            panel.style.paddingTop = Theme != null ? Theme.PanelPaddingY : 18f;
            panel.style.paddingBottom = Theme != null ? Theme.PanelPaddingY : 18f;
            panel.style.overflow = Overflow.Visible;   // рамка выступает наружу
            float r = Theme != null ? Theme.PanelCornerRadius : 12f;
            panel.style.borderTopLeftRadius = r; panel.style.borderTopRightRadius = r;
            panel.style.borderBottomLeftRadius = r; panel.style.borderBottomRightRadius = r;
            UiStyle.ApplyBackground(panel, Theme?.PanelSprite, Theme != null ? Theme.PanelSlice : 0);
            if (Theme?.PanelSprite == null) LvnChrome.Frame(panel);
            _inputScrim.Add(panel);

            var promptText = (string)cmd["prompt"];
            if (!string.IsNullOrEmpty(promptText) && _strings != null && _strings.TryGetValue(promptText, out var trPrompt))
                promptText = trPrompt; // localization catalog, keyed by the source prompt
            if (_player != null) promptText = TextInterpolation.Apply(promptText, _player.Vars);
            if (!string.IsNullOrEmpty(promptText))
            {
                var prompt = new Label(promptText);
                // Вопрос задаёт история — значит и цвет у него тот же, каким
                // подписан говорящий.
                prompt.style.color = Theme != null ? Theme.SpeakerColor : LvnTokens.Accent;
                prompt.style.fontSize = Theme != null ? Theme.BodyFontSize : 30;
                prompt.style.whiteSpace = WhiteSpace.Normal;
                prompt.style.marginBottom = 18;
                LvnFonts.Apply(prompt, Theme?.Font);
                panel.Add(prompt);
            }

            var field = new TextField();
            field.value = (string)cmd["default"] ?? string.Empty;
            int max = 0;
            try { max = cmd["max"] != null ? (int)cmd["max"] : 0; } catch { }   // экран ввода снесли на полуслове — история продолжится
            if (max > 0) field.maxLength = max;
            field.style.fontSize = Theme != null ? Theme.BodyFontSize : 30;
            field.style.marginBottom = 20;
            LvnFonts.Apply(field, Theme?.Font);
            // Красится ВНУТРЕННИЙ элемент поля: у TextField своя подложка, и
            // цвет, поставленный снаружи, до неё не доходит.
            LvnChrome.Field(field, LvnTokens.SurfaceHi, Theme != null ? Theme.TextColor : LvnTokens.Text);
            var inner = field.Q(TextField.textInputUssName);
            if (inner != null)
            {
                LvnChrome.Round(inner, Mathf.Max(8f, r * 0.4f));
                LvnChrome.Border(inner, Theme != null ? Theme.SpeakerColor : LvnTokens.Accent, 2f);
                inner.style.minHeight = 72;   // палец, а не курсор
            }
            // Каретка и выделение — тоже тема: системное синее выделение на
            // тёмной панели читается как чужой элемент операционной системы.
            field.textSelection.cursorColor = Theme != null ? Theme.SpeakerColor : LvnTokens.Accent;
            var sel = Theme != null ? Theme.SpeakerColor : LvnTokens.Accent;
            sel.a = 0.35f;
            field.textSelection.selectionColor = sel;
            panel.Add(field);

            string okLabel = Theme?.MenuLabels != null
                && Theme.MenuLabels.TryGetValue("input_ok", out var v) && !string.IsNullOrEmpty(v)
                ? v : "OK";
            var ok = new Button(() => ConfirmInput(field.value)) { text = okLabel };
            // Та же кнопка, что и у выбора в диалоге: игрок уже знает, как она
            // выглядит и что делает.
            ok.style.height = Theme != null ? Theme.ChoiceMinHeight : 96f;
            ok.style.fontSize = Theme != null ? Theme.ChoiceFontSize : 28;
            ok.style.color = Theme != null ? Theme.ChoiceTextColor : LvnTokens.Text;
            ok.style.backgroundColor = Theme != null ? Theme.ChoiceColor : LvnTokens.Surface;
            ok.style.borderLeftWidth = 0; ok.style.borderRightWidth = 0;
            ok.style.borderTopWidth = 0; ok.style.borderBottomWidth = 0;
            LvnChrome.Round(ok, Mathf.Max(8f, r * 0.4f));
            LvnChrome.Border(ok, Theme != null ? Theme.SpeakerColor : LvnTokens.Accent, 2f);
            LvnFonts.Apply(ok, Theme?.Font);
            LvnMotion.Tappable(ok);
            panel.Add(ok);

            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                    ConfirmInput(field.value);
            });

            _uiRoot.Add(_inputScrim);
            field.schedule.Execute(() => field.Focus()).ExecuteLater(50);
        }

        /// <summary>Commit the typed text into the story variable and continue.
        /// Internal so the PlayMode smoke can drive the production path.</summary>
        internal void ConfirmInput(string value)
        {
            if (!_awaitingInput) return;
            _awaitingInput = false;
            if (_player != null && !string.IsNullOrEmpty(_inputVar))
                _player.Vars[_inputVar] = value ?? string.Empty;
            CloseInput();
            _player?.Advance();
            AutosaveNow(); // the entered value is exactly what a crash must not lose
        }

        private void CloseInput()
        {
            _inputScrim?.RemoveFromHierarchy();
            _inputScrim = null;
        }
    }
}
