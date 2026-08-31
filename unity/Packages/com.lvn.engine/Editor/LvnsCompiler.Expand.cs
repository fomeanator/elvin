using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Lvn.Editor
{
    /// <summary>
    /// РАЗВОРОТЫ ДО РАЗБОРА — включения, вызовы функций, циклы.
    ///
    /// <para>Всё здесь работает со СТРОКАМИ, а не с командами: к моменту, когда
    /// компилятор начнёт разбирать строку в команду, развороты уже случились.
    /// Отсюда и цена ошибки: промах в них даёт не ошибку сборки, а НЕ ТУ ИГРУ —
    /// цикл, свёрнутый на шаг короче, или вызов, подставивший чужие доводы,
    /// компилируются молча и играются неправильно.</para>
    ///
    /// <para>Жило это в одном файле с разбором команд, и файл был крупнейшим в
    /// репозитории — 1703 строки, при том что канон о разложении крупных
    /// классов на него не распространялся. Две попытки разрезать его подсчётом
    /// фигурных скобок развалили сборку: в компиляторе полно строковых
    /// литералов со скобками, он про них и написан. Третья попытка резала
    /// разбором — маской «это код, а не литерал».</para>
    /// </summary>
    public static partial class LvnsCompiler
    {
        static readonly Regex reInclude = new Regex("^\\s*include\\s+\"([^\"]+)\"\\s*$");

        static string ExpandIncludes(string src, string baseDir, int depth)
        {
            if (depth > 8)
                throw new LvnsCompileException("include: слишком глубокая вложенность (цикл?)");
            var sb = new StringBuilder();
            foreach (var line in src.Replace("\r\n", "\n").Split('\n'))
            {
                var m = reInclude.Match(line);
                if (!m.Success) { sb.Append(line).Append('\n'); continue; }
                string rel = m.Groups[1].Value;
                if (rel.StartsWith("@"))
                    throw new LvnsCompileException(
                        $"include \"{rel}\": пакеты собирает lvnconv (deps), редакторный импорт их не тянет");
                string full = System.IO.Path.Combine(baseDir ?? ".", rel);
                if (!System.IO.File.Exists(full))
                    throw new LvnsCompileException($"include \"{rel}\": файл не найден рядом со скриптом");
                sb.Append(ExpandIncludes(System.IO.File.ReadAllText(full),
                                         System.IO.Path.GetDirectoryName(full), depth + 1));
            }
            return sb.ToString();
        }

        static Dictionary<string, List<string>> CollectFuncs(string src)
        {
            var m = new Dictionary<string, List<string>>();
            var declaredAt = new Dictionary<string, int>();
            string[] lines = src.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int chevDepth = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                // Строка «func …» внутри «…» — проза, а не объявление.
                if (chevDepth > 0 || ChevRun(0, lines[i]) > 0)
                {
                    chevDepth = ChevRun(chevDepth, lines[i]);
                    continue;
                }
                Match mm = reFuncDef.Match(lines[i]);
                if (!mm.Success) continue;
                var ps = new List<string>();
                foreach (string p in mm.Groups[2].Value.Split(','))
                {
                    string t = p.Trim();
                    if (t != "") ps.Add(t);
                }
                string name = mm.Groups[1].Value;
                // ДВА ОПРЕДЕЛЕНИЯ ОДНОГО ИМЕНИ — ошибка, а не тихая замена.
                // Второе молча затирало первое, и половина вызовов уходила не
                // туда: автор видит не ошибку, а игру, которая ведёт себя не
                // так. Go отвечает на это ошибкой с номером строки.
                if (declaredAt.TryGetValue(name, out int prev))
                    throw new LvnsCompileException(
                        $"line {i + 1}: func {name}: already declared on line {prev}");
                declaredAt[name] = i + 1;
                m[name] = ps;
            }
            return m;
        }

        static string ExpandCalls(string src, Dictionary<string, List<string>> funcs)
        {
            var outLines = new List<string>();
            int lineNo = 0;
            foreach (string line in src.Split('\n'))
            {
                lineNo++;
                string t = line.Trim();

                if (t.StartsWith("return "))
                {
                    string expr = t.Substring("return ".Length).Trim();
                    if (expr != "")
                    {
                        outLines.Add("__ret = " + expr);
                        outLines.Add("return");
                        continue;
                    }
                }

                Match mm = reCall.Match(t);
                if (mm.Success)
                {
                    string lhs = mm.Groups[1].Value;
                    string fname = mm.Groups[2].Value;
                    string argstr = mm.Groups[3].Value;
                    if (funcs.TryGetValue(fname, out var pars))
                    {
                        var args = SplitArgs(argstr);
                        // ЧИСЛО ДОВОДОВ СВЕРЯЕТСЯ. Недостающие молча оставляли
                        // параметр со значением от ПРОШЛОГО вызова, лишние —
                        // молча пропадали. И то и другое даёт не ошибку сборки,
                        // а не ту игру.
                        if (args.Count != pars.Count)
                            throw new LvnsCompileException(
                                $"line {lineNo}: {fname}() takes {pars.Count} argument(s), got {args.Count}");
                        for (int k = 0; k < pars.Count; k++)
                            outLines.Add(pars[k] + " = " + args[k]);
                        outLines.Add("call __fn_" + fname);
                        if (lhs != "") outLines.Add(lhs + " = __ret");
                        continue;
                    }
                }
                outLines.Add(line);
            }
            RefuseInlineCalls(outLines, funcs);
            return string.Join("\n", outLines);
        }

        /// <summary>
        /// ВЫЗОВ ВНУТРИ ВЫРАЖЕНИЯ — ОТКАЗ, А НЕ МОЛЧАНИЕ.
        ///
        /// <para>Go подставляет тело функции прямо в выражение:
        /// <c>x = 1 + add(2, 3)</c> становится <c>set expr="1 + (2 + 3)"</c>,
        /// без единой метки. Этот порт так не умеет — он лоуэрит функцию
        /// процедурой, и <c>add(2, 3)</c> оставался в выражении неразобранным.
        /// Из одного исходника выходили два РАЗНЫХ скрипта: через CLI глава
        /// считала, в редакторе — читала ноль и играла не ту ветку.</para>
        ///
        /// <para>Пока подстановки здесь нет, это ОБЯЗАНО падать, а не тихо
        /// расходиться — ровно то же правило, по которому в этом файле объявлен
        /// список неподдержанных исходных команд. Автору сказано, что делать:
        /// собрать главу через lvnconv.</para>
        /// </summary>
        static void RefuseInlineCalls(List<string> lines, Dictionary<string, List<string>> funcs)
        {
            if (funcs.Count == 0) return;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                bool carriesExpr = t.StartsWith("set ") || t.StartsWith("if ") || t.StartsWith("inc ");
                foreach (var fname in funcs.Keys)
                {
                    int from = 0;
                    while (true)
                    {
                        int at = t.IndexOf(fname + "(", from, StringComparison.Ordinal);
                        if (at < 0) break;
                        from = at + 1;
                        // Имя должно стоять отдельным словом, а не хвостом чужого.
                        if (at > 0 && (char.IsLetterOrDigit(t[at - 1]) || t[at - 1] == '_')) continue;
                        // Считается либо строка-носитель выражения, либо любая
                        // подстановка {…}: в прозе «беги(быстро)» — просто слова.
                        if (!carriesExpr && !InsideBraces(t, at)) continue;
                        throw new LvnsCompileException(
                            $"line {i + 1}: {fname}() внутри выражения — редакторный импорт подстановку "
                            + "тела в выражение не делает; соберите главу через lvnconv "
                            + "(или вынесите вызов на отдельную строку)");
                    }
                }
            }
        }

        static bool InsideBraces(string s, int at)
        {
            int open = s.LastIndexOf('{', at);
            if (open < 0) return false;
            int close = s.IndexOf('}', open);
            return close > at;
        }

        static List<string> SplitArgs(string s)
        {
            var args = new List<string>();
            char inStr = '\0';
            int chev = 0, depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr != '\0') { if (c == inStr) inStr = '\0'; continue; }
                if (c == '«') chev++;
                else if (c == '»') { if (chev > 0) chev--; }
                else if (chev > 0) { }
                else if (c == '"' || c == '\'') inStr = c;
                else if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    args.Add(s.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            string last = s.Substring(start).Trim();
            if (last != "") args.Add(last);
            return args;
        }

        /// <summary>Сколько «…» осталось незакрытыми после этой строки.
        /// Многострочная проза — не код: её `{` и `}` это подстановка или
        /// просто знаки, и ни развороты, ни сбор функций их не считают.</summary>
        static int ChevRun(int depth, string s)
        {
            foreach (char r in s)
            {
                if (r == '«') depth++;
                else if (r == '»' && depth > 0) depth--;
            }
            return depth;
        }

        /// <summary>
        /// РАЗЛОЖИТЬ ОДНОСТРОЧНЫЕ БЛОКИ по строкам — то, чего ждут все
        /// последующие проходы. Внутри «…» строка проходит КАК ЕСТЬ: её
        /// фигурные скобки — подстановка или просто знаки.
        ///
        /// <para>Раньше это делалось внутри разворота циклов, а сбор функций
        /// читал СЫРОЙ текст. Однострочное объявление
        /// <c>func bow(a) { Кто: {a} }</c> в словарь не попадало вовсе:
        /// число доводов не сверялось, а строка вызова <c>bow(1)</c> не
        /// разворачивалась, командой не была и уходила в наррацию — то есть
        /// ПЕЧАТАЛАСЬ ИГРОКУ. Через CLI та же глава играла правильно.</para>
        /// </summary>
        static List<string> FlattenInline(string src)
        {
            var outLines = new List<string>();
            int chev = 0;
            foreach (string raw in src.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                if (chev > 0 || ChevRun(0, raw) > 0)
                {
                    outLines.Add(raw);
                    chev = ChevRun(chev, raw);
                    continue;
                }
                outLines.AddRange(SplitInline(raw));
            }
            return outLines;
        }

        static string ExpandLoops(string src)
        {
            var stack = new List<Frame>();
            var outLines = new List<string>();

            var srcLines = FlattenInline(src);
            var names = new SynthNamer(srcLines);

            int chev = 0;
            foreach (string raw in srcLines)
            {
                // Проза внутри «…» — не команды: её скобки не открывают и не
                // закрывают блоков.
                if (chev > 0 || ChevRun(0, raw) > 0)
                {
                    outLines.Add(raw);
                    chev = ChevRun(chev, raw);
                    continue;
                }
                string det = raw.Trim();
                int ci = det.IndexOf("//", StringComparison.Ordinal);
                if (ci >= 0) det = det.Substring(0, ci).Trim();

                // A choice option's `{ … }` body is NOT control flow: it is the
                // literal command list the option carries. Pass it through
                // untouched for the choice scanner in Convert.
                if (stack.Count > 0 && stack[stack.Count - 1].kind == "opt")
                {
                    if (det == "}")
                    {
                        stack.RemoveAt(stack.Count - 1);
                        outLines.Add(raw);
                        continue;
                    }
                    // A nested OPTION block is legal: a woven branch may hold
                    // another choice whose options carry blocks. Push a frame so
                    // the matching `}` closes the INNER one — a flat scan would
                    // close the outer block on the inner brace.
                    if (IsOptionBlockOpen(det))
                    {
                        outLines.Add(raw);
                        stack.Add(new Frame { kind = "opt" });
                        continue;
                    }
                    if (det.EndsWith("{", StringComparison.Ordinal))
                        throw new LvnsCompileException(
                            $"choice option body: nested blocks are not allowed (\"{det}\") — the body is a flat " +
                            "command list; move branching to a label and lead there with '-> label'");
                    outLines.Add(raw);
                    continue;
                }

                names.Enter(det); // a `:label` line opens the next naming scope

                if (IsOptionBlockOpen(det))
                {
                    outLines.Add(raw);
                    stack.Add(new Frame { kind = "opt" });
                }
                else if (det.StartsWith("for ") && det.EndsWith("{"))
                {
                    string inner = det.Substring(4);
                    inner = inner.Substring(0, inner.Length - 1).Trim(); // drop trailing {
                    int pos = inner.IndexOf(" in ", StringComparison.Ordinal);
                    if (pos < 0)
                        throw new LvnsCompileException($"for: expected 'for <var> in <expr> {{', got \"{det}\"");
                    string itemVar = inner.Substring(0, pos).Trim();
                    string expr = inner.Substring(pos + 4).Trim();
                    if (itemVar == "" || expr == "")
                        throw new LvnsCompileException($"for: empty variable or collection in \"{det}\"");
                    string tag = names.Site();
                    string idx = names.Name("i", tag), sv = names.Name("src", tag),
                           loop = names.Name("loop", tag), body = names.Name("body", tag),
                           end = names.Name("end", tag);
                    outLines.Add($"set key={sv} expr={GoQuote(expr)}");
                    outLines.Add($"set key={idx} value=0");
                    outLines.Add(":" + loop);
                    outLines.Add($"if expr={GoQuote($"{idx} < len({sv})")} then={body} else={end}");
                    outLines.Add(":" + body);
                    outLines.Add($"set key={itemVar} expr={GoQuote($"{sv}[{idx}]")}");
                    stack.Add(new Frame { kind = "for", loopLbl = loop, endLbl = end, idxVar = idx });
                }
                else if (det.StartsWith("while ") && det.EndsWith("{"))
                {
                    string expr = det.Substring(6);
                    expr = expr.Substring(0, expr.Length - 1).Trim();
                    if (expr == "")
                        throw new LvnsCompileException($"while: empty condition in \"{det}\"");
                    string tag = names.Site();
                    string loop = names.Name("loop", tag), body = names.Name("body", tag), end = names.Name("end", tag);
                    outLines.Add(":" + loop);
                    outLines.Add($"if expr={GoQuote(expr)} then={body} else={end}");
                    outLines.Add(":" + body);
                    stack.Add(new Frame { kind = "while", loopLbl = loop, endLbl = end });
                }
                else if (det.StartsWith("func ") && det.EndsWith("{"))
                {
                    string inner = det.Substring("func ".Length);
                    inner = inner.Substring(0, inner.Length - 1).Trim();
                    string name = inner;
                    int p = inner.IndexOf('(');
                    if (p >= 0) name = inner.Substring(0, p).Trim();
                    if (name == "")
                        throw new LvnsCompileException($"func: missing name in \"{det}\"");
                    // Derived from the function NAME — as stable as a name gets.
                    string skip = names.Name("fnskip", name);
                    outLines.Add("goto " + skip);
                    outLines.Add(":__fn_" + name);
                    stack.Add(new Frame { kind = "func", endLbl = skip });
                }
                else if (det.StartsWith("if ") && det.EndsWith("{"))
                {
                    string cond = det.Substring(3);
                    cond = cond.Substring(0, cond.Length - 1).Trim();
                    if (cond == "")
                        throw new LvnsCompileException($"if: empty condition in \"{det}\"");
                    string tag = names.Site();
                    string thenL = names.Name("then", tag), elseL = names.Name("else", tag), endL = names.Name("end", tag);
                    outLines.Add($"if expr={GoQuote(cond)} then={thenL} else={elseL}");
                    outLines.Add(":" + thenL);
                    stack.Add(new Frame { kind = "if", endLbl = endL, elseLbl = elseL });
                }
                else if (det.Replace(" ", "") == "}else{")
                {
                    if (stack.Count == 0 || stack[stack.Count - 1].kind != "if")
                        throw new LvnsCompileException("'} else {' without a matching 'if … {'");
                    Frame f = stack[stack.Count - 1];
                    outLines.Add("goto " + f.endLbl);
                    outLines.Add(":" + f.elseLbl);
                    f.sawElse = true;
                    stack[stack.Count - 1] = f;
                }
                else if (det == "}")
                {
                    if (stack.Count == 0)
                        throw new LvnsCompileException("unmatched '}' (no open for/while/if block)");
                    Frame f = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    switch (f.kind)
                    {
                        case "for":
                            outLines.Add($"set key={f.idxVar} expr={GoQuote($"{f.idxVar} + 1")}");
                            outLines.Add("goto " + f.loopLbl);
                            outLines.Add(":" + f.endLbl);
                            break;
                        case "while":
                            outLines.Add("goto " + f.loopLbl);
                            outLines.Add(":" + f.endLbl);
                            break;
                        case "func":
                            // Страховочный `return` — ТОЛЬКО если тело им не
                            // кончается. Иначе лоуэринг ставил его дважды, и
                            // второй недостижим: валидатор потом на него и
                            // жалуется. Go так и делает; редакторный путь
                            // ставил всегда.
                            var lastStmt = outLines.Count > 0 ? outLines[outLines.Count - 1].Trim() : "";
                            if (lastStmt != "return" && !lastStmt.StartsWith("return "))
                                outLines.Add("return");
                            outLines.Add(":" + f.endLbl);
                            break;
                        case "if":
                            if (f.sawElse)
                                outLines.Add(":" + f.endLbl);
                            else
                            {
                                outLines.Add(":" + f.elseLbl);
                                outLines.Add(":" + f.endLbl);
                            }
                            break;
                    }
                }
                else
                {
                    outLines.Add(raw);
                }
            }

            if (stack.Count > 0)
                throw new LvnsCompileException(stack[stack.Count - 1].kind == "opt"
                    ? "unclosed choice option body (missing '}')"
                    : "unclosed for/while block (missing '}')");
            return string.Join("\n", outLines);
        }
    }
}
