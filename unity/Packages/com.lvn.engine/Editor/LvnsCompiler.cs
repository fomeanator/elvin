using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lvn.Editor
{
    /// <summary>Thrown when LVNScript source cannot be compiled.</summary>
    public class LvnsCompileException : Exception
    {
        public LvnsCompileException(string message) : base(message) { }
    }

    /// <summary>
    /// Compiles LVNScript (<c>.lvns</c>) source to the <c>.lvn</c> JSON container.
    ///
    /// This is a faithful port of the Go transcoder
    /// (<c>tools/lvnconv/internal/lvns/convert.go</c>). The two implementations are
    /// kept identical by a shared golden corpus (Tests/Editor). Keep this in sync
    /// with the Go source; the Go module remains the single source of truth for the
    /// CLI, server and browser-WASM paths.
    ///
    /// <para>Разложен по темам: `.Expand` — развороты ДО разбора (включения,
    /// вызовы функций, циклы: они работают со строками, а не с командами, и
    /// промах в них даёт не ошибку сборки, а НЕ ТУ ИГРУ); `.Anim` — как читается
    /// авторская запись анимации в обеих её формах. Здесь остаётся разбор строки
    /// в команду.</para>
    /// </summary>
    public static partial class LvnsCompiler
    {
        static readonly Regex reDialogue =
            new Regex(@"^([^:=\n]+?)(?:\s*\[([^\]]+)\])?\s*:\s*(.*)$", RegexOptions.Singleline);
        static readonly Regex reFuncDef =
            new Regex(@"^\s*func\s+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*\{\s*$");
        static readonly Regex reCall =
            new Regex(@"^\s*(?:([A-Za-z_]\w*)\s*=\s*)?([A-Za-z_]\w*)\s*\((.*)\)\s*$");

        // Ops the .lvns layer recognises (convert.go KnownOps — includes `move`,
        // which lowers to an `anim` command).
        static readonly HashSet<string> KnownOps = new HashSet<string>
        {
            // track "имя" — метка конверсии (сахар над хост-опом ext track).
            // Без неё строка, начинающаяся со слова track, стала бы РЕПЛИКОЙ и
            // напечаталась игроку.
            "track",
            "say", "choice", "bg", "bg3d", "actor", "obj",
            // cutscene on=1 zoom=1.1 dur=3 — плоские поля, разбираются общим
            // хвостом key=value. Без объявления строка стала бы РЕПЛИКОЙ.
            "cutscene",
            // A bare `clear` — no fields — compiles through the generic
            // key=value tail below. Listed here for the same reason as
            // everything else in this set, and for no other.
            // NB: keep braces and quoted JSON out of comments in this file —
            // the Go guard scrapes this literal textually and stops at the
            // first closing-brace-semicolon it meets.
            "clear",
            "fade", "dim", "flash", "tint", "blur",
            "portal",
            "camera", "particles",
            "fx", "sfx", // мультиэффект кадра и спрайтовые эффекты — зеркально Go
            "audio", "wait", "preload", "text_pace",
            "text",
            "save", "load",
            "label", "goto", "if",
            "set", "inc", "hint",
            "call", "return",
            "anim", "move",
            // Added after a drift audit: this set had fallen six entries behind
            // convert.go, and a word that is NOT in it silently becomes a
            // DIALOGUE LINE (see the narration branch below). So `input var=…`
            // and `wardrobe_show char=…` used to print themselves on screen in
            // the Unity import path. Both compile correctly through the generic
            // key=value tail, so listing them is the whole fix.
            "input", "wardrobe_show",
            // `ext <op> k=v` declares a HOST op; without it the line became narration
            // and a call into the game's own C# printed itself as dialogue.
            "ext",
        };

        // Recognised by the .lvns language but NOT lowered here. Each needs
        // multi-line state this single-pass compiler does not keep (a pending
        // voice url, a table of named animations), so implementing them is a
        // real change, not a missing case label. Until then they must FAIL, not
        // silently become dialogue.
        static readonly Dictionary<string, string> UnsupportedSourceOps = new Dictionary<string, string>
        {
            ["voice"] = "voice-over attaches to the following line, which needs cross-line state",
            ["defanim"] = "named animations need a definition table",
            ["play"] = "it expands a `defanim` definition, which this importer does not keep",
            // `ui` — блок с вложенным деревом и своими правилами счёта скобок
            // (их полно внутри «…» и "…"). Этот импортёр разбирает строку за
            // строкой и такого не умеет. Объявлено ЯВНО: иначе строка «ui бой {»
            // ушла бы в наррацию и напечаталась игроку — ровно тот молчаливый
            // отказ, ради которого страж и стоит.
            ["ui"] = "it is a nested block with its own brace rules; this line-by-line importer cannot parse it",
        };

        /// <summary>Compile source to indented .lvn JSON ({scene?, script}).</summary>
        public static string Compile(string src)
        {
            JObject doc = Convert(src);
            return doc.ToString(Formatting.Indented) + "\n";
        }

        /// <summary>
        /// СБОРКА С ПУТЁМ — единственный способ пережить `include`.
        ///
        /// <para>Порт не знал `include` вовсе, и строка молча становилась
        /// НАРРАЦИЕЙ: слово вне списка команд превращается в реплику, поэтому
        /// автор, собравший главу в редакторе, показал бы игроку строку
        /// «include "endings.lvns"». Через CLI та же глава собиралась верно —
        /// расхождение видел только тот, кто работает в Unity.</para>
        ///
        /// <para>Подключение разворачивается ДО разбора, как в Go: текст
        /// вставляется на место строки, пути считаются от подключающего файла.
        /// Пакеты (`@scope/pkg/...`) редакторный путь не поддерживает — их
        /// приносит `lvnconv deps`, и об этом сказано ошибкой, а не молчанием.</para>
        /// </summary>
        public static string CompileFile(string path)
        {
            string src = System.IO.File.ReadAllText(path);
            return Compile(ExpandIncludes(src, System.IO.Path.GetDirectoryName(path), 0));
        }

        // ── Convert: the main pipeline (mirrors Go Convert) ──────────────────
        static JObject Convert(string src) { return Convert(src, null, null); }

        /// <summary>Convert with the label namespace of an ENCLOSING document.
        /// A woven option block is compiled by calling back in here, and with a
        /// fresh namer every nesting level restarts at seq 1 — an inner weave and
        /// an outer one both minted `__weave_head_1`, a duplicate label the
        /// validator refuses. Mirrors Go convertWith.</summary>
        static JObject Convert(string src, SynthNamer inherited, Dictionary<string, string> outerActorMaps)
        {
            // Уплощаем ОДИН раз и до всего: сбор функций читал сырой текст, и
            // однострочное объявление `func bow(a) { … }` в словарь не
            // попадало — вызов уходил в наррацию и печатался игроку. Go
            // уплощает первым шагом ровно поэтому.
            string flat = string.Join("\n", FlattenInline(src));
            var funcs = CollectFuncs(flat);
            string expanded = ExpandLoops(flat);
            src = ExpandCalls(expanded, funcs);

            string scene = null;
            var script = new JArray();
            var actorMaps = new Dictionary<string, string>();

            // Pre-process lines: strip // comments (guarding URLs), skip blanks/#,
            // and buffer multi-line «…» strings into one logical line.
            var lines = new List<string>();
            string[] rawLines = src.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            const string urlGuard = "\x00PROTO\x00";

            int chevDepth = 0;
            var cbuf = new StringBuilder();
            foreach (string raw in rawLines)
            {
                if (chevDepth > 0)
                {
                    cbuf.Append("\n");
                    cbuf.Append(raw);
                    foreach (char r in raw)
                    {
                        if (r == '«') chevDepth++;
                        else if (r == '»' && chevDepth > 0) chevDepth--;
                    }
                    if (chevDepth == 0)
                    {
                        lines.Add(cbuf.ToString().Trim());
                        cbuf.Clear();
                    }
                    continue;
                }

                string line = raw.Replace("://", urlGuard);
                int ci = line.IndexOf("//", StringComparison.Ordinal);
                if (ci >= 0) line = line.Substring(0, ci);
                line = line.Replace(urlGuard, "://").Trim();

                if (line.Length == 0 || line.StartsWith("#")) continue;

                int d = 0;
                foreach (char r in line)
                {
                    if (r == '«') d++;
                    else if (r == '»' && d > 0) d--;
                }
                if (d > 0)
                {
                    chevDepth = d;
                    cbuf.Clear();
                    cbuf.Append(line);
                    continue;
                }

                lines.Add(line);
            }
            if (cbuf.Length > 0) lines.Add(cbuf.ToString().Trim());

            // Fall-through labels for `if … -> …` are minted the same derived,
            // edit-stable way the block lowering uses: they are save anchors.
            SynthNamer nfNames;
            if (inherited != null) { nfNames = inherited; nfNames.Absorb(lines); }
            else nfNames = new SynthNamer(lines);
            // A woven block is part of the SAME chapter, so it inherits what the
            // chapter already declared above it. Without the speaker map a line
            // inside a block lost its who_id and the stage highlighted nobody.
            if (outerActorMaps != null)
                foreach (var kv in outerActorMaps) actorMaps[kv.Key] = kv.Value;

            for (int i = 0; i < lines.Count;)
            {
                string line = lines[i];

                // 1. scene
                if (line.StartsWith("scene "))
                {
                    scene = line.Substring(6).Trim();
                    i++; continue;
                }
                if (line.StartsWith("scene:"))
                {
                    scene = line.Substring(6).Trim();
                    i++; continue;
                }

                // 2. actor_map
                if (line.StartsWith("actor_map "))
                {
                    string mapping = line.Substring(10).Trim();
                    int eq = mapping.IndexOf('=');
                    if (eq >= 0)
                        actorMaps[mapping.Substring(0, eq).Trim()] = mapping.Substring(eq + 1).Trim();
                    i++; continue;
                }

                // 3. label  :name
                if (line.StartsWith(":"))
                {
                    string labelId = line.Substring(1).Trim();
                    if (labelId == "")
                        throw new LvnsCompileException($"line {i + 1}: label cannot be empty");
                    script.Add(new JObject { ["op"] = "label", ["id"] = labelId });
                    nfNames.Enter(line); // this label scopes the fall-through names below it
                    i++; continue;
                }

                // 4. choice — consecutive lines starting with `-` (but not `->`)
                if (line.StartsWith("-") && !line.StartsWith("->"))
                {
                    var options = new JArray();
                    var weaves = new List<WeaveBlock>(); // blocks too rich for a body
                    int j = i;
                    while (j < lines.Count)
                    {
                        string curr = lines[j];
                        if (curr.StartsWith("-") && !curr.StartsWith("->"))
                        {
                            JObject opt = ParseChoiceOption(curr, j + 1);
                            j++;
                            // `- text -> label … {` … `}` — the option's BODY: the
                            // commands LvnPlayer.Choose runs on pick, before the
                            // jump. Without this form the body had no source
                            // spelling and a re-save silently dropped it.
                            if (IsOptionBlockOpen(curr))
                            {
                                var bodySrc = new List<string>();
                                bool closed = false;
                                // Depth-counted: an option block may contain another
                                // choice with blocks of its own, and a flat scan
                                // would end the OUTER block on the INNER brace.
                                int depth = 1;
                                int blockLine = j;
                                for (; j < lines.Count; j++)
                                {
                                    string t = lines[j].Trim();
                                    if (t == "}")
                                    {
                                        depth--;
                                        if (depth == 0) { closed = true; j++; break; }
                                    }
                                    else if (IsOptionBlockOpen(t)) depth++;
                                    bodySrc.Add(lines[j]);
                                }
                                if (!closed)
                                    throw new LvnsCompileException($"line {j}: unclosed choice option body (missing '}}')");
                                JArray cmds = ParseBlockCommands(bodySrc, nfNames, actorMaps);
                                var target = (string)opt["goto"];
                                if (NeedsWeaving(cmds))
                                {
                                    // WEAVE: prose or flow cannot ride in a runtime
                                    // `body` (Choose dispatches only set/inc/goto
                                    // there, and a body command has no script index,
                                    // so it would vanish on the first save/restore).
                                    // Same syntax, lowered into script instead.
                                    string lbl = nfNames.Name("weave", nfNames.Site());
                                    weaves.Add(new WeaveBlock { label = lbl, cmds = cmds, target = target, line = blockLine });
                                    opt.Remove("goto");
                                    opt["goto"] = lbl;
                                }
                                else
                                {
                                    // The header's `-> label` is the jump the body
                                    // ends with — keeping it on the option line is
                                    // what makes the target readable at a glance.
                                    if (!string.IsNullOrEmpty(target))
                                    {
                                        cmds.Add(new JObject { ["op"] = "goto", ["label"] = target });
                                        opt.Remove("goto");
                                    }
                                    opt["body"] = cmds;
                                }
                            }
                            options.Add(opt);
                        }
                        else break;
                    }
                    script.Add(new JObject { ["op"] = "choice", ["options"] = options });
                    EmitWeaves(script, nfNames, weaves);
                    i = j; continue;
                }

                // 4b. arrow goto  -> label
                if (line.StartsWith("->"))
                {
                    string target = line.Substring(2).Trim();
                    if (target == "")
                        throw new LvnsCompileException($"line {i + 1}: '->' needs a label");
                    script.Add(new JObject { ["op"] = "goto", ["label"] = target });
                    i++; continue;
                }

                // 4c. single-branch if  `if <cond> -> <label>`
                if (line.StartsWith("if ") && line.Contains("->"))
                {
                    string rest = line.Substring(3).Trim();
                    int ai = rest.IndexOf("->", StringComparison.Ordinal);
                    string cond = rest.Substring(0, ai).Trim();
                    string target = rest.Substring(ai + 2).Trim();
                    if (cond == "" || target == "")
                        throw new LvnsCompileException($"line {i + 1}: expected 'if <cond> -> <label>'");
                    string fall = nfNames.Name("nf", nfNames.Site());
                    script.Add(new JObject { ["op"] = "if", ["expr"] = cond, ["then"] = target, ["else"] = fall });
                    script.Add(new JObject { ["op"] = "label", ["id"] = fall });
                    i++; continue;
                }

                // 4d. assignment  name = expr
                if (TryParseAssign(line, out string akey, out string aexpr) && !KnownOps.Contains(akey))
                {
                    script.Add(new JObject { ["op"] = "set", ["key"] = akey, ["expr"] = aexpr });
                    i++; continue;
                }

                // 5. commands + dialogue
                string[] words = SplitFields(line);
                string firstWord = words.Length > 0 ? words[0] : "";

                // .lvns constructs the reference compiler lowers but this one does
                // not implement. Left unlisted they fell into the narration branch
                // and PRINTED THEMSELVES to the player — the worst possible
                // outcome, because nothing errors and the bug ships. Say so
                // instead: an import error names the construct and the way out.
                if (UnsupportedSourceOps.TryGetValue(firstWord, out var why))
                    throw new LvnsCompileException(
                        $"line {i + 1}: `{firstWord}` is not supported by the Unity .lvns importer — {why}. " +
                        "Compile with `lvnconv convert` and import the resulting .lvn instead.");

                bool isCommand = false;
                JObject cmd = null;

                if (KnownOps.Contains(firstWord))
                {
                    // `fx off` — сброс мультиэффекта (зеркально Go): голое слово
                    // без `=` не проходит parseKeyValue и падало в наррацию.
                    if (firstWord == "fx" && line.Substring(2).Trim() == "off")
                    {
                        script.Add(new JObject { ["op"] = "fx", ["off"] = true });
                        i++; continue;
                    }
                    if (firstWord == "sfx" && line.Trim().EndsWith(" off"))
                    {
                        var t = line.Trim(); t = t.Substring(3, t.Length - 3 - 3).Trim();
                        var sp = ParseKeyValue(t);
                        var so = new JObject { ["op"] = "sfx", ["off"] = true };
                        foreach (var kv in sp) so[kv.Key] = JToken.FromObject(kv.Value);
                        script.Add(so);
                        i++; continue;
                    }
                    if (firstWord == "anim" || firstWord == "move")
                    {
                        string rest = line.Substring(firstWord.Length).Trim();
                        string[] toks = SplitFields(rest);
                        Dictionary<string, object> p;
                        if (toks.Length > 0 && !toks[0].Contains("="))
                            p = ParseAnimPositional(firstWord, rest);
                        else
                            p = ParseKeyValue(rest);
                        cmd = BuildAnimCmd(firstWord, p);
                        isCommand = true;
                    }
                    else if (firstWord == "actor")
                    {
                        string rest = line.Substring("actor".Length).Trim();
                        string[] toks = SplitFields(rest);
                        if (toks.Length > 0 && !toks[0].Contains("="))
                        {
                            var ac = new JObject { ["op"] = "actor", ["id"] = toks[0], ["show"] = true };
                            for (int t = 1; t < toks.Length; t++)
                            {
                                string tok = toks[t];
                                if (tok.Contains("="))
                                {
                                    int e = tok.IndexOf('=');
                                    string k = tok.Substring(0, e);
                                    string v = tok.Substring(e + 1);
                                    if (k == "w") k = "width";
                                    else if (k == "h") k = "height";
                                    ac[k] = Tok(ScalarVal(v));
                                }
                                else
                                {
                                    switch (tok)
                                    {
                                        case "hide": ac["show"] = false; break;
                                        case "show": ac["show"] = true; break;
                                        // Словарь мест — один (Placement.SlotNames).
                                        // «center_left» и «center_right» здесь не
                                        // значились, и слово молча становилось
                                        // ЭМОЦИЕЙ: герой получал не место, а
                                        // эмоцию, которой у него нет.
                                        case "left":
                                        case "right":
                                        case "center":
                                        case "center_left":
                                        case "center_right":
                                        case "far_left":
                                        case "far_right":
                                        case "offscreen_left":
                                        case "offscreen_right":
                                            ac["position"] = tok; break;
                                        default:
                                            ac["emotion"] = tok; break;
                                    }
                                }
                            }
                            cmd = ac; isCommand = true;
                        }
                        else
                        {
                            var p = ParseKeyValue(rest);
                            cmd = new JObject { ["op"] = "actor" };
                            foreach (var kv in p) cmd[kv.Key] = Tok(kv.Value);
                            isCommand = true;
                        }
                    }
                    else if (firstWord == "bg")
                    {
                        string rest = line.Substring("bg".Length).Trim();
                        if (rest != "" && !rest.Contains("="))
                        {
                            var c = new JObject { ["op"] = "bg", ["sprite_url"] = StripQuotes(rest) };
                            string base_ = rest;
                            int sl = base_.LastIndexOfAny(new[] { '/', '\\' });
                            if (sl >= 0) base_ = base_.Substring(sl + 1);
                            int dot = base_.LastIndexOf('.');
                            if (dot >= 0) base_ = base_.Substring(0, dot);
                            if (base_ != "") c["id"] = base_;
                            cmd = c; isCommand = true;
                        }
                        else
                        {
                            var p = ParseKeyValue(rest);
                            cmd = new JObject { ["op"] = "bg" };
                            foreach (var kv in p) cmd[kv.Key] = Tok(kv.Value);
                            isCommand = true;
                        }
                    }
                    else if (firstWord == "text")
                    {
                        string rem = line.Substring("text".Length).Trim();
                        NextWord(rem, out string id, out string after);
                        if (id != "")
                        {
                            var c = new JObject { ["op"] = "text", ["id"] = id };
                            rem = after;
                            while (true)
                            {
                                NextWord(rem, out string w, out string next);
                                if (w == "") break;
                                if (w == "hide" && next.Trim() == "")
                                {
                                    c["hide"] = true; rem = ""; break;
                                }
                                if (w.Contains("="))
                                {
                                    int e = w.IndexOf('=');
                                    c[w.Substring(0, e)] = Tok(ScalarVal(w.Substring(e + 1)));
                                    rem = next; continue;
                                }
                                break; // w begins the template
                            }
                            string tmpl = rem.Trim();
                            if (tmpl != "") c["text"] = StripQuotes(tmpl);
                            cmd = c; isCommand = true;
                        }
                    }
                    else if (firstWord == "return" && words.Length == 1)
                    {
                        cmd = new JObject { ["op"] = "return" }; isCommand = true;
                    }
                    else if ((firstWord == "goto" || firstWord == "call") && words.Length == 2)
                    {
                        cmd = new JObject { ["op"] = firstWord, ["label"] = words[1] }; isCommand = true;
                    }
                    else if (firstWord == "track")
                    {
                        // ВЕТКИ НЕ БЫЛО, хотя имя стояло в KnownOps и рядом
                        // висело предупреждение «без неё строка стала бы
                        // репликой». Именно это и происходило: метка конверсии,
                        // написанная в редакторе, компилировалась в РЕПЛИКУ и
                        // показывалась игроку. Через CLI тот же скрипт собирался
                        // правильно — расхождение видел только тот, кто собирал
                        // в Unity.
                        //
                        // Имя берём как есть, вместе с пробелами: «первый
                        // поцелуй» читается в отчёте, «первый_поцелуй» — нет.
                        string trackName = line.Substring("track".Length).Trim().Trim('"', '\'', '«', '»');
                        if (trackName.Length == 0)
                            throw new LvnsCompileException(
                                $"line {i + 1}: track: нужно имя метки — track \"первый поцелуй\"");
                        cmd = new JObject { ["op"] = "track", ["name"] = trackName };
                        isCommand = true;
                    }
                    else if (firstWord == "ext")
                    {
                        // `ext <op> k=v …` declares a HOST op: emit it verbatim.
                        // Without this branch the whole line fell into narration
                        // and a call into the game's own C# printed itself as
                        // dialogue instead of running.
                        string rest = line.Substring(3).Trim();
                        var extWords = rest.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (extWords.Length == 0)
                            throw new LvnsCompileException($"line {i + 1}: `ext` needs an op name");
                        cmd = new JObject { ["op"] = extWords[0] };
                        if (extWords.Length > 1)
                        {
                            var ep = ParseKeyValueSafe(extWords[1].Trim());
                            if (ep == null)
                                throw new LvnsCompileException($"line {i + 1}: `ext {extWords[0]}` expects key=value arguments");
                            foreach (var kv in ep) cmd[kv.Key] = Tok(kv.Value);
                        }
                        isCommand = true;
                    }
                    else if (firstWord != "return" && firstWord != "goto" && firstWord != "call")
                    {
                        string rest = line.Substring(firstWord.Length).Trim();
                        if (rest == "")
                        {
                            cmd = new JObject { ["op"] = firstWord }; isCommand = true;
                        }
                        else
                        {
                            var p = ParseKeyValueSafe(rest);
                            if (p != null)
                            {
                                cmd = new JObject { ["op"] = firstWord };
                                foreach (var kv in p) cmd[kv.Key] = Tok(kv.Value);
                                isCommand = true;
                            }
                        }
                    }
                }

                if (isCommand)
                {
                    script.Add(cmd);
                    i++; continue;
                }

                // ПРОЗА, ВЗЯТАЯ В «…», — ЦЕЛИКОМ ТЕКСТ, И ДВОЕТОЧИЕ В НЕЙ
                // ОБЫЧНЫЙ ЗНАК. Портировано из Go-транскодера (28.08): разрез
                // «имя: текст» шёл по сырой строке, не глядя на кавычки, и
                // «Вывеска гласила: закрыто.» превращалась в реплику говорящего
                // «Вывеска гласила». Тот же путь советует сообщение компилятора
                // про неизвестную команду — и совет не работал.
                //
                // Кавычки ВНУТРИ реплики (`Анна: «Пауза»`) это не задевает:
                // проверяется, что строка НАЧИНАЕТСЯ с «, то есть имени перед
                // ней нет.
                if (line.StartsWith("«") && line.EndsWith("»") && line.Length > 2)
                {
                    string prose = line.Substring("«".Length, line.Length - "«".Length - "»".Length).Trim();
                    script.Add(new JObject { ["op"] = "say", ["text"] = prose });
                    i++; continue;
                }

                // Dialogue: Name [emotion]: Text   — or narration
                Match m = reDialogue.Match(line);
                if (m.Success)
                {
                    string speaker = m.Groups[1].Value.Trim();
                    string emotion = m.Groups[2].Value.Trim();
                    string text = m.Groups[3].Value.Trim();
                    text = StripQuotes(text);

                    if (emotion != "")
                    {
                        if (!actorMaps.TryGetValue(speaker, out string actorID))
                            actorID = speaker.ToLowerInvariant().Replace(" ", "_");
                        script.Add(new JObject { ["op"] = "actor", ["id"] = actorID, ["emotion"] = emotion });
                    }
                    var sc = new JObject { ["op"] = "say", ["who"] = speaker, ["text"] = text };
                    // ЯВНЫЙ actor_map значит, что экранное имя и id спрайта
                    // РАСХОДЯТСЯ («Ash» говорит через спрайт «hill»), и id
                    // нужно донести до сцены — иначе она подсвечивает и
                    // синхронит губы НИКОМУ. Go это поле кладёт с самого
                    // начала; редакторный путь его терял, и глава, собранная в
                    // Unity, играла иначе, чем та же глава через CLI.
                    if (actorMaps.TryGetValue(speaker, out string whoId)) sc["who_id"] = whoId;
                    script.Add(sc);
                }
                else
                {
                    script.Add(new JObject { ["op"] = "say", ["text"] = StripQuotes(line) });
                }

                i++;
            }

            var outDoc = new JObject();
            if (scene != null && scene != "") outDoc["scene"] = scene;
            outDoc["script"] = script;
            return outDoc;
        }

        // ── sugar lowering ───────────────────────────────────────────────────

        struct Frame
        {
            public string kind; // "for" | "while" | "if" | "func" | "opt"
            public string loopLbl, endLbl;
            public string idxVar;
            public string elseLbl;
            public bool sawElse;
        }

        // ── synthetic label names (mirrors Go synthNamer) ────────────────────
        //
        // The names the lowering mints (`__then…`, `__else…`, `__end…`, `__nf…`)
        // are not private to the compiler: they become `label` ops, and a SAVE is
        // anchored on the id of the nearest preceding label (LvnPlayer.AnchorOf).
        // A name that moves when an unrelated part of the chapter is edited moves
        // every player's bookmark, so a name is DERIVED — nearest preceding author
        // label + the ordinal of the lowering site inside that label's scope —
        // never counted from the top of the file.
        class SynthNamer
        {
            string _scope = "head";
            readonly Dictionary<string, int> _seq = new Dictionary<string, int>();
            readonly HashSet<string> _taken = new HashSet<string>(StringComparer.Ordinal);

            public SynthNamer(IEnumerable<string> lines)
            {
                foreach (string l in lines)
                {
                    string id = SourceLabelId(l);
                    if (id != null) _taken.Add(id);
                }
            }

            /// <summary>A `:label` the AUTHOR wrote opens a new naming scope (a
            /// `__`-prefixed one is itself a lowering artifact).</summary>
            public void Enter(string line)
            {
                string id = SourceLabelId(line);
                if (id != null && !id.StartsWith("__", StringComparison.Ordinal)) _scope = id;
            }

            /// <summary>Register a nested source's author labels here, so a minted
            /// name can never collide with a label written inside a woven block.</summary>
            public void Absorb(IEnumerable<string> lines)
            {
                foreach (string l in lines)
                {
                    string id = SourceLabelId(l);
                    if (id != null) _taken.Add(id);
                }
            }

            /// <summary>The tag shared by every label one lowering site needs.</summary>
            public string Site()
            {
                _seq.TryGetValue(_scope, out int n);
                _seq[_scope] = n + 1;
                return _scope + "_" + (n + 1);
            }

            /// <summary>One collision-free label for a site tag.</summary>
            public string Name(string kind, string tag)
            {
                string baseName = "__" + kind + "_" + tag;
                string name = baseName;
                for (int i = 2; _taken.Contains(name); i++) name = baseName + "_" + i;
                _taken.Add(name);
                return name;
            }

            static string SourceLabelId(string line)
            {
                string t = StripLineComment(line.Trim()).Trim();
                if (!t.StartsWith(":", StringComparison.Ordinal)) return null;
                string id = t.Substring(1).Trim();
                return id == "" ? null : id;
            }
        }

        /// <summary>Does this line open a choice option's `{ … }` body? An option
        /// line whose LAST character is the brace — which is what keeps option text
        /// free to carry `{expr}` interpolation.</summary>
        static bool IsOptionBlockOpen(string det)
        {
            return det.StartsWith("-", StringComparison.Ordinal)
                && !det.StartsWith("->", StringComparison.Ordinal)
                && det.EndsWith("{", StringComparison.Ordinal);
        }

        // splitInline: flatten one-line control blocks to own-line brace form.
        static List<string> SplitInline(string line)
        {
            string t = StripLineComment(line.Trim());
            string det = t.Trim();
            if (det == "") return new List<string> { line };

            bool isCtl = det.StartsWith("if ") || det.StartsWith("for ") ||
                         det.StartsWith("while ") || det.StartsWith("func ") ||
                         det.StartsWith("}");
            if (!isCtl || det.EndsWith("{") || det == "}" ||
                det.Replace(" ", "") == "}else{")
                return new List<string> { line };

            int open = FirstBlockBrace(det);
            if (open < 0) return new List<string> { line };
            int close = MatchBrace(det, open);
            if (close < 0) return new List<string> { line };

            var outList = new List<string>();
            if (det.StartsWith("}"))
                outList.Add("} else {");
            else
                outList.Add(det.Substring(0, open).Trim() + " {");

            string body = det.Substring(open + 1, close - open - 1).Trim();
            if (body != "") outList.AddRange(SplitInline(body));

            string tail = det.Substring(close + 1).Trim();
            if (tail == "")
                outList.Add("}");
            else if (tail.StartsWith("else"))
                outList.AddRange(SplitInline("} " + tail));
            else
            {
                outList.Add("}");
                outList.AddRange(SplitInline(tail));
            }
            return outList;
        }

        static int FirstBlockBrace(string rs)
        {
            char inStr = '\0';
            int chev = 0;
            for (int i = 0; i < rs.Length; i++)
            {
                char c = rs[i];
                if (inStr != '\0') { if (c == inStr) inStr = '\0'; continue; }
                if (c == '«') chev++;
                else if (c == '»') { if (chev > 0) chev--; }
                else if (chev > 0) { }
                else if (c == '"' || c == '\'') inStr = c;
                else if (c == '{') return i;
            }
            return -1;
        }

        static int MatchBrace(string rs, int open)
        {
            char inStr = '\0';
            int chev = 0, depth = 0;
            for (int i = open; i < rs.Length; i++)
            {
                char c = rs[i];
                if (inStr != '\0') { if (c == inStr) inStr = '\0'; continue; }
                if (c == '«') chev++;
                else if (c == '»') { if (chev > 0) chev--; }
                else if (chev > 0) { }
                else if (c == '"' || c == '\'') inStr = c;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        static string StripLineComment(string s)
        {
            char inStr = '\0';
            int chev = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr != '\0') { if (c == inStr) inStr = '\0'; continue; }
                if (c == '«') chev++;
                else if (c == '»') { if (chev > 0) chev--; }
                else if (chev > 0) { }
                else if (c == '"' || c == '\'') inStr = c;
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    if (i > 0 && s[i - 1] == ':') continue; // part of ://
                    return s.Substring(0, i);
                }
            }
            return s;
        }

        // ── choice / key-value parsing ───────────────────────────────────────

        static JObject ParseChoiceOption(string line, int lineNo)
        {
            string text = line.Substring(1).Trim(); // strip '-'
            // A trailing `{` opens the option's body block (the caller collects
            // it); the brace is not part of the option line itself.
            bool hasBody = IsOptionBlockOpen(line.Trim());
            if (hasBody) text = text.Substring(0, text.Length - 1).Trim();

            string optText, targetLabel = "", paramsStr = "";
            int arrowIdx = text.IndexOf("->", StringComparison.Ordinal);
            if (arrowIdx >= 0)
            {
                optText = text.Substring(0, arrowIdx).Trim();
                string rest = text.Substring(arrowIdx + 2).Trim();
                if (rest == "")
                    throw new LvnsCompileException($"line {lineNo}: choice option must specify a target label after '->'");
                int spaceIdx = IndexOfAny(rest, ' ', '\t');
                if (spaceIdx == -1) targetLabel = rest;
                else { targetLabel = rest.Substring(0, spaceIdx); paramsStr = rest.Substring(spaceIdx + 1).Trim(); }
            }
            else
            {
                if (!hasBody)
                    throw new LvnsCompileException($"line {lineNo}: choice option must have a target label (use '-> label')");
                // Body-only option: the body IS the whole action and the flow falls
                // through past the choice once it has run — no target to name.
                SplitOptionParams(text, out optText, out paramsStr);
            }
            if (optText == "")
                throw new LvnsCompileException($"line {lineNo}: choice option text cannot be empty");

            var opt = new JObject { ["text"] = StripQuotes(optText) };
            if (targetLabel != "") opt["goto"] = targetLabel;
            if (paramsStr != "")
            {
                var pars = ParseKeyValue(paramsStr);
                foreach (var kv in pars) opt[kv.Key] = Tok(kv.Value);
            }
            return opt;
        }

        static readonly Regex OptParamRe = new Regex(@"(^|[ \t])[a-z_][a-z0-9_]*=");

        /// <summary>Separate a body-only option's text from its trailing
        /// `key=value …` attributes: with no `-> label` to split on, the first
        /// token that both LOOKS like an attribute AND parses as one opens the
        /// parameter run — prose that merely contains an `=` stays prose.</summary>
        static void SplitOptionParams(string s, out string text, out string parameters)
        {
            foreach (Match m in OptParamRe.Matches(s))
            {
                int at = m.Index;
                while (at < s.Length && (s[at] == ' ' || s[at] == '\t')) at++;
                string cand = s.Substring(at).Trim();
                if (ParseKeyValueSafe(cand) != null)
                {
                    text = s.Substring(0, at).Trim();
                    parameters = cand;
                    return;
                }
            }
            text = s.Trim();
            parameters = "";
        }

        /// <summary>
        /// ЧТО ИМЕЕТ ПРАВО ЕХАТЬ РАНТАЙМ-ТЕЛОМ ОПЦИИ — и ничего сверх.
        ///
        /// <para>Тело опции исполняется БЕЗ СОБСТВЕННОГО ИНДЕКСА в скрипте, а
        /// картинку сцены игра восстанавливает по следу исполнения — списку
        /// индексов. У чего нет индекса, того нет и в следе: команда отработает
        /// один раз и исчезнет при первом же сохранении.</para>
        ///
        /// <para>Для переменных это безобидно: их значения снимок несёт сам. Для
        /// всего остального — нет. Партнёрский отчёт «вышел в меню, вернулся —
        /// персонажа нет» ровно об этом: <c>actor</c> в теле опции показал
        /// героиню, автосохранение её не запомнило, и возврат собрал сцену без
        /// неё — фон на месте, реплики идут, фигуры нет.</para>
        ///
        /// <para>Список был обратным — перечислял запрещённое, — и всё, чего в
        /// нём не назвали (а это вся постановка: <c>actor</c>, <c>bg</c>,
        /// <c>fade</c>, <c>audio</c>, <c>obj</c>, <c>camera</c>, <c>fx</c>…),
        /// молча ехало телом. Перечислять безопасное короче и, главное,
        /// безопаснее: новая команда языка по умолчанию плетётся в скрипт, а не
        /// теряется.</para>
        /// </summary>
        static readonly HashSet<string> OptionBodyKeeps = new HashSet<string>(StringComparer.Ordinal)
        {
            "set", "inc", "goto",
        };

        /// <summary>Compile a choice option's `{ … }` block. It does NOT judge the
        /// contents — the caller does, via NeedsWeaving: set/inc/goto rides along
        /// as a runtime `body`, anything richer is lowered into script.</summary>
        static JArray ParseBlockCommands(List<string> bodyLines, SynthNamer names, Dictionary<string, string> actorMaps)
        {
            var compiled = (JArray)Convert(string.Join("\n", bodyLines), names, actorMaps)["script"];
            var body = new JArray();
            foreach (JToken t in compiled) body.Add(t.DeepClone()); // detach from the throwaway doc
            return body;
        }

        /// <summary>Развилка плетения: помещается блок в рантайм-тело или обязан
        /// стать скриптом? Зеркалит Go needsWeaving.</summary>
        static bool NeedsWeaving(JArray cmds)
        {
            foreach (JToken t in cmds)
            {
                var op = (string)t["op"];
                if (op == null || !OptionBodyKeeps.Contains(op)) return true;
            }
            return false;
        }

        /// <summary>One option block deferred until after the choice is emitted.</summary>
        class WeaveBlock
        {
            public string label;
            public JArray cmds;
            public string target;
            public int line;
        }

        /// <summary>Write the lowering right behind the choice: a jump to the
        /// convergence for the option that continues past the choice, then one
        /// labelled branch per woven block, then the convergence label. Mirrors Go
        /// emitWeaves — including doing NOTHING when nothing was woven, so an
        /// ordinary choice still costs zero labels.</summary>
        static void EmitWeaves(JArray script, SynthNamer names, List<WeaveBlock> weaves)
        {
            if (weaves.Count == 0) return;
            string end = names.Name("wend", names.Site());
            script.Add(new JObject { ["op"] = "goto", ["label"] = end });
            foreach (WeaveBlock w in weaves)
            {
                script.Add(new JObject { ["op"] = "label", ["id"] = w.label });
                foreach (JToken c in w.cmds) script.Add(c);
                if (EndsFlow(w.cmds)) continue; // the block already jumped away
                script.Add(new JObject { ["op"] = "goto", ["label"] = string.IsNullOrEmpty(w.target) ? end : w.target });
            }
            script.Add(new JObject { ["op"] = "label", ["id"] = end });
        }

        static bool EndsFlow(JArray cmds)
        {
            if (cmds.Count == 0) return false;
            var op = (string)cmds[cmds.Count - 1]["op"];
            return op == "goto" || op == "return";
        }

        // ParseKeyValue throws on malformed input (mirrors Go error return used as
        // a hard failure at choice/anim/legacy-command sites).
        static Dictionary<string, object> ParseKeyValue(string s)
        {
            var res = new Dictionary<string, object>();
            s = s.Trim();
            while (s.Length > 0)
            {
                int eqIdx = s.IndexOf('=');
                if (eqIdx == -1)
                    throw new LvnsCompileException($"expected '=' in key-value pair at \"{s}\"");
                string key = s.Substring(0, eqIdx).Trim();
                if (!IsValidKey(key))
                    throw new LvnsCompileException($"invalid key name \"{key}\"");
                s = s.Substring(eqIdx + 1).TrimStart();
                if (s.Length == 0)
                    throw new LvnsCompileException($"missing value for key \"{key}\"");

                string val;
                if (s[0] == '"' || s[0] == '\'')
                {
                    char quote = s[0];
                    int end = -1;
                    for (int i = 1; i < s.Length; i++)
                    {
                        if (s[i] == quote)
                        {
                            int nb = 0;
                            for (int jj = i - 1; jj >= 1 && s[jj] == '\\'; jj--) nb++;
                            if (nb % 2 == 0) { end = i; break; }
                        }
                    }
                    if (end == -1)
                        throw new LvnsCompileException($"unclosed quote for key \"{key}\"");
                    val = s.Substring(1, end - 1);
                    val = val.Replace("\\\"", "\"").Replace("\\'", "'");
                    s = s.Substring(end + 1);
                }
                else
                {
                    int spaceIdx = IndexOfAny(s, ' ', '\t');
                    if (spaceIdx == -1) { val = s; s = ""; }
                    else { val = s.Substring(0, spaceIdx); s = s.Substring(spaceIdx + 1); }
                }

                res[key] = TypeScalar(val);
                s = s.Trim();
            }
            return res;
        }

        static Dictionary<string, object> ParseKeyValueSafe(string s)
        {
            try { return ParseKeyValue(s); }
            catch (LvnsCompileException) { return null; }
        }

        // TypeScalar coerces a bare (unquoted) value the way Go parseKeyValue does:
        // bool/null, then int64 (no dot) or float64, else string.
        static object TypeScalar(string val)
        {
            if (val == "true") return true;
            if (val == "false") return false;
            if (val == "null") return null;
            if (double.TryParse(val, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double n))
            {
                if (!val.Contains("."))
                {
                    if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long vi))
                        return vi;
                    return n;
                }
                return n;
            }
            return val;
        }

        static bool IsValidKey(string k)
        {
            if (k.Length == 0) return false;
            foreach (char r in k)
            {
                // Any unicode letter — authors write Russian variable names
                // (`ждал = true`) as naturally as English ones (mirrors Go
                // isValidKey; ASCII-only here silently turned such assignments
                // into prose and forked weave vs body differently than Go).
                bool ok = char.IsLetter(r) || char.IsDigit(r) || r == '_' || r == '.';
                if (!ok) return false;
            }
            return true;
        }

        // ── animation (anim/move) ────────────────────────────────────────────

        static double PropIdentity(string prop)
        {
            switch (prop)
            {
                case "scale":
                case "scalex":
                case "scaley":
                case "alpha":
                    return 1;
                default:
                    return 0;
            }
        }

        static void ParseLoop(object v, out bool loop, out bool yoyo)
        {
            loop = false; yoyo = false;
            if (v is bool b) { loop = b; return; }
            if (v is string s)
            {
                switch (s)
                {
                    case "yoyo":
                    case "pingpong": loop = true; yoyo = true; break;
                    case "true":
                    case "restart":
                    case "loop": loop = true; break;
                }
            }
        }

        // ── small helpers ────────────────────────────────────────────────────

        static object ScalarVal(string v)
        {
            v = v.Trim();
            if (double.TryParse(v, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double n))
                return n;
            return StripQuotes(v);
        }

        static string StripQuotes(string s)
        {
            s = s.Trim();
            if (s.Length >= 2)
            {
                if ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\''))
                    return s.Substring(1, s.Length - 2);
            }
            if (s.StartsWith("«") && s.EndsWith("»"))
            {
                string inner = s.Substring("«".Length, s.Length - "«".Length - "»".Length);
                return inner.Trim();
            }
            return s;
        }

        static bool NumParam(object v, out double result)
        {
            switch (v)
            {
                case double d: result = d; return true;
                case long l: result = l; return true;
                case int i: result = i; return true;
                default: result = 0; return false;
            }
        }

        static bool TryParseAssign(string line, out string key, out string expr)
        {
            key = ""; expr = "";
            int eq = -1;
            for (int idx = 0; idx < line.Length; idx++)
            {
                if (line[idx] != '=') continue;
                char prev = idx > 0 ? line[idx - 1] : '\0';
                char next = idx + 1 < line.Length ? line[idx + 1] : '\0';
                if (prev == '!' || prev == '<' || prev == '>' || prev == '=' || next == '=') continue;
                eq = idx; break;
            }
            if (eq < 0) return false;
            key = line.Substring(0, eq).Trim();
            expr = line.Substring(eq + 1).Trim();
            if (expr == "" || !IsValidKey(key)) return false;
            return true;
        }

        static void NextWord(string s, out string word, out string rest)
        {
            s = s.TrimStart(' ', '\t');
            if (s == "") { word = ""; rest = ""; return; }
            int i = IndexOfAny(s, ' ', '\t', '\n');
            if (i >= 0) { word = s.Substring(0, i); rest = s.Substring(i); }
            else { word = s; rest = ""; }
        }

        static int IndexOfAny(string s, params char[] chars)
        {
            return s.IndexOfAny(chars);
        }

        static string[] SplitFields(string s) =>
            s.Split(new[] { ' ', '\t', '\n', '\r', '\f', '\v' }, StringSplitOptions.RemoveEmptyEntries);

        // GoQuote mirrors Go's %q (strconv.Quote) for the subset that appears in
        // lowered control lines, paired with ParseKeyValue's limited unescaping.
        static string GoQuote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }

        // G formats a float like Go's %g (shortest round-trip), used to build keys.
        static string G(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        // Tok wraps a parsed scalar (bool/long/double/string/null) as a JToken.
        static JToken Tok(object v)
        {
            if (v == null) return JValue.CreateNull();
            switch (v)
            {
                case bool b: return new JValue(b);
                case long l: return new JValue(l);
                case int i: return new JValue((long)i);
                case double d: return new JValue(d);
                case string s: return new JValue(s);
                default: return JToken.FromObject(v);
            }
        }
    }
}
