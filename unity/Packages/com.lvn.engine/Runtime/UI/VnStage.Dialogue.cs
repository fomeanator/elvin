using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// РЕПЛИКА — как слова доходят до игрока: показ строки (с отложенным
    /// первым словом, пока держит карточка главы), кто её говорит и с какой
    /// стороны, окно и его видимость, псевдонимы рендерера и уборка в конце
    /// главы. Выбор игрока — соседняя тема и соседний файл
    /// (<c>VnStage.Choice</c>).
    /// </summary>
    public sealed partial class VnStage
    {
        /// <summary>One fixed dialogue-card duration. Content length never enters
        /// this calculation; a dialogue fade may be quicker than actor motion but
        /// never outlive it.</summary>
        private float DialogueFadeSeconds()
        {
            float card = VnTheme.Motion(Mathf.Max(0f, Theme?.BoxAppearDuration ?? 0.22f));
            float actor = VnTheme.Motion(Mathf.Max(0f, Theme?.ActorTransition ?? 0.35f));
            return actor > 0.001f ? Mathf.Min(card, actor) : card;
        }

        // The dialogue frame is chrome for a LINE — between chapters (and while
        // the next chapter's script/art loads) there is no line, and the empty
        // skinned box floating over a bare stage read as a glitch. Hidden on
        // every stage reset, shown again by the first ShowSay.
        private void SetSayVisible(bool on, Action shown = null)
        {
            if (!on)
            {
                _dialogueSwapGeneration++; // cancel a pending card replacement
                _pendingSay = null;        // отменённая реплика не должна воскреснуть у выбора
                _choiceCommitInFlight = false;
                _dialogueSurfaceFresh = false;
            }
            if (_dialogue != null)
            {
                bool wasOn = _dialogue.style.display == DisplayStyle.Flex;
                var kind = LvnAppear.Parse(Theme?.BoxAppear);
                int ms = Mathf.RoundToInt(DialogueFadeSeconds() * 1000f);

                if (on && !wasOn && kind != LvnAppearKind.None)
                {
                    _dialogue.ResetCardVisual();
                    _dialogue.style.display = DisplayStyle.Flex;
                    _dialogue.SlideIn(ms, shown);
                }
                else if (!on && wasOn && kind != LvnAppearKind.None)
                {
                    // Окно уходит СВОИМ ходом и прячется хвостом анимации: снять
                    // его сразу значит не показать уход вовсе.
                    int hideGen = _dialogueSwapGeneration;
                    _dialogue.DropOut(ms,
                        done: () =>
                        {
                            if (hideGen == _dialogueSwapGeneration && _dialogue != null)
                                _dialogue.style.display = DisplayStyle.None;
                        });
                }
                else
                {
                    _dialogue.ResetCardVisual();
                    _dialogue.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
                    if (on) shown?.Invoke();
                }
            }
            NotifyUiStage();
        }

        /// <summary>Сообщить слою `ui`, что сейчас на экране: идёт реплика,
        /// показан выбор, и какой высоты окно. Слой сам решит, какие деревья
        /// прятать и насколько поджаться снизу — иначе автор подбирал бы
        /// отступ руками, и тот разъезжался бы на первом длинном имени.</summary>
        private void NotifyUiStage()
        {
            if (_uiLayer == null) return;
            bool say = _awaitingTap;
            bool choice = _curChoices != null && _curChoices.Count > 0;
            float h = 0f;
            // Высота берётся у ВИДИМОГО окна, даже когда чтение уже кончилось:
            // окно остаётся на экране, и нижний этаж обязан его обходить.
            bool boxUp = DialogueOnScreen;
            if (boxUp) h = _dialogue.resolvedStyle.height;
            else if (choice && _choices != null) h = _choices.resolvedStyle.height;
            _uiLayer.SetStage(say, choice, boxUp || choice ? h : 0f);
            SyncChoicesBelowBox();
        }

        /// <summary>Fires when the choice list appears/disappears — the shell's
        /// reading-mode HUD listens (visible while a priced choice is up).</summary>
        public event Action<bool> ChoicesVisibleChanged;




        /// <summary>Host hook clearing a REAL (wallet-priced) option: spend
        /// <c>amount</c> of <c>currency</c>, true on success. Null → priced
        /// options pick through for free (engine-only setups stay playable).</summary>
        public Func<string, long, Task<bool>> ChoiceSpend;




        /// <summary>A tiny neutral beat between one card going dark and the next
        /// appearing. It is short enough not to feel like latency, but gives the
        /// dissolve a punctuation mark instead of making two panels overlap.</summary>
        private void AfterBeatPause(int generation, Action next)
        {
            if (generation != _dialogueSwapGeneration || next == null) return;
            int ms = VnTheme.MotionMs(Mathf.Max(0f, Theme?.BeatPause ?? 0.06f));
            if (ms <= 0 || _dialogue == null) { next(); return; }
            _dialogue.schedule.Execute(() =>
            {
                if (generation == _dialogueSwapGeneration) next();
            }).ExecuteLater(ms);
        }

        /// <summary>
        /// СЦЕНА БОЛЬШЕ НИЧЕГО НЕ ЖДЁТ ОТ ИГРОКА — снять всё, чем она его
        /// спрашивала.
        ///
        /// <para>Ожидание держится в трёх местах разом: поле «ждём тап», список
        /// показанных вариантов и сам виджет выбора на экране. Гасили их пятью
        /// местами — перетаскивание, клик по горячей зоне (дважды), команда
        /// перехода, фиксация выбора — и каждое своим набором: где-то с отменой
        /// таймера, где-то без; где-то <c>_choices.Dismiss()</c>, где-то
        /// <c>?.</c>; порядок всякий раз новый.</para>
        ///
        /// <para>Забыть одну строку из трёх ничего не ломает СРАЗУ: экран
        /// чистый, а поле помнит вчерашние варианты — и следующий тап уходит в
        /// выбор, которого игрок не видит. Такую ошибку находят не в коде, а по
        /// жалобе «нажал и провалился не туда».</para>
        ///
        /// <para><paramref name="cancelTimer"/> — единственная законная разница:
        /// таймер отменяет тот, кто ОПЕРЕДИЛ его (клик по зоне, пока идёт
        /// обратный отсчёт), а фиксация уже сделанного выбора его не трогает.</para>
        /// </summary>
        private void StopWaitingForPlayer(bool cancelTimer = true)
        {
            if (cancelTimer) CancelPendingWait();
            _awaitingTap = false;
            _curChoices = null;
            _choices?.Dismiss();
        }


        // ── ILvnStage ─────────────────────────────────────────────────────────

        /// <summary>Entry choreography gate, set by the host per chapter entry:
        /// the loader reveal + chapter-title card play OVER the dressed stage,
        /// and the FIRST line must not start typing under them. ShowSay defers
        /// its first reveal until this completes; taps and auto-advance hold
        /// too. Null = no hold (resume, cross-chapter load).</summary>
        public Task EntryGate;
        private bool _entryGateArmed; // only the first say of a run defers

        private bool EntryGatePending => EntryGate != null && !EntryGate.IsCompleted;

        private async Task DeferredFirstSayAsync(Task gate, string who, string text, string style)
        {
            int epoch = _stageEpoch;
            try { await gate; } catch { /* choreography failures never eat the line */ }
            if (!StageCurrent(epoch)) return; // chapter changed while the title played
            ShowSay(who, text, style);        // _entryGateArmed already consumed
        }

        public void ShowSay(string who, string text, string style)
        {
            if (_entryGateArmed)
            {
                _entryGateArmed = false;
                var gate = EntryGate;
                if (gate != null && !gate.IsCompleted)
                {
                    LvnAsync.Fire(DeferredFirstSayAsync(gate, who, text, style), "DeferredFirstSay");
                    return; // the dressed stage waits under the title card
                }
            }
            // Each line is a fresh readable card. The old complete line releases
            // and falls first; only then do we install the new text and slide its
            // replacement into place. This is independent of speaker identity.
            bool replacing = _sayUp && _dialogue != null &&
                _dialogue.style.display == DisplayStyle.Flex && !_dialogueSurfaceFresh;
            var kind = LvnAppear.Parse(Theme?.BoxAppear);
            if (replacing && kind != LvnAppearKind.None)
            {
                int gen = ++_dialogueSwapGeneration;
                _awaitingTap = false; // a tap during the hand-off cannot skip the new line
                _audio?.StopVoice();
                // Пара «реплика + выбор» приходит одним тактом: ShowChoice будет
                // вызван в этом же кадре и перебьёт поколение — отложенный
                // PresentSay погибнет, а окно вернётся со СТАРЫМ текстом (живой
                // репорт: варианты повисли под предыдущей репликой). Реплика
                // хранится здесь, и ShowChoice доводит её показ сам.
                _pendingSay = (who, text, style);
                int outMs = Mathf.RoundToInt(DialogueFadeSeconds() * 1000f);
                _dialogue.DropOut(outMs, done: () =>
                {
                    if (!BoxMine(gen)) return;
                    _dialogue.style.display = DisplayStyle.None;
                    AfterBeatPause(gen, () => PresentSay(gen, who, text, style));
                });
                return;
            }

            int directGen = ++_dialogueSwapGeneration;
            PresentSay(directGen, who, text, style);
        }

        // Реплика, чей показ отложен анимацией падения прежней карточки —
        // ShowChoice того же такта обязан показать её вместе с выбором.
        private (string who, string text, string style)? _pendingSay;

        private void PresentSay(int gen, string who, string text, string style)
        {
            if (!BoxMine(gen)) return;
            _pendingSay = null; // доехала штатно
            _dialogueSurfaceFresh = false;
            _awaitingTap = false;
            _dialogue.SetSpeaker(who, DialogueSideForCurrentSpeaker(who));
            _dialogue.ApplyStyle(style);
            _dialogue.SuppressAdvanceHint(false); // a plain line invites the tap again
            _dialogue.Reveal(text);
            _sayUp = true;
            _sayUpSince = LvnClock.Now(); // для самоисцеления тапов
            _curChoices = null;
            SetSayVisible(true, () => UnlockSayWhenChoreographyReady(gen));
            // Voice-over: the line's clip starts with its text; the previous line's
            // voice stops (never overlaps). Silent lines just stop the old one.
            if (_audio != null)
                LvnAsync.Fire(_audio.PlayVoiceAsync(_player?.CurrentVoiceUrl, Assets, _cts != null ? _cts.Token : default), "PlayVoice");
            _lastSayLength = text?.Length ?? 0; // drives the auto-advance reading delay
            _autoRevealDoneAt = -1f;
            PrefetchAhead(); // warm the next beats' art/audio while the player reads

            // Speaker changes must not recolour the cast. Clear any legacy focus
            // tint that may survive a hot rebuild; solo visibility remains a
            // separate authored staging mode below.
            SceneHighlightSpeaker(null);
            ApplySpeakerSolo(_player?.CurrentSpeakerId ?? ResolveSpeakerId(who));

            // Lip-sync: only the speaking actor's mouth moves while the line is up.
            var spId = _player?.CurrentSpeakerId ?? ResolveSpeakerId(who);
            foreach (var kv in _talkAnims) SceneTalk(kv.Key, kv.Value, kv.Key == spId);
        }

        /// <summary>Open tap input only when every visual owner of this beat has
        /// released it. The check repeats because an async actor load can start
        /// its real entrance after the dialogue card has already arrived.</summary>
        private void UnlockSayWhenChoreographyReady(int gen)
        {
            if (!BoxMine(gen) || !_sayUp) return;
            if (_curChoices != null && _curChoices.Count > 0) return;
            float left = _clock.Remaining(LvnStageClock.ActorVisibilityBarrier);
            if (left > 0.001f)
            {
                _dialogue.schedule.Execute(() => UnlockSayWhenChoreographyReady(gen))
                    .ExecuteLater(Mathf.Max(1, Mathf.CeilToInt(left * 1000f)));
                return;
            }
            _awaitingTap = true;
        }

        // Scene calls go through the ISceneRenderer seam — способ рисовать живёт
        // внутри CanvasSceneRenderer, а не в условиях на каждом вызове. Эти
        // тонкие переходники сохраняют привычные имена вызовов.
        private void SceneSetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames) => _renderer?.SetFrames(id, frames);
        private void SceneEnsureIdle(string id, LvnAnim a) => _renderer?.EnsureIdle(id, a);
        private void SceneEnsureBlink(string id, LvnAnim a) => _renderer?.EnsureBlink(id, a);
        private void ScenePlayGesture(string id, LvnAnim g, LvnAnim idle) => _renderer?.PlayGesture(id, g, idle);
        private void ScenePlayAnim(string id, string channel, LvnAnim a) => _renderer?.PlayAnim(id, channel, a);
        private void ScenePlayAnimQueued(string id, string channel, LvnAnim a) => _renderer?.PlayAnimQueued(id, channel, a);
        private void SceneStopAnim(string id, string target) => _renderer?.StopAnim(id, target);
        private void SceneTalk(string id, LvnAnim t, bool on) => _renderer?.Talk(id, t, on);
        private void SceneHighlightSpeaker(string who) => _renderer?.HighlightSpeaker(who);

        // ── Solo focus (novel mode) ─────────────────────────────────────────
        // «Виден только говорящий»: классическая новелла. Трогаем ТОЛЬКО
        // персонажей, которые хоть раз говорили (_spokenIds) — постановочные
        // объекты (враг боя, руки, иконки) реплик не имеют и не задеваются.
        // Реплики подряд одного персонажа не мигают: он уже виден, остальные
        // уже спрятаны. Наррация (без who) прячет всех говоривших. Скрытие и
        // показ идут ШТАТНЫМ путём актёра с fade-переходом — сейвы/реплей
        // восстанавливают состояние сами.
        private readonly HashSet<string> _spokenIds = new HashSet<string>();
        private readonly HashSet<string> _soloHidden = new HashSet<string>();

        private void ApplySpeakerSolo(string speakerId)
        {
            // Через дом закрытого слова: опечатка в `ui.stage.speaker_focus`
            // молча означала «dim», и режим новеллы просто не включался.
            if (Theme == null) return;
            if (LvnAuthorWord.Pick(Theme.SpeakerFocus, "ui.stage.speaker_focus", "dim",
                                   "dim", "solo") != "solo") return;

            if (!string.IsNullOrEmpty(speakerId))
            {
                _spokenIds.Add(speakerId);
                // Говорящий возвращается, если соло его прятало — или впервые
                // выходит на сцену сам (каталожный арт), без ручного actor-опа.
                if (_soloHidden.Remove(speakerId) ||
                    (Catalog != null && Catalog.Has(speakerId) && !_memory.HasWhere(speakerId)))
                {
                    LvnAsync.Fire(ApplyActorAsync(new JObject
                    {
                        ["op"] = "actor", ["id"] = speakerId,
                        ["show"] = true, ["enter"] = "fade", ["transition_duration"] = 0.3f,
                    }), "ApplyActor");
                }
            }

            foreach (var id in _spokenIds)
            {
                if (id == speakerId || _soloHidden.Contains(id)) continue;
                if (!_memory.TryWhere(id, out var pl) || !pl.Show) continue;
                _soloHidden.Add(id);
                LvnAsync.Fire(ApplyActorAsync(new JObject
                {
                    ["op"] = "actor", ["id"] = id,
                    ["show"] = false, ["exit"] = "fade", ["transition_duration"] = 0.3f,
                }), "ApplyActor");
            }
        }

        // Speaker label → on-stage actor id (mirrors the authoring speakerEntity
        // rule: actor_map alias, else the lowercased name).
        private string ResolveSpeakerId(string who)
        {
            if (string.IsNullOrEmpty(who)) return null;
            var sb = new StringBuilder(who.Length);
            foreach (var c in who.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        /// <summary>Map the current semantic speaker to the side of the stage
        /// they actually occupy. Pending placement wins so an async sprite load
        /// cannot leave the nameplate on the previous/default side.</summary>
        private DialogueSpeakerSide DialogueSideForCurrentSpeaker(string who)
        {
            if (string.IsNullOrEmpty(who)) return DialogueSpeakerSide.Unanchored;
            string id = _player?.CurrentSpeakerId;
            if (string.IsNullOrEmpty(id))
            {
                // Direct-authored scripts may omit actor_map/who_id. Mirror the
                // renderer's loose key match so `actor Bob` + `Bob: ...` still
                // receives a spatial nameplate.
                string key = Lvn.LvnKey.Normalize(who);
                foreach (var kv in _memory.Targets())
                    if (Lvn.LvnKey.Normalize(kv.Key) == key) { id = kv.Key; break; }
                if (string.IsNullOrEmpty(id))
                    foreach (var kv in _memory.Wheres())
                        if (Lvn.LvnKey.Normalize(kv.Key) == key) { id = kv.Key; break; }
            }
            if (string.IsNullOrEmpty(id)) return DialogueSpeakerSide.Unanchored;

            Placement p;
            bool found = _memory.TryTarget(id, out p)
                || _memory.TryWhere(id, out p);
            if (!found || !p.Show) return DialogueSpeakerSide.Unanchored;
            if (p.X < 0.45f) return DialogueSpeakerSide.Left;
            if (p.X > 0.55f) return DialogueSpeakerSide.Right;
            return DialogueSpeakerSide.Center;
        }




        public void OnEnd()
        {
            // The chapter is finished — its mid-chapter autosave must not hijack the
            // next entry back to a stale position.
            LvnSaveStore.Delete(_saveTitleId, LvnSaveStore.AutoSlot);
            // Garbage-collect the scene when the chapter ends: without this the last
            // actors keep their (looping) animations running and bleed into the menu
            // or the next chapter. ResetStage stops coroutines, removes actors,
            // clears the background and FX.
            ResetStage();
            _dialogue.SetSpeaker(null);
            _dialogue.SetText(string.Empty);
        }
    }
}
