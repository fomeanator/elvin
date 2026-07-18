using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The boot auth screen, themed from an <see cref="AuthConfig"/> (manifest
    /// <c>ui.auth</c>): backdrop + logo + welcome text, an optional nickname
    /// field and a start button, with a status line mirroring the silent
    /// device sign-in (<see cref="Lvn.Services.LvnBackend"/>) underneath.
    /// Deliberately NOT a gate — the device account needs no credentials, so
    /// Start always works, online or offline; the screen is the game's face on
    /// top of the registration, not a login form in front of it.
    /// <see cref="AskAsync"/> fades in, waits for Start and returns the
    /// nickname (empty when the field is disabled or left blank).
    /// </summary>
    public sealed class AuthScreen : VisualElement
    {
        private readonly AuthConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly TextField _field;
        private readonly Label _status;
        private readonly int _maxLength;

        private TaskCompletionSource<string> _tcs;

        public AuthScreen(AuthConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new AuthConfig();
            _assets = assets;
            _maxLength = _cfg.max_length ?? PlayerNameInput.MaxLength;

            ScreenUi.Stretch(this);
            style.backgroundColor = UiColor.Parse(_cfg.bg_color, new Color(0.06f, 0.06f, 0.08f));
            style.opacity = 0f;
            style.display = DisplayStyle.None;

            var bg = ScreenUi.Stretch(new VisualElement());
            bg.pickingMode = PickingMode.Ignore;
            Add(bg);

            // logo, centred horizontally on the configured line
            float logoW = Mathf.Clamp01(_cfg.logo_width ?? 0.5f);
            float logoY = Mathf.Clamp01(_cfg.logo_y ?? 0.28f);
            var logo = new VisualElement { pickingMode = PickingMode.Ignore };
            logo.style.position = Position.Absolute;
            logo.style.left = Length.Percent((1f - logoW) * 50f);
            logo.style.width = Length.Percent(logoW * 100f);
            logo.style.top = Length.Percent(logoY * 100f - 15f);
            logo.style.height = Length.Percent(30f);
            logo.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            logo.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            logo.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            logo.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            Add(logo);

            // ── bottom panel: title, subtitle, (nickname), start, status ──
            var panel = new VisualElement();
            panel.style.position = Position.Absolute;
            panel.style.left = Length.Percent(8f);
            panel.style.right = Length.Percent(8f);
            panel.style.bottom = Length.Percent(7f);
            panel.style.paddingTop = 26;
            panel.style.paddingBottom = 22;
            panel.style.paddingLeft = 26;
            panel.style.paddingRight = 26;
            panel.style.backgroundColor = UiColor.Parse(_cfg.panel_color, new Color(0f, 0f, 0f, 0.65f));
            panel.style.borderTopLeftRadius = 16;
            panel.style.borderTopRightRadius = 16;
            panel.style.borderBottomLeftRadius = 16;
            panel.style.borderBottomRightRadius = 16;
            Add(panel);

            var title = new Label(_cfg.title ?? "Welcome");
            title.style.color = UiColor.Parse(_cfg.title_color, new Color(0.96f, 0.93f, 0.85f));
            title.style.fontSize = 42;
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            panel.Add(title);

            if (!string.IsNullOrEmpty(_cfg.subtitle))
            {
                var subtitle = new Label(_cfg.subtitle);
                subtitle.style.color = UiColor.Parse(_cfg.subtitle_color, new Color(0.80f, 0.72f, 0.56f));
                subtitle.style.fontSize = 26;
                subtitle.style.marginTop = 6;
                subtitle.style.unityTextAlign = TextAnchor.MiddleCenter;
                subtitle.style.whiteSpace = WhiteSpace.Normal;
                panel.Add(subtitle);
            }

            var textColor = UiColor.Parse(_cfg.text_color, new Color(0.96f, 0.93f, 0.85f));
            // The app NEVER asks the player's name — the novel does, at its
            // start (the Liminal pattern). A title can still opt the nickname
            // field in explicitly (ui.auth.ask_nickname: true).
            if (_cfg.ask_nickname ?? false)
            {
                var prompt = new Label(_cfg.name_prompt ?? "Your name");
                prompt.style.color = UiColor.Parse(_cfg.subtitle_color, new Color(0.80f, 0.72f, 0.56f));
                prompt.style.fontSize = 24;
                prompt.style.marginTop = 20;
                prompt.style.marginBottom = 8;
                panel.Add(prompt);

                _field = new TextField { maxLength = _maxLength };
                _field.style.fontSize = 32;
                StyleField(_field, UiColor.Parse(_cfg.field_color, new Color(0.11f, 0.11f, 0.13f)), textColor);
                _field.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) Confirm();
                });
                panel.Add(_field);
                if (!string.IsNullOrEmpty(_cfg.field_url)) _ = ScreenUi.AssignBgAsync(_field, _cfg.field_url, _assets);
            }

            var start = new Button(Confirm) { text = _cfg.start_text ?? "Start" };
            start.style.fontSize = 32;
            start.style.marginTop = 22;
            start.style.paddingTop = 16;
            start.style.paddingBottom = 16;
            start.style.color = UiColor.Parse(_cfg.button_text_color, new Color(0.08f, 0.08f, 0.10f));
            start.style.backgroundColor = UiColor.Parse(_cfg.button_color, new Color(0.78f, 0.63f, 0.31f));
            start.style.borderTopLeftRadius = 12;
            start.style.borderTopRightRadius = 12;
            start.style.borderBottomLeftRadius = 12;
            start.style.borderBottomRightRadius = 12;
            panel.Add(start);
            if (!string.IsNullOrEmpty(_cfg.button_url)) _ = ScreenUi.AssignBgAsync(start, _cfg.button_url, _assets);

            // Platform sign-in — a button per provider the HOST actually
            // plugged into LvnPlatformAuth (no SDK, no button). Signing in
            // switches this device to that identity's account: the standard
            // cross-device recovery, wallet and saves included.
            var provRow = new VisualElement();
            provRow.style.flexDirection = FlexDirection.Row;
            provRow.style.justifyContent = Justify.Center;
            provRow.style.marginTop = 12;
            panel.Add(provRow);
            AddProviderButton(provRow, "google", _cfg.show_google ?? true,
                _cfg.google_text ?? "Sign in with Google", textColor);
            AddProviderButton(provRow, "apple", _cfg.show_apple ?? true,
                _cfg.apple_text ?? "Sign in with Apple", textColor);
#if UNITY_EDITOR
            AddProviderButton(provRow, "dev", true, "Dev sign-in", textColor);
#endif

            _status = new Label("");
            _status.style.color = UiColor.Parse(_cfg.status_color, new Color(0.60f, 0.58f, 0.54f));
            _status.style.fontSize = 20;
            _status.style.marginTop = 14;
            _status.style.unityTextAlign = TextAnchor.MiddleCenter;
            _status.pickingMode = PickingMode.Ignore;
            panel.Add(_status);

            _ = ScreenUi.AssignBgAsync(bg, _cfg.bg_url, _assets);
            _ = ScreenUi.AssignBgAsync(logo, _cfg.logo_url, _assets);
        }

        /// <summary>Show the screen, kick the silent device sign-in (its result
        /// only drives the status line) and resolve with the nickname once the
        /// player taps Start. Empty string when the field is off or blank.</summary>
        public async Task<string> AskAsync(CancellationToken ct = default)
        {
            style.display = DisplayStyle.Flex;
            if (_field != null)
            {
                var known = Lvn.Services.LvnBackend.DisplayName;
                _field.value = !string.IsNullOrEmpty(known) ? known : (_cfg.default_name ?? "");
            }
            _ = DriveStatusAsync();
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.3f, ct);

            _tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetCanceled());

            string name;
            try { name = await _tcs.Task; }
            finally
            {
                await ScreenFx.FadeAsync(this, 1f, 0f, 0.3f, CancellationToken.None);
                style.display = DisplayStyle.None;
            }
            // Fire-and-forget: the name lands on the account when the network
            // allows; Start never waits on the round-trip.
            if (!string.IsNullOrEmpty(name)) _ = Lvn.Services.LvnBackend.SetDisplayNameAsync(name);
            return name;
        }

        public void Hide()
        {
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _tcs?.TrySetCanceled();
        }

        private void Confirm()
        {
            var name = _field != null ? PlayerNameInput.Sanitize(_field.value, _maxLength) : "";
            _tcs?.TrySetResult(name ?? "");
        }

        private void AddProviderButton(VisualElement row, string provider, bool allowed, string label, Color textColor)
        {
            if (!allowed || !Lvn.Services.LvnPlatformAuth.Has(provider)) return;
            var b = new Button { text = label };
            b.style.fontSize = 22;
            b.style.marginLeft = 6; b.style.marginRight = 6;
            b.style.paddingTop = 10; b.style.paddingBottom = 10;
            b.style.paddingLeft = 18; b.style.paddingRight = 18;
            b.style.color = textColor;
            b.style.backgroundColor = new Color(1f, 1f, 1f, 0.10f);
            b.style.borderTopLeftRadius = 10; b.style.borderTopRightRadius = 10;
            b.style.borderBottomLeftRadius = 10; b.style.borderBottomRightRadius = 10;
            b.clicked += async () =>
            {
                b.SetEnabled(false);
                _status.text = _cfg.signing_text ?? "Connecting…";
                bool ok = await Lvn.Services.LvnPlatformAuth.SignInAsync(provider);
                b.SetEnabled(true);
                _status.text = ok
                    ? (_cfg.provider_done_text ?? "Signed in")
                    : (_cfg.offline_text ?? "Offline — progress stays on this device");
                // the recovered account may carry a display name — pre-fill it
                if (ok && _field != null && !string.IsNullOrEmpty(Lvn.Services.LvnBackend.DisplayName))
                    _field.value = Lvn.Services.LvnBackend.DisplayName;
            };
            row.Add(b);
        }

        private async Task DriveStatusAsync()
        {
            _status.text = _cfg.signing_text ?? "Connecting…";
            bool ok;
            try { ok = await Lvn.Services.LvnBackend.EnsureRegisteredAsync(); }
            catch { ok = false; }
            _status.text = ok
                ? (_cfg.signed_text ?? "Connected")
                : (_cfg.offline_text ?? "Offline — progress stays on this device");
        }

        private static void StyleField(TextField f, Color bg, Color text)
        {
            f.style.color = text;
            var input = f.Q(TextField.textInputUssName);
            if (input != null)
            {
                input.style.backgroundColor = bg;
                input.style.color = text;
                input.style.paddingTop = 12;
                input.style.paddingBottom = 12;
                input.style.paddingLeft = 14;
                input.style.paddingRight = 14;
            }
        }
    }
}
