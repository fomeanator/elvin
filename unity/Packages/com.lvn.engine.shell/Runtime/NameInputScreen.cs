using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The character name-input screen, themed from a <see cref="NameInputConfig"/>
    /// (manifest <c>ui.name_input</c>): a full-screen backdrop, optional character
    /// art, a prompt, a text field and a confirm button. <see cref="AskAsync"/>
    /// fades it in, waits for a valid name (sanitised by the pure
    /// <see cref="PlayerNameInput"/> rules), and returns it. Tapping confirm or
    /// pressing Enter commits; an empty/whitespace value is rejected.
    /// </summary>
    public sealed class NameInputScreen : VisualElement
    {
        private readonly NameInputConfig _cfg;
        private readonly DialogueConfig _dlg;
        private readonly ILvnAssets _assets;
        private readonly VisualElement _bg;
        private readonly VisualElement _hero;
        private readonly Label _prompt;
        private readonly TextField _field;
        private readonly Button _confirm;
        private readonly int _maxLength;

        private TaskCompletionSource<string> _tcs;
        private string _prefill;

        public NameInputScreen(NameInputConfig cfg, ILvnAssets assets)
            : this(cfg, null, assets) { }

        /// <summary>The NATIVE skin: the prompt panel dresses itself in the
        /// game's own dialogue form (panel colour/art/text from
        /// <paramref name="dlg"/>) — the ask reads as the story's first line,
        /// not as a form. ui.name_input fields stay as point overrides.</summary>
        public NameInputScreen(NameInputConfig cfg, DialogueConfig dlg, ILvnAssets assets)
        {
            _cfg = cfg ?? new NameInputConfig();
            _dlg = dlg;
            _assets = assets;
            _maxLength = _cfg.max_length ?? PlayerNameInput.MaxLength;

            ScreenUi.Stretch(this);
            style.backgroundColor = UiColor.Parse(_cfg.bg_color, new Color(0.06f, 0.06f, 0.08f));
            style.opacity = 0f;
            style.display = DisplayStyle.None;

            _bg = ScreenUi.Stretch(new VisualElement());
            Add(_bg);

            _hero = new VisualElement();
            _hero.style.position = Position.Absolute;
            _hero.style.left = 0;
            _hero.style.right = 0;
            _hero.style.top = Length.Percent(8f);
            _hero.style.bottom = Length.Percent(28f);
            _hero.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _hero.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _hero.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            _hero.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            _hero.pickingMode = PickingMode.Ignore;
            Add(_hero);

            // ── bottom panel: prompt + (field | confirm) ──
            var panel = new VisualElement();
            panel.style.position = Position.Absolute;
            panel.style.left = Length.Percent(8f);
            panel.style.right = Length.Percent(8f);
            panel.style.bottom = Length.Percent(8f);
            panel.style.paddingTop = 24;
            panel.style.paddingBottom = 24;
            panel.style.paddingLeft = 24;
            panel.style.paddingRight = 24;
            panel.style.backgroundColor = UiColor.Parse(_dlg?.panel_color, new Color(0f, 0f, 0f, 0.55f));
            float radius = _dlg?.corner_radius ?? 14f;
            panel.style.borderTopLeftRadius = radius;
            panel.style.borderTopRightRadius = radius;
            panel.style.borderBottomLeftRadius = radius;
            panel.style.borderBottomRightRadius = radius;
            Add(panel);
            // the game's dialogue-panel art (9-slice) IS the ask's frame
            if (!string.IsNullOrEmpty(_dlg?.panel_image))
                _ = ScreenUi.AssignNineSliceAsync(panel, _dlg.panel_image, _dlg.panel_slice ?? 0, _assets);

            _prompt = new Label(_cfg.prompt ?? "Enter your name");
            _prompt.style.color = UiColor.Parse(_cfg.prompt_color ?? _dlg?.speaker_color, new Color(0.80f, 0.72f, 0.56f));
            _prompt.style.fontSize = 30;
            _prompt.style.marginBottom = 14;
            panel.Add(_prompt);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            panel.Add(row);

            _field = new TextField { maxLength = _maxLength };
            _field.style.flexGrow = 1;
            _field.style.fontSize = 32;
            _field.style.marginRight = 16;
            var fieldColor = UiColor.Parse(_cfg.field_color, new Color(0.11f, 0.11f, 0.13f));
            var textColor = UiColor.Parse(_cfg.text_color ?? _dlg?.text_color, new Color(0.96f, 0.93f, 0.85f));
            StyleField(_field, fieldColor, textColor);
            _field.value = _cfg.default_name ?? "";
            _field.RegisterCallback<KeyDownEvent>(OnKey);
            row.Add(_field);

            _confirm = new Button { text = _cfg.confirm_text ?? "Confirm" };
            _confirm.style.fontSize = 28;
            _confirm.style.paddingLeft = 28;
            _confirm.style.paddingRight = 28;
            _confirm.style.paddingTop = 12;
            _confirm.style.paddingBottom = 12;
            _confirm.style.color = textColor;
            _confirm.style.backgroundColor = UiColor.Parse(_cfg.button_color, new Color(0.23f, 0.23f, 0.27f));
            _confirm.clicked += TryConfirm;
            row.Add(_confirm);

            _ = ScreenUi.AssignBgAsync(_bg, _cfg.bg_url, _assets);
            _ = ScreenUi.AssignBgAsync(_hero, _cfg.hero_url, _assets);
            if (!string.IsNullOrEmpty(_cfg.field_url)) _ = ScreenUi.AssignBgAsync(_field, _cfg.field_url, _assets);
            if (!string.IsNullOrEmpty(_cfg.button_url)) _ = ScreenUi.AssignBgAsync(_confirm, _cfg.button_url, _assets);
        }

        /// <summary>Show the screen and resolve with the player's sanitised name
        /// once they confirm a non-empty value. Cancelling the token abandons the
        /// prompt (the task cancels).</summary>
        public async Task<string> AskAsync(CancellationToken ct = default)
            => await AskAsync(null, null, ct);

        /// <summary>Ask over a specific backdrop — the shell passes the opening
        /// chapter's background so the moment reads as the story's first frame
        /// — with the KNOWN name prefilled (a replaying player may want to
        /// change it, or just confirm). Nulls fall back to ui.name_input.</summary>
        public async Task<string> AskAsync(string bgUrl, string prefill, CancellationToken ct = default)
            => await AskAsync(bgUrl, prefill, overlay: false, ct);

        /// <summary>Overlay mode: the LIVE scene is the backdrop — the screen
        /// goes transparent and only the dialogue-skinned bottom panel shows.
        /// The chapter-entry ask (after the title card) uses this.</summary>
        public async Task<string> AskAsync(string bgUrl, string prefill, bool overlay, CancellationToken ct = default)
        {
            style.backgroundColor = overlay
                ? Color.clear
                : UiColor.Parse(_cfg.bg_color, new Color(0.06f, 0.06f, 0.08f));
            _bg.style.display = overlay ? DisplayStyle.None : DisplayStyle.Flex;
            _hero.style.display = overlay ? DisplayStyle.None : DisplayStyle.Flex;
            if (!overlay && !string.IsNullOrEmpty(bgUrl))
                _ = ScreenUi.AssignBgAsync(_bg, bgUrl, _assets);
            _prefill = prefill;
            style.display = DisplayStyle.Flex;
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.3f, ct);

            _field.value = !string.IsNullOrEmpty(_prefill) ? _prefill : (_cfg.default_name ?? "");
            _field.schedule.Execute(() => { _field.Focus(); _field.SelectAll(); }).ExecuteLater(16);

            _tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetCanceled());

            string result;
            try { result = await _tcs.Task; }
            finally
            {
                await ScreenFx.FadeAsync(this, 1f, 0f, 0.3f, CancellationToken.None);
                style.display = DisplayStyle.None;
            }
            return result;
        }

        public void Hide()
        {
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _tcs?.TrySetCanceled();
        }

        private void OnKey(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                TryConfirm();
        }

        private void TryConfirm()
        {
            var name = PlayerNameInput.Sanitize(_field?.value, _maxLength);
            if (string.IsNullOrEmpty(name)) return;
            _tcs?.TrySetResult(name);
        }

        private static void StyleField(TextField f, Color bg, Color text)
        {
            f.style.color = text;
            var input = f.Q(TextField.textInputUssName);
            if (input != null)
            {
                input.style.backgroundColor = bg;
                input.style.color = text;
                input.style.paddingTop = 10;
                input.style.paddingBottom = 10;
                input.style.paddingLeft = 14;
                input.style.paddingRight = 14;
            }
        }
    }
}
