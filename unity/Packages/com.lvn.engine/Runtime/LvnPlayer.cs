using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// The .lvn interpreter: a cursor over the command list plus a variable bag.
    /// It owns flow control (goto / if / choice / call-return) and hands every
    /// presentation command to an <see cref="ILvnStage"/>. It has no Unity
    /// dependency — the same player drives a UI Toolkit stage, a uGUI stage, or
    /// a headless test host.
    ///
    /// Drive it by alternating with the stage:
    ///   • call <see cref="Advance"/> to run until the next pause (a say, a
    ///     choice, or the end);
    ///   • after a say, call <see cref="Advance"/> again on the player's tap;
    ///   • after a choice, call <see cref="Choose"/> then <see cref="Advance"/>.
    /// </summary>
    public sealed class LvnPlayer
    {
        private JArray _script; // swappable for hot-reload (see TryReplaceScript)
        private readonly ILvnStage _stage;
        private readonly Dictionary<string, int> _labels = new Dictionary<string, int>();
        private readonly Stack<int> _callStack = new Stack<int>();

        /// <summary>Authored + bookkeeping variables. Public for save/restore.</summary>
        public readonly Dictionary<string, JToken> Vars = new Dictionary<string, JToken>();

        /// <summary>Fired when a say command is executed. Arguments: who, text, style.</summary>
        public Action<string, string, string> OnSay;

        /// <summary>Optional trace sink — set by the host (e.g. to Debug.Log) to get
        /// a full step-by-step log of execution. No-op when null (zero overhead).</summary>
        public static Action<string> Log;

        /// <summary>Optional localization catalog: <c>text_id</c> → string for the
        /// active language. When a say/choice carries a <c>text_id</c> (instead of
        /// inline <c>text</c>), it is resolved here. Swap this to switch language;
        /// the <c>.lvn</c> structure is language-independent.</summary>
        public IReadOnlyDictionary<string, string> Strings;

        // Resolve a line's text in the active language. Two keying schemes share
        // one catalog: an explicit "text_id" (stable id, e.g. an articy GUID), or —
        // for inline-authored lines — the source string itself as the key
        // (gettext/Ren'Py style). Missing translation falls back to the source.
        private string Localized(JObject c)
        {
            var inline = (string)c["text"];
            if (inline != null)
                return Strings != null && Strings.TryGetValue(inline, out var tr) ? tr : inline;
            var id = (string)c["text_id"];
            if (id == null) return "";
            return Strings != null && Strings.TryGetValue(id, out var s) ? s : id;
        }

        // Speaker display names resolve through the same catalog (keyed by the
        // source name), so a translated cast renders without touching the script.
        private string LocalizedWho(string who)
            => who != null && Strings != null && Strings.TryGetValue(who, out var t) ? t : who;

        /// <summary>
        /// Optional override for string <c>expr</c> conditions (option filters
        /// and <c>if</c>). When unset, the built-in <see cref="LvnExpression"/>
        /// evaluator is used; set this only to plug in a different expression
        /// dialect. Structured <c>cond</c> is unaffected.
        /// </summary>
        public Func<string, IReadOnlyDictionary<string, JToken>, bool> ExprEvaluator;

        // A malformed expression in the content must never crash the runtime — a
        // bad condition simply gates closed (false). Authoring tools catch these at
        // compile time; the player degrades gracefully.
        private bool EvalExpr(string expr)
        {
            try
            {
                return ExprEvaluator != null ? ExprEvaluator(expr, Vars) : LvnExpression.EvaluateBool(expr, Vars);
            }
            catch (LvnException)
            {
                return false;
            }
        }

        private int _ip;

        public bool Finished { get; private set; }
        public int Index => _ip;

        /// <summary>Total command count — pairs with <see cref="Index"/> to drive
        /// a chapter-progress readout (e.g. the in-game HUD percent).</summary>
        public int Count => _script.Count;

        // Furthest MAIN-LINE command reached this chapter. The raw cursor moves
        // backward on any loop or hub revisit (a `while`, a choice that jumps to an
        // earlier label) and dives into out-of-order labels during a `call`, so a
        // percent built from Index alone visibly "resets and starts over" — the
        // reported bug. Progress is the running max, and it's frozen while inside a
        // call (callStack non-empty) so a subroutine whose label sits late in the
        // file (e.g. `call levelup`) doesn't spike the bar to ~100% and back.
        private int _progressMax;

        /// <summary>Monotonic chapter progress index (0..<see cref="Count"/>) for
        /// the HUD percent — never runs backward on a loop/hub jump. Pair with
        /// <see cref="Count"/> exactly like <see cref="Index"/>.</summary>
        public int ProgressIndex => System.Math.Min(_progressMax, _script.Count);

        public LvnPlayer(LvnDocument doc, ILvnStage stage)
        {
            _script = doc.Script;
            _stage = stage;
            for (int i = 0; i < _script.Count; i++)
            {
                if (_script[i] is JObject c && (string)c["op"] == "label")
                {
                    var id = (string)c["id"];
                    if (!string.IsNullOrEmpty(id))
                        _labels[id] = i;
                }
            }
        }

        /// <summary>Restore a saved position and state (for autosave/resume).</summary>
        public void Restore(int index, IDictionary<string, JToken> vars, IEnumerable<int> callStack)
        {
            _ip = index;
            _progressMax = index; // resume: the bar reflects where we land, then climbs
            Finished = false;
            Vars.Clear();
            if (vars != null)
                foreach (var kv in vars) Vars[kv.Key] = kv.Value;
            _callStack.Clear();
            if (callStack != null)
            {
                // Save() emits the stack top-first (Stack.ToArray order); push in
                // reverse so the restored stack matches the original exactly.
                var frames = new List<int>(callStack);
                for (int i = frames.Count - 1; i >= 0; i--) _callStack.Push(frames[i]);
            }
        }

        /// <summary>Re-apply the persistent visual side-effects (background, actors,
        /// HUD labels, idle animations, and the net FX/audio state) of commands
        /// <c>0..upto</c> without showing any dialogue — used after
        /// <see cref="Restore(LvnSnapshot)"/> to rebuild the scene a save was taken
        /// in before resuming.</summary>
        public void ReplayVisuals(int upto)
        {
            if (_script == null) return;
            int end = System.Math.Min(upto, _script.Count);
            // Three replay classes. Structural ops (bg/obj/anim/text) accumulate,
            // so they re-run in order. FX/audio are stateful overlays where only the
            // LAST setting matters — re-running every fade/tint/track of the chapter
            // would flash through all of them — so they collapse to the final value
            // per state key and apply once at the end. ACTORS collapse per id: every
            // command for the same id MERGES into one (later fields win), replayed
            // INLINE at that id's LAST occurrence — so relative order against bg/obj
            // is preserved exactly as authored — and only if the merged result ends
            // visible, so a chapter that showed & hid ten Spine scenes rebuilds just
            // the one on screen, not all ten (each an expensive skeleton build).
            var fx = new Dictionary<string, JObject>();
            var fxOrder = new List<string>();
            void SetFx(string key, JObject cmd)
            {
                if (!fx.ContainsKey(key)) fxOrder.Add(key);
                fx[key] = cmd;
            }

            // Pass 1: merge every actor id's fields across all its occurrences, and
            // note the LAST index it appears at — that's where the merged result
            // replays in pass 2, keeping it in its natural position in the sequence.
            var actorMerged = new Dictionary<string, JObject>();
            var actorLastIndex = new Dictionary<string, int>();
            for (int i = 0; i < end; i++)
            {
                if (!(_script[i] is JObject c) || (string)c["op"] != "actor") continue;
                var aid = (string)c["id"];
                if (string.IsNullOrEmpty(aid)) continue;
                if (!actorMerged.TryGetValue(aid, out var m)) { m = new JObject(); actorMerged[aid] = m; }
                foreach (var prop in c.Properties()) m[prop.Name] = prop.Value.DeepClone(); // later fields win
                actorLastIndex[aid] = i;
            }

            // Pass 2: replay inline, in original order. An actor op fires exactly
            // once — at its LAST index, with the fully-merged fields; earlier
            // occurrences of the same id are silent (already folded into that one).
            for (int i = 0; i < end; i++)
            {
                if (!(_script[i] is JObject c)) continue;
                var op = (string)c["op"];
                if (op == "actor")
                {
                    var aid = (string)c["id"];
                    if (!string.IsNullOrEmpty(aid) && actorLastIndex.TryGetValue(aid, out var last) && last == i)
                    {
                        var m = actorMerged[aid];
                        if (BoolOr(m["show"], true)) _stage.ApplyStage(m);
                    }
                    continue;
                }
                if (IsReapplyable(op)) { _stage.ApplyStage(c); continue; }
                switch (op)
                {
                    case "fade":
                    case "dim":
                    case "tint":
                    case "blur":
                        SetFx(op, c);
                        break;
                    case "particles":
                        SetFx("particles:" + ((string)c["type"] ?? ""), c);
                        break;
                    case "camera":
                        // zoom/pan persist; reset returns both to default (so drop
                        // them); shake is transient and never replayed.
                        var act = (string)c["action"];
                        if (act == "zoom" || act == "pan") SetFx("camera:" + act, c);
                        else if (act == "reset") { fx.Remove("camera:zoom"); fx.Remove("camera:pan"); }
                        break;
                    case "audio":
                        // The looping channels (music/ambient) resume their last
                        // track (or stay stopped if the last command was a stop);
                        // sfx one-shots don't replay.
                        var ch = (string)c["channel"] ?? "sfx";
                        if (ch != "sfx") SetFx("audio:" + ch, c);
                        break;
                }
            }
            foreach (var key in fxOrder)
                if (fx.TryGetValue(key, out var cmd))
                    _stage.ApplyStage(cmd);
        }

        /// <summary>Set the cursor and run forward to the next pause — the resume
        /// step after a load (the scene is rebuilt by <see cref="ReplayVisuals"/>).</summary>
        public void ContinueFrom(int index)
        {
            _ip = index;
            Finished = false;
            Advance();
        }

        // ── rollback ─────────────────────────────────────────────────────────
        // A bounded history of "beats": a snapshot pushed as each say (or a choice
        // with no say line of its own) is shown, taken BEFORE the beat runs — so
        // rolling back to a choice restores the variables as they were before the
        // pick (an option's set/inc is undone). A say immediately followed by a
        // choice is ONE beat anchored at the say, so a rollback re-shows the line
        // together with its options.

        /// <summary>Rollback history depth cap. Oldest beats fall off.</summary>
        public const int MaxHistory = 100;

        private readonly List<LvnSnapshot> _history = new List<LvnSnapshot>();

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
        public IEnumerable<JObject> PeekForward(int maxCommands)
        {
            if (_script == null) yield break;
            int end = System.Math.Min(_ip + maxCommands, _script.Count);
            for (int i = System.Math.Max(_ip, 0); i < end; i++)
                if (_script[i] is JObject c)
                    yield return c;
        }

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
            /// <summary>Per-frame anchors for <see cref="CallStack"/> (same order,
            /// top-first). Return addresses are raw indices too, so they need the
            /// same label+offset relocation as the cursor; null on older saves.</summary>
            public string[] CallAnchorLabels;
            public int[] CallAnchorSteps;
        }

        /// <summary>Capture the current state for serialization.</summary>
        public LvnSnapshot Save()
        {
            var (aLabel, aSteps) = AnchorOf(_ip);
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
            };
        }

        /// <summary>Restore from a snapshot. Resolves the position by its label anchor
        /// first (so a save survives the script being edited/re-imported), falling back
        /// to the raw index for older saves that lack an anchor.</summary>
        public void Restore(LvnSnapshot snapshot)
        {
            if (snapshot == null) return;
            int at = snapshot.AnchorLabel != null
                ? Relocate(snapshot.AnchorLabel, snapshot.AnchorSteps, snapshot.Index)
                : snapshot.Index;
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
        private (string label, int steps) AnchorOf(int index)
        {
            int from = System.Math.Min(index, _script.Count) - 1;
            for (int i = from; i >= 0; i--)
                if (_script[i] is JObject c && (string)c["op"] == "label")
                    return ((string)c["id"], index - i);
            return (null, index);
        }

        // Resolve an anchor back to an index in the CURRENT script (call after _labels
        // is rebuilt). Falls back to the raw index if the label is gone. Clamped.
        private int Relocate(string label, int steps, int fallback)
        {
            int at = fallback;
            if (!string.IsNullOrEmpty(label) && _labels.TryGetValue(label, out var i))
                at = i + steps;
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
                    foreach (var i in reapply) _stage.ApplyStage((JObject)_script[i]);
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
        private static bool IsReapplyable(string op) =>
            op == "bg" || op == "obj" || op == "anim" || op == "text"; // actor collapses per id (see ReplayVisuals)

        /// <summary>
        /// Re-issue the stage command for the beat currently on screen (the say
        /// just shown, or the choice we're waiting on) — called after a hot-swap so
        /// an edit to the visible line appears immediately without advancing. Does
        /// not fire <see cref="OnSay"/> (so the history backlog isn't duplicated).
        /// </summary>
        public void RerenderCurrent()
        {
            if (_script == null || _script.Count == 0 || Finished) return;
            // A choice pauses AT its index; a say advances past it, so look back one.
            if (_ip >= 0 && _ip < _script.Count && _script[_ip] is JObject atIp && (string)atIp["op"] == "choice")
            {
                _stage.ShowChoice(BuildOptions(atIp));
                return;
            }
            int j = _ip - 1;
            if (j >= 0 && j < _script.Count && _script[j] is JObject c && (string)c["op"] == "say")
            {
                CurrentVoiceUrl = (string)c["voice"];
                CurrentSpeakerId = (string)c["who_id"];
                var who = TextInterpolation.Apply(LocalizedWho((string)c["who"]), Vars);
                // mutate:false — a re-render shows the SAME variant, never advancing
                // the {a|b|c} sequence or re-rolling {~shuffle} (that would silently
                // change the visible line on every hot-reload / chrome rebuild).
                var text = TextAlternatives.Apply(Localized(c), Vars, j, null, mutate: false);
                text = TextInterpolation.Apply(text, Vars);
                _stage.ShowSay(who, text, (string)c["style"]);
            }
        }

        /// <summary>Run commands until the next pause point or the end.</summary>
        public void Advance()
        {
            // A pause (say/choice) or the end breaks this loop. A guard catches a
            // cyclic goto with no pause between iterations, which would otherwise
            // spin the main thread forever (a freeze) instead of failing loudly.
            int budget = _script.Count + 100000;
            while (!Finished && _ip >= 0 && _ip < _script.Count)
            {
                // Advance the monotonic progress high-water mark, but only on the
                // main line — inside a call the cursor visits a subroutine's
                // (possibly late) labels, which shouldn't move the chapter bar.
                if (_callStack.Count == 0 && _ip > _progressMax) _progressMax = _ip;
                if (--budget < 0)
                    throw new LvnException("possible infinite loop: a goto cycle has no say/choice between jumps");
                // Malformed content must never crash the runtime: a non-object
                // command (bad export/hand-edited JSON) is skipped, not cast-thrown.
                if (!(_script[_ip] is JObject c)) { _ip++; continue; }
                var curOp = (string)c["op"];
                if (Log != null) Log("#" + _ip + " " + curOp + DescribeCmd(c));
                switch (curOp)
                {
                    case "label":
                        _ip++;
                        break;

                    case "set":
                    case "inc":
                        ApplyData(c);
                        Log?.Invoke("    → " + (string)c["key"] + " = " + (Vars.TryGetValue((string)c["key"] ?? "", out var nv) ? nv.ToString() : "?"));
                        _ip++;
                        break;

                    case "goto":
                        Jump((string)c["label"]);
                        break;

                    case "call":
                        _callStack.Push(_ip + 1);
                        Jump((string)c["label"]);
                        break;

                    case "return":
                        _ip = _callStack.Count > 0 ? _callStack.Pop() : _script.Count;
                        break;

                    case "if":
                        bool cond = EvalCond(c);
                        var branch = cond ? (string)c["then"] : (string)c["else"];
                        Log?.Invoke("    if \"" + (string)c["expr"] + "\" → " + cond + " → :" + branch);
                        SeekTo(branch);
                        break;

                    case "choice":
                        // A choice directly after a say is the same beat (the line
                        // and its options show together) — the say already pushed.
                        bool paired = _ip > 0 && _script[_ip - 1] is JObject prevCmd
                                      && (string)prevCmd["op"] == "say";
                        if (!paired) PushHistory();
                        _stage.ShowChoice(BuildOptions(c));
                        return;

                    case "say":
                        PushHistory();
                        CurrentVoiceUrl = (string)c["voice"]; // the stage picks it up in ShowSay
                        CurrentSpeakerId = (string)c["who_id"]; // actor id behind the display name (actor_map)
                        // Ink-style alternatives first (their counters key off the
                        // command index), then {var} interpolation — for both the
                        // line and the speaker name.
                        var sayWho = TextInterpolation.Apply(LocalizedWho((string)c["who"]), Vars);
                        var sayText = TextAlternatives.Apply(Localized(c), Vars, _ip);
                        sayText = TextInterpolation.Apply(sayText, Vars);
                        var sayStyle = (string)c["style"];
                        Log?.Invoke("    \"" + (string.IsNullOrEmpty(sayWho) ? "" : sayWho + ": ") + sayText + "\"");
                        OnSay?.Invoke(sayWho, sayText, sayStyle);
                        _stage.ShowSay(sayWho, sayText, sayStyle);
                        _ip++;
                        // If a choice follows immediately, present it together with
                        // this line — the dialogue (prompt) and the choices show in
                        // one step, no tap between. They stay two separate, fully
                        // themable UIs; layout is up to the theme.
                        if (_ip < _script.Count && _script[_ip] is JObject afterSay && (string)afterSay["op"] == "choice")
                            break;
                        return;

                    case "wait":
                        _stage.ApplyStage(c);
                        _ip++;
                        return;

                    case "input":
                        // The stage shows a text-entry overlay; the story pauses
                        // here until the host writes the variable and re-Advances.
                        _stage.ApplyStage(c);
                        _ip++;
                        return;

                    case "preload":
                        _stage.ApplyStage(c);
                        _ip++;
                        break;

                    case "load":
                        // The stage restores a snapshot and resumes (ReplayVisuals +
                        // ContinueFrom), which runs its own Advance — so bail out of
                        // this one instead of falling through to _ip++.
                        _stage.ApplyStage(c);
                        return;

                    default:
                        // Host-registered custom ops (LvnOps) run the game's own
                        // C# — with flow control: Hold() pauses here, Resume()
                        // continues, GoTo() reroutes. Unregistered unknown ops
                        // keep flowing to the stage (which ignores them).
                        if (LvnOps.TryGet(curOp, out var custom))
                        {
                            var octx = new OpContext(this);
                            try { custom(c, octx); }
                            catch (System.Exception e) { Log?.Invoke("custom op '" + curOp + "' failed: " + e.Message); }
                            if (!octx.Jumped) _ip++;
                            if (octx.Held) { octx.Armed = true; return; }
                            break;
                        }
                        _stage.ApplyStage(c);
                        _ip++;
                        break;
                }
            }
            if (!Finished)
                Finish();
        }

        // The per-invocation flow context handed to a custom op handler.
        private sealed class OpContext : ILvnOpContext
        {
            private readonly LvnPlayer _p;
            public bool Held { get; private set; }
            public bool Jumped { get; private set; }
            public bool Armed; // the outer Advance returned while held — Resume restarts it

            public OpContext(LvnPlayer p) => _p = p;

            public System.Collections.Generic.IDictionary<string, JToken> Vars => _p.Vars;
            public ILvnStage Stage => _p._stage;

            public void GoTo(string label)
            {
                _p.GoTo(label);
                Jumped = true;
            }

            public void Hold() => Held = true;

            public void Resume()
            {
                if (!Held) return;
                Held = false;
                if (Armed) { Armed = false; _p.Advance(); }
            }
        }

        /// <summary>True when the cursor is sitting on a choice command — the only
        /// time <see cref="Choose"/> is valid. Hosts check this before forwarding a
        /// click so a stale choice button (left over after a reload/load) is ignored
        /// rather than throwing.</summary>
        public bool AtChoice =>
            !Finished && _script != null && _ip >= 0 && _ip < _script.Count
            && _script[_ip] is JObject c && (string)c["op"] == "choice";

        /// <summary>The voice-over url of the line currently on screen (null for a
        /// silent line) — set just before <see cref="ILvnStage.ShowSay"/> fires, so
        /// the stage can start the clip with the text.</summary>
        public string CurrentVoiceUrl { get; private set; }

        /// <summary>The speaking character's ACTOR id for the line on screen, when
        /// the script mapped its display name to a different sprite id
        /// (<c>actor_map Ash=hill</c> → say carries <c>who_id:"hill"</c>). Null for
        /// unmapped speakers — the stage then matches by the loose name key.</summary>
        public string CurrentSpeakerId { get; private set; }

        /// <summary>Seconds the current choice gives the player before its
        /// timeout branch fires — 0 means untimed. Valid while <see cref="AtChoice"/>;
        /// the stage reads it to run the countdown UI.</summary>
        public float CurrentChoiceTimeout
        {
            get
            {
                if (!AtChoice) return 0f;
                var t = ((JObject)_script[_ip])["timeout"];
                if (t == null) return 0f;
                try { return (float)t; } catch { return 0f; }
            }
        }

        /// <summary>The countdown expired: jump to the choice's <c>timeout_goto</c>
        /// branch and continue. Returns false when not at a (timed) choice — a
        /// stale timer after a load/rollback is simply ignored.</summary>
        public bool ResolveChoiceTimeout()
        {
            if (!AtChoice) return false;
            var target = (string)((JObject)_script[_ip])["timeout_goto"];
            if (string.IsNullOrEmpty(target)) return false;
            Log?.Invoke("CHOICE TIMEOUT → :" + target);
            Jump(target);
            Advance();
            return true;
        }

        /// <summary>
        /// Resolve a picked option (by its <see cref="LvnOption.Index"/>). Sets
        /// up the next position; the caller then calls <see cref="Advance"/>.
        /// </summary>
        public void Choose(int optionIndex)
        {
            if (!AtChoice)
                throw new InvalidOperationException("Choose called when not at a choice");
            var c = (JObject)_script[_ip];

            // Degrade gracefully on a malformed choice (missing/typed-wrong options,
            // or an out-of-range index) — skip past it instead of aborting the whole
            // chapter with a cast/index exception. The validator flags these authoring.
            var opts = c["options"] as JArray;
            if (opts == null || optionIndex < 0 || optionIndex >= opts.Count) { _ip++; return; }
            var opt = opts[optionIndex] as JObject;
            if (opt == null) { _ip++; return; }
            Log?.Invoke("CHOOSE [" + optionIndex + "] \"" + (string)opt["text"] + "\"" + (opt["goto"] != null ? " → :" + opt["goto"] : ""));

            if (opt["body"] is JArray body)
            {
                foreach (var bt in body)
                {
                    if (!(bt is JObject bc)) continue; // malformed body element — skip, don't crash the choice
                    var bop = (string)bc["op"];
                    if (bop == "set" || bop == "inc") ApplyData(bc);
                    else if (bop == "goto") { Jump((string)bc["label"]); return; }
                    else _stage.ApplyStage(bc);
                }
                _ip++; // body without a goto → fall through past the choice
                return;
            }

            var target = (string)opt["goto"];
            if (target != null) Jump(target);
            else _ip++;
        }

        /// <summary>
        /// Jump to a label on demand — the hook for clickable hotspots and other
        /// out-of-band navigation. The caller then calls <see cref="Advance"/>.
        /// Re-activates a finished player so a hotspot on an end screen can drive
        /// flow again. This plus placeable, clickable objects is enough to build
        /// a button-driven game: each screen is a pause with its own hotspots.
        /// </summary>
        public void GoTo(string label)
        {
            Log?.Invoke("GoTo :" + label);
            Finished = false;
            SeekTo(label);
        }

        // ── internals ────────────────────────────────────────────────────────

        // A short human suffix for the per-command trace (id/label/key).
        private static string DescribeCmd(JObject c)
        {
            var id = (string)c["id"]; if (id != null) return " id=" + id + (c["on_click"] != null ? " on_click=" + c["on_click"] : "");
            var lbl = (string)c["label"]; if (lbl != null) return " :" + lbl;
            var key = (string)c["key"]; if (key != null) return " " + key;
            return "";
        }

        private void Jump(string label) => SeekTo(label);

        private void SeekTo(string label)
        {
            if (string.IsNullOrEmpty(label) || label == "__end")
            {
                Finish();
                return;
            }
            if (_labels.TryGetValue(label, out var i)) { _ip = i; }
            else { Log?.Invoke("  !! unknown label :" + label + " → end"); Finish(); } // validator catches these pre-ship
        }

        private void Finish()
        {
            Log?.Invoke("FINISHED @#" + _ip);
            Finished = true;
            _stage.OnEnd();
        }

        private List<LvnOption> BuildOptions(JObject choice)
        {
            var result = new List<LvnOption>();
            var opts = choice["options"] as JArray;
            if (opts == null) return result; // malformed choice → no options (validator flags this)
            for (int i = 0; i < opts.Count; i++)
            {
                var o = opts[i] as JObject;
                if (o == null) continue;

                var requires = (string)o["requires_stat"];
                if (requires != null && VarNum(requires) < Num(o["min"], 0))
                    continue;

                var expr = (string)o["expr"];
                if (expr != null && !EvalExpr(expr))
                    continue;

                // {expr} interpolation so option text/cost track variables too
                // (e.g. "Атаковать ({wname})", "Купить (-{price} зол)").
                var optText = TextInterpolation.Apply(Localized(o), Vars);
                var optCost = TextInterpolation.Apply((string)o["cost"], Vars);
                result.Add(new LvnOption(i, optText, optCost));
            }
            return result;
        }

        private bool EvalCond(JObject c)
        {
            var expr = (string)c["expr"];
            if (expr != null)
                return EvalExpr(expr);

            if (!(c["cond"] is JObject cond))
                return false;

            var key = (string)cond["key"];
            var left = key != null && Vars.TryGetValue(key, out var lv) ? lv : null;
            var right = cond["value"];
            switch ((string)cond["op"])
            {
                // eq/ne compare by value (strings & bools too, with ink "unset == 0/
                // false/'' " semantics), not just numerically.
                case "eq": return JEq(left, right);
                case "ne": return !JEq(left, right);
                case "lt": return Num(left, 0) < Num(right, 0);
                case "lte": return Num(left, 0) <= Num(right, 0);
                case "gt": return Num(left, 0) > Num(right, 0);
                case "gte": return Num(left, 0) >= Num(right, 0);
                default: return Num(left, 0) != 0;
            }
        }

        // Value equality with ink-style defaulting: an unset (null) variable equals
        // 0 / false / "" so first-visit gates hold before anything sets them.
        private static bool JEq(JToken a, JToken b)
        {
            bool an = a == null || a.Type == JTokenType.Null;
            bool bn = b == null || b.Type == JTokenType.Null;
            if (an || bn)
            {
                var o = an ? b : a;
                if (o == null || o.Type == JTokenType.Null) return true;
                switch (o.Type)
                {
                    case JTokenType.Integer:
                    case JTokenType.Float: return o.Value<double>() == 0;
                    case JTokenType.Boolean: return o.Value<bool>() == false;
                    case JTokenType.String: return string.IsNullOrEmpty((string)o);
                    default: return false;
                }
            }
            if (a.Type == JTokenType.String || b.Type == JTokenType.String)
                return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
            return Num(a, 0) == Num(b, 0);
        }

        private void ApplyData(JObject c)
        {
            var key = (string)c["key"];
            if (string.IsNullOrEmpty(key)) return;
            // `default:true` = initialise-only. A global-variable default must not
            // overwrite a value carried in from an earlier chapter or a loaded save,
            // so skip it when the key already holds a value.
            if (c["default"] != null && BoolOr(c["default"], false) && GetVarPath(key) != null)
                return;
            if ((string)c["op"] == "inc")
            {
                SetVarPath(key, new JValue(VarNum(key) + Num(c["by"], 1)));
                return;
            }
            // set: a computed `expr` (mirrors `if expr`) takes priority over a
            // literal `value`, so `set key="score" expr="courage + bonus*2"` works.
            var exprTok = c["expr"];
            if (exprTok != null && exprTok.Type == JTokenType.String)
            {
                // A malformed set-expression must not crash the novel; fall back to
                // the literal value (or leave the variable untouched).
                try { SetVarPath(key, LvnExpression.Evaluate((string)exprTok, Vars)); }
                catch (LvnException) { if (c["value"] != null) SetVarPath(key, c["value"]); }
            }
            else
                SetVarPath(key, c["value"] ?? JValue.CreateNull());
        }

        private double VarNum(string key) => Num(GetVarPath(key), 0);

        // Read a possibly-dotted variable path ("global.rep") — navigates nested
        // JObjects, mirroring the expression evaluator's member access so `set`/
        // `inc key="a.b"` and `if a.b` / `{a.b}` refer to the SAME value. A plain
        // key is a direct Vars lookup; a missing segment reads as null.
        private JToken GetVarPath(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            int dot = key.IndexOf('.');
            if (dot < 0) return Vars.TryGetValue(key, out var flat) ? flat : null;
            if (!Vars.TryGetValue(key.Substring(0, dot), out var rootTok) || !(rootTok is JObject node))
                return null;
            JToken cur = node;
            foreach (var seg in key.Substring(dot + 1).Split('.'))
            {
                if (!(cur is JObject o)) return null;
                cur = o[seg];
                if (cur == null) return null;
            }
            return cur;
        }

        /// <summary>Set a story variable from host code exactly as the `set` op does
        /// (dotted paths nest). Used by the in-story wardrobe to write the player's
        /// pick back into the novel's state so downstream logic reads it.</summary>
        public void SetVar(string key, JToken value) => SetVarPath(key, value);

        // Write a possibly-dotted variable path, creating intermediate JObjects.
        // A plain key writes Vars directly (unchanged behaviour); "a.b.c" nests
        // under the root object `a`, so `global.*` all live in one `global` object
        // the state store persists as a unit (per-player, cross-novel).
        private void SetVarPath(string key, JToken value)
        {
            value ??= JValue.CreateNull();
            int dot = key.IndexOf('.');
            if (dot < 0) { Vars[key] = value; return; }
            var root = key.Substring(0, dot);
            var node = Vars.TryGetValue(root, out var t) && t is JObject o ? o : new JObject();
            var segs = key.Substring(dot + 1).Split('.');
            var cur = node;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                if (!(cur[segs[i]] is JObject next)) { next = new JObject(); cur[segs[i]] = next; }
                cur = next;
            }
            cur[segs[segs.Length - 1]] = value;
            Vars[root] = node;
        }

        // Tolerant boolean read: malformed content (a string "да", null) degrades to
        // the default instead of throwing out of Advance and killing the chapter.
        private static bool BoolOr(JToken t, bool def)
        {
            if (t == null) return def;
            if (t.Type == JTokenType.Boolean) return t.Value<bool>();
            try { return t.Value<bool>(); } catch { return def; }
        }

        private static double Num(JToken t, double def)
        {
            if (t == null) return def;
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return t.Value<double>();
                case JTokenType.Boolean:
                    return t.Value<bool>() ? 1 : 0;
                default:
                    return def;
            }
        }
    }
}
