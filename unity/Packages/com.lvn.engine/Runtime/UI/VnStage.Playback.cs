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
    /// Chapter playback lifecycle: starting a script (Play / hot-swap), the
    /// staged opening warmups, the stage reset between scenes, the dialogue
    /// backlog, rollback, and the skip / auto-advance gears.
    /// </summary>
    public sealed partial class VnStage
    {
        // ── skip (fast-forward) ──────────────────────────────────────────────
        // The genre's re-read gear: lines fly by until something needs the
        // player — a choice, a tap, the chapter's end, an opened menu.

        /// <summary>True while fast-forward is running.</summary>
        public bool Skipping { get; private set; }

        /// <summary>Fast-forward lines until a choice, a tap, or the chapter ends.</summary>
        public void StartSkip()
        {
            if (_player == null || _player.Finished) return;
            Skipping = true;
        }

        public void StopSkip() => Skipping = false;

        private void SkipTick()
        {
            if (!Skipping) return;
            if (_player == null || _player.Finished || _player.AtChoice)
            {
                Skipping = false; // something needs the player — gear down
                return;
            }
            if (InputBlocked || _chromeHidden || _awaitingWait || _awaitingInput) return; // paused, not cancelled
            if (_dialogue != null && _dialogue.IsRevealing) { _dialogue.Complete(); return; }
            if (_awaitingTap)
            {
                _awaitingTap = false;
                _player.Advance();
            }
        }

        // ── auto-advance ─────────────────────────────────────────────────────
        // Reading delay after the reveal completes, scaled by line length and the
        // player's preference — the standard hands-free mode.
        private float _autoRevealDoneAt = -1f;
        private int _lastSayLength;

        /// <summary>Extra gate a host/menu can close to hold auto-advance (and
        /// tap handling) while an overlay is up. An open shared panel
        /// (<see cref="VnPanelHost"/>) blocks implicitly, so a wardrobe sheet or
        /// in-script screen can't be tapped/auto-advanced through.</summary>
        public bool InputBlocked
        {
            get => _inputBlockedFlag || (_panelHost != null && _panelHost.IsOpen);
            set => _inputBlockedFlag = value;
        }
        private bool _inputBlockedFlag;

        /// <summary>Set when the player asks to leave the chapter (the quick
        /// menu's Exit). The host's play loop watches it and returns to the
        /// title screen; position and stats are already autosaved, so Continue
        /// brings the player back to this exact line.</summary>
        public bool ExitRequested { get; private set; }

        /// <summary>Player-initiated exit to the menu: autosave the position,
        /// then signal the host loop.</summary>
        public void RequestExit()
        {
            AutosaveNow();
            ExitRequested = true;
        }

        /// <summary>Host acknowledgment — called by the play loop once it has
        /// acted on the request (and by Play for a fresh chapter).</summary>
        public void ClearExitRequest() => ExitRequested = false;

        private void AutoAdvanceTick()
        {
            if (!LvnPrefs.AutoAdvance || InputBlocked || _chromeHidden
                || _player == null || _player.Finished || _player.AtChoice
                || !_awaitingTap || _awaitingWait || _awaitingInput
                || EntryGatePending
                || _dialogue == null || _dialogue.IsRevealing)
            {
                _autoRevealDoneAt = -1f;
                return;
            }
            // First tick after the reveal finished: start the reading timer.
            if (_autoRevealDoneAt < 0f)
            {
                _autoRevealDoneAt = Time.realtimeSinceStartup;
                return;
            }
            float delay = (0.55f + 0.035f * _lastSayLength) * LvnPrefs.AutoDelayScale;
            if (Time.realtimeSinceStartup - _autoRevealDoneAt < delay) return;
            _autoRevealDoneAt = -1f;
            _awaitingTap = false;
            _player.Advance();
        }

        /// <summary>
        /// Live-edit hot-swap: replace the running chapter's script WITHOUT
        /// restarting it, when the edit didn't change the command structure. The
        /// player keeps its position, variables and call stack, the stage keeps its
        /// current background/actors, and the beat on screen is re-rendered so an
        /// edit to the visible line shows at once. Returns false when nothing is
        /// playing or the structure changed — the caller should then <see
        /// cref="Play"/> from the top.
        /// </summary>
        public bool TryHotSwap(string lvnJson)
        {
            if (_player == null || _player.Finished) return false;
            LvnDocument doc;
            try { doc = LvnDocument.Parse(lvnJson); }
            catch { return false; }
            if (!_player.TryReplaceScript(doc)) return false;
            _cast = SpriteComposer.ParseCast(doc.Cast); // cast metadata is safe to refresh in place
            _player.RerenderCurrent();
            return true;
        }

        /// <summary>Wipe the stage to a clean slate NOW — actors, background, FX,
        /// dialogue. The host calls this when a chapter starts (before the script
        /// finishes downloading) so the previous chapter never lingers during the
        /// load, not only when the previous one ended.</summary>
        public void ClearStage()
        {
            if (!_built) return;
            ResetStage();
            _sayUp = false;
            _curChoices = null;
            _dialogue?.SetSpeaker(null);
            _dialogue?.SetText(string.Empty);
        }

        /// <summary>Persistent variables to preload into the next chapter BEFORE it
        /// runs (set by the host from its state store). With the imported global
        /// defaults marked `default:true`, these carried-in values survive the
        /// chapter's init block — so relationship/route/memory stats flow from one
        /// chapter to the next and across sessions.</summary>
        public Newtonsoft.Json.Linq.JObject SeedVars;

        /// <summary>Parse and start playing a .lvn document.
        /// <paramref name="warmIntroSpine"/>: pass false when a snapshot restore
        /// follows immediately (resume/load) — the intro warmup would otherwise
        /// build the CHAPTER-OPENING spine that the restore is about to discard,
        /// doubling the entry wait with a scene the player isn't even on.</summary>
        public void Play(string lvnJson, bool warmIntroSpine = true)
        {
            var doc = LvnDocument.Parse(lvnJson);
            LvnPlayer.Log?.Invoke("════ PLAY scene=" + doc.Scene + " (" + (doc.Script?.Count ?? 0) + " cmds) ════");
            ExitRequested = false; // a fresh chapter is a fresh run
            _entryGateArmed = true; // the first say defers to the entry choreography
            _cast = SpriteComposer.ParseCast(doc.Cast);
            PrewarmGlyphs(doc); // rasterize the chapter's glyphs NOW, not mid-typewriter
            ResetStage();
            _player = new LvnPlayer(doc, this);
            _player.Strings = Strings; // localization catalog (text_id → string), if any
            if (SeedVars != null)      // carry stats in before the init defaults run
                foreach (var p in SeedVars.Properties()) _player.Vars[p.Name] = p.Value;
            _player.OnSay += RecordSay;
            ++_startGen;
            // warmIntroSpine=false ⇒ a RestoreSnapshot follows immediately and
            // advances via ContinueFrom. Running the intro here anyway (the old
            // behaviour) kicked the chapter-opening spine's build just to have
            // the restore reset it — the player watched the WRONG scene load
            // before their saved one.
            if (warmIntroSpine) StartWithSpineWarmup(_player, _startGen);
        }

        // The dialogue font is a DYNAMIC SDF asset — a glyph seen for the first
        // time rasterizes into the atlas on the render thread, a visible hitch in
        // the middle of a typewriter reveal. The chapter's full text corpus is
        // known here (script + localization catalog), so bake every distinct
        // character into the atlas up-front, behind the loading screen.
        private string _prewarmCorpus = ""; // kept so a late-arriving theme font warms too

        private void PrewarmGlyphs(LvnDocument doc)
        {
            var sb = new System.Text.StringBuilder(8192);
            if (doc?.Script != null)
                foreach (var c in doc.Script)
                {
                    if (!(c is JObject o)) continue;
                    sb.Append((string)o["text"]).Append((string)o["who"]);
                    if (o["options"] is JArray opts)
                        foreach (var opt in opts)
                            sb.Append((string)opt["text"]).Append((string)opt["cost"]);
                }
            if (Strings != null)
                foreach (var v in Strings.Values) sb.Append(v);
            _prewarmCorpus = sb.ToString();
            if (Theme != null && Theme.Font != null) // else: warms when the font arrives
                LvnFonts.Prewarm(Theme.Font, _prewarmCorpus);
        }

        // Bumped by every fresh start AND every snapshot restore, so a pending
        // intro warmup can tell its run was superseded. Pinning the player
        // reference alone is not enough: a resume REUSES the player Play just
        // created, and the stale warmup's Advance() would push the restored
        // chapter one beat past its saved position.
        private int _startGen;

        // The staged opening: everything the first beats show is built hidden
        // BEFORE the intro advances — the first Spine scene (skeleton build) AND
        // the plain art (background + character layers, decoded into the sprite
        // cache) warm in parallel behind the entry fade. Otherwise the
        // typewriter starts and then freezes mid-sentence while art decodes.
        // Capped: a dead network can't hold the intro hostage — whatever missed
        // the window loads on-demand exactly as before.
        private async void StartWithSpineWarmup(LvnPlayer player, int gen)
        {
            // Plain art warms in the BACKGROUND — it races the reader, never the
            // intro (holding the first beat hostage to 12 decodes read as a
            // multi-second black screen). Only an imminent Spine scene gates the
            // start: its skeleton build is the one cost that visibly freezes the
            // typewriter mid-line if it lands cold.
            _ = WarmUpcomingArtAsync(12);
            try { await WarmUpcomingSpineAsync(12); }
            catch (System.OperationCanceledException) { return; }
            catch { /* warmup is best-effort; the show path reloads what it needs */ }
            if (_player == player && _startGen == gen) player.Advance();
        }

        /// <summary>
        /// Wipe the stage to a clean slate before a chapter plays. Without this,
        /// actors, the background and effect veils left on screen by the previous
        /// chapter (or a live hot-reload) bleed into the new one — e.g. a character
        /// standing on the very first beat, before any <c>actor</c> command runs.
        /// </summary>
        // Bumped on every stage reset (chapter change / load). An async content
        // apply (bg, actor, spine, audio) captures it before its first await and
        // bails if it changed — otherwise a slow load from the PREVIOUS chapter
        // resolves after the reset and paints the new one (ghost actor, wrong bg,
        // wrong music). The shared _cts only cancels on OnDisable, not here.
        private int _stageEpoch;

        /// <summary>True if <paramref name="epoch"/> is still the current stage
        /// generation — a content apply calls this after each await and stops
        /// touching the stage once it's stale.</summary>
        private bool StageCurrent(int epoch) => _stageEpoch == epoch;

        private void ResetStage()
        {
            _stageEpoch++; // supersede any in-flight content apply from the old scene
            // Close the quick menu FIRST: it may be mid-open (IsOpen + InputBlocked
            // set, its clean-frame screenshot coroutine pending). The StopAllCoroutines
            // below would kill that coroutine before its OpenSheetChrome callback,
            // stranding InputBlocked=true forever — a soft-lock. Close() resets both.
            _menu?.Close();
            // Kill any in-flight `wait` coroutine — it reads the _player field, so
            // after Play() swaps in a new player it would otherwise fire Advance()
            // on the fresh chapter when its old timer elapses.
            StopAllCoroutines();
            _hotspots.Clear();
            HasBackdrop = false;
            // A resume veil (1/255 alpha) left by an aborted restore must not
            // black out the NEXT chapter — reset it at every scene boundary.
            if (_renderer is CanvasSceneRenderer resetCanvas && resetCanvas.Root != null)
            {
                var g = resetCanvas.Root.GetComponent<CanvasGroup>();
                if (g != null) g.alpha = 1f;
            }
            // A story panel (wardrobe sheet…) left open across a chapter change
            // would float over the new scene — dismiss it with the old one.
            if (_panelHost != null) _ = _panelHost.HideAsync();
            _renderer?.RemoveAll();
            _renderer?.ResetCamera(0f);
            _talkAnims.Clear();
            _renderer?.ClearBackground();
            _particles?.Set("rain", false);
            _particles?.Set("snow", false);
            _fx?.Clear(0f);
            _fx?.ClearBlur(0f);
            _backlog.Clear();
            _prefetched.Clear(); // the next chapter/load re-warms from scratch
            SetChromeHidden(false); // never carry a hidden UI across a reset
            StopSkip();             // fast-forward dies with the scene it was skipping
            _awaitingTap = false;
            _awaitingWait = false;
            _sayUp = false;
            SetSayVisible(false);
            _curChoices = null;
            StopChoiceTimer();
            CloseInput();
            _awaitingInput = false;
            _audio?.StopVoice();
            _draggables.Clear();
            _placements.Clear();
            _actorCmds.Clear();
            _dragId = null;
            _dragCandidate = null;
            foreach (var kv in _spineActors) if (kv.Value != null) Destroy(kv.Value);
            UnpinAllSpinePages(); // release page-texture pins so the LRU can reclaim them
            _spineActors.Clear();
            _spineLoading.Clear();
            _spinePendingPlay.Clear();
            _choices?.Dismiss(); // clear any on-screen choice buttons (avoid stale clicks)
            _labelLayer?.Clear();
            _labelEls.Clear();
            _labelTmpl.Clear();
            _hintHide?.Pause(); _hintHide = null;
            _hintCard = null; _hintLabel = null; // detached by the Clear above
        }

        private void RecordSay(string who, string text, string style)
        {
            // After a rollback, the restored beat re-runs and would duplicate its
            // own backlog entry — swallow exactly that one repeat.
            if (_suppressDupSay)
            {
                _suppressDupSay = false;
                if (_backlog.Count > 0)
                {
                    var last = _backlog[_backlog.Count - 1];
                    if (last.who == who && last.text == text) return;
                }
            }
            _backlog.Add((who, text, style));
            // Read tracking: remember the line, and if fast-forward is in
            // read-only gear, stop it the moment something NEW comes up — the
            // line stays on screen with its typewriter, exactly where the
            // player's actual reading resumes.
            bool wasNew = LvnReadStore.MarkRead(_saveTitleId, who, text);
            if (wasNew && Skipping && LvnPrefs.SkipReadOnly) StopSkip();
            // Rolling autosave so a crash mid-scene loses a few lines at most.
            if (++_saySinceAutosave >= 5)
            {
                _saySinceAutosave = 0;
                AutosaveNow();
            }
        }

        private bool _suppressDupSay;

        /// <summary>True when there is a previous beat to roll back to.</summary>
        public bool CanRollback => _player != null && _player.CanRollback && !_awaitingWait;

        /// <summary>Step one beat back (a mis-tap safety net): restore the previous
        /// say/choice's snapshot — variables as they were BEFORE it ran, so a picked
        /// option's set/inc is undone — rebuild the scene there and re-show it.
        /// Returns false when already at the first beat.</summary>
        public bool RollbackStep() => RollbackSteps(1);

        /// <summary>Roll back several beats in one hop (clamped to the recorded
        /// history) — the History panel's tap-to-return. The same recipe as a
        /// single step, but one scene rebuild instead of N.</summary>
        public bool RollbackSteps(int steps)
        {
            if (_player == null || _awaitingWait || steps < 1) return false;
            int actual = Mathf.Min(steps, _player.HistoryDepth - 1);
            var snap = _player.PopRollback(actual);
            if (snap == null) return false;

            // ResetStage wipes the dialogue history; a rewind must keep it minus
            // the beats being undone (their re-runs are dedup'd in RecordSay).
            var kept = new List<(string who, string text, string style)>(_backlog);
            for (int i = 0; i < actual; i++)
            {
                if (kept.Count > 0) kept.RemoveAt(kept.Count - 1);
                // A trailing choice mark belongs to the pick being undone: the
                // options re-present and the (re-)pick records a fresh mark.
                while (kept.Count > 0 && kept[kept.Count - 1].style == "choice")
                    kept.RemoveAt(kept.Count - 1);
            }

            ResetStage();
            _backlog.AddRange(kept);
            _suppressDupSay = true;

            _player.Restore(snap);
            int at = _player.Index;
            _player.ReplayVisuals(at);
            _player.ContinueFrom(at);
            return true;
        }
    }
}
