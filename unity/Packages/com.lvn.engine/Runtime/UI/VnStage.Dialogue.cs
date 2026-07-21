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
    /// The ILvnStage presentation surface: say lines (with the entry-gate
    /// deferral), choice presentation and commit (including wallet-priced
    /// options), the renderer aliases and the chapter-end cleanup.
    /// </summary>
    public sealed partial class VnStage
    {
        // The dialogue frame is chrome for a LINE — between chapters (and while
        // the next chapter's script/art loads) there is no line, and the empty
        // skinned box floating over a bare stage read as a glitch. Hidden on
        // every stage reset, shown again by the first ShowSay.
        private void SetSayVisible(bool on)
        {
            if (_dialogue != null)
                _dialogue.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Fires when the choice list appears/disappears — the shell's
        /// reading-mode HUD listens (visible while a priced choice is up).</summary>
        public event Action<bool> ChoicesVisibleChanged;

        private void OnChoicesVisibleChanged(bool visible) => ChoicesVisibleChanged?.Invoke(visible);

        /// <summary>Host hook clearing a REAL (wallet-priced) option: spend
        /// <c>amount</c> of <c>currency</c>, true on success. Null → priced
        /// options pick through for free (engine-only setups stay playable).</summary>
        public Func<string, long, Task<bool>> ChoiceSpend;

        private void OnChoiceSelected(int index)
        {
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
                _ = SpendThenChooseAsync(index, picked);
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
                ApplyHint(new JObject
                {
                    ["text"] = $"Не хватает {picked.WalletAmount} {picked.WalletCurrency}",
                    ["duration"] = 3
                });
                return; // menu stays up; the player picks something else
            }
            CommitChoice(index, picked.Text);
        }

        private void CommitChoice(int index, string pickedText)
        {
            StopChoiceTimer(); // the pick beat the clock
            PlayUiSound(_sndChoice != null ? _sndChoice : _sndClick);
            _choices.Dismiss();
            _curChoices = null;
            _awaitingTap = false;
            // Ignore a click on a stale button (the beat moved on via load/hot-reload
            // and these options no longer apply) instead of throwing.
            if (_player == null || !_player.AtChoice) return;
            // History: record which branch was taken (rendered as a marked line).
            if (!string.IsNullOrEmpty(pickedText)) _backlog.Add((null, pickedText, "choice"));
            _player.Choose(index);
            _player.Advance();
            // A picked branch is exactly what a crash must not lose — autosave here.
            AutosaveNow();
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
                    _ = DeferredFirstSayAsync(gate, who, text, style);
                    return; // the dressed stage waits under the title card
                }
            }
            SetSayVisible(true);
            _dialogue.SetSpeaker(who);
            _dialogue.ApplyStyle(style);
            _dialogue.SuppressAdvanceHint(false); // a plain line invites the tap again
            _dialogue.Reveal(text);
            // Voice-over: the line's clip starts with its text; the previous line's
            // voice stops (never overlaps). Silent lines just stop the old one.
            if (_audio != null)
                _ = _audio.PlayVoiceAsync(_player?.CurrentVoiceUrl, Assets, _cts != null ? _cts.Token : default);
            _lastSayLength = text?.Length ?? 0; // drives the auto-advance reading delay
            _autoRevealDoneAt = -1f;
            _awaitingTap = true;
            _sayUp = true;
            _curChoices = null;
            PrefetchAhead(); // warm the next beats' art/audio while the player reads

            // Classic VN focus: the speaker is at full brightness, everyone else
            // present dims — so a two-shot reads as "this one is talking" instead of
            // a flat row. actor_map'd speakers carry their true actor id (who_id);
            // without it the loose name↔slot key match applies.
            SceneHighlightSpeaker(_player?.CurrentSpeakerId ?? who);

            // Lip-sync: only the speaking actor's mouth moves while the line is up.
            var spId = _player?.CurrentSpeakerId ?? ResolveSpeakerId(who);
            foreach (var kv in _talkAnims) SceneTalk(kv.Key, kv.Value, kv.Key == spId);
        }

        // Scene calls go through the ISceneRenderer seam — path-specific behaviour
        // lives inside UitkSceneRenderer / CanvasSceneRenderer, not in per-call-site
        // conditionals here. These thin aliases keep historical call names readable.
        private void SceneSetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames) => _renderer?.SetFrames(id, frames);
        private void SceneEnsureIdle(string id, LvnAnim a) => _renderer?.EnsureIdle(id, a);
        private void SceneEnsureBlink(string id, LvnAnim a) => _renderer?.EnsureBlink(id, a);
        private void ScenePlayGesture(string id, LvnAnim g, LvnAnim idle) => _renderer?.PlayGesture(id, g, idle);
        private void ScenePlayAnim(string id, string channel, LvnAnim a) => _renderer?.PlayAnim(id, channel, a);
        private void ScenePlayAnimQueued(string id, string channel, LvnAnim a) => _renderer?.PlayAnimQueued(id, channel, a);
        private void SceneStopAnim(string id, string target) => _renderer?.StopAnim(id, target);
        private void SceneTalk(string id, LvnAnim t, bool on) => _renderer?.Talk(id, t, on);
        private void SceneHighlightSpeaker(string who) => _renderer?.HighlightSpeaker(who);

        // Speaker label → on-stage actor id (mirrors the authoring speakerEntity
        // rule: actor_map alias, else the lowercased name).
        private string ResolveSpeakerId(string who)
        {
            if (string.IsNullOrEmpty(who)) return null;
            var sb = new StringBuilder(who.Length);
            foreach (var c in who.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        public void ShowChoice(IReadOnlyList<LvnOption> options)
        {
            _awaitingTap = false;
            _curChoices = options;
            _dialogue?.SuppressAdvanceHint(true); // a choice is up — don't invite a tap
            _choices.Present(options);
            // A timed choice races the player: countdown bar over the options,
            // expiry takes the timeout branch (VnStage.Input.cs).
            StartChoiceTimer(_player != null ? _player.CurrentChoiceTimeout : 0f);
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
