using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// ВЫБОР ИГРОКА — показ вариантов, их место на экране и то, что происходит
    /// после нажатия.
    ///
    /// <para>Жил вместе с репликой, и это две РАЗНЫЕ темы: реплика — про то,
    /// как слова доходят до игрока, выбор — про то, как его решение доходит до
    /// истории. Общего у них ровно столько, сколько видно снаружи: стопка
    /// вариантов опускается под окно реплики, а окно растёт по ходу печати.</para>
    ///
    /// <para>Здесь же живёт цена варианта: выбор, за который платят, обязан
    /// сперва взять деньги и только потом увести игрока по ветке — иначе
    /// оборванная покупка оставляет историю ушедшей, а кошелёк нетронутым.</para>
    /// </summary>
    public sealed partial class VnStage
    {
        /// <summary>Опустить стопку выборов под диалоговое окно, когда оба на
        /// экране. Зов идёт из NotifyUiStage (смена реплики/выбора) и с каждого
        /// изменения геометрии окна — текст печатается, окно растёт, и граница
        /// сдвигается прямо по ходу реплики.</summary>
        private void SyncChoicesBelowBox()
        {
            if (_choices == null || Theme == null || Theme.ChoiceYPercent < 0f || Theme.Nvl) return;
            float clampY = -1f;
            if (_dialogue != null && _dialogue.style.display == DisplayStyle.Flex)
            {
                var box = _dialogue.Q("vn-box");
                var host = _choices.parent;
                if (box != null && host != null && box.resolvedStyle.height > 1f)
                    clampY = box.worldBound.yMax - host.worldBound.y + Theme.ChoiceSpacing;
            }
            _choices.ClampBelow(clampY);
        }
        /// <summary>Bind choice placement to the geometry that actually grows:
        /// the inner dialogue box. The outer absolute host can keep the same
        /// bounds while a wrapped body becomes taller, so listening only to it
        /// misses exactly the two/three-line case. Called after every chrome
        /// rebuild because UI Toolkit callbacks stay on the discarded instance.</summary>
        private void WireChoiceGeometrySync()
        {
            if (_dialogue == null) return;
            var box = _dialogue.Q("vn-box");
            (box ?? (VisualElement)_dialogue)
                .RegisterCallback<GeometryChangedEvent>(_ => SyncChoicesBelowBox());
        }
        private void OnChoicesVisibleChanged(bool visible)
        {
            NotifyUiStage();
            ChoicesVisibleChanged?.Invoke(visible);
        }
        private void OnChoiceSelected(int index)
        {
            if (_choiceCommitInFlight) return;
            LvnOption picked = default;
            bool found = false;
            if (_curChoices != null)
                foreach (var o in _curChoices)
                    if (o.Index == index) { picked = o; found = true; break; }

            // A wallet-priced option must clear the spend BEFORE it consumes the
            // choice — a refused spend leaves the menu up (nothing advanced).
            if (found && !string.IsNullOrEmpty(picked.WalletCurrency)
                && picked.WalletAmount > 0 && ChoiceSpend != null)
            {
                LvnAsync.Fire(SpendThenChooseAsync(index, picked), "SpendThenChoose");
                return;
            }
            CommitChoice(index, found ? picked.Text : null);
        }
        private async Task SpendThenChooseAsync(int index, LvnOption picked)
        {
            int epoch = _stageEpoch;
            bool paid = false;
            try { paid = await ChoiceSpend(picked.WalletCurrency, picked.WalletAmount); }
            catch { /* a wallet failure must never crash the choice UI */ }
            if (!StageCurrent(epoch) || _player == null || !_player.AtChoice) return;
            if (!paid)
            {
                // Слово авторское: фраза была вписана в движок по-русски, и
                // английская новелла показывала русский текст. {amount}/{currency}
                // подставляются — автору незачем знать порядок слов движка.
                // Сумму и имя валюты берём у ЦЕННИКА, как весь остальной
                // интерфейс: здесь подставлялся служебный id («crystals») и
                // число без разрядов, так что в сцене игрок читал «Not enough
                // crystals: need 1200», а в гардеробе — «Не хватает: 1 200
                // кристаллов». Одна нехватка, две записи.
                var not_enough = Theme.Word("choice_not_enough", "Not enough {currency}: need {amount}")
                    .Replace("{amount}", LvnPriceTag.Amount(picked.WalletAmount))
                    .Replace("{currency}", LvnPriceTag.Of(picked.WalletCurrency).Name ?? "");
                ApplyHint(new JObject { ["text"] = not_enough, ["duration"] = 3 });
                return; // menu stays up; the player picks something else
            }
            CommitChoice(index, picked.Text);
        }
        private void CommitChoice(int index, string pickedText)
        {
            StopChoiceTimer(); // the pick beat the clock
            PlayUiSound(_sndChoice != null ? _sndChoice : _sndClick);
            _choiceCommitInFlight = true;
            _choices.SetEnabled(false);

            var kind = LvnAppear.Parse(Theme?.BoxAppear);
            if (kind != LvnAppearKind.None && _dialogue != null &&
                _dialogue.style.display == DisplayStyle.Flex)
            {
                int gen = ++_dialogueSwapGeneration;
                int outMs = Mathf.RoundToInt(DialogueFadeSeconds() * 1000f);
                _dialogue.DropOut(outMs);
                LvnAppear.Play(_choices, kind, appearing: false, ms: outMs,
                    done: () => AfterBeatPause(gen,
                        () => FinishChoiceCommit(gen, index, pickedText)));
                return;
            }
            FinishChoiceCommit(_dialogueSwapGeneration, index, pickedText);
        }
        private void FinishChoiceCommit(int gen, int index, string pickedText)
        {
            if (gen != _dialogueSwapGeneration) return;
            StopWaitingForPlayer(cancelTimer: false);   // выбор сделан — таймер уже не его дело
            _choiceCommitInFlight = false;
            _sayUp = false;
            if (_dialogue != null)
            {
                _dialogue.ResetCardVisual();
                _dialogue.style.display = DisplayStyle.None;
            }
            // Ignore a click on a stale button (the beat moved on via load/hot-reload
            // and these options no longer apply) instead of throwing.
            if (_player == null || !_player.AtChoice) return;
            // History: record which branch was taken (rendered as a marked line).
            if (!string.IsNullOrEmpty(pickedText)) _backlog.Add((null, pickedText, "choice"));
            _player.Choose(index);
            _player.Advance();
            // A picked branch is exactly what a crash must not lose — autosave here.
            AutosaveNow();
            // Skip was gearing down FOR this exact choice (not a manual stop) —
            // she just picked it consciously, so resume the re-read gear right
            // away instead of forcing a re-arm at every single decision.
            if (_resumeSkipAfterChoice) { _resumeSkipAfterChoice = false; StartSkip(); }
        }
        public void ShowChoice(IReadOnlyList<LvnOption> options)
        {
            _awaitingTap = false;
            _curChoices = options;
            _dialogue?.SuppressAdvanceHint(true); // a choice is up — don't invite a tap
            _choiceCommitInFlight = false;

            var kind = LvnAppear.Parse(Theme?.BoxAppear);
            // Пара «реплика + выбор» одним тактом: ShowSay этого же кадра ещё
            // роняет прежнюю карточку, его PresentSay отложен. Перебить
            // поколение и просто поднять окно — значит показать выбор под
            // СТАРОЙ репликой (вопрос съеден — живой репорт). Доводим сами:
            // после падения ставим отложенную реплику и выбор вместе.
            if (_pendingSay is { } ps && _dialogue != null && kind != LvnAppearKind.None)
            {
                _pendingSay = null;
                int gen = ++_dialogueSwapGeneration;
                int outMs = Mathf.RoundToInt(DialogueFadeSeconds() * 1000f);
                _dialogue.DropOut(outMs, done: () =>
                {
                    if (gen != _dialogueSwapGeneration || _dialogue == null) return;
                    _dialogue.style.display = DisplayStyle.None;
                    AfterBeatPause(gen, () =>
                    {
                        PresentSay(gen, ps.who, ps.text, ps.style);
                        _curChoices = options; // PresentSay их сбрасывает — выбор этого же такта
                        PresentChoiceBeat(gen, options, kind);
                    });
                });
                return;
            }
            if (_dialogue != null && _dialogue.style.display == DisplayStyle.Flex &&
                kind != LvnAppearKind.None)
            {
                int gen = ++_dialogueSwapGeneration;
                int outMs = Mathf.RoundToInt(DialogueFadeSeconds() * 1000f);
                _dialogue.DropOut(outMs,
                    done: () => AfterBeatPause(gen,
                        () => PresentChoiceBeat(gen, options, kind)));
                return;
            }
            PresentChoiceBeat(_dialogueSwapGeneration, options, kind);
        }
        private void PresentChoiceBeat(int gen, IReadOnlyList<LvnOption> options, LvnAppearKind kind)
        {
            if (gen != _dialogueSwapGeneration || _choices == null) return;
            _choices.Present(options);
            _choices.SetEnabled(false);
            // Present() makes the list visible before UI Toolkit has completed
            // its new layout. Re-evaluate once after that pass; subsequent text
            // wrapping is covered by WireChoiceGeometrySync above.
            SyncChoicesBelowBox();
            _choices.schedule.Execute(SyncChoicesBelowBox).ExecuteLater(1);
            if (kind != LvnAppearKind.None)
            {
                int enterMs = Mathf.RoundToInt(DialogueFadeSeconds() * 1000f);
                _dialogue?.ResetCardVisual();
                _dialogue.SlideIn(enterMs);
                LvnAppear.Play(_choices, kind, appearing: true,
                    ms: enterMs,
                    done: () =>
                    {
                        EnableChoiceWhenChoreographyReady(gen);
                    });
                return;
            }
            EnableChoiceWhenChoreographyReady(gen);
        }
        private void EnableChoiceWhenChoreographyReady(int gen)
        {
            if (gen != _dialogueSwapGeneration || _choices == null
                || _curChoices == null || _curChoices.Count == 0) return;
            float left = _clock.Remaining(LvnStageClock.ActorVisibilityBarrier);
            if (left > 0.001f)
            {
                _choices.schedule.Execute(() => EnableChoiceWhenChoreographyReady(gen))
                    .ExecuteLater(Mathf.Max(1, Mathf.CeilToInt(left * 1000f)));
                return;
            }
            _choices.SetEnabled(true);
            // A timed choice starts only when it is visible and can be pressed.
            StartChoiceTimer(_player != null ? _player.CurrentChoiceTimeout : 0f);
        }
    }
}
