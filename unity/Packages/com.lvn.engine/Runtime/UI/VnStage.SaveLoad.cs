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
    /// Save / resume: the in-script save/load ops, snapshot restore with the
    /// resume veil, persistent per-title slots and the rolling autosave.
    /// </summary>
    public sealed partial class VnStage
    {
        /// <summary>Raised after any successful save (autosave, quick, slots) —
        /// the host's hook for cloud sync / analytics. Argument: the slot name.</summary>
        public event Action<string> Saved;

        /// <summary>Raised after every resume with how faithfully the cursor could
        /// be placed. The one a host MUST handle is
        /// <see cref="LvnPlayer.RestoreFidelity.ChapterChanged"/>: the chapter was
        /// rewritten under the save, the position is gone and the chapter restarted
        /// from the top (variables kept). Say so — "this chapter has been updated,
        /// starting it again" — instead of letting the player wonder why "Continue"
        /// opened a scene they had never reached.</summary>
        public event Action<LvnPlayer.RestoreFidelity> Resumed;

        // ── save / load ──────────────────────────────────────────────────────
        // `save [slot=name]` writes the player snapshot (cursor + vars + call stack)
        // to PlayerPrefs; `load [slot=name]` restores it, rebuilds the scene from the
        // saved point (ReplayVisuals) and resumes. Default slot is "quick".
        private string SaveKey(JObject cmd)
        {
            var slot = (string)cmd["slot"];
            // Namespaced by title id — two novels in one app (or the IDE preview
            // next to a game) must not read each other's quick saves.
            var ns = string.IsNullOrEmpty(_saveTitleId) ? "" : _saveTitleId + "_";
            return "lvn_save_" + ns + (string.IsNullOrEmpty(slot) ? "quick" : slot);
        }

        private void SaveSlot(JObject cmd)
        {
            if (_player == null) return;
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(_player.Save());
                LvnKeep.Put(SaveKey(cmd), json);
                LvnPlayer.Log?.Invoke("saved → " + SaveKey(cmd));
            }
            catch (System.Exception e) { Debug.LogWarning("[lvn] save failed: " + e.Message); }
        }

        private void LoadSlot(JObject cmd)
        {
            if (_player == null) return;
            var json = LvnKeep.Get(SaveKey(cmd), "");
            if (string.IsNullOrEmpty(json))
            {
                // Legacy fallback: saves written before keys were title-namespaced.
                var slot = (string)cmd["slot"];
                json = LvnKeep.Get("lvn_save_" + (string.IsNullOrEmpty(slot) ? "quick" : slot), "");
            }
            LvnPlayer.LvnSnapshot snap = null;
            if (!string.IsNullOrEmpty(json))
                try { snap = Newtonsoft.Json.JsonConvert.DeserializeObject<LvnPlayer.LvnSnapshot>(json); }
                catch (System.Exception e) { Debug.LogWarning("[lvn] load parse failed: " + e.Message); }

            if (snap == null || snap.Vars == null)
            {
                _player.ContinueFrom(_player.Index + 1); // no/invalid save → skip the load op
                return;
            }
            RestoreSnapshot(snap);
        }

        /// <summary>Restore a snapshot of the CURRENT chapter in place: clean the
        /// stage, restore cursor/vars/call stack (position resolves via its label
        /// anchor), rebuild the scene's visuals/FX/audio up to that point, resume.
        /// The shared machinery behind the in-script `load` op, the save/load
        /// panel and the autosave resume.</summary>
        public async void RestoreSnapshot(LvnPlayer.LvnSnapshot snap)
        {
            // async void: an escaped exception here would take down the frame with
            // no caller to observe it — catch, log, and try to resume anyway.
            try { await RestoreSnapshotAsync(snap); }
            catch (System.Exception e)
            {
                LvnLog.Error("[lvn] restore failed: " + e);
                try { _player?.Advance(); } catch { /* stage unusable; error already logged */ }
            }
        }

        private async Task RestoreSnapshotAsync(LvnPlayer.LvnSnapshot snap)
        {
            if (_player == null) return;
            if (snap == null) { _player.Advance(); return; } // no snapshot after all — play from the top (Play skipped its own advance expecting us)
            var player = _player;               // pin: a re-entry mid-await must not resume a dead run
            int gen = ++_startGen;              // supersede a pending intro warmup (see StartWithSpineWarmup)
            ResetStage();                       // clean slate
            // Capture AFTER ResetStage — it bumps the epoch; capturing before it
            // made this guard always-false and silently swallowed every resume.
            int epoch = _stageEpoch;            // an Exit/chapter change mid-restore must not repaint the cleared stage
            player.Restore(snap);               // cursor (via label anchor) + vars + call stack
            var fidelity = player.LastRestore;
            if (fidelity == LvnPlayer.RestoreFidelity.ChapterChanged)
                Debug.LogWarning("[lvn] resume: the chapter changed under this save — " +
                                 "no anchor survived, restarting the chapter (variables kept)");
            Resumed?.Invoke(fidelity);
            player.ClearHistory();              // the rollback trail no longer describes the path here
            int at = player.Index;              // the anchor-relocated cursor, not the raw saved index
            at = player.ResumeRenderIndex(at);  // step back onto the say the player was reading (never skip a seen beat)
            player.ReplayVisuals(at);           // rebuild bg / actors / FX / audio up to the saved point
            // Veil the half-built stage: between ReplayVisuals and the settled
            // builds only the bg is up — the player saw that as a white flash
            // on resume. NOT alpha 0: the Canvas would cull the children's
            // draw calls and defeat the spine warm pulse (PSO compile +
            // texture upload need real draws); 1/255 is imperceptible but
            // keeps the GPU warm. (Same trick as LvnSpineFader.WarmAlpha —
            // that constant lives in the optional Spine assembly.)
            const float veilWarmAlpha = 1f / 255f;
            CanvasGroup veil = null;
            if (_renderer is CanvasSceneRenderer veilCanvas && veilCanvas.Root != null)
            {
                veil = veilCanvas.Root.GetComponent<CanvasGroup>();
                if (veil == null) veil = veilCanvas.Root.AddComponent<CanvasGroup>();
                veil.alpha = veilWarmAlpha;
            }
            // The staged opening, resume flavour: ReplayVisuals fires its spine
            // builds without awaiting them — rendering the saved beat now would
            // typewrite over a still-building stage and freeze mid-sentence.
            // Реальное время: это ЗАМЕР для лога («сколько заняло»), и мерить
            // его кадровыми часами значило бы не заметить простоя.
            var t0 = Lvn.LvnClock.Wall();
            int inFlight = PendingSpineBuilds;
            try { await SpineBuildsSettled(); } catch { }   // снимок повреждён — начнём главу с начала, а не упадём
            if (inFlight > 0)
                LvnLog.Trace($"[lvn] resume warmed {inFlight} spine build(s) in {(Lvn.LvnClock.Wall() - t0):F2}s before rendering");
            if (veil != null)
            {
                // Reveal the fully built scene with a short fade instead of a pop.
                for (float a = veilWarmAlpha; a < 1f && RunCurrent(player, gen) && StageCurrent(epoch); a += Time.unscaledDeltaTime / 0.15f)
                {
                    veil.alpha = a;
                    await Task.Yield();
                }
                // Only finish the reveal if we're STILL the current restore — a
                // newer one may have re-veiled this same canvas to warm-alpha, and
                // slamming it to 1 here would flash its half-built stage.
                if (RunCurrent(player, gen) && StageCurrent(epoch)) veil.alpha = 1f;
            }
            if (RunCurrent(player, gen) && StageCurrent(epoch))
                player.ContinueFrom(at); // resume → renders the saved beat
        }

        // ── persistent save slots (per title, survive restarts) ─────────────

        /// <summary>Save-slot namespace + labels, set by the host per chapter entry
        /// (title id keys the slot store; script url tags snapshots so a slot is
        /// only restored into the chapter it belongs to).</summary>
        public void SetSaveContext(string titleId, string chapterId, string scriptUrl)
        {
            _saveTitleId = titleId;
            _saveChapterId = chapterId;
            _saveScriptUrl = scriptUrl;
        }

        private string _saveTitleId, _saveChapterId, _saveScriptUrl;
        private int _saySinceAutosave;

        /// <summary>The title id save slots are namespaced under (host-set).</summary>
        public string SaveTitleId => _saveTitleId;

        /// <summary>Write the current position into a named persistent slot.</summary>
        public bool SaveToSlot(string slot)
        {
            if (_player == null || string.IsNullOrEmpty(slot)) return false;
            var snap = _player.Save();
            snap.ScriptUrl = _saveScriptUrl;
            var last = _backlog.Count > 0 ? _backlog[_backlog.Count - 1].text : "";
            if (!LvnSaveStore.Put(_saveTitleId, slot, new LvnSaveSlot
            {
                Snap = snap,
                ChapterId = _saveChapterId,
                Preview = last,
            })) return false;
            // Manual slots get the scene screenshot captured when the menu
            // opened (null wipes a stale one). The rolling autosave doesn't —
            // its capture moment would be arbitrary.
            if (slot != LvnSaveStore.AutoSlot)
                LvnSaveStore.WriteThumb(_saveTitleId, slot, _pendingThumb);
            LvnPlayer.Log?.Invoke("saved slot '" + slot + "' @#" + snap.Index);
            Saved?.Invoke(slot); // host hook: cloud sync, achievements, analytics
            return true;
        }

        public bool LoadFromSlot(string slot)
        {
            var s = LvnSaveStore.Get(_saveTitleId, slot);
            if (s?.Snap == null || _player == null) return false;
            // Через LvnScriptRef: прямое сравнение считало, что адрес всегда
            // записан одинаково, и отвергало сейв, у которого кириллица в
            // адресе записана процентами или другой юникодной формой.
            if (!string.IsNullOrEmpty(s.Snap.ScriptUrl)
                && !Lvn.Content.LvnScriptRef.Same(s.Snap.ScriptUrl, _saveScriptUrl)) return false;
            RestoreSnapshot(s.Snap);
            return true;
        }

        /// <summary>Host hook for loading a slot that belongs to ANOTHER chapter:
        /// resolve the chapter by <c>Snap.ScriptUrl</c>, fetch its script, play it
        /// and restore. Wired by NovelApp; when null, cross-chapter slots simply
        /// aren't loadable (greyed out in the menu).</summary>
        public Func<LvnSaveSlot, Task<bool>> CrossChapterLoader;

        /// <summary>Load a slot wherever it points: in-place for the current
        /// chapter, via <see cref="CrossChapterLoader"/> for another one.</summary>
        public async Task<bool> LoadFromSlotAsync(string slot)
        {
            if (LoadFromSlot(slot)) return true;
            var s = LvnSaveStore.Get(_saveTitleId, slot);
            if (s?.Snap == null || CrossChapterLoader == null) return false;
            try { return await CrossChapterLoader(s); }
            catch (Exception e)
            {
                Debug.LogWarning("[lvn] cross-chapter load failed: " + e.Message);
                return false;
            }
        }

        /// <summary>True when the slot exists and is reachable — taken in the
        /// current chapter, or in another one the host can route to.</summary>
        public bool CanLoadSlot(string slot)
        {
            var s = LvnSaveStore.Get(_saveTitleId, slot);
            if (s?.Snap == null) return false;
            if (string.IsNullOrEmpty(s.Snap.ScriptUrl)
                || Lvn.Content.LvnScriptRef.Same(s.Snap.ScriptUrl, _saveScriptUrl)) return true;
            return CrossChapterLoader != null;
        }

        /// <summary>
        /// СКОЛЬКО РЕПЛИК МОЖНО ПОТЕРЯТЬ при падении приложения.
        ///
        /// <para>Продуктовое решение, а не деталь отрисовки: между двумя
        /// автосейвами игрок читает столько-то строк, и ровно столько он
        /// перечитает после сбоя. Число стояло безымянной пятёркой в глубине
        /// метода, который рисует реплику, — там его никто не искал бы, меняя
        /// «как часто мы сохраняемся».</para>
        ///
        /// <para>Выбор и ввод значения сохраняются НЕЗАВИСИМО от счётчика: их
        /// нельзя терять вовсе, и это отдельные вызовы.</para>
        /// </summary>
        public const int AutosaveEveryLines = 5;

        /// <summary>Autosave into the reserved slot now — called by the host on
        /// app pause, and internally on choices / every few lines.</summary>
        public void AutosaveNow()
        {
            if (!Playing) return;
            SaveToSlot(LvnSaveStore.AutoSlot);
        }
    }
}
