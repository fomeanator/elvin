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
    public sealed class AuthScreen : VisualElement, ILvnHides
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
            style.backgroundColor = UiColor.Named(_cfg.bg_color, LvnTokens.Bg);
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
            LvnPicture.Fit(logo, cover: false);
            Add(logo);

            // ── bottom panel: title, subtitle, (nickname), start, status ──
            var panel = Lvn.UI.LvnChrome.Sheet(new VisualElement());
            panel.style.bottom = Length.Percent(7f);
            panel.style.paddingTop = LvnTokens.Space4;
            panel.style.paddingBottom = 22;
            panel.style.paddingLeft = LvnTokens.Space4;
            panel.style.paddingRight = LvnTokens.Space4;
            panel.style.backgroundColor = UiColor.Named(_cfg.panel_color, LvnTokens.Veil(0.65f));
            LvnChrome.Round(panel, LvnTokens.Radius);
            Add(panel);

            var title = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Pick("auth.welcome", _cfg.title, "Welcome"));
            title.style.color = UiColor.Named(_cfg.title_color, LvnTokens.Text);
            title.style.fontSize = LvnTokens.TextLg;
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            panel.Add(title);

            if (!string.IsNullOrEmpty(_cfg.subtitle))
            {
                var subtitle = new Label(_cfg.subtitle);
                subtitle.style.color = UiColor.Named(_cfg.subtitle_color, LvnTokens.TextDim);
                subtitle.style.fontSize = LvnTokens.TextSm;
                subtitle.style.marginTop = LvnTokens.Space1;
                subtitle.style.unityTextAlign = TextAnchor.MiddleCenter;
                subtitle.style.whiteSpace = WhiteSpace.Normal;
                panel.Add(subtitle);
            }

            var textColor = UiColor.Named(_cfg.text_color, LvnTokens.Text);
            // The app NEVER asks the player's name — the novel does, at its
            // start (the Liminal pattern). A title can still opt the nickname
            // field in explicitly (ui.auth.ask_nickname: true).
            if (_cfg.ask_nickname ?? false)
            {
                var prompt = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Pick("auth.name_prompt", _cfg.name_prompt, "Your name"));
                prompt.style.color = UiColor.Named(_cfg.subtitle_color, LvnTokens.TextDim);
                prompt.style.fontSize = LvnTokens.TextSm;
                prompt.style.marginTop = LvnTokens.Space3;
                prompt.style.marginBottom = LvnTokens.Space1;
                panel.Add(prompt);

                _field = new TextField { maxLength = _maxLength };
                _field.style.fontSize = LvnTokens.TextBase;
                LvnChrome.Field(_field, UiColor.Named(_cfg.field_color, LvnTokens.Surface), textColor);
                _field.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) Confirm();
                });
                panel.Add(_field);
                if (!string.IsNullOrEmpty(_cfg.field_url)) LvnPicture.Skin(_field, _cfg.field_url, _assets);
            }

            var start = Lvn.UI.LvnRedress.Bind(new Button(Confirm), () => LvnWords.Pick("auth.start", _cfg.start_text, "Start"));
            start.style.fontSize = LvnTokens.TextBase;
            start.style.marginTop = 22;
            start.style.paddingTop = LvnTokens.Space3;
            start.style.paddingBottom = LvnTokens.Space3;
            start.style.color = UiColor.Named(_cfg.button_text_color, LvnTokens.OnAccent);
            start.style.backgroundColor = UiColor.Named(_cfg.button_color, LvnTokens.Accent);
            LvnChrome.Round(start, LvnTokens.RadiusSm);
            panel.Add(start);
            if (!string.IsNullOrEmpty(_cfg.button_url)) LvnPicture.Skin(start, _cfg.button_url, _assets);

            // Platform sign-in — a button per provider the HOST actually
            // plugged into LvnPlatformAuth (no SDK, no button). Signing in
            // switches this device to that identity's account: the standard
            // cross-device recovery, wallet and saves included.
            var provRow = new VisualElement();
            provRow.style.flexDirection = FlexDirection.Row;
            provRow.style.justifyContent = Justify.Center;
            provRow.style.marginTop = LvnTokens.Space2;
            panel.Add(provRow);
            AddProviderButton(provRow, "google", _cfg.show_google ?? true,
                LvnWords.Pick("auth.google", _cfg.google_text, "Sign in with Google"), textColor);
            AddProviderButton(provRow, "apple", _cfg.show_apple ?? true,
                LvnWords.Pick("auth.apple", _cfg.apple_text, "Sign in with Apple"), textColor);
#if UNITY_EDITOR
            AddProviderButton(provRow, "dev", true, "Dev sign-in", textColor);
#endif

            _status = new Label("");
            _status.style.color = UiColor.Named(_cfg.status_color, LvnTokens.TextDim);
            _status.style.fontSize = LvnTokens.TextXs;
            _status.style.marginTop = LvnTokens.Space2;
            _status.style.unityTextAlign = TextAnchor.MiddleCenter;
            _status.pickingMode = PickingMode.Ignore;
            panel.Add(_status);

            LvnPicture.Photo(bg, _cfg.bg_url, _assets);
            LvnPicture.Photo(logo, _cfg.logo_url, _assets, cover: false);
        }

        /// <summary>Show the screen, kick the silent device sign-in (its result
        /// only drives the status line) and resolve with the nickname once the
        /// player taps Start. Empty string when the field is off or blank.</summary>
        /// <summary>
        /// ЗНАКОМСТВО — ОДИН РАЗ ЗА УСТАНОВКУ.
        ///
        /// <para>Обряд из четырёх частей: не знакомились ли уже, спросить,
        /// поставить метку, посеять имя игрока. Стоял он ДВАЖДЫ — на первом
        /// запуске оболочки и по команде <c>auth</c> из скрипта, — и обе копии
        /// правили одну и ту же метку устройства.</para>
        ///
        /// <para>Порядок в нём не произвольный: метка ставится ПОСЛЕ ответа, а
        /// не до, — иначе игрок, закрывший форму на полпути, больше никогда её
        /// не увидит и останется безымянным навсегда. Пустое имя при этом
        /// метку всё равно ставит: «не захотел называться» — тоже ответ, и
        /// спрашивать снова значит не услышать его.</para>
        ///
        /// <para>КОГДА спрашивать — дело зовущего: у оболочки это «вводная уже
        /// пройдена», у скрипта — авторская команда. Здесь только «как».</para>
        /// </summary>
        public async Task AskOnceAsync(CancellationToken ct = default)
        {
            if (LvnPrefs.SeenWelcome) return;
            var nick = await AskAsync(ct);
            LvnPrefs.SeenWelcome = true;
            if (!string.IsNullOrEmpty(nick)) LvnPlayerName.Set(nick);
        }

        public async Task<string> AskAsync(CancellationToken ct = default)
        {
            style.display = DisplayStyle.Flex;
            if (_field != null)
            {
                var known = Lvn.Services.LvnBackend.DisplayName;
                _field.value = !string.IsNullOrEmpty(known) ? known : (_cfg.default_name ?? "");
            }
            LvnAsync.Fire(DriveStatusAsync(), "DriveStatus");
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
            if (!string.IsNullOrEmpty(name)) LvnAsync.Fire(Lvn.Services.LvnBackend.SetDisplayNameAsync(name), "SetDisplayName");
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
            b.style.fontSize = LvnTokens.TextSm;
            b.style.marginLeft = LvnTokens.Space1; b.style.marginRight = LvnTokens.Space1;
            b.style.paddingTop = LvnTokens.Space2; b.style.paddingBottom = LvnTokens.Space2;
            b.style.paddingLeft = LvnTokens.Space3; b.style.paddingRight = LvnTokens.Space3;
            b.style.color = textColor;
            b.style.backgroundColor = new Color(1f, 1f, 1f, 0.10f);
            LvnChrome.Round(b, LvnTokens.RadiusSm);
            // Через дом занятости: вход ждёт СЕТЬ, и сорванное ожидание
            // оставляло кнопку выключенной навсегда, а подпись — «Connecting…».
            Lvn.UI.LvnBusy.OnClick(b, async () =>
            {
                _status.text = LvnWords.Pick("auth.connecting", _cfg.signing_text, "Connecting…");
                bool ok = await Lvn.Services.LvnPlatformAuth.SignInAsync(provider);
                _status.text = ok
                    ? (LvnWords.Pick("account.signed_in", _cfg.provider_done_text, "Signed in"))
                    : (LvnWords.Pick("auth.offline", _cfg.offline_text, "Offline — progress stays on this device"));
                // the recovered account may carry a display name — pre-fill it
                if (ok && _field != null && !string.IsNullOrEmpty(Lvn.Services.LvnBackend.DisplayName))
                    _field.value = Lvn.Services.LvnBackend.DisplayName;
            }, busyText: null, what: "SignIn");
            row.Add(b);
        }

        private async Task DriveStatusAsync()
        {
            _status.text = LvnWords.Pick("auth.connecting", _cfg.signing_text, "Connecting…");
            bool ok;
            try { ok = await Lvn.Services.LvnBackend.EnsureRegisteredAsync(); }
            catch { ok = false; }
            _status.text = ok
                ? (LvnWords.Pick("auth.connected", _cfg.signed_text, "Connected"))
                : (LvnWords.Pick("auth.offline", _cfg.offline_text, "Offline — progress stays on this device"));
        }
    }
}
