using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The universal modal popup: a full-screen scrim plus a centered card with
    /// a title, a message and a row of 1–N buttons. One reusable overlay for
    /// every warning / confirmation / error in the app — "not enough energy",
    /// "buy currency?", generic alerts — so hosts and screens stop hand-rolling
    /// their own dialogs.
    ///
    /// TCS-gated like the other overlays (<see cref="StoreScreen"/>):
    /// <see cref="ShowAsync"/> resolves with the index of the button the player
    /// pressed, or −1 if the popup was dismissed (scrim tap / cancellation).
    /// Themed from <see cref="PopupConfig"/> (manifest <c>ui.popup</c>).
    /// </summary>
    public sealed class PopupScreen : VisualElement
    {
        /// <summary>One button in a popup: its label and whether it's the
        /// highlighted primary/confirm action.</summary>
        public readonly struct Button
        {
            public readonly string Label;
            public readonly bool Primary;
            public Button(string label, bool primary = false) { Label = label; Primary = primary; }
        }

        private readonly PopupConfig _cfg;
        private readonly VisualElement _card;
        private readonly Label _title;
        private readonly Label _message;
        private readonly VisualElement _buttons;

        private readonly Color _text;
        private readonly Color _titleColor;
        private readonly Color _btnColor;
        private readonly Color _btnText;
        private readonly Color _primaryColor;
        private readonly Color _primaryText;
        private readonly float _radius;

        private TaskCompletionSource<int> _tcs;
        private bool _open;
        private bool _dismissable;

        /// <summary>True while a popup is on screen (blocks the scene beneath).</summary>
        public bool IsOpen => _open;

        public PopupScreen(PopupConfig cfg)
        {
            _cfg = cfg ?? new PopupConfig();
            _text = UiColor.Parse(_cfg.text_color, new Color(0.91f, 0.89f, 0.85f));
            _titleColor = UiColor.Parse(_cfg.title_color, new Color(0.96f, 0.93f, 0.85f));
            _btnColor = UiColor.Parse(_cfg.button_color, new Color(1f, 1f, 1f, 0.08f));
            _btnText = UiColor.Parse(_cfg.button_text_color, _text);
            _primaryColor = UiColor.Parse(_cfg.primary_color, new Color(0.78f, 0.63f, 0.31f));
            _primaryText = UiColor.Parse(_cfg.primary_text_color, new Color(0.08f, 0.08f, 0.10f));
            _radius = _cfg.corner_radius ?? 12f;

            ScreenUi.Stretch(this);
            style.backgroundColor = UiColor.Parse(_cfg.scrim_color, new Color(0f, 0f, 0f, 0.7f));
            style.justifyContent = Justify.Center;
            style.alignItems = Align.Center;
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            // Tapping the scrim (not the card) dismisses — but only when allowed.
            RegisterCallback<ClickEvent>(e => { if (e.target == this && _dismissable) Resolve(-1); });

            _card = new VisualElement();
            _card.style.maxWidth = 560;
            _card.style.width = Length.Percent(80f);
            _card.style.backgroundColor = UiColor.Parse(_cfg.panel_color, new Color(0.078f, 0.078f, 0.10f, 0.97f));
            Round(_card, _radius + 4f);
            _card.style.paddingTop = 24;
            _card.style.paddingBottom = 20;
            _card.style.paddingLeft = 24;
            _card.style.paddingRight = 24;
            Add(_card);

            _title = new Label { name = "popup-title" };
            _title.style.color = _titleColor;
            _title.style.fontSize = 30;
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _title.style.whiteSpace = WhiteSpace.Normal;
            _title.style.unityTextAlign = TextAnchor.MiddleCenter;
            _title.style.marginBottom = 10;
            _card.Add(_title);

            _message = new Label { name = "popup-message" };
            _message.style.color = _text;
            _message.style.fontSize = 24;
            _message.style.whiteSpace = WhiteSpace.Normal;
            _message.style.unityTextAlign = TextAnchor.MiddleCenter;
            _message.style.marginBottom = 20;
            _card.Add(_message);

            _buttons = new VisualElement();
            _buttons.style.flexDirection = FlexDirection.Row;
            _buttons.style.justifyContent = Justify.Center;
            _card.Add(_buttons);
        }

        /// <summary>Show a popup with arbitrary buttons; resolves with the index
        /// of the pressed button, or −1 if dismissed. The latest call wins: a
        /// popup already on screen is cancelled (−1) and replaced.</summary>
        public async Task<int> ShowAsync(string title, string message, IReadOnlyList<Button> buttons,
                                         bool dismissable = true, CancellationToken ct = default)
        {
            // Re-entrancy: cancel any popup currently up, then take over.
            if (_open) { _tcs?.TrySetResult(-1); _tcs = null; }
            _open = true;
            _dismissable = dismissable;

            _title.text = title ?? "";
            _title.style.display = string.IsNullOrEmpty(title) ? DisplayStyle.None : DisplayStyle.Flex;
            _message.text = message ?? "";
            _message.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;

            _buttons.Clear();
            var list = (buttons != null && buttons.Count > 0)
                ? buttons
                : new List<Button> { new Button(_cfg.ok_text ?? "OK", true) };
            for (int i = 0; i < list.Count; i++) _buttons.Add(MakeButton(list[i], i, list.Count));

            style.display = DisplayStyle.Flex;
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.18f, ct);
            // Hidden mid-fade (host tore down) — don't park on a TCS nobody resolves.
            if (!_open) return -1;

            _tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs?.TrySetResult(-1));
            int result;
            try { result = await _tcs.Task; }
            finally
            {
                await ScreenFx.FadeAsync(this, 1f, 0f, 0.18f, CancellationToken.None);
                style.display = DisplayStyle.None;
                _open = false;
                _tcs = null;
            }
            return result;
        }

        /// <summary>A single-button notice. Resolves when dismissed.</summary>
        public Task AlertAsync(string title, string message, string ok = null, CancellationToken ct = default)
            => ShowAsync(title, message,
                new[] { new Button(ok ?? _cfg.ok_text ?? "OK", true) }, dismissable: true, ct);

        /// <summary>A two-button confirm. Returns true if the player pressed the
        /// primary/confirm button, false on cancel or dismissal.</summary>
        public async Task<bool> ConfirmAsync(string title, string message, string confirm = null,
                                             string cancel = null, CancellationToken ct = default)
        {
            var buttons = new[]
            {
                new Button(cancel ?? _cfg.cancel_text ?? "Cancel", false),
                new Button(confirm ?? _cfg.ok_text ?? "OK", true),
            };
            // Index 1 is the confirm button.
            return await ShowAsync(title, message, buttons, dismissable: true, ct) == 1;
        }

        /// <summary>Force-close without a result (host teardown / scene reset).</summary>
        public void Hide()
        {
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _open = false;
            _tcs?.TrySetResult(-1);
            _tcs = null;
        }

        private void Resolve(int index) => _tcs?.TrySetResult(index);

        private UnityEngine.UIElements.Button MakeButton(Button spec, int index, int count)
        {
            var b = new UnityEngine.UIElements.Button(() => Resolve(index)) { text = spec.Label ?? "" };
            b.style.fontSize = 24;
            b.style.flexGrow = count > 1 ? 1 : 0;
            b.style.minWidth = 120;
            b.style.marginLeft = index > 0 ? 8 : 0;
            b.style.paddingTop = 12;
            b.style.paddingBottom = 12;
            b.style.paddingLeft = 18;
            b.style.paddingRight = 18;
            b.style.color = spec.Primary ? _primaryText : _btnText;
            b.style.backgroundColor = spec.Primary ? _primaryColor : _btnColor;
            b.style.unityTextAlign = TextAnchor.MiddleCenter;
            ClearBorder(b);
            Round(b, _radius);
            return b;
        }

        private static void Round(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r;
            el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r;
            el.style.borderBottomRightRadius = r;
        }

        private static void ClearBorder(VisualElement el)
        {
            el.style.borderTopWidth = 0;
            el.style.borderBottomWidth = 0;
            el.style.borderLeftWidth = 0;
            el.style.borderRightWidth = 0;
        }
    }
}
