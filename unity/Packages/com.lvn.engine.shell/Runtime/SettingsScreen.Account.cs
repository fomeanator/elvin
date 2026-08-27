using System;
using Lvn.Services;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// АККАУНТ И РЕКВИЗИТЫ — часть <see cref="SettingsScreen"/>: кто вошёл, что
    /// за сборка играет, куда писать и где читать правила. Скучная, но
    /// обязательная часть — сторы требуют её целиком.
    /// </summary>
    public sealed partial class SettingsScreen
    {
        private VisualElement UidRow()
        {
            var row = RowEx(_cfg.uid_label ?? "ID игрока",
                "Назовите его, если обратитесь в поддержку");
            var uid = LvnBackend.UserId;
            var shortId = string.IsNullOrEmpty(uid) ? "—" : (uid.Length > 12 ? uid.Substring(0, 12) + "…" : uid);
            var val = new Label(shortId);
            val.style.color = _dim;
            val.style.fontSize = 22;
            val.style.marginRight = 10;
            row.Add(val);

            var copy = new Button { text = _cfg.copy_text ?? "Копировать" };
            StyleValueButton(copy, false);
            copy.SetEnabled(!string.IsNullOrEmpty(uid));
            copy.clicked += () =>
            {
                GUIUtility.systemCopyBuffer = uid ?? "";
                LvnMotion.FlashText(copy, _cfg.copied_text ?? "Скопировано");
            };
            row.Add(copy);
            return row;
        }

        private VisualElement VersionRow()
        {
            var row = RowEx(_cfg.version_label ?? "Версия", null);
            var val = new Label(Application.version + EditorBuildStamp());
            val.style.color = _dim;
            val.style.fontSize = 22;
            row.Add(val);
            return row;
        }

        /// <summary>В РЕДАКТОРЕ — время сборки движка рядом с версией.
        /// Unity не пересобирает C# на ходу: правка, сделанная во время Play,
        /// доедет только после Stop→Play, и снаружи это неотличимо от «фича не
        /// работает». Штамп отвечает на вопрос «я вообще на свежем коде?» за
        /// пять секунд, без консоли. В собранной игре строки нет.</summary>
        private static string EditorBuildStamp()
        {
#if UNITY_EDITOR
            try
            {
                var path = typeof(Lvn.UI.VnStage).Assembly.Location;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return "";
                return "  · движок " + System.IO.File.GetLastWriteTime(path).ToString("HH:mm:ss");
            }
            catch { return ""; }
#else
            return "";
#endif
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
                    LvnAsync.Fire(ScreenUi.AssignBgAsync(icon, s.icon, _assets), "AssignBg");
                    el = icon;
                }
                else
                {
                    var lbl = new Label(s.name ?? "link");
                    lbl.style.color = _accent;
                    lbl.style.fontSize = 24;
                    el = lbl;
                }
                el.style.marginLeft = 10; el.style.marginRight = 10;
                var url = s.url;
                el.RegisterCallback<ClickEvent>(_ => LvnWebView.Open(url));
                row.Add(el);
            }
            return row;
        }

        private async Task RefreshAccountAsync()
        {
            var providers = await LvnBackend.GetProvidersAsync();
            if (!IsOpen || _accountRow == null) return;
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
            val.style.fontSize = 22;
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
    }
}
