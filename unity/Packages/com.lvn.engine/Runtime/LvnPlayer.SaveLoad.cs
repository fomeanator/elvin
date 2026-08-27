using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// СНИМКИ И ОТКАТ — сохранения, «назад» и переезд позиции на новый текст.
    ///
    /// <para>Снимок — это не только индекс команды: с ним едут переменные,
    /// стек вызовов и след исполнения, иначе загруженная игра окажется в
    /// правильном месте с неправильной сценой. Отдельная забота — ЯКОРЬ: после
    /// правки скрипта номер команды больше ничего не значит, и позиция
    /// переносится по ближайшей метке со смещением, а не по числу.</para>
    /// </summary>
    public sealed partial class LvnPlayer
    {
        private readonly List<LvnSnapshot> _history = new List<LvnSnapshot>();

        // The actually-EXECUTED visual/audio command indices, in execution order —
        // the truthful path for ReplayVisuals. A linear script prefix lies the
        // moment the chapter branches: ops from never-taken branches would leak
        // into the rebuilt scene (wrong bg, resurrected/hidden actors).
        private List<int> _trace = new List<int>();

        /// <summary>True when there is a previous beat to roll back to.</summary>
        public bool CanRollback => _history.Count >= 2;

        /// <summary>Pop the current beat and return the previous one to restore
        /// (null when at the first beat). The returned beat re-enters the history
        /// when it re-runs, so repeated rollbacks walk further back.</summary>
        public LvnSnapshot PopRollback()
        {
            if (_history.Count < 2) return null;
            _history.RemoveAt(_history.Count - 1); // the beat currently on screen
            var prev = _history[_history.Count - 1];
            _history.RemoveAt(_history.Count - 1); // re-pushed when it re-runs
            return prev;
        }

        /// <summary>How many beats the rollback history currently holds — the
        /// deepest multi-step rollback is <c>HistoryDepth - 1</c>.</summary>
        public int HistoryDepth => _history.Count;

        /// <summary>Multi-step rollback: pop <paramref name="steps"/> beats in one
        /// hop and return the beat to restore (clamped to the recorded history;
        /// null when there's nothing to roll back to). Equivalent to that many
        /// single rollbacks, minus the intermediate re-runs — the History panel's
        /// tap-to-return uses it for one scene rebuild instead of N.</summary>
        public LvnSnapshot PopRollback(int steps)
        {
            if (steps > _history.Count - 1) steps = _history.Count - 1;
            if (steps < 1) return null;
            _history.RemoveRange(_history.Count - steps, steps);
            var prev = _history[_history.Count - 1];
            _history.RemoveAt(_history.Count - 1); // re-pushed when it re-runs
            return prev;
        }

        /// <summary>Drop the rollback history — call after restoring an external
        /// save, where the recorded beats no longer describe the path taken.</summary>
        public void ClearHistory() => _history.Clear();

        /// <summary>Pop and return the CURRENT beat's snapshot (taken before it
        /// ran) — the re-render anchor for a chrome rebuild after a disable/
        /// enable cycle. Null when no beat has run yet. The beat re-enters the
        /// history when it re-runs.</summary>
        public LvnSnapshot PopCurrent()
        {
            if (_history.Count < 1) return null;
            var cur = _history[_history.Count - 1];
            _history.RemoveAt(_history.Count - 1);
            return cur;
        }

        /// <summary>The index a resume should render from. A <c>say</c> pauses
        /// with the cursor already PAST it (see its <c>_ip++</c>), so restoring
        /// at the raw saved index silently skips the line the player was reading
        /// — re-entry "jumped a beat forward". Stepping back onto the say
        /// re-shows the last seen line and then naturally continues; a choice
        /// pauses ON its own op, so it needs no correction.</summary>
        public int ResumeRenderIndex(int at)
        {
            if (at > 0 && at <= _script.Count && _script[at - 1] is JObject p && (string)p["op"] == "say")
                return at - 1;
            return at;
        }

        /// <summary>The next <paramref name="maxCommands"/> commands ahead of the
        /// cursor, in script order (a linear look-ahead — jumps are not followed).
        /// The stage uses it to warm the art/audio the scene is about to need, so
        /// a cold sprite never pops in mid-line.</summary>

        private void PushHistory()
        {
            // A re-presented beat (a tap while the same choice is up, a re-render)
            // must not duplicate. Note: a revisit of the same index via a loop is
            // also collapsed — rolling back to it lands on the FIRST visit's state.
            if (_history.Count > 0 && _history[_history.Count - 1].Index == _ip) return;
            _history.Add(Save());
            if (_history.Count > MaxHistory) _history.RemoveAt(0);
        }

        public IReadOnlyCollection<int> CallStack => _callStack;

        /// <summary>Snapshot of the player's state for save/load. <see cref="CommandCount"/>
        /// and <see cref="Finished"/> let a host feed <see cref="ResumePlanner"/> so
        /// a resume survives the script changing length between sessions;
        /// <see cref="ScriptUrl"/> is set by the host (the player doesn't know it).</summary>
        public class LvnSnapshot
        {
            public int Index;
            public Dictionary<string, JToken> Vars;
            public int[] CallStack;
            /// <summary>Command count of the script when this snapshot was taken.</summary>
            public int CommandCount;
            /// <summary>True if the chapter had reached its end when saved.</summary>
            public bool Finished;
            /// <summary>Host-supplied id/url of the script this slot belongs to.</summary>
            public string ScriptUrl;
            /// <summary>Stable position anchor: the label the cursor was under and the
            /// offset past it. Resume relocates by this first, so a save survives the
            /// script being edited/re-imported (indices shifting) between sessions;
            /// falls back to <see cref="Index"/> when the label is gone.</summary>
            public string AnchorLabel;
            public int AnchorSteps;
            /// <summary>Second, SHOCKPROOF anchor: the nearest preceding label the
            /// AUTHOR wrote (never a `__`-prefixed one the compiler minted) and the
            /// offset past it. The minted names are derived from the chapter's own
            /// labels and no longer renumber on a re-save, but they are still the
            /// compiler's to change; a bookmark must not depend on one. Used when
            /// <see cref="AnchorLabel"/> is gone; null on older saves.</summary>
            public string AnchorStableLabel;
            public int AnchorStableSteps;
            /// <summary>Per-frame anchors for <see cref="CallStack"/> (same order,
            /// top-first). Return addresses are raw indices too, so they need the
            /// same label+offset relocation as the cursor; null on older saves.</summary>
            public string[] CallAnchorLabels;
            public int[] CallAnchorSteps;
            /// <summary>Executed visual-op indices up to <see cref="Index"/> —
            /// the truthful replay path. Null on older saves (legacy linear
            /// replay) and discarded when the script's command count changed.</summary>
            public int[] Trace;
            /// <summary>Position of the random stream behind <c>rand()</c> /
            /// <c>chance()</c> (<see cref="LvnRandom.SaveState"/>). Without it a
            /// reload re-rolled every fight and every loot table — save-scumming
            /// was a feature of the engine, not a choice of the game. Null on
            /// saves written before this field existed and whenever
            /// <see cref="PersistRandomState"/> is off; a restore then leaves the
            /// live stream alone (see <see cref="Restore(LvnSnapshot)"/>).
            ///
            /// <para>Rollback rides the same field: each beat's snapshot holds the
            /// stream as it was BEFORE the beat ran, so stepping back and
            /// replaying re-draws the same numbers.</para></summary>
            public string RngState;
        }

        /// <summary>Whether <see cref="Save"/> records the random stream's
        /// position. Default true: a reload continues the run it saved. Set false
        /// for a game that WANTS a reload to re-roll (the engine's behaviour
        /// before the stream became part of the snapshot) — old saves, which
        /// carry no stream, behave that way regardless.</summary>
        public static bool PersistRandomState = true;

        /// <summary>Capture the current state for serialization.</summary>
        public LvnSnapshot Save()
        {
            var (aLabel, aSteps) = AnchorOf(_ip);
            var (sLabel, sSteps) = AnchorOf(_ip, authorLabelsOnly: true);
            var frames = _callStack.ToArray();
            var caLabels = new string[frames.Length];
            var caSteps = new int[frames.Length];
            for (int i = 0; i < frames.Length; i++)
                (caLabels[i], caSteps[i]) = AnchorOf(frames[i]);
            return new LvnSnapshot
            {
                Index = _ip,
                Vars = new Dictionary<string, JToken>(Vars),
                CallStack = frames,
                CallAnchorLabels = caLabels,
                CallAnchorSteps = caSteps,
                CommandCount = _script.Count,
                Finished = Finished,
                AnchorLabel = aLabel,
                AnchorSteps = aSteps,
                AnchorStableLabel = sLabel,
                AnchorStableSteps = sSteps,
                Trace = _trace.ToArray(),
                RngState = PersistRandomState ? LvnExpression.Random.SaveState() : null,
            };
        }

        /// <summary>How faithfully the last <see cref="Restore(LvnSnapshot)"/> could
        /// place the cursor. A host shows the player something honest for
        /// <see cref="RestoreFidelity.ChapterChanged"/> instead of dropping them in
        /// an arbitrary scene.</summary>
        public enum RestoreFidelity
        {
            /// <summary>The script is the one the save was taken on.</summary>
            Exact,
            /// <summary>The script changed; the saved label still exists and the
            /// cursor moved with it. The saved beat, at its new index.</summary>
            Relocated,
            /// <summary>The saved label is gone. The cursor was placed inside the
            /// same author-labelled scene (or by raw index on a script whose length
            /// is unchanged) — the right scene, possibly not the exact line.</summary>
            Approximate,
            /// <summary>Nothing left to anchor on: the chapter was rewritten under
            /// the save. The cursor was reset to the top of the chapter (variables
            /// and progress kept) — the host MUST tell the player.</summary>
            ChapterChanged,
        }

        /// <summary>Fidelity of the most recent <see cref="Restore(LvnSnapshot)"/>.</summary>
        public RestoreFidelity LastRestore { get; private set; } = RestoreFidelity.Exact;

        /// <summary>Restore from a snapshot. Resolves the position by its label anchor
        /// first (so a save survives the script being edited/re-imported), then by the
        /// author-label anchor, and only then by the raw index — and when NONE of them
        /// can be trusted it restarts the chapter and says so through
        /// <see cref="LastRestore"/> rather than landing the player in the wrong scene.
        ///
        /// <para>That last rung is the whole point. A re-saved chapter renames the
        /// labels the compiler minted and shifts every index; the old code then fell
        /// back to the raw index, which is exactly as wrong as the label — and said
        /// nothing. "Continue" opened a scene the player had never reached.</para></summary>
        public void Restore(LvnSnapshot snapshot)
        {
            if (snapshot == null) return;
            bool sameShape = _script != null && snapshot.CommandCount == _script.Count;
            int at;
            if (snapshot.AnchorLabel != null && _labels.ContainsKey(snapshot.AnchorLabel))
            {
                at = Relocate(snapshot.AnchorLabel, snapshot.AnchorSteps, snapshot.Index);
                LastRestore = sameShape ? RestoreFidelity.Exact : RestoreFidelity.Relocated;
            }
            else if (snapshot.AnchorStableLabel != null && _labels.ContainsKey(snapshot.AnchorStableLabel))
            {
                // The scene survived, the beat inside it may not have. Clamping to
                // the scene keeps the promise that matters: never resume in a
                // DIFFERENT scene than the one the player was reading.
                at = Relocate(snapshot.AnchorStableLabel, snapshot.AnchorStableSteps, snapshot.Index);
                LastRestore = RestoreFidelity.Approximate;
            }
            else if (sameShape)
            {
                // Same length, no label to relocate by: a pure rename (or a save
                // taken before the first label). Indices did not move.
                at = snapshot.Index;
                LastRestore = snapshot.AnchorLabel == null ? RestoreFidelity.Exact : RestoreFidelity.Approximate;
            }
            else
            {
                at = 0;
                LastRestore = RestoreFidelity.ChapterChanged;
                Log?.Invoke("restore: chapter changed under the save (anchor '" +
                            (snapshot.AnchorLabel ?? "-") + "' gone, " + snapshot.CommandCount +
                            " → " + (_script == null ? 0 : _script.Count) + " commands) — restarting it");
            }
            // A shortened script must not resume PAST its end — that would
            // instantly Finish() the chapter and silently mark it completed.
            // Landing on the last beat keeps the progress and the player's seat.
            if (_script != null && _script.Count > 0 && at >= _script.Count)
                at = _script.Count - 1;
            // The replay path is only truthful against the EXACT script it was
            // recorded on — an edited/re-imported script falls back to legacy.
            _trace = snapshot.Trace != null && snapshot.CommandCount == _script.Count
                ? new List<int>(snapshot.Trace)
                : new List<int>();
            // Put the dice back where the save left them. A save from before this
            // field existed carries nothing, and a stream is not something we can
            // guess: reseeding to some constant would make EVERY old save re-roll
            // the same numbers, and reseeding randomly is what already happens.
            // So: leave the live stream running — the pre-2026-07-26 behaviour,
            // for exactly the saves written under it.
            if (!string.IsNullOrEmpty(snapshot.RngState) &&
                !LvnExpression.Random.TryLoadState(snapshot.RngState))
                Log?.Invoke("restore: unreadable rng state '" + snapshot.RngState +
                            "' — keeping the current stream (rolls will differ)");
            // Return addresses shift with the script just like the cursor does —
            // relocate each frame by its own anchor, falling back to the raw index.
            var stack = snapshot.CallStack;
            if (stack != null && snapshot.CallAnchorLabels != null
                && snapshot.CallAnchorLabels.Length == stack.Length
                && snapshot.CallAnchorSteps != null
                && snapshot.CallAnchorSteps.Length == stack.Length)
            {
                var relocated = new int[stack.Length];
                for (int i = 0; i < stack.Length; i++)
                    relocated[i] = snapshot.CallAnchorLabels[i] != null
                        ? Relocate(snapshot.CallAnchorLabels[i], snapshot.CallAnchorSteps[i], stack[i])
                        : stack[i];
                stack = relocated;
            }
            Restore(at, snapshot.Vars, stack);
        }

        /// <summary>
        /// Hot-swap the underlying script in place — for a live edit that didn't
        /// change the command STRUCTURE — keeping the cursor, variables and call
        /// stack so the chapter continues exactly where it is. Returns false when
        /// the structure changed (different command count, a changed op, or a moved
        /// label id): the host must then restart the chapter from the top, because
        /// the saved cursor no longer means the same beat. Text/parameter edits
        /// (a reworded line, a tweaked emotion or position) all pass.
        /// </summary>
        // A stable anchor for a script index: the nearest PRECEDING label id plus the
        // offset from it. Labels are jump targets and don't move meaning across edits,
        // so an anchor survives a script whose command indices shifted (a line added /
        // removed, a re-import). Returns (null, index) when the cursor is before any
        // label (the leading set/init block).
        //
        // `authorLabelsOnly` skips the labels the COMPILER minted (`__then…`,
        // `__nf…`, `__end…`): those names belong to the lowering, not to the story,
        // and a save must have a second anchor that survives the compiler changing
        // its mind about them.
        private (string label, int steps) AnchorOf(int index, bool authorLabelsOnly = false)
        {
            int from = System.Math.Min(index, _script.Count) - 1;
            for (int i = from; i >= 0; i--)
            {
                if (!(_script[i] is JObject c) || (string)c["op"] != "label") continue;
                var id = (string)c["id"];
                if (authorLabelsOnly && (id == null || id.StartsWith("__", StringComparison.Ordinal))) continue;
                return (id, index - i);
            }
            return (null, index);
        }

        // Resolve an anchor back to an index in the CURRENT script (call after _labels
        // is rebuilt). Falls back to the raw index if the label is gone. Clamped — and
        // never past the NEXT label: an offset counted in a scene that has since lost
        // commands would otherwise spill into the following scene, which is precisely
        // the silent "continue opens the wrong beat" this anchor exists to prevent.
        private int Relocate(string label, int steps, int fallback)
        {
            int at = fallback;
            if (!string.IsNullOrEmpty(label) && _labels.TryGetValue(label, out var i))
            {
                at = i + steps;
                int scopeEnd = _script.Count;
                for (int k = i + 1; k < _script.Count; k++)
                    if (_script[k] is JObject n && (string)n["op"] == "label") { scopeEnd = k; break; }
                if (at > scopeEnd) at = scopeEnd;
            }
            if (at < 0) at = 0;
            if (at > _script.Count) at = _script.Count;
            return at;
        }

        public bool TryReplaceScript(LvnDocument doc)
        {
            var next = doc?.Script;
            if (next == null || next.Count == 0) return false;
            int oldCount = _script.Count;

            // Anchor the cursor BEFORE swapping, so we can restore the same beat even
            // if the edit changed the command count and shifted every index. Call-stack
            // return addresses are raw indices with the same problem — anchor each frame.
            var (aLabel, aSteps) = AnchorOf(_ip);
            var frames = _callStack.ToArray(); // top-first
            var frameAnchors = new (string label, int steps)[frames.Length];
            for (int i = 0; i < frames.Length; i++) frameAnchors[i] = AnchorOf(frames[i]);

            // Index-aligned edit (same length + same op structure) → keep the cursor
            // exactly and re-issue only the visual ops that changed. The common "fix a
            // typo" path: no reposition, no re-fade.
            bool aligned = next.Count == oldCount;
            List<int> reapply = null;
            if (aligned)
                for (int i = 0; i < next.Count; i++)
                {
                    var a = _script[i] as JObject;
                    var b = next[i] as JObject;
                    if (a == null || b == null) { aligned = false; break; }
                    var op = (string)a["op"];
                    if (op != (string)b["op"]) { aligned = false; break; }
                    if (op == "label" && (string)a["id"] != (string)b["id"]) { aligned = false; break; }
                    if (i < _ip && IsReapplyable(op) && !JToken.DeepEquals(a, b))
                        (reapply ??= new List<int>()).Add(i);
                }

            _script = next;
            _labels.Clear();
            for (int i = 0; i < _script.Count; i++)
                if (_script[i] is JObject c && (string)c["op"] == "label")
                {
                    var id = (string)c["id"];
                    if (!string.IsNullOrEmpty(id)) _labels[id] = i;
                }

            if (aligned)
            {
                if (_ip > _script.Count) _ip = _script.Count;
                if (reapply != null)
                    foreach (var i in reapply) StageApply((JObject)_script[i]);
            }
            else
            {
                // Indices shifted — relocate the cursor to the same beat via its label
                // anchor and rebuild the visible stage there. No restart, no jump.
                _ip = Relocate(aLabel, aSteps, _ip);
                if (frames.Length > 0)
                {
                    _callStack.Clear();
                    for (int i = frames.Length - 1; i >= 0; i--)
                        _callStack.Push(Relocate(frameAnchors[i].label, frameAnchors[i].steps, frames[i]));
                }
                ReplayVisuals(_ip);
            }
            return true;
        }

        // Pure-visual staging ops safe to re-apply on a hot-swap (no side effects
        // on vars/flow/pauses). NOT set/inc (would double-count) nor say/choice/wait.
        /// <summary>
        /// Единственная дверь на сцену. Раньше её открывали в десяти местах, и
        /// любая обработка команды перед показом означала бы десять правок —
        /// девять из которых однажды забыли бы.
        /// </summary>
    }
}
