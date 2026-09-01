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
            // Землю ставит Ground() при показе: чем закрывать — зависит от
            // того, что под нами, а на сборке это ещё неизвестно.
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
            LvnPicture.Fit(logo, cover: false);
            Add(logo);

            // ── bottom panel: title, subtitle, (nickname), start, status ──
            var panel = Lvn.UI.LvnChrome.Sheet(new VisualElement());
            panel.style.bottom = Length.Percent(7f);
            LvnStyler.Panel(panel, UiColor.Named(_cfg.panel_color, LvnTokens.Veil(0.65f)));
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
            LvnAir.PadY(start, LvnTokens.Space3);
            start.style.marginTop = LvnTokens.Space4;
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

        /// <summary>
        /// ЗНАКОМСТВО — ОДИН РАЗ ЗА УСТАНОВКУ. Показывает экран, запускает тихий
        /// вход по устройству (его исход двигает только строку состояния) и
        /// отдаёт имя, когда игрок нажал «Начать»; пустая строка — если поля
        /// нет или оно не заполнено.
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

        /// <summary>
        /// ЧЕМ ЗАКРЫВАТЬ ТО, ЧТО ПОД НАМИ.
        ///
        /// <para>Экран знакомства приходит по двум поводам, и под ним лежит
        /// РАЗНОЕ. На первом запуске под ним пусто — тогда он и есть земля, и
        /// красит себя цветом темы. По команде сценария («агент просит
        /// представиться») под ним ЖИВАЯ СЦЕНА, которую игрок в этот момент
        /// разглядывает, — и непрозрачная заливка стирает её начисто.</para>
        ///
        /// <para>Правило одно и то же по всей игре: наложение поверх сцены её
        /// ЗАВЕШИВАЕТ. Так ведут себя форма ввода, выборы и меню; экран
        /// знакомства был единственным, кто сцену заменял, — и заметно это
        /// стало только когда сценарий начал звать его посреди главы.</para>
        ///
        /// <para>Названный автором цвет сильнее любого правила: он писал его,
        /// глядя на свой экран.</para>
        /// </summary>
        private void Ground()
        {
            if (!string.IsNullOrEmpty(_cfg.bg_color))
            {
                style.backgroundColor = UiColor.Named(_cfg.bg_color, LvnTokens.Bg);
                return;
            }
            style.backgroundColor = LvnScreenDirector.Current.InChapter
                ? LvnTokens.Veil(0.72f)   // под нами сцена — завешиваем
                : LvnTokens.Bg;           // под нами пусто — мы и есть земля
        }

        public async Task<string> AskAsync(CancellationToken ct = default)
        {
            Ground();
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
            if (!string.IsNullOrEmpty(name)) LvnAsync.Fire(SaveNameOrSayAsync(name), "SetDisplayName");
            return name;
        }

        /// <summary>Имя, которое не доехало до учётки. Локально оно уже стоит,
        /// поэтому игрок ничего не заметит — до второго устройства, где он
        /// окажется безымянным. Ждать ответа на входе нельзя (это задержало бы
        /// начало игры), а промолчать — значит потерять единственный след.</summary>
        private static async Task SaveNameOrSayAsync(string name)
        {
            if (await Lvn.Services.LvnBackend.SetDisplayNameAsync(name)) return;
            Debug.LogWarning($"[lvn] имя «{name}» не сохранилось на учётке — " +
                "на другом устройстве игрок окажется безымянным.");
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
            LvnAir.Pad(b, LvnTokens.Space3, LvnTokens.Space2);
            LvnAir.MarginX(b, LvnTokens.Space1);
            // РОЛЬ, А НЕ ЦВЕТ. Плашка была белой по жёсткому числу (10% белого),
            // и в тёмно-бирюзовой теме, где все приглушённые плашки подкрашены,
            // эти три кнопки оставались единственными белыми. Тихая плашка —
            // роль второстепенного действия, и цвет ей выбирает тема.
            LvnStyler.Plate(b, LvnTokens.Faint, textColor, LvnTokens.RadiusSm);
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
