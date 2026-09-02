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
        /// <summary>
        /// СБРОС АККАУНТА — строка, за которой стоит обряд забвения.
        ///
        /// <para>Подтверждение встроено в саму кнопку, а не выведено попапом:
        /// первое нажатие меняет надпись на «Точно?», второе стирает, и через
        /// несколько секунд без второго нажатия она возвращается к прежнему
        /// виду. Так игрок не сносит себя случайным тапом, а тестировщику не
        /// приходится закрывать окно ради каждого прогона воронки.</para>
        ///
        /// <para>Стирается только УСТРОЙСТВО. Серверный аккаунт живёт своей
        /// жизнью: удаление там — отдельное действие с отдельной ценой, и
        /// путать их нельзя.</para>
        /// </summary>
        private VisualElement ResetRow()
        {
            var row = RowEx(LvnWords.Of("settings.reset", "Reset account"),
                LvnWords.Of("settings.reset_hint",
                    "Wipes progress, purchases and name on this device — the game starts over"));

            // ВЗВЕДЕНО, НО НЕ НАВСЕГДА. Забытая взведённой кнопка — мина:
            // игрок вернётся в настройки через минуту и снесёт себя одним
            // касанием, думая, что нажал впервые. Обряд целиком — у дома
            // (Lvn.UI.LvnAskTwice): срок взвода, разоружение при уходе с
            // экрана и подпись, читающая состояние, а не назначенная руками.
            var btn = new Button();
            StyleValueButton(btn, false);
            btn.style.color = Lvn.UI.LvnTheme.Current.Warn;
            Lvn.UI.LvnAskTwice.AskTwice(btn,
                calm: () => LvnWords.Of("common.reset", "Reset"),
                armed: () => LvnWords.Of("common.sure", "Sure?"),
                confirmed: () =>
                {
                    OnResetAccount?.Invoke();
                    LvnMotion.FlashText(btn, LvnWords.Of("common.done", "Done"));
                });
            row.Add(btn);
            return row;
        }

        private VisualElement UidRow()
        {
            var row = RowEx(LvnWords.Pick("account.uid", _cfg.uid_label, "Player ID"),
                LvnWords.Of("account.uid_hint", "Quote it if you contact support"));
            var uid = LvnBackend.UserId;
            var shortId = string.IsNullOrEmpty(uid) ? "—" : Lvn.LvnClip.Id(uid);
            var val = new Label(shortId);
            val.style.color = _dim;
            val.style.fontSize = LvnTokens.TextSm;
            val.style.marginRight = LvnTokens.Space2;
            row.Add(val);

            var copy = Lvn.UI.LvnRedress.Bind(new Button(), () => LvnWords.Pick("settings.copy", _cfg.copy_text, "Copy"));
            StyleValueButton(copy, false);
            copy.SetEnabled(!string.IsNullOrEmpty(uid));
            copy.clicked += () =>
            {
                GUIUtility.systemCopyBuffer = uid ?? "";
                LvnMotion.FlashText(copy, LvnWords.Pick("settings.copied", _cfg.copied_text, "Copied"));
            };
            row.Add(copy);
            return row;
        }

        private VisualElement VersionRow()
        {
            var row = RowEx(LvnWords.Pick("settings.version", _cfg.version_label, "Version"), null);
            var val = new Label(Application.version + EditorBuildStamp());
            val.style.color = _dim;
            val.style.fontSize = LvnTokens.TextSm;
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
            LvnAir.MarginY(row, LvnTokens.Space1);
            if (hasTerms) row.Add(LinkLabel(LvnWords.Pick("account.terms", _cfg.terms_text, "Terms of Use"), _cfg.terms_url));
            if (hasTerms && hasPrivacy)
            {
                var dot = new Label("·"); dot.style.color = _dim; dot.style.marginLeft = LvnTokens.Space2; dot.style.marginRight = LvnTokens.Space2;
                row.Add(dot);
            }
            if (hasPrivacy) row.Add(LinkLabel(LvnWords.Pick("account.privacy", _cfg.privacy_text, "Privacy Policy"), _cfg.privacy_url));
            return row;
        }

        private VisualElement SocialRow()
        {
            if (_cfg.social == null || _cfg.social.Count == 0) return null;
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.Center;
            row.style.marginTop = LvnTokens.Space2;
            foreach (var s in _cfg.social)
            {
                if (s == null || string.IsNullOrEmpty(s.url)) continue;
                VisualElement el;
                if (!string.IsNullOrEmpty(s.icon))
                {
                    var icon = new VisualElement();
                    icon.style.width = 44; icon.style.height = 44;
                    LvnPicture.Photo(icon, s.icon, _assets, cover: false);
                    el = icon;
                }
                else
                {
                    var lbl = new Label(s.name ?? "link");
                    lbl.style.color = _accent;
                    lbl.style.fontSize = LvnTokens.TextSm;
                    el = lbl;
                }
                LvnAir.MarginX(el, LvnTokens.Space2);
                var url = s.url;
                el.RegisterCallback<ClickEvent>(_ => LvnWebView.Open(url));
                row.Add(el);
            }
            return row;
        }

        private async Task RefreshAccountAsync()
        {
            // ВОПРОС У СТРОКИ ОДИН: переживёт ли прогресс смену телефона. Пока
            // аккаунт только на устройстве — не переживёт; привязанный к Google
            // или Apple — переживёт. Провайдеры и токены игрока не касаются.
            //
            // Раньше строка показывала многоточие, пока шёл запрос, и оставалась
            // им навсегда при отказе сети: «Аккаунт · …» не отвечает ни на что и
            // выглядит поломкой («странный и непонятный пункт», Илья 28.08).
            var providers = await LvnBackend.GetProvidersAsync();
            if (!IsOpen || _accountRow == null) return;
            if (providers == null)
            {
                // Спросить не вышло — говорим об этом, а не молчим точками.
                SetAccountStatus(LvnWords.Of("account.unknown", "No connection — will check later"),
                    showSignIn: false);
                return;
            }
            if (providers.Length > 0)
            {
                string via = string.Join(", ", System.Array.ConvertAll(providers, Capitalize));
                SetAccountStatus(LvnWords.Pick("account.signed_in", _cfg.signed_in_text, "Signed in") + " · " + via,
                    showSignIn: false);
                return;
            }
            // Только устройство: главное здесь не «как вошёл», а ЧЕМ РИСКУЕТ.
            // Авторское слово уважается и здесь: игра вправе назвать это
            // по-своему («только на телефоне»), а перевод — сильнее их обоих.
            SetAccountStatus(LvnWords.Pick("account.device_only", _cfg.device_text,
                "Progress lives on this device only"), showSignIn: OnSignIn != null);
        }

        private void SetAccountStatus(string text, bool showSignIn)
        {
            if (_accountRow == null) return;
            // Rebuild the row's value side (keep the label at index 0).
            for (int i = _accountRow.childCount - 1; i >= 1; i--)
                _accountRow.RemoveAt(i);
            var val = new Label(text);
            val.style.color = _dim;
            val.style.fontSize = LvnTokens.TextSm;
            val.style.marginRight = LvnTokens.Space2;
            _accountRow.Add(val);
            if (showSignIn)
            {
                var btn = Lvn.UI.LvnRedress.Bind(new Button(), () => LvnWords.Pick("account.sign_in", _cfg.sign_in_text, "Sign in"));
                StyleValueButton(btn, true);
                // ЧЕРЕЗ ДОМ ЗАНЯТОЙ КНОПКИ. Вход в аккаунт ждёт сеть и открывает
                // экран платформы; второй тап по неотвеченной кнопке запускал
                // второй такой вход. Дом гасит кнопку на время работы и
                // отклоняет повторный тап — ровно для этого он и заведён.
                // Отпускать не нужно: удачный вход уводит с этого экрана сам.
                Lvn.UI.LvnBusy.OnClick(btn, () => OnSignIn(),
                    releaseOnSuccess: false, what: "SignIn");
                _accountRow.Add(btn);
            }
        }
    }
}
