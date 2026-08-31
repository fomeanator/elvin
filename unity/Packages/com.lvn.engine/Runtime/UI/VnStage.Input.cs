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
        // На чьём расписании заведён отсчёт: сцену пересобирают, и чужое
        // расписание после этого молчит.
        private VisualElement _choiceTickHost;
        private float _choiceDeadline;
        private float _choiceTotal;
        private float _choiceLastTick;   // когда такт был в прошлый раз — для честной паузы

        private void StartChoiceTimer(float seconds)
        {
            StopChoiceTimer();
            if (seconds <= 0f || _uiRoot == null) return;
            _choiceTotal = seconds;
            _choiceDeadline = LvnClock.Now() + seconds;
            _choiceLastTick = 0f;   // первый такт нового отсчёта паузе не считается
            _choices?.SetTimer(1f);
            // ОДИН ОТСЧЁТ НА ВСЕ ВЫБОРЫ. Заводился новый на каждый выбор со
            // сроком, а прежний оставался в расписании панели навсегда:
            // остановка его только усыпляла.
            if (_choiceTick != null && ReferenceEquals(_choiceTickHost, _uiRoot))
            {
                _choiceTick.Resume();
                return;
            }
            _choiceTickHost = _uiRoot;
            _choiceTick = _uiRoot.schedule.Execute(() =>
            {
                // An open menu or the art view freezes the clock — a timed choice
                // must race the player, not their settings screen.
                //
                // ОПЛАТА ВАРИАНТА — ТОЖЕ ПАУЗА. Поход за деньгами идёт через
                // сеть, и срок продолжал тикать: истёк на медленной связи —
                // время уводило игрока по своей ветке, а пришедший следом
                // успешный платёж молча уходил в никуда. Деньги списаны, ветка
                // чужая, игрок не понял, за что заплатил.
                // ПАУЗА СДВИГАЕТ СРОК НА ФАКТИЧЕСКИ ПРОШЕДШЕЕ, а не на шаг
                // расписания. Здесь прибавлялась ровно десятая доля секунды —
                // столько, сколько шаг ДОЛЖЕН длиться. На слабом устройстве
                // такт приходит реже (при тридцати кадрах — раз в ~133 мс), и
                // пауза протекала: срок расходовался на четверть, а при десяти
                // кадрах — наполовину. То есть ровно тот дефект, который пауза
                // и закрывает, только медленнее — и невоспроизводимо на
                // машине разработчика.
                float now = LvnClock.Now();
                float since = _choiceLastTick > 0f ? now - _choiceLastTick : 0f;
                _choiceLastTick = now;
                if (InputBlocked || _chromeHidden || _choiceCommitInFlight)
                { _choiceDeadline += since; return; }
                float left = _choiceDeadline - now;
                _choices?.SetTimer(left / _choiceTotal);
                if (left > 0f) return;
                StopChoiceTimer();

                // СПРОСИТЬ, ЕСТЬ ЛИ КУДА ИДТИ, — И ТОЛЬКО ПОТОМ СНИМАТЬ МЕНЮ.
                //
                // Меню снималось первым, безусловно. У выбора со сроком, но БЕЗ
                // ветки времени (валидатор такое лишь предупреждает) идти
                // некуда: варианты уже сняты с экрана, а история осталась стоять
                // на том же выборе. Игрок видит, как полоска дотикала и
                // варианты исчезли, — и либо возвращает их тапом, чтобы они
                // исчезли снова, либо упирается в глухой стоп посреди главы.
                //
                // Stale after a load/rollback is a no-op inside the player.
                if (_player == null || !_player.ResolveChoiceTimeout()) return;
                // Гасим тем же способом, что и клик: свой таймер уже остановлен.
                StopWaitingForPlayer(cancelTimer: false);
                _dialogue?.SuppressAdvanceHint(false);
                AutosaveNow(); // time picked the branch — same crash contract as a tap
            }).Every(100);
        }

        // Отсчёт УСЫПЛЯЕТСЯ, а не выбрасывается: он один на все выборы главы,
        // и следующий срок разбудит тот же.
        private void StopChoiceTimer() => _choiceTick?.Pause();

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
            LvnChrome.Stretch(_inputScrim);
            // Затемнение — авторского цвета, если он его назвал: «#161018f2» в
            // манифесте это не украшение, а решение о том, сквозь что игрок
            // смотрит на сцену, задавая ответ.
            _inputScrim.style.backgroundColor = UiColor.Parse(NameInput?.bg_color, new Color(0f, 0f, 0f, 0.62f));
            _inputScrim.style.justifyContent = Justify.Center;
            _inputScrim.style.alignItems = Align.Center;
            _inputScrim.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());

            // ФОРМА ВВОДА — ЭТО ТОЖЕ ИГРА. Она выглядела отладочной: серая
            // коробка, белое поле с системным выделением и серая кнопка посреди
            // нарисованной сцены. Одевается тем же, чем одето окно диалога —
            // цвет панели, скругление, рисованная рамка темы, — потому что
            // игрок в этот момент разговаривает с персонажем, а не заполняет
            // анкету.
            var panel = new VisualElement();
            panel.style.width = Length.Percent(82);
            panel.style.maxWidth = 900;
            // Запасной цвет — из токенов, а не числами: без темы сцены поле
            // всё равно обязано быть цвета действующей палитры.
            panel.style.backgroundColor = Theme != null ? Theme.PanelColor : LvnTokens.Panel(0.94f);
            panel.style.paddingLeft = Theme != null ? Theme.PanelPaddingX : 22f;
            panel.style.paddingRight = Theme != null ? Theme.PanelPaddingX : 22f;
            panel.style.paddingTop = Theme != null ? Theme.PanelPaddingY : 18f;
            panel.style.paddingBottom = Theme != null ? Theme.PanelPaddingY : 18f;
            panel.style.overflow = Overflow.Visible;   // рамка выступает наружу
            float r = Theme != null ? Theme.PanelCornerRadius : 12f;
            LvnChrome.Round(panel, r);
            UiStyle.ApplyBackground(panel, Theme?.PanelSprite, Theme != null ? Theme.PanelSlice : 0);
            if (Theme?.PanelSprite == null) LvnChrome.Frame(panel);
            _inputScrim.Add(panel);

            // Вопрос: команда сильнее манифеста — она ближе к месту, где его
            // задают; манифест отвечает, когда команда молчит.
            var promptText = (string)cmd["prompt"] ?? NameInput?.prompt;
            if (!string.IsNullOrEmpty(promptText) && _strings != null && _strings.TryGetValue(promptText, out var trPrompt))
                promptText = trPrompt; // localization catalog, keyed by the source prompt
            if (_player != null) promptText = TextInterpolation.Apply(promptText, _player.Vars);
            if (!string.IsNullOrEmpty(promptText))
            {
                var prompt = new Label(promptText);
                // Вопрос задаёт история — значит и цвет у него тот же, каким
                // подписан говорящий.
                prompt.style.color = UiColor.Parse(NameInput?.prompt_color,
                    Theme != null ? Theme.SpeakerColor : LvnTokens.Accent);
                prompt.style.fontSize = Theme != null ? Theme.BodyFontSize : 30;
                prompt.style.whiteSpace = WhiteSpace.Normal;
                prompt.style.marginBottom = 18;
                LvnFonts.Apply(prompt, Theme?.Font);
                panel.Add(prompt);
            }

            // ПОДПИСЬ ПОЛЯ («Имя») — что именно у игрока спрашивают, когда
            // сам вопрос уже прозвучал репликой. Автор назвал её в манифесте.
            if (!string.IsNullOrEmpty(NameInput?.speaker_label))
            {
                var badge = new Label(NameInput.speaker_label);
                badge.style.color = UiColor.Parse(NameInput.prompt_color,
                    Theme != null ? Theme.SpeakerColor : LvnTokens.Accent);
                badge.style.fontSize = (Theme != null ? Theme.BodyFontSize : 30) * 0.7f;
                badge.style.marginBottom = 6;
                LvnFonts.Apply(badge, Theme?.Font);
                panel.Add(badge);
            }

            var field = new TextField();
            field.value = (string)cmd["default"] ?? NameInput?.default_name ?? string.Empty;
            int max = 0;
            try { max = cmd["max"] != null ? (int)cmd["max"] : 0; } catch { }   // экран ввода снесли на полуслове — история продолжится
            if (max <= 0 && NameInput?.max_length > 0) max = NameInput.max_length.Value;
            if (max > 0) field.maxLength = max;
            field.style.fontSize = Theme != null ? Theme.BodyFontSize : 30;
            field.style.marginBottom = 20;
            LvnFonts.Apply(field, Theme?.Font);
            // Красится ВНУТРЕННИЙ элемент поля: у TextField своя подложка, и
            // цвет, поставленный снаружи, до неё не доходит.
            LvnChrome.Field(field,
                UiColor.Parse(NameInput?.field_color, LvnTokens.SurfaceHi),
                UiColor.Parse(NameInput?.text_color, Theme != null ? Theme.TextColor : LvnTokens.Text));
            var inner = field.Q(TextField.textInputUssName);
            if (inner != null)
            {
                LvnChrome.Round(inner, Mathf.Max(8f, r * 0.4f));
                LvnChrome.Border(inner, Theme != null ? Theme.SpeakerColor : LvnTokens.Accent, 2f);
                inner.style.minHeight = 72;   // палец, а не курсор
            }
            // Каретка и выделение — тоже тема: системное синее выделение на
            // тёмной панели читается как чужой элемент операционной системы.
            field.textSelection.cursorColor = Theme != null ? Theme.SpeakerColor : LvnTokens.Accent;
            var sel = Theme != null ? Theme.SpeakerColor : LvnTokens.Accent;
            sel.a = 0.35f;
            field.textSelection.selectionColor = sel;
            panel.Add(field);

            // Слово на кнопке: манифест старше темы — он ближе к автору, чем
            // набор подписей темы, и «Подтвердить» он пишет именно там.
            string okLabel = !string.IsNullOrEmpty(NameInput?.confirm_text)
                ? NameInput.confirm_text : Theme.Word("input_ok", "OK");
            var ok = new Button(() => ConfirmInput(field.value)) { text = okLabel };
            // Та же кнопка, что и у выбора в диалоге: игрок уже знает, как она
            // выглядит и что делает.
            ok.style.height = Theme != null ? Theme.ChoiceMinHeight : 96f;
            ok.style.fontSize = Theme != null ? Theme.ChoiceFontSize : 28;
            ok.style.color = UiColor.Parse(NameInput?.button_text_color,
                Theme != null ? Theme.ChoiceTextColor : LvnTokens.Text);
            ok.style.backgroundColor = UiColor.Parse(NameInput?.button_color,
                Theme != null ? Theme.ChoiceColor : LvnTokens.Surface);
            ok.style.borderLeftWidth = 0; ok.style.borderRightWidth = 0;
            ok.style.borderTopWidth = 0; ok.style.borderBottomWidth = 0;
            LvnChrome.Round(ok, Mathf.Max(8f, r * 0.4f));
            LvnChrome.Border(ok, Theme != null ? Theme.SpeakerColor : LvnTokens.Accent, 2f);
            LvnFonts.Apply(ok, Theme?.Font);
            LvnMotion.Tappable(ok);
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
            {
                _player.Vars[_inputVar] = value ?? string.Empty;
                // Игрок назвался — об этом обязана узнать и ОБОЛОЧКА. Раньше имя
                // оставалось внутри истории: в прологе игрок вводил своё, а хаб,
                // профиль и гардероб продолжали звать его каталожным именем
                // героини, потому что смотрели в настройки устройства (TR-59).
                if (Lvn.UI.LvnPlayerName.IsNameVar(_inputVar) && !string.IsNullOrEmpty(value))
                    Lvn.UI.LvnPlayerName.Set(value);
            }
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
