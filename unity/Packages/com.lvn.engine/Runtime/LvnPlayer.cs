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
    public sealed partial class LvnPlayer
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
            "ui", "cutscene",
            "hint", "save", "clear", "fx", "sfx", "portal",
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
        // КОГДА ПОКАЗАЛИ ВЫБОР — отметка ДОМАШНИХ часов, а не DateTime.
        //
        // Мерилось это `DateTime.UtcNow`, мимо `LvnClock`, и потому не
        // проверялось ничем: подменить часы тест может, системное время — нет.
        // Число уезжает в телеметрию как «сколько игрок думал», и до сих пор
        // никто не мог сказать, верное ли оно.
        //
        // Взято КАДРОВОЕ время (LvnClock.Now), а не настенное, по той же
        // причине, что и у автопродвижения: свернул игру на десять минут —
        // это не «думал десять минут». Прежнее поведение считало именно так.
        //
        // −1 значит «выбор не показывали»: ноль — законная отметка сразу после
        // запуска, и по нему «не показывали» от «показали на первом кадре» не
        // отличить.
        private float _choiceShownAt = -1f;

        /// <summary>Optional localization catalog: <c>text_id</c> → string for the
        /// active language. When a say/choice carries a <c>text_id</c> (instead of
        /// inline <c>text</c>), it is resolved here. Swap this to switch language;
        /// the <c>.lvn</c> structure is language-independent.</summary>
        public IReadOnlyDictionary<string, string> Strings;

        // Resolve a line's text in the active language. Two keying schemes share
        // one catalog: an explicit "text_id" (stable id, e.g. an articy GUID), or —
        // for inline-authored lines — the source string itself as the key
        // (gettext/Ren'Py style). Missing translation falls back to the source.
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

        // ПРОГРЕСС СЧИТАЕТСЯ ПО КРАТЧАЙШЕМУ ПУТИ ДО КОНЦА, а не по номеру
        // команды в файле. Номер врал дважды. Во-первых, импорт линеаризует
        // ветки: тела выборов лежат в ХВОСТЕ файла, и в живой главе Time
        // Romance спина кончается на 80% файла — пройдя главу целиком, игрок
        // видел 80% и рывок на 100%. Во-вторых, любая петля или возврат в хаб
        // двигали курсор назад, и прежняя защита (высшая отметка плюс учёт
        // «отлучек») откатывала полосу на дальнем прыжке назад.
        //
        // Теперь доля пройденного = 1 − остаток/полный_остаток, где остаток —
        // число шагов до конца по кратчайшему маршруту (см. LvnFlowDistance).
        // В начале ноль, в конце РОВНО сто любым маршрутом, а длинная ветка
        // просто отдаёт проценты медленнее — до конца по ней и правда дальше.
        private int[] _toEnd;
        private int _toEndStart;    // кратчайший путь от начала главы (для сида резюма)
        private int _progressSeen;  // ОТКАТОВ НЕТ: показанное только растёт (промилле)
        private int _stepsDone;     // команд реального маршрута пройдено
        private bool _stepsSeeded;  // резюм: первый счёт сеет пройденное по BFS

        // Промилле-шкала: доля = пройдено / (пройдено + осталось-до-конца).
        // Пройдено — реальные шаги ЭТОГО маршрута, осталось — BFS до конца
        // (LvnFlowDistance). Растёт на КАЖДОМ шаге: длинная ветка отдаёт
        // проценты медленнее, но не замирает (живой случай: «дальше 80 не
        // проходит» — прежняя формула прибивала полосу к кратчайшему пути и
        // в длинной ветке вставала намертво до слияния с магистралью).
        private const int ProgressScale = 1000;

        /// <summary>Промилле прогресса (0..<see cref="ProgressTotal"/>).
        /// Пара для полосы — вместе с <see cref="ProgressTotal"/>.</summary>
        public int ProgressIndex
        {
            get
            {
                EnsureDistances();
                // Глава кончилась — значит пройдена целиком, чем бы ни был
                // занят курсор. Иначе последняя команда (например `goto __end`)
                // оставляла бы читателя на 90% у финальной реплики.
                if (Finished) return _progressSeen = ProgressScale;
                int left = _ip >= 0 && _ip < _toEnd.Length ? _toEnd[_ip] : 0;
                if (left == int.MaxValue) return _progressSeen; // мёртвый код — стоим
                if (!_stepsSeeded)
                {
                    // Резюм/горячая замена: счётчик шагов не переживает сейв —
                    // сеем его оценкой BFS, чтобы полоса встала на честное
                    // место сразу, а не на ноль.
                    if (_toEndStart - left > _stepsDone) _stepsDone = _toEndStart - left;
                    _stepsSeeded = true;
                }
                int pm = (int)System.Math.Round(
                    ProgressScale * (double)_stepsDone / System.Math.Max(1, _stepsDone + left));
                // Сто — только финал: длинный маршрут не смеет доползти до
                // 100% раньше последней команды.
                if (pm > ProgressScale - 10) pm = ProgressScale - 10;
                if (pm > _progressSeen) _progressSeen = pm;
                return _progressSeen;
            }
        }

        /// <summary>Знаменатель полосы (промилле-шкала).</summary>
        public int ProgressTotal
        {
            get { return ProgressScale; }
        }

        private void EnsureDistances()
        {
            if (_toEnd != null) return;
            _toEnd = LvnFlowDistance.ToEnd(_script, _labels);
            int start = _toEnd.Length > 0 ? _toEnd[0] : 0;
            _toEndStart = start == int.MaxValue || start <= 0 ? System.Math.Max(1, _toEnd.Length) : start;
        }

        public LvnPlayer(LvnDocument doc, ILvnStage stage)
        {
            _script = doc.Script;
            _scene = doc.Scene; // only ever read back in a diagnostic message
            _stage = stage;
            // Сцена получает доступ к истории СРАЗУ: оператору `ui` нужны живые
            // значения переменных и прыжок по нажатию, а искать их потом по
            // хостам значило бы требовать настройки от каждого, кто встраивает
            // движок.
            _stage?.BindStory(() => Vars, GoTo);
            for (int i = 0; i < _script.Count; i++)
            {
                if (_script[i] is JObject c && (string)c["op"] == "label")
                {
                    var id = (string)c["id"];
                    if (!string.IsNullOrEmpty(id))
                        _labels[id] = i;
                }
            }
            BuildAnchorMap();
        }

        /// <summary>Restore a saved position and state (for autosave/resume).</summary>
        public void Restore(int index, IDictionary<string, JToken> vars, IEnumerable<int> callStack)
        {
            _ip = index;
            _progressSeen = 0;    // возобновление: полоса берётся от места, где встали
            _stepsDone = 0;
            _stepsSeeded = false; // первый счёт после резюма посеет шаги по BFS
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

        public IEnumerable<JObject> PeekForward(int maxCommands)
        {
            if (_script == null) yield break;
            int end = System.Math.Min(_ip + maxCommands, _script.Count);
            for (int i = System.Math.Max(_ip, 0); i < end; i++)
                if (_script[i] is JObject c)
                    yield return c;
        }

        private void StageApply(JObject cmd)
        {
            ResolveAnimTargets(cmd);
            // ПОДПИСЬ ИСТОРИИ. Реплей после загрузки шлёт те же авторские
            // команды, но игрок их не видел — Помрежу это различие нужно, чтобы
            // объяснять поток в журнале; старшинство у них одно.
            _stage.ApplyStage(cmd, _replaying ? LvnSender.Replay : LvnSender.Story);
        }

        /// <summary>Идёт ли восстановление кадра после загрузки.</summary>
        private bool _replaying;

        // Последняя посчитанная цель по каналу анимации: id + слой + свойство.
        private readonly Dictionary<string, double> _animLast = new Dictionary<string, double>();

        /// <summary>
        /// Досчитывает <c>anim … to="{выражение}"</c> — цель, известную только
        /// во время игры (доля здоровья, счёт, прогресс).
        ///
        /// <para>Считается ЗДЕСЬ, а не на сцене: переменные живут у игрока, и
        /// сцена о них не знает — это и держит её пригодной для реплея, где
        /// никаких переменных нет.</para>
        ///
        /// <para>Начало берётся не из кадра, а из ПРЕДЫДУЩЕЙ посчитанной цели
        /// того же канала. Иначе полоса здоровья каждый раз прыгала бы в ноль
        /// и оттуда наполнялась: у вычисляемой цели «покой» — это не ноль, а
        /// то место, где полоса стоит сейчас.</para>
        /// </summary>
        private void ResolveAnimTargets(JObject cmd)
        {
            // Через шлюз идёт ЛЮБАЯ команда, а не только анимация: у неё поле
            // "anim" может быть чем угодно или отсутствовать. Проверка типа, а
            // не просто «не null»: обращение к полю у скалярного значения в
            // Newtonsoft бросает исключение, и страж контракта опов поймал
            // именно это.
            var payload = cmd?["anim"] as JObject;
            var tracks = payload?["tracks"] as JArray;
            if (tracks == null) return;
            string id = (string)cmd["id"] ?? "";
            foreach (var t in tracks)
            {
                var expr = (string)t["to_expr"];
                if (string.IsNullOrEmpty(expr)) continue;
                var keys = t["keys"] as JArray;
                if (keys == null || keys.Count == 0) continue;

                double target;
                try
                {
                    var v = LvnExpression.Evaluate(expr, Vars);
                    target = v == null ? 0d : v.Value<double>();
                }
                catch { continue; }   // сломанное выражение не должно ронять сцену

                string lane = id + "|" + (string)t["layer"] + "|" + (string)t["prop"];
                var k0 = keys[0] as JArray;
                double from = _animLast.TryGetValue(lane, out var prev)
                    ? prev
                    : (k0 != null && k0.Count > 1 ? k0[1].Value<double>() : 0d);

                if (k0 != null && k0.Count > 1) k0[1] = from;
                var kn = keys[keys.Count - 1] as JArray;
                if (kn != null && kn.Count > 1) kn[1] = target;
                _animLast[lane] = target;
            }
        }

        private static bool IsReapplyable(string op) =>
            // `ui` belongs here for the same reason `text` does: a tree declared
            // before the save has to be back on screen after the load, and the
            // path order already gives hide/show/drop their meaning.
            op == "bg" || op == "obj" || op == "anim" || op == "text" || op == "ui"; // actor collapses per id (see ReplayVisuals)

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
                if (IsLegacyHintSpeaker(who))
                {
                    StageApply(LegacyHintCommand(text));
                    return;
                }
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
                if (--budget < 0)
                    throw new LvnException("possible infinite loop: a goto cycle has no say/choice between jumps");
                // Malformed content must never crash the runtime: a non-object
                // command (bad export/hand-edited JSON) is skipped, not cast-thrown.
                if (!(_script[_ip] is JObject c)) { _ip++; continue; }
                _stepsDone++; // реальная длина ЭТОГО маршрута — числитель полосы
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
                        {
                            var built = BuildOptions(c);
                            // В сценарии вариантов может быть больше, чем игрок
                            // увидит: закрытые гейтом до показа не доходят
                            // вовсе. Разница «написано три, доступен один» и
                            // есть ощущение развилки — считаем оба числа.
                            int written = (c["options"] as JArray)?.Count ?? 0;

                            // НИ ОДНОГО ВАРИАНТА — НЕ ВСТАЁМ.
                            //
                            // Если все варианты закрыты порогом стата или
                            // условием, показывать нечего, и прежний код всё
                            // равно показывал: пустую стопку и ожидание выбора,
                            // которого игрок сделать не может. Замер 05.09:
                            // «choice:0», AtChoice=true, и каждый следующий тап
                            // снова «choice:0» — глава стояла навсегда, выход
                            // только через меню.
                            //
                            // Идём дальше по скрипту: это единственный шаг, не
                            // теряющий игрока и не требующий от автора нового
                            // синтаксиса. Автор узнаёт об этом строкой в
                            // журнале и числом в телеметрии (написано N,
                            // показано 0), а инструмент предупреждает ещё на
                            // публикации (lvnconv validate).
                            if (built.Count == 0)
                            {
                                try { ChoiceShown?.Invoke(written, 0, _ip); }
                                catch { /* телеметрия не смеет ронять главу */ }
                                Log?.Invoke($"    [выбор] ни один из {written} вариантов не прошёл условие — иду дальше");
                                Warn?.Invoke($"[lvn-player] выбор на шаге {_ip}: ни один из {written} "
                                           + "вариантов не доступен при текущих статах — играть было бы нечем, "
                                           + "продолжаю со следующей команды");
                                _ip++;
                                break;
                            }

                            // A choice directly after a say is the same beat (the
                            // line and its options show together) — the say
                            // already pushed.
                            bool paired = _ip > 0 && _script[_ip - 1] is JObject prevCmd
                                          && (string)prevCmd["op"] == "say"
                                          && !IsLegacyHintSpeaker(TextInterpolation.Apply(
                                              LocalizedWho((string)prevCmd["who"]), Vars));
                            if (!paired) PushHistory();
                            _stage.ShowChoice(built);
                            _choiceShownAt = LvnClock.Now();
                            try { ChoiceShown?.Invoke(written, built.Count, _ip); }
                            catch { /* телеметрия не смеет ронять главу */ }
                        }
                        return;

                    case "say":
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
                        if (IsLegacyHintSpeaker(sayWho))
                        {
                            StageApply(LegacyHintCommand(sayText));
                            _ip++;
                            break; // a real hint floats over the next playable beat
                        }
                        PushHistory();
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
                        StageApply(c);
                        _ip++;
                        return;

                    case "input":
                        // The stage shows a text-entry overlay; the story pauses
                        // here until the host writes the variable and re-Advances.
                        StageApply(c);
                        _ip++;
                        return;

                    case "preload":
                        StageApply(c);
                        _ip++;
                        break;

                    case "load":
                        // The stage restores a snapshot and resumes (ReplayVisuals +
                        // ContinueFrom), which runs its own Advance — so bail out of
                        // this one instead of falling through to _ip++.
                        StageApply(c);
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
                        StageApply(c);
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
                var thought = _choiceShownAt < 0f ? 0f : LvnClock.Since(_choiceShownAt);
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
                    else StageApply(bc);
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
        /// <summary>
        /// ПУТЬ ПЕРЕМЕННОЙ — ОДИН ОБХОД НА ЧЕТЫРЕ РАБОТЫ.
        ///
        /// <para>«Есть ли», «взять», «убрать», «записать» ходили по <c>a.b.c</c>
        /// каждый своим кодом — четыре копии одного обхода. Копии уже успели
        /// разойтись в мелочи, которая стоит главы: пустой ключ терпело только
        /// «взять», остальные три на нём падали исключением ПРЯМО ИЗ ШАГА
        /// истории. Путь этот АВТОРСКИЙ (<c>global.stats.wins</c>), и
        /// расхождение здесь значит «записал, а „есть ли“ не видит».</para>
        ///
        /// <para>Возвращает держателя последнего звена и его имя.
        /// <c>holder == null</c> — ключ плоский, держатель сам словарь
        /// переменных. <paramref name="create"/> заводит недостающие звенья по
        /// дороге; без него отсутствующее звено — это «нет пути».</para>
        /// </summary>
        private bool WalkVarPath(string key, bool create, out JObject holder, out string leaf)
        {
            holder = null;
            leaf = null;
            if (string.IsNullOrEmpty(key)) return false;

            int dot = key.IndexOf('.');
            if (dot < 0) { leaf = key; return true; }   // плоский ключ: держатель — Vars

            var root = key.Substring(0, dot);
            JObject node;
            if (Vars.TryGetValue(root, out var t) && t is JObject o) node = o;
            else if (create) node = new JObject();
            else return false;

            var segs = key.Substring(dot + 1).Split('.');
            var cur = node;
            for (int i = 0; i < segs.Length - 1; i++)
            {
                if (cur[segs[i]] is JObject next) cur = next;
                else if (create) { var made = new JObject(); cur[segs[i]] = made; cur = made; }
                else return false;
            }
            if (create) Vars[root] = node;   // корень мог быть заведён только что
            holder = cur;
            leaf = segs[segs.Length - 1];
            return true;
        }

        private bool HasVarPath(string key)
            => WalkVarPath(key, false, out var holder, out var leaf)
               && (holder == null ? Vars.ContainsKey(leaf) : holder[leaf] != null);

        private void RemoveVarPath(string key)
        {
            if (!WalkVarPath(key, false, out var holder, out var leaf)) return;
            if (holder == null) Vars.Remove(leaf);
            else holder.Remove(leaf);
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
                // Полосе прогресса прыжок больше не интересен: расстояние до
                // конца считается от места, где мы оказались.
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
            if (!WalkVarPath(key, false, out var holder, out var leaf)) return null;
            if (holder != null) return holder[leaf];
            return Vars.TryGetValue(leaf, out var flat) ? flat : null;
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
            if (!WalkVarPath(key, true, out var holder, out var leaf)) return;
            if (holder == null) Vars[leaf] = value;
            else holder[leaf] = value;
        }

        // Tolerant boolean read: malformed content (a string "да", null) degrades to
        // the default instead of throwing out of Advance and killing the chapter.
        private static bool BoolOr(JToken t, bool def) => Lvn.LvnBool.Of(t, def);

        // Число из значения состояния — у Lvn.LvnNum. Здесь строка не
        // разбиралась вовсе, поэтому inc над введённым игроком «10» давал 1,
        // стирая значение: ввод сохраняется СТРОКОЙ (VnStage.Input).
        private static double Num(JToken t, double def) => LvnNum.Value(t, def);
    }
}
