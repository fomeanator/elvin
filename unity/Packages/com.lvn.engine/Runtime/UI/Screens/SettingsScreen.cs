using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The app-level settings overlay: master sound switch, language, player id
    /// with a copy button, account/sign-in status, app version, social links and
    /// Terms/Privacy. Themed from <see cref="SettingsConfig"/> (manifest
    /// <c>ui.settings</c>). Distinct from the quick-menu's in-game settings panel
    /// (playback tweaks) — this is the standalone screen with account + legal.
    ///
    /// TCS-gated like <see cref="StoreScreen"/>: <see cref="ShowAsync"/> resolves
    /// when the player closes it. Sound/language write straight to
    /// <see cref="LvnPrefs"/> (the stage reacts live); links open through the
    /// <see cref="LvnWebView"/> seam; "Sign in" is delegated to the host via
    /// <see cref="OnSignIn"/>.
    /// </summary>
    public sealed class SettingsScreen : VisualElement
    {
        /// <summary>Host hook for the "Sign in" button — route to the auth screen
        /// / platform sign-in. Null hides the button.</summary>
        public System.Func<Task> OnSignIn;

        private readonly SettingsConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly ScrollView _list;
        private readonly Color _text;
        private readonly Color _dim;
        private readonly Color _accent;
        private readonly float _radius;

        private TaskCompletionSource<bool> _tcs;
        private bool _open;
        private VisualElement _accountRow;

        public SettingsScreen(SettingsConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new SettingsConfig();
            _assets = assets;
            _text = UiColor.Parse(_cfg.text_color, new Color(0.95f, 0.93f, 0.88f));
            _dim = UiColor.Parse(_cfg.dim_text_color, new Color(0.60f, 0.58f, 0.54f));
            _accent = UiColor.Parse(_cfg.accent_color, new Color(0.78f, 0.63f, 0.31f));
            _radius = _cfg.corner_radius ?? 12f;

            ScreenUi.Stretch(this);
            style.backgroundColor = UiColor.Parse(_cfg.scrim_color, new Color(0f, 0f, 0f, 0.7f));
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            RegisterCallback<ClickEvent>(e => { if (e.target == this) Close(); });

            var sheet = new VisualElement();
            sheet.style.position = Position.Absolute;
            sheet.style.left = Length.Percent(6f);
            sheet.style.right = Length.Percent(6f);
            sheet.style.top = Length.Percent(8f);
            sheet.style.bottom = Length.Percent(8f);
            sheet.style.backgroundColor = UiColor.Parse(_cfg.panel_color, new Color(0.078f, 0.078f, 0.10f, 0.97f));
            Round(sheet, _radius + 4f);
            sheet.style.paddingTop = 22; sheet.style.paddingBottom = 18;
            sheet.style.paddingLeft = 20; sheet.style.paddingRight = 20;
            Add(sheet);

            var title = new Label(_cfg.title ?? "Settings");
            title.style.color = UiColor.Parse(_cfg.title_color, new Color(0.96f, 0.93f, 0.85f));
            title.style.fontSize = 34;
            title.style.marginBottom = 14;
            sheet.Add(title);

            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.style.flexGrow = 1;
            sheet.Add(_list);

            var close = new Button(Close) { text = _cfg.close_text ?? "Close" };
            close.style.fontSize = 24;
            close.style.marginTop = 12;
            close.style.paddingTop = 12; close.style.paddingBottom = 12;
            close.style.color = _text;
            close.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
            ClearBorder(close);
            Round(close, _radius);
            sheet.Add(close);
        }

        /// <summary>Open the settings overlay; resolves when the player closes it.</summary>
        public async Task ShowAsync(CancellationToken ct = default)
        {
            if (_open) return;
            _open = true;
            style.display = DisplayStyle.Flex;
            Rebuild();
            _ = RefreshAccountAsync(); // fills the account row from /v1/auth/me
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.25f, ct);
            if (!_open) return;

            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetResult(false));
            try { await _tcs.Task; }
            finally
            {
                await ScreenFx.FadeAsync(this, 1f, 0f, 0.25f, CancellationToken.None);
                style.display = DisplayStyle.None;
                _open = false;
            }
        }

        public void Hide()
        {
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _open = false;
            _tcs?.TrySetResult(false);
        }

        private void Close() => _tcs?.TrySetResult(true);

        /// <summary>(Re)build the settings rows from the current prefs/config. Called
        /// by <see cref="ShowAsync"/>; public so tests and hosts can render on demand.</summary>
        public void Rebuild()
        {
            _list.Clear();
            _list.Add(SoundRow());
            if (LvnPrefs.AvailableLocales != null && LvnPrefs.AvailableLocales.Count > 0)
                _list.Add(LanguageRow());
            _list.Add(UidRow());
            _accountRow = Row(_cfg.account_label ?? "Account");
            _list.Add(_accountRow);
            SetAccountStatus("…", showSignIn: false);
            _list.Add(VersionRow());
            var links = LinksRow();
            if (links != null) _list.Add(links);
            var socials = SocialRow();
            if (socials != null) _list.Add(socials);
        }

        // ── rows ──────────────────────────────────────────────────────────────

        private VisualElement SoundRow()
        {
            var row = Row(_cfg.sound_label ?? "Sound");
            var btn = new Button { text = LvnPrefs.SoundOn ? (_cfg.on_text ?? "On") : (_cfg.off_text ?? "Off") };
            StyleValueButton(btn, LvnPrefs.SoundOn);
            btn.clicked += () =>
            {
                LvnPrefs.SoundOn = !LvnPrefs.SoundOn;
                btn.text = LvnPrefs.SoundOn ? (_cfg.on_text ?? "On") : (_cfg.off_text ?? "Off");
                StyleValueButton(btn, LvnPrefs.SoundOn);
            };
            row.Add(btn);
            return row;
        }

        private VisualElement LanguageRow()
        {
            var row = Row(_cfg.language_label ?? "Language");
            var seg = new VisualElement();
            seg.style.flexDirection = FlexDirection.Row;
            row.Add(seg);

            // The script's inline language, then each localized catalog.
            var options = new List<string> { "" };
            options.AddRange(LvnPrefs.AvailableLocales);
            var buttons = new List<(Button b, string loc)>();
            void Highlight() { foreach (var (b, loc) in buttons) StyleValueButton(b, LvnPrefs.Locale == loc); }
            foreach (var loc in options)
            {
                var captured = loc;
                var b = new Button { text = LocaleName(loc) };
                b.style.marginLeft = 6;
                b.clicked += () =>
                {
                    LvnPrefs.Locale = captured; // NovelApp reloads the string catalog live
                    Highlight();
                };
                buttons.Add((b, captured));
                seg.Add(b);
            }
            Highlight();
            return row;
        }

        private VisualElement UidRow()
        {
            var row = Row(_cfg.uid_label ?? "Player ID");
            var uid = LvnBackend.UserId;
            var shortId = string.IsNullOrEmpty(uid) ? "—" : (uid.Length > 12 ? uid.Substring(0, 12) + "…" : uid);
            var val = new Label(shortId);
            val.style.color = _dim;
            val.style.fontSize = 20;
            val.style.marginRight = 10;
            row.Add(val);

            var copy = new Button { text = _cfg.copy_text ?? "Copy" };
            StyleValueButton(copy, false);
            copy.SetEnabled(!string.IsNullOrEmpty(uid));
            copy.clicked += () =>
            {
                GUIUtility.systemCopyBuffer = uid ?? "";
                copy.text = _cfg.copied_text ?? "Copied";
                copy.schedule.Execute(() => copy.text = _cfg.copy_text ?? "Copy").ExecuteLater(1200);
            };
            row.Add(copy);
            return row;
        }

        private VisualElement VersionRow()
        {
            var row = Row(_cfg.version_label ?? "Version");
            var val = new Label(Application.version);
            val.style.color = _dim;
            val.style.fontSize = 20;
            row.Add(val);
            return row;
        }

        private VisualElement LinksRow()
        {
            bool hasTerms = !string.IsNullOrEmpty(_cfg.terms_url);
            bool hasPrivacy = !string.IsNullOrEmpty(_cfg.privacy_url);
            if (!hasTerms && !hasPrivacy) return null;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.Center;
            row.style.marginTop = 8; row.style.marginBottom = 6;
            if (hasTerms) row.Add(LinkLabel(_cfg.terms_text ?? "Terms of Use", _cfg.terms_url));
            if (hasTerms && hasPrivacy)
            {
                var dot = new Label("·"); dot.style.color = _dim; dot.style.marginLeft = 10; dot.style.marginRight = 10;
                row.Add(dot);
            }
            if (hasPrivacy) row.Add(LinkLabel(_cfg.privacy_text ?? "Privacy Policy", _cfg.privacy_url));
            return row;
        }

        private VisualElement SocialRow()
        {
            if (_cfg.social == null || _cfg.social.Count == 0) return null;
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.Center;
            row.style.marginTop = 12;
            foreach (var s in _cfg.social)
            {
                if (s == null || string.IsNullOrEmpty(s.url)) continue;
                VisualElement el;
                if (!string.IsNullOrEmpty(s.icon))
                {
                    var icon = new VisualElement();
                    icon.style.width = 44; icon.style.height = 44;
                    icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                    icon.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                    _ = ScreenUi.AssignBgAsync(icon, s.icon, _assets);
                    el = icon;
                }
                else
                {
                    var lbl = new Label(s.name ?? "link");
                    lbl.style.color = _accent;
                    lbl.style.fontSize = 22;
                    el = lbl;
                }
                el.style.marginLeft = 10; el.style.marginRight = 10;
                var url = s.url;
                el.RegisterCallback<ClickEvent>(_ => LvnWebView.Open(url));
                row.Add(el);
            }
            return row;
        }

        // ── account status (async from /v1/auth/me) ─────────────────────────────

        private async Task RefreshAccountAsync()
        {
            var providers = await LvnBackend.GetProvidersAsync();
            if (!_open || _accountRow == null) return;
            if (providers != null && providers.Length > 0)
            {
                string via = string.Join(", ", System.Array.ConvertAll(providers, Capitalize));
                SetAccountStatus((_cfg.signed_in_text ?? "Signed in") + " · " + via, showSignIn: false);
            }
            else
            {
                // A device-only (or offline) account — offer to link Google/Apple.
                string via = _cfg.device_text ?? "device";
                SetAccountStatus((_cfg.signed_in_text ?? "Signed in") + " · " + via, showSignIn: OnSignIn != null);
            }
        }

        private void SetAccountStatus(string text, bool showSignIn)
        {
            if (_accountRow == null) return;
            // Rebuild the row's value side (keep the label at index 0).
            for (int i = _accountRow.childCount - 1; i >= 1; i--)
                _accountRow.RemoveAt(i);
            var val = new Label(text);
            val.style.color = _dim;
            val.style.fontSize = 20;
            val.style.marginRight = 10;
            _accountRow.Add(val);
            if (showSignIn)
            {
                var btn = new Button { text = _cfg.sign_in_text ?? "Sign in" };
                StyleValueButton(btn, true);
                btn.clicked += () => { if (OnSignIn != null) _ = OnSignIn(); };
                _accountRow.Add(btn);
            }
        }

        // ── shared bits ─────────────────────────────────────────────────────────

        // A label + a right-aligned value area.
        private VisualElement Row(string label)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginBottom = 6;
            row.style.paddingTop = 10; row.style.paddingBottom = 10;
            var lbl = new Label(label);
            lbl.style.color = _text;
            lbl.style.fontSize = 24;
            lbl.style.flexGrow = 1;
            row.Add(lbl);
            return row;
        }

        private Label LinkLabel(string text, string url)
        {
            var lbl = new Label(text);
            lbl.style.color = _accent;
            lbl.style.fontSize = 20;
            lbl.RegisterCallback<ClickEvent>(_ => LvnWebView.Open(url));
            return lbl;
        }

        private void StyleValueButton(Button b, bool active)
        {
            b.style.fontSize = 22;
            b.style.paddingTop = 8; b.style.paddingBottom = 8;
            b.style.paddingLeft = 16; b.style.paddingRight = 16;
            b.style.color = active ? new Color(0.08f, 0.08f, 0.10f) : _text;
            b.style.backgroundColor = active ? _accent : new Color(1f, 1f, 1f, 0.08f);
            ClearBorder(b);
            Round(b, _radius);
        }

        private static string LocaleName(string loc) =>
            string.IsNullOrEmpty(loc) ? "Orig" : loc.ToUpperInvariant();

        private static string Capitalize(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static void Round(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r; el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r; el.style.borderBottomRightRadius = r;
        }

        private static void ClearBorder(VisualElement el)
        {
            el.style.borderTopWidth = 0; el.style.borderBottomWidth = 0;
            el.style.borderLeftWidth = 0; el.style.borderRightWidth = 0;
        }
    }
}
