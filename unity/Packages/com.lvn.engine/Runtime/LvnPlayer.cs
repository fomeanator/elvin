using System;
using System.Collections.Generic;
using System.Text;
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
        private readonly string _scene; // chapter name, for diagnostics only
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

        // ── unclaimed-op diagnostics ──────────────────────────────────────────
        // "Unknown is an error, never a silent skip" is a COMPILE-time rule (the
        // README says so in the same breath: "content bugs surface at compile
        // time"). lvnconv rejects a typo'd op, ext-grammar.json declares the
        // host's own, and /conformance/ops-owners.json pins which package owns
        // which. The runtime deliberately does NOT re-litigate that verdict: a
        // command nobody handles keeps flowing to the stage and the story plays
        // on. Throwing here would be worse than the bug — Advance() is called
        // from tap / auto-advance / choice / drag handlers, so an exception is a
        // permanent soft-lock in a player's hands, and a shell-owned op on a bare
        // com.lvn.engine install is a DECLARED, allowed gap, not a defect.
        //
        // What it must not be is INVISIBLE. So: report it, once, with enough
        // context to act on, and keep playing.

        /// <summary>Where the once-per-op unclaimed-op diagnostic goes. Defaults
        /// to the Unity console (stderr outside Unity); a host may redirect it to
        /// its own telemetry, or null it to go silent. Unlike <see cref="Log"/>
        /// (a verbose opt-in trace) this one is ON by default — the whole point
        /// is that an ignored command cannot pass unnoticed.</summary>
        public static Action<string> Warn =
#if UNITY_5_3_OR_NEWER
            message => UnityEngine.Debug.LogWarning(message);
#else
            message => Console.Error.WriteLine(message);
#endif

        // The ops the ENGINE ITSELF forwards to ILvnStage — exactly the rows with
        // owner=engine, csharp=stage in /conformance/ops-owners.json. Anything
        // else that reaches `default:` without an LvnOps handler is unclaimed:
        // nobody in this build will act on it.
        //
        // Deliberately a private literal rather than StagingOps.Known, which is a
        // DIFFERENT set (it also lists flow ops the player consumes itself) and
        // is public API other packages read. OpDispatchContractTests walks the
        // ownership table and goes red if this list falls behind it, so the
        // duplication cannot rot.
        private static readonly HashSet<string> _engineStageOps = new HashSet<string>(StringComparer.Ordinal)
        {
            "bg", "bg3d", "actor", "obj", "text", "audio", "fade", "dim", "tint",
            "flash", "blur", "camera", "particles", "anim", "text_pace",
            "hint", "save", "clear", "fx", "sfx",
        };

        // op name → how many commands went unclaimed. Static on purpose: the
        // budget is one line per op for the whole SESSION, not one per chapter,
        // per replay or (heaven forbid) per command. A key being present is also
        // the "already reported" flag, so one dictionary does both jobs.
        private static readonly Dictionary<string, int> _unclaimed =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Ops that reached the stage with nobody to handle them this
        /// session → how many commands each. For a host boot report / debug
        /// overlay: an EMPTY bag is the assertion worth making.
        ///
        /// <para>Ops that have since gained an <see cref="LvnOps"/> handler are
        /// filtered out. That covers the registration race — a host that calls
        /// <c>LvnOps.Register</c> from its own <c>MonoBehaviour.Start</c> can lose
        /// it to the runner's <c>Start</c> (Unity does not order the two), and the
        /// first commands would otherwise leave a permanent false entry in the
        /// report for an op that is, in fact, handled.</para></summary>
        public static IReadOnlyDictionary<string, int> UnclaimedOps
        {
            get
            {
                var live = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var kv in _unclaimed)
                    if (!LvnOps.TryGet(kv.Key, out _)) live[kv.Key] = kv.Value;
                return live;
            }
        }

        /// <summary>Forget every op reported so far, so the next unclaimed one is
        /// reported afresh — tests, and a host that wants a per-session report.</summary>
        public static void ResetOpDiagnostics() => _unclaimed.Clear();

        /// <summary>
        /// Игрок дошёл до авторской метки. Метка — это «слайд» в терминах
        /// автора, и по ней строится воронка ВНУТРИ главы: между входом и
        /// выходом раньше было пусто, и «на каком месте отваливаются» ответить
        /// было нечем.
        ///
        /// <para>Служебные метки компилятора (<c>__nf_</c>, <c>__then_</c>,
        /// <c>__end</c> и прочие) сюда НЕ попадают: они разметка ветвей, а не
        /// места в истории, и утроили бы поток событий без единого нового
        /// факта.</para>
        /// </summary>
        /// <para>Второй аргумент — индекс команды в скомпилированной главе.
        /// По нему отчёт кладёт метку на ту же ось, что и точки выхода, и
        /// восстанавливает кадр; без него «метка» и «место, где бросили» —
        /// два несводимых списка.</para>
        public static event Action<string, int> LabelReached;

        /// <summary>Выбор показан: сколько вариантов НАПИСАНО в сценарии и
        /// сколько игрок реально видит. Закрытые гейтом до показа не доходят, и
        /// разница «написано три, доступен один» — это и есть ощущение
        /// развилки; по ней же видно, не заперт ли выбор наглухо.</summary>
        /// <para>Третий аргумент — индекс команды выбора: сам по себе выбор
        /// безымянен, и без адреса его некуда положить в воронке главы.
        /// Метку и тексты вариантов сервер достаёт из скрипта сам — он у него
        /// есть, а доверять клиенту в том, что можно прочитать у себя, незачем.</para>
        public static event Action<int, int, int> ChoiceShown;

        /// <summary>Игрок выбрал: индекс, текст варианта и сколько секунд
        /// думал. Время — единственный способ отличить «жал не глядя» от
        /// «сомневался», а именно на сомнении и уходят.</summary>
        /// <para>Последний аргумент — индекс команды выбора, тот же, что у
        /// <see cref="ChoiceShown"/>: только он связывает «показали» и
        /// «выбрали» в одну развилку.</para>
        public static event Action<int, string, float, int> ChoicePicked;

        // Когда показали текущий выбор — чтобы измерить раздумье.
        private DateTime _choiceShownAt;

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
                return Lookup(inline) ?? inline;
            var id = (string)c["text_id"];
            if (id == null) return "";
            return Lookup(id) ?? id;
        }

        // Speaker display names resolve through the same catalog (keyed by the
        // source name), so a translated cast renders without touching the script.
        private string LocalizedWho(string who)
            => who == null ? null : Lookup(who) ?? who;

        /// <summary>
        /// Поиск строки в каталоге, устойчивый к форме записи юникода.
        ///
        /// <para>«Ё» и любая буква с диакритикой существуют в двух видах: одним
        /// символом (NFC) и буквой с комбинирующим знаком (NFD). macOS отдаёт
        /// имена и содержимое в NFD, редакторы и веб-формы — по-разному, и
        /// каталог, собранный из одной формы, молча не находит строку в другой.
        /// Цена промаха несоразмерна причине: реплика остаётся непереведённой
        /// без единого сообщения, и ищут это в переводе, а не в кодировке.</para>
        ///
        /// <para>Сначала точное совпадение (это горячий путь, лишней работы в
        /// нём нет), и только при промахе — попытка нормализованным ключом.</para>
        /// </summary>
        private string Lookup(string key)
        {
            if (Strings == null || key == null) return null;
            if (Strings.TryGetValue(key, out var hit)) return hit;
            var nfc = key.Normalize(NormalizationForm.FormC);
            if (!string.Equals(nfc, key, StringComparison.Ordinal)
                && Strings.TryGetValue(nfc, out var normalized)) return normalized;
            return null;
        }

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

        // Linearized imports append choice BODIES at the file tail (a pick jumps
        // ~to the end, plays the branch, jumps back to the spine). Position there
        // says nothing about story progress, so a far forward jump marks the
        // cursor DISPLACED (bar frozen at the spine mark) until the matching far
        // return. The far return also clamps the mark down to its landing —
        // healing a snapshot that was restored INSIDE a body (its index latched
        // the mark at ~99%).
        private int _displaced;
        private int FarJump => System.Math.Max(64, _script.Count / 10);

        /// <summary>Chapter progress index (0..<see cref="Count"/>) for the HUD
        /// percent: the high-water mark of the cursor along the MAIN line. Climbs
        /// while flow is linear, holds through calls and far-displaced choice
        /// bodies, and a far return jump may clamp it down to its landing (that's
        /// the heal for a save restored inside a linearized tail body). Pair with
        /// <see cref="Count"/> exactly like <see cref="Index"/>.</summary>
        public int ProgressIndex => System.Math.Min(_progressMax, _script.Count);

        public LvnPlayer(LvnDocument doc, ILvnStage stage)
        {
            _script = doc.Script;
            _scene = doc.Scene; // only ever read back in a diagnostic message
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
            _displaced = 0;       // (a body-resumed index self-heals on its far return)
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
            // The truthful path: the ops the player ACTUALLY executed (recorded
            // by Advance, restored from the snapshot). Old saves / edited
            // scripts fall back to the linear prefix — branch-blind, but the
            // corrected merge semantics below still apply.
            IReadOnlyList<int> path;
            if (_trace != null && _trace.Count > 0) path = _trace;
            else
            {
                int end = System.Math.Min(upto, _script.Count);
                var lin = new List<int>(end);
                for (int i = 0; i < end; i++) lin.Add(i);
                path = lin;
            }
            ReplayPath(path);
        }

        // Placement fields are STICKY live (the sticky merge in VnStage keeps an
        // actor where the last positioning op put her) — so a rebuild accumulates
        // them across the path. Everything else (axes, transitions, gestures) is
        // per-op live and must come from the LAST op only.
        private static readonly HashSet<string> StickyActorFields = new HashSet<string>
        {
            "position", "x", "y", "width", "height", "scale", "z",
            "flip", "mirror", "anchor", "opacity", "hover_opacity",
        };

        private void ReplayPath(IReadOnlyList<int> path)
        {
            // Three replay classes. Structural ops (bg/obj/anim/text) accumulate,
            // so they re-run in path order. FX/audio are stateful overlays where
            // only the LAST setting matters — they collapse to the final value per
            // state key and apply once at the end. ACTORS rebuild to their LIVE
            // final state: the LAST op's own fields (show semantics mirror the
            // stage: an op without `show` shows), sticky placement accumulated
            // across the path, transitions stripped (a rebuild snaps into place).
            var fx = new Dictionary<string, JObject>();
            var fxOrder = new List<string>();
            void SetFx(string key, JObject cmd)
            {
                if (!fx.ContainsKey(key)) fxOrder.Add(key);
                fx[key] = cmd;
            }

            // Pass 1: per actor — sticky placement accumulation + last position in path.
            var actorSticky = new Dictionary<string, JObject>();
            var actorLastPos = new Dictionary<string, int>();
            for (int pi = 0; pi < path.Count; pi++)
            {
                int i = path[pi];
                if (i < 0 || i >= _script.Count) continue;
                if (!(_script[i] is JObject c) || (string)c["op"] != "actor") continue;
                var aid = (string)c["id"];
                if (string.IsNullOrEmpty(aid)) continue;
                if (!actorSticky.TryGetValue(aid, out var st)) { st = new JObject(); actorSticky[aid] = st; }
                foreach (var prop in c.Properties())
                    if (StickyActorFields.Contains(prop.Name))
                        st[prop.Name] = prop.Value.DeepClone();
                actorLastPos[aid] = pi;
            }

            // Pass 2: replay inline, in path order. An actor fires exactly once —
            // at its LAST occurrence — and only if it ends VISIBLE by the live
            // rule (`show` absent = show; a re-issue after a hide shows again).
            for (int pi = 0; pi < path.Count; pi++)
            {
                int i = path[pi];
                if (i < 0 || i >= _script.Count) continue;
                if (!(_script[i] is JObject c)) continue;
                var op = (string)c["op"];
                if (op == "actor")
                {
                    var aid = (string)c["id"];
                    if (string.IsNullOrEmpty(aid) || actorLastPos[aid] != pi) continue;
                    if (!BoolOr(c["show"], true)) continue; // ends hidden — skip entirely
                    var m = (JObject)c.DeepClone();
                    m["show"] = true;
                    m.Remove("enter"); m.Remove("exit"); m.Remove("play"); // no transitions on a rebuild
                    foreach (var prop in actorSticky[aid].Properties())
                        if (m[prop.Name] == null)
                            m[prop.Name] = prop.Value.DeepClone();
                    _stage.ApplyStage(m);
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

        // Record an executed visual op into the replay path. Capped: a looping
        // script must not grow the trace (and every snapshot's copy) unbounded —
        // dropping the oldest half keeps the recent scene truthful.
        private void RecordTrace(int index)
        {
            _trace.Add(index);
            if (_trace.Count > 20000) _trace.RemoveRange(0, 10000);
        }

        // An op nobody claimed: no case in the switch above, no LvnOps handler,
        // and not one of the engine's own staging ops — so it is forwarded to a
        // stage that has no case for it and quietly does nothing.
        //
        // Counted every time, REPORTED once per op per session. That budget is
        // the whole design: `ext vibrate` inside a loop, or thirty chapters that
        // each carry a wardrobe_show, must cost one line — not one per command,
        // which would both drown the console and put string formatting into the
        // player's inner loop.
        private void NoteUnclaimed(string op)
        {
            if (_unclaimed.TryGetValue(op, out var seen)) { _unclaimed[op] = seen + 1; return; }
            _unclaimed[op] = 1;

            Log?.Invoke("    !! unclaimed op '" + op + "' — forwarded to a stage with no case for it");
            Warn?.Invoke(
                "[lvn] unclaimed op '" + op + "' at command #" + _ip +
                " of scene '" + (string.IsNullOrEmpty(_scene) ? "?" : _scene) + "': nothing in this build handles it, " +
                "so this command — and every later '" + op + "' — is IGNORED. The story keeps playing. " +
                "Reported once per op per session; see LvnPlayer.UnclaimedOps for the full count. Fix ONE of:\n" +
                "  1) the op belongs to a package this build did not install — conformance/ops-owners.json names its " +
                "owner (e.g. 'wardrobe_show' lives in com.lvn.engine.shell);\n" +
                "  2) the op is host-defined — call LvnOps.Register(\"" + op + "\", handler) in your game, and declare " +
                "it in ext-grammar.json so lvnconv validates its fields instead of warning 'unknown op';\n" +
                "  3) it IS registered, but too late — registration must happen before the first Advance(). Register " +
                "from [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)], as the " +
                "ExtensionPlugin sample does; a MonoBehaviour.Start races the runner's Start;\n" +
                "  4) it is simply a typo — lvnconv validate flags it as 'unknown op' at build time.");
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
                // (possibly late) labels, and inside a far-displaced choice body
                // it sits at the linearized tail; neither should move the bar.
                if (_callStack.Count == 0 && _displaced == 0 && _ip > _progressMax) _progressMax = _ip;
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
                        {
                            var labelId = (string)c["id"];
                            // «__» — служебная разметка компилятора, а не место
                            // в истории: считать её слайдом значит утопить
                            // воронку в шуме.
                            if (!string.IsNullOrEmpty(labelId) && !labelId.StartsWith("__"))
                            {
                                try { LabelReached?.Invoke(labelId, _ip); }
                                catch { /* телеметрия не смеет ронять главу */ }
                            }
                        }
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
                        // A MISSING branch falls through to the next command, as
                        // both the language reference and the cheatsheet promise
                        // ("if true — jump, otherwise fall through"). It used to go
                        // through SeekTo, where an empty label means __end — so a
                        // false `if` with no `else` silently ENDED THE CHAPTER.
                        // Our own compiler always emits `else`, which is why this
                        // never bit: it only reaches hand-written .lvn and other
                        // producers, and `.lvn` is advertised as a container any
                        // tool may write. The browser player already fell through.
                        if (string.IsNullOrEmpty(branch))
                        {
                            _ip++;
                            break;
                        }
                        SeekTo(branch);
                        break;

                    case "choice":
                        // A choice directly after a say is the same beat (the line
                        // and its options show together) — the say already pushed.
                        bool paired = _ip > 0 && _script[_ip - 1] is JObject prevCmd
                                      && (string)prevCmd["op"] == "say";
                        if (!paired) PushHistory();
                        {
                            var built = BuildOptions(c);
                            _stage.ShowChoice(built);
                            _choiceShownAt = DateTime.UtcNow;
                            // В сценарии вариантов может быть больше, чем игрок
                            // увидит: закрытые гейтом до показа не доходят
                            // вовсе. Разница «написано три, доступен один» и
                            // есть ощущение развилки — считаем оба числа.
                            int written = (c["options"] as JArray)?.Count ?? 0;
                            try { ChoiceShown?.Invoke(written, built?.Count ?? 0, _ip); }
                            catch { /* телеметрия не смеет ронять главу */ }
                        }
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
                        RecordTrace(_ip);
                        // One ordinal hash lookup on the hot path (every bg /
                        // actor / fade passes here); the miss branch — string
                        // building included — is cold and runs once per op.
                        // `?? ""` for the same reason LvnOps.TryGet has it: a
                        // command with no "op" at all reaches default: too, and
                        // malformed content must never crash the runtime.
                        var claimName = curOp ?? "";
                        if (!_engineStageOps.Contains(claimName)) NoteUnclaimed(claimName);
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
            try
            {
                var thought = _choiceShownAt == default ? 0f : (float)(DateTime.UtcNow - _choiceShownAt).TotalSeconds;
                ChoicePicked?.Invoke(optionIndex, (string)opt["text"], thought, _ip);
            }
            catch { /* телеметрия не смеет ронять главу */ }

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

        /// <summary>
        /// Apply a title-level variable declaration block: each key is set ONLY
        /// when still unset (the `set default=true` semantics), so a player's
        /// progress and a resumed snapshot always win over the declaration.
        /// This replaces the per-chapter boilerplate of hundreds of default
        /// sets — declare once per game, apply on every chapter entry.
        /// </summary>
        public void ApplyDefaults(JObject defaults)
        {
            if (defaults == null) return;
            foreach (var p in defaults.Properties())
                if (!HasVarPath(p.Name))
                    SetVarPath(p.Name, p.Value.DeepClone());
        }

        /// <summary>Forget chapter-scoped variables (a fresh chapter entry):
        /// the keys reset to their declared defaults via <see cref="ApplyDefaults"/>
        /// right after — chapter-local state never leaks across chapters.</summary>
        public void ResetScope(IEnumerable<string> keys)
        {
            if (keys == null) return;
            foreach (var k in keys) RemoveVarPath(k);
        }

        // Dotted keys are NESTED paths (Way.Moral → Vars["Way"]["Moral"]) — the
        // declaration must probe and remove the same way SetVarPath writes, or
        // a default would stomp nested progress on every chapter entry.
        private bool HasVarPath(string key)
        {
            int dot = key.IndexOf('.');
            if (dot < 0) return Vars.ContainsKey(key);
            if (!Vars.TryGetValue(key.Substring(0, dot), out var t) || !(t is JObject cur))
                return false;
            var segs = key.Substring(dot + 1).Split('.');
            for (int i = 0; i < segs.Length - 1; i++)
            {
                if (!(cur[segs[i]] is JObject next)) return false;
                cur = next;
            }
            return cur[segs[segs.Length - 1]] != null;
        }

        private void RemoveVarPath(string key)
        {
            int dot = key.IndexOf('.');
            if (dot < 0) { Vars.Remove(key); return; }
            if (!Vars.TryGetValue(key.Substring(0, dot), out var t) || !(t is JObject cur)) return;
            var segs = key.Substring(dot + 1).Split('.');
            for (int i = 0; i < segs.Length - 1; i++)
            {
                if (!(cur[segs[i]] is JObject next)) return;
                cur = next;
            }
            cur.Remove(segs[segs.Length - 1]);
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
            if (_labels.TryGetValue(label, out var i))
            {
                // Displacement bookkeeping for the progress bar (see _displaced):
                // hops WITHIN the tail are small, so one far-forward marks the
                // excursion and one far-back closes it.
                int delta = i - _ip;
                if (delta > FarJump) _displaced++;
                else if (delta < -FarJump)
                {
                    if (_displaced > 0) _displaced--;
                    if (i < _progressMax) _progressMax = i; // heal a body-latched mark
                }
                _ip = i;
            }
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
                // The importer writes "requires_min" (see parseOptionTails); "min"
                // is accepted too for hand-authored .lvn that used the shorter name.
                if (requires != null && VarNum(requires) < Num(o["requires_min"] ?? o["min"], 0))
                    continue;

                var expr = (string)o["expr"];
                if (expr != null && !EvalExpr(expr))
                    continue;

                // {expr} interpolation so option text/cost track variables too
                // (e.g. "Атаковать ({wname})", "Купить (-{price} зол)").
                var optText = TextInterpolation.Apply(Localized(o), Vars);
                var optCost = TextInterpolation.Apply((string)o["cost"], Vars);
                // A REAL price (imported "[premium]" choices): the host must
                // clear a wallet spend before the pick goes through.
                string wCur = null; long wAmt = 0;
                if (o["wallet_cost"] is JObject w)
                {
                    wCur = (string)w["currency"];
                    wAmt = (long?)w["amount"] ?? 0;
                }
                List<LvnOptionEffect> effects = null;
                if (o["effects"] is JArray effArr)
                {
                    foreach (var e in effArr)
                    {
                        if (e is not JObject eo) continue;
                        var label = (string)eo["label"];
                        int delta = (int?)eo["delta"] ?? 0;
                        if (string.IsNullOrEmpty(label) || delta == 0) continue;
                        (effects ??= new List<LvnOptionEffect>()).Add(new LvnOptionEffect(label, delta));
                    }
                }
                result.Add(new LvnOption(i, optText, optCost, wCur, wAmt, effects));
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

        /// <summary>Read a story variable exactly as `{key}` interpolation / `if key`
        /// would (dotted paths navigate nested objects). The counterpart to
        /// <see cref="SetVar"/> — used by the in-story wardrobe to seed its "what's
        /// worn right now" check from the story's OWN current value, not just its
        /// own separate equip registry.</summary>
        public JToken GetVar(string key) => GetVarPath(key);

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
