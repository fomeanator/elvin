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
            _inputScrim.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            _inputScrim.style.justifyContent = Justify.Center;
            _inputScrim.style.alignItems = Align.Center;
            _inputScrim.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());

            var panel = new VisualElement();
            panel.style.width = Length.Percent(70);
            panel.style.backgroundColor = Theme != null ? Theme.MenuBgColor : new Color(0.08f, 0.08f, 0.10f, 0.97f);
            panel.style.paddingLeft = 22; panel.style.paddingRight = 22;
            panel.style.paddingTop = 18; panel.style.paddingBottom = 18;
            float r = Theme != null ? Theme.MenuCornerRadius : 12f;
            panel.style.borderTopLeftRadius = r; panel.style.borderTopRightRadius = r;
            panel.style.borderBottomLeftRadius = r; panel.style.borderBottomRightRadius = r;
            _inputScrim.Add(panel);

            var promptText = (string)cmd["prompt"];
            if (_player != null) promptText = TextInterpolation.Apply(promptText, _player.Vars);
            if (!string.IsNullOrEmpty(promptText))
            {
                var prompt = new Label(promptText);
                prompt.style.color = Theme != null ? Theme.MenuTextColor : Color.white;
                prompt.style.fontSize = 22;
                prompt.style.whiteSpace = WhiteSpace.Normal;
                prompt.style.marginBottom = 12;
                if (Theme?.Font != null) prompt.style.unityFont = new StyleFont(Theme.Font);
                panel.Add(prompt);
            }

            var field = new TextField();
            field.value = (string)cmd["default"] ?? string.Empty;
            int max = 0;
            try { max = cmd["max"] != null ? (int)cmd["max"] : 0; } catch { }
            if (max > 0) field.maxLength = max;
            field.style.fontSize = 22;
            field.style.marginBottom = 14;
            panel.Add(field);

            string okLabel = Theme?.MenuLabels != null
                && Theme.MenuLabels.TryGetValue("input_ok", out var v) && !string.IsNullOrEmpty(v)
                ? v : "OK";
            var ok = new Button(() => ConfirmInput(field.value)) { text = okLabel };
            ok.style.height = 44;
            ok.style.fontSize = 20;
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
