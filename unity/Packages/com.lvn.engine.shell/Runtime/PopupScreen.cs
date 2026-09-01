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
    public sealed class PopupScreen : VisualElement, ILvnHides
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
        /// <summary>Какой показ сейчас на экране. Отвечает на «а экран ещё
        /// мой?» — см. ShowAsync.</summary>
        private int _showGen;

        private bool _openFlag;

        /// <summary>Алерт на экране. Пишется ТОЛЬКО здесь, и этим же движением
        /// он встаёт на стопку поверхностей Режиссёра. Раньше «алерт открыт»
        /// знал только сам экран, и спрашивали его, заглядывая в стиль показа
        /// снаружи; Режиссёр — тот, кто отвечает на «кто сейчас наверху», —
        /// про алерт не знал ничего, и сцена закрывала свою панель из-под
        /// поднятого над ней вопроса.</summary>
        private bool _open
        {
            get => _openFlag;
            set
            {
                if (_openFlag == value) return;
                _openFlag = value;
                if (value) Lvn.UI.LvnScreenDirector.Current.Open(Lvn.UI.LvnScreenDirector.Alert);
                else Lvn.UI.LvnScreenDirector.Current.Close(Lvn.UI.LvnScreenDirector.Alert);
            }
        }
        private bool _dismissable;

        /// <summary>True while a popup is on screen (blocks the scene beneath).</summary>
        public bool IsOpen => _open;

        public PopupScreen(PopupConfig cfg)
        {
            _cfg = cfg ?? new PopupConfig();
            _text = UiColor.Named(_cfg.text_color, LvnTokens.Text);
            _titleColor = UiColor.Named(_cfg.title_color, LvnTokens.Text);
            _btnColor = UiColor.Named(_cfg.button_color, LvnTokens.Faint);
            _btnText = UiColor.Named(_cfg.button_text_color, _text);
            _primaryColor = UiColor.Named(_cfg.primary_color, LvnTokens.Accent);
            _primaryText = UiColor.Named(_cfg.primary_text_color, LvnTokens.OnAccent);
            _radius = _cfg.corner_radius ?? LvnTokens.RadiusSm;

            ScreenUi.Stretch(this);
            // Закрытие тапом мимо — только у попапа, который вправе закрыться
            // без ответа; у вопроса с выбором его нет.
            Lvn.UI.LvnChrome.Scrim(this, () => { if (_dismissable) Resolve(-1); },
                UiColor.Named(_cfg.scrim_color, LvnTokens.Scrim));
            style.justifyContent = Justify.Center;
            style.alignItems = Align.Center;
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            // Tapping the scrim (not the card) dismisses — but only when allowed.

            _card = new VisualElement();
            _card.style.maxWidth = 560;
            _card.style.width = Length.Percent(80f);
            _card.style.backgroundColor = UiColor.Named(_cfg.panel_color, LvnTokens.PanelBg);
            LvnChrome.Round(_card, _radius + 4f);
            LvnAir.PadX(_card, LvnTokens.Space4);
            _card.style.paddingBottom = LvnTokens.Space3;
            _card.style.paddingTop = LvnTokens.Space4;
            Add(_card);

            _title = new Label { name = "popup-title" };
            _title.style.color = _titleColor;
            _title.style.fontSize = LvnTokens.TextBase;
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _title.style.whiteSpace = WhiteSpace.Normal;
            _title.style.unityTextAlign = TextAnchor.MiddleCenter;
            _title.style.marginBottom = LvnTokens.Space2;
            _card.Add(_title);

            _message = new Label { name = "popup-message" };
            ScreenUi.Quiet(_message, LvnTokens.TextSm, _text);
            _message.style.unityTextAlign = TextAnchor.MiddleCenter;
            _message.style.marginBottom = LvnTokens.Space3;
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
            // ЧЕЙ СЕЙЧАС ЭКРАН. Правило «последний вызов побеждает» было
            // записано только наполовину: отменённый показ доживал до своей
            // уборки уже ПОСЛЕ того, как экран занял второй, — и убирал за
            // ним. Гасил показ, снимал поверхность у Режиссёра и обнулял
            // ЧУЖОЕ ожидание: кнопки становились мертвы, попап невидим, а
            // ждущий не возвращался никогда. Два вопроса подряд — а по тапу на
            // запертую карточку они и идут подряд — вешали игру в том месте,
            // где ждали ответа. Метка поколения отвечает на «а экран ещё мой?».
            int gen = ++_showGen;
            bool wasUp = _open;   // вопрос сменяет вопрос — карточка уже видна
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
                : new List<Button> { new Button(LvnWords.Pick("common.ok", _cfg.ok_text, "OK"), true) };
            for (int i = 0; i < list.Count; i++) _buttons.Add(MakeButton(list[i], i, list.Count));

            style.display = DisplayStyle.Flex;
            // Смена вопроса не мигает: карточка уже на экране, и проявлять её
            // с нуля значит погасить и зажечь заново — игрок читает это как
            // сбой, а не как новый вопрос.
            await ScreenFx.FadeAsync(this, wasUp ? 1f : 0f, 1f, 0.18f, ct);
            // Убрали посреди появления (хозяин свернулся) или сменили вторым
            // вопросом — не парковаться на ожидании, которое некому решить.
            if (gen != _showGen || !_open) return -1;

            // Ждём СВОЁ ожидание, а не поле: пока мы стоим на нём, поле может
            // уже принадлежать сменщику.
            var mine = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tcs = mine;
            using var reg = ct.Register(() => mine.TrySetResult(-1));
            int result;
            try { result = await mine.Task; }
            finally
            {
                // Убирать за собой — только если экран ещё наш. Иначе уборка
                // достанется сменщику.
                if (gen == _showGen)
                {
                    await ScreenFx.FadeAsync(this, 1f, 0f, 0.18f, CancellationToken.None);
                    // И ЕЩЁ РАЗ ПОСЛЕ ЗАТУХАНИЯ. Одной проверки до него мало:
                    // гаснем 0,18 с, и это целое окно, за которое экран может
                    // занять новый вопрос — тогда три строки ниже уберут за
                    // НИМ, и висяк вернётся, только на 180 мс уже.
                    if (gen == _showGen)
                    {
                        style.display = DisplayStyle.None;
                        _open = false;
                        _tcs = null;
                    }
                }
            }
            return result;
        }

        /// <summary>A single-button notice. Resolves when dismissed.</summary>
        public Task AlertAsync(string title, string message, string ok = null, CancellationToken ct = default)
            => ShowAsync(title, message,
                new[] { new Button(ok ?? LvnWords.Pick("common.ok", _cfg.ok_text, "OK"), true) }, dismissable: true, ct);

        /// <summary>A two-button confirm. Returns true if the player pressed the
        /// primary/confirm button, false on cancel or dismissal.</summary>
        public async Task<bool> ConfirmAsync(string title, string message, string confirm = null,
                                             string cancel = null, CancellationToken ct = default)
        {
            var buttons = new[]
            {
                new Button(cancel ?? LvnWords.Pick("common.cancel", _cfg.cancel_text, "Cancel"), false),
                new Button(confirm ?? LvnWords.Pick("common.ok", _cfg.ok_text, "OK"), true),
            };
            // Index 1 is the confirm button.
            return await ShowAsync(title, message, buttons, dismissable: true, ct) == 1;
        }

        /// <summary>Force-close without a result (host teardown / scene reset).</summary>
        public void Hide()
        {
            // Поколение сдвигаем и здесь: показ, стоящий на ожидании, не
            // должен потом убирать ЕЩЁ раз — экран уже убран, и его повторное
            // затухание вернуло бы непрозрачность на кадр.
            _showGen++;
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
            b.style.fontSize = LvnTokens.TextSm;
            b.style.flexGrow = count > 1 ? 1 : 0;
            b.style.minWidth = 120;
            LvnAir.Pad(b, LvnTokens.Space3, LvnTokens.Space2);
            b.style.marginLeft = index > 0 ? 8 : 0;
            LvnStyler.Plate(b, spec.Primary ? _primaryColor : _btnColor,
                spec.Primary ? _primaryText : _btnText, _radius);
            b.style.unityTextAlign = TextAnchor.MiddleCenter;
            return b;
        }
    }
}
