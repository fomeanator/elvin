using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// A tiny expression evaluator over the player's variable bag — the runtime
    /// behind the <c>expr</c> field of <c>if</c> / option commands, for the
    /// cases the structured single-clause <c>cond</c> can't express:
    /// <code>
    ///   courage >= 2 &amp;&amp; !lied        (a + b) * 2        name == "Mara"
    /// </code>
    /// Grammar: <c>|| &amp;&amp; !</c> (also <c>or</c>/<c>and</c>/<c>not</c>),
    /// <c>== != &gt; &gt;= &lt; &lt;=</c>, <c>+ - * / %</c>, unary <c>-</c>,
    /// parentheses, literals (numbers, '…'/"…" strings, true/false/null) and
    /// identifiers (variables; unknown reads as null). Truthiness: bool itself,
    /// number != 0, non-empty string, null = false. <c>+</c> concatenates when
    /// either side is a string.
    /// </summary>
    public static class LvnExpression
    {
        /// <summary>
        /// Функции, которых ядро знать не может: баланс кошелька, купленные
        /// вещи, группа A/B-теста. Всё это живёт в сервисном слое, а он
        /// снаружи движка — граница сборок здесь не формальность, а причина,
        /// по которой движок остаётся движком, а не частью одного продукта.
        ///
        /// <para>Сервисный слой ставит сюда обработчик; ядро зовёт его для
        /// любого имени, которого нет среди встроенных. Не поставили — вызов
        /// падает ровно как раньше, с «unknown function».</para>
        ///
        /// <para>Обработчик получает имя и аргументы простыми объектами
        /// (string, double, bool, null) и возвращает такой же простой объект.
        /// Внутренние типы вычислителя наружу не выходят: иначе любой их
        /// пересмотр ломал бы чужой код.</para>
        ///
        /// <para>Читать надо ЖИВОЕ значение, а не снимок при старте главы:
        /// игрок покупает платье посреди сцены и должен сразу увидеть ветку,
        /// которая за него отвечает.</para>
        /// </summary>
        public static System.Func<string, System.Collections.Generic.IReadOnlyList<object>, object> HostFunction;

        /// <summary>Ответ обработчика «это имя не моё» — чтобы отличить его от
        /// честного null. Без этого различия опечатка в имени функции молча
        /// превращалась бы в пустое значение вместо ошибки.</summary>
        public static readonly object NotHandled = new object();

        /// <summary>
        /// Добавить свои функции в выражения, НЕ ПОТЕРЯВ чужие.
        ///
        /// <para>Каждый сервис делал это сам: сохранял предыдущий обработчик,
        /// строил цепочку, возвращал NotHandled дальше по ней. Приём верный, но
        /// повторённый дословно — а забудь кто-нибудь сохранить предыдущий, и
        /// функции соседнего сервиса тихо исчезнут из языка: выражение с ними
        /// не упадёт, а просто перестанет что-либо значить.</para>
        /// </summary>
        public static void AddHostFunction(
            System.Func<string, System.Collections.Generic.IReadOnlyList<object>, object> handler)
        {
            if (handler == null) return;
            var previous = HostFunction;
            HostFunction = (name, args) =>
            {
                var mine = handler(name, args);
                if (mine != NotHandled) return mine;
                return previous != null ? previous(name, args) : NotHandled;
            };
        }
        /// <summary>The random stream <c>rand()</c> / <c>chance()</c> draw from
        /// when no explicit one is passed. It is an OBJECT with a seed and a
        /// position (see <see cref="LvnRandom"/>), so a save can carry it and a
        /// test harness can pin it — the old process-wide
        /// <see cref="System.Random"/> could do neither. Never null: assigning
        /// null installs a fresh stream.
        ///
        /// <para>Replace it to make a run reproducible:
        /// <c>LvnExpression.Random = new LvnRandom(seed)</c>. The player snapshots
        /// this stream's position in <c>LvnSnapshot.RngState</c> and restores it
        /// on load, so a reload replays the same rolls instead of re-rolling
        /// them.</para></summary>
        public static LvnRandom Random
        {
            get => _shared;
            set => _shared = value ?? new LvnRandom();
        }

        private static LvnRandom _shared = new LvnRandom();

        public static JToken Evaluate(string expr, IReadOnlyDictionary<string, JToken> vars)
            => Evaluate(expr, vars, null);

        /// <summary>Evaluate against an explicit random stream — for a host that
        /// runs several stories at once and wants each to own its own rolls
        /// instead of sharing <see cref="Random"/>. Null uses the ambient one.</summary>
        public static JToken Evaluate(string expr, IReadOnlyDictionary<string, JToken> vars, LvnRandom rng)
        {
            var p = new Parser(expr, vars, rng);
            var v = p.ParseOr();
            p.ExpectEnd();
            return v.ToJToken();
        }

        public static bool EvaluateBool(string expr, IReadOnlyDictionary<string, JToken> vars)
            => EvaluateBool(expr, vars, null);

        /// <inheritdoc cref="Evaluate(string, IReadOnlyDictionary{string, JToken}, LvnRandom)"/>
        public static bool EvaluateBool(string expr, IReadOnlyDictionary<string, JToken> vars, LvnRandom rng)
        {
            var p = new Parser(expr, vars, rng);
            var v = p.ParseOr();
            p.ExpectEnd();
            return v.Truthy();
        }

        // ── Value model ─────────────────────────────────────────────────────

        private enum Kind { Null, Bool, Num, Str, Json }

        private readonly struct Val
        {
            public readonly Kind Kind;
            public readonly bool B;
            public readonly double N;
            public readonly string S;
            public readonly JToken J; // arrays & objects (lists / maps) — for inventories, stats tables, etc.

            private Val(Kind k, bool b, double n, string s, JToken j) { Kind = k; B = b; N = n; S = s; J = j; }

            public static readonly Val Null = new Val(Kind.Null, false, 0, null, null);
            public static Val Of(bool b) => new Val(Kind.Bool, b, 0, null, null);
            public static Val Of(double n) => new Val(Kind.Num, false, n, null, null);
            public static Val Of(string s) => new Val(Kind.Str, false, 0, s, null);
            public static Val Of(JToken j) => new Val(Kind.Json, false, 0, null, j); // j must be JArray/JObject

            /// <summary>Значение наружу — простым объектом. Внутренние типы
            /// вычислителя не должны просачиваться в чужой код: иначе их
            /// пересмотр ломает всех, кто на них сослался.</summary>
            public object Boxed()
            {
                switch (Kind)
                {
                    case Kind.Bool: return B;
                    case Kind.Num: return N;
                    case Kind.Str: return S;
                    case Kind.Json: return J;
                    default: return null;
                }
            }

            /// <summary>Значение внутрь — из того, что вернул сервисный слой.
            /// Числа приходят каким угодно типом (кошелёк считает в long,
            /// выражения в double), поэтому приводим все, а не только double:
            /// иначе баланс молча станет пустым значением.</summary>
            public static Val From(object o)
            {
                switch (o)
                {
                    case null: return Null;
                    case bool b: return Of(b);
                    case string str: return Of(str);
                    case double d: return Of(d);
                    case float f: return Of((double)f);
                    case int i: return Of((double)i);
                    case long l: return Of((double)l);
                    case JToken t: return From(t);
                    default: return Of(o.ToString());
                }
            }

            public static Val From(JToken t)
            {
                if (t == null) return Null;
                switch (t.Type)
                {
                    case JTokenType.Boolean: return Of((bool)t);
                    case JTokenType.Integer: return Of((double)(long)t);
                    case JTokenType.Float: return Of((double)t);
                    case JTokenType.String: return Of((string)t);
                    case JTokenType.Null: return Null;
                    case JTokenType.Array:
                    case JTokenType.Object: return Of(t);
                    default: return Of(t.ToString());
                }
            }

            public JArray Arr => J as JArray;
            public JObject Obj => J as JObject;

            public bool Truthy()
            {
                switch (Kind)
                {
                    case Kind.Bool: return B;
                    case Kind.Num: return N != 0;
                    case Kind.Str: return !string.IsNullOrEmpty(S);
                    case Kind.Json: return Arr != null ? Arr.Count > 0 : Obj != null ? Obj.Count > 0 : J != null;
                    default: return false;
                }
            }

            public double AsNum()
            {
                switch (Kind)
                {
                    case Kind.Num: return N;
                    case Kind.Bool: return B ? 1 : 0;
                    case Kind.Str:
                        if (double.TryParse(S, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                            return d;
                        throw new LvnException($"expr: '{S}' is not a number");
                    case Kind.Json: throw new LvnException("expr: a list/map is not a number");
                    default: return 0; // null counts as 0, like an unset counter
                }
            }

            public string AsStr()
            {
                switch (Kind)
                {
                    case Kind.Str: return S;
                    case Kind.Bool: return B ? "true" : "false";
                    case Kind.Num:
                        return N == Math.Floor(N) && !double.IsInfinity(N)
                            ? ((long)N).ToString(CultureInfo.InvariantCulture)
                            : N.ToString(CultureInfo.InvariantCulture);
                    case Kind.Json: return J?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
                    default: return "";
                }
            }

            public JToken ToJToken()
            {
                switch (Kind)
                {
                    case Kind.Bool: return B;
                    case Kind.Str: return S;
                    case Kind.Num: return N == Math.Floor(N) && !double.IsInfinity(N) ? (long)N : (JToken)N;
                    case Kind.Json: return J;
                    default: return JValue.CreateNull();
                }
            }

            public bool EqualTo(Val o)
            {
                // An unset variable is null here, but in script terms it defaults
                // to 0 / false / "" (ink semantics). So `unseen == 0`, `flag ==
                // false` and `name == ""` all hold before anything sets them —
                // which is what makes once-only choice gates (`__once == 0`) and
                // first-visit checks work on the very first pass.
                if (Kind == Kind.Null || o.Kind == Kind.Null)
                {
                    var other = Kind == Kind.Null ? o : this;
                    switch (other.Kind)
                    {
                        case Kind.Null: return true;
                        case Kind.Num: return other.N == 0;
                        case Kind.Bool: return !other.B;
                        case Kind.Str: return string.IsNullOrEmpty(other.S);
                        default: return false; // an empty list/map isn't "unset"
                    }
                }
                if (Kind == Kind.Json || o.Kind == Kind.Json) return JToken.DeepEquals(ToJToken(), o.ToJToken());
                if (Kind == Kind.Str || o.Kind == Kind.Str) return AsStr() == o.AsStr();
                return AsNum() == o.AsNum();
            }
        }

        // ── Recursive-descent parser/evaluator ──────────────────────────────

        private sealed class Parser
        {
            private readonly string _s;
            private readonly IReadOnlyDictionary<string, JToken> _vars;
            private readonly LvnRandom _rng;
            private int _i;

            public Parser(string s, IReadOnlyDictionary<string, JToken> vars, LvnRandom rng = null)
            {
                _s = s ?? "";
                _vars = vars;
                _rng = rng ?? Random;
            }

            public void ExpectEnd()
            {
                SkipWs();
                if (_i < _s.Length)
                    throw new LvnException($"expr: unexpected '{_s.Substring(_i)}' in \"{_s}\"");
            }

            public Val ParseOr()
            {
                var v = ParseAnd();
                while (TakeOp("||") || TakeWord("or"))
                {
                    var r = ParseAnd();
                    v = Val.Of(v.Truthy() || r.Truthy());
                }
                return v;
            }

            private Val ParseAnd()
            {
                var v = ParseNot();
                while (TakeOp("&&") || TakeWord("and"))
                {
                    var r = ParseNot();
                    v = Val.Of(v.Truthy() && r.Truthy());
                }
                return v;
            }

            private Val ParseNot()
            {
                SkipWs();
                if (Peek('!') && !(_i + 1 < _s.Length && _s[_i + 1] == '='))
                {
                    _i++;
                    return Val.Of(!ParseNot().Truthy());
                }
                if (TakeWord("not")) return Val.Of(!ParseNot().Truthy());
                return ParseCmp();
            }

            private Val ParseCmp()
            {
                var v = ParseAdd();
                SkipWs();
                string op = null;
                foreach (var cand in new[] { "==", "!=", ">=", "<=", ">", "<" })
                {
                    if (TakeOp(cand)) { op = cand; break; }
                }
                if (op == null) return v;
                var r = ParseAdd();
                switch (op)
                {
                    case "==": return Val.Of(v.EqualTo(r));
                    case "!=": return Val.Of(!v.EqualTo(r));
                    case ">": return Val.Of(v.AsNum() > r.AsNum());
                    case ">=": return Val.Of(v.AsNum() >= r.AsNum());
                    case "<": return Val.Of(v.AsNum() < r.AsNum());
                    default: return Val.Of(v.AsNum() <= r.AsNum());
                }
            }

            private Val ParseAdd()
            {
                var v = ParseMul();
                while (true)
                {
                    SkipWs();
                    if (TakeOp("+"))
                    {
                        var r = ParseMul();
                        v = (v.Kind == Kind.Str || r.Kind == Kind.Str)
                            ? Val.Of(v.AsStr() + r.AsStr())
                            : Val.Of(v.AsNum() + r.AsNum());
                    }
                    else if (PeekBinaryMinus() && TakeOp("-"))
                    {
                        v = Val.Of(v.AsNum() - ParseMul().AsNum());
                    }
                    else return v;
                }
            }

            private Val ParseMul()
            {
                var v = ParseUnary();
                while (true)
                {
                    SkipWs();
                    if (TakeOp("*")) v = Val.Of(v.AsNum() * ParseUnary().AsNum());
                    else if (TakeOp("/"))
                    {
                        var r = ParseUnary().AsNum();
                        if (r == 0) throw new LvnException("expr: division by zero");
                        v = Val.Of(v.AsNum() / r);
                    }
                    else if (TakeOp("%"))
                    {
                        var r = ParseUnary().AsNum();
                        if (r == 0) throw new LvnException("expr: modulo by zero");
                        v = Val.Of(v.AsNum() % r);
                    }
                    else return v;
                }
            }

            private Val ParseUnary()
            {
                SkipWs();
                if (TakeOp("-")) return Val.Of(-ParseUnary().AsNum());
                return ParsePostfix();
            }

            // Postfix indexing / member access on lists & maps: inv[0], bag["potion"],
            // bag.potion, name[i]. Out-of-range / missing keys read as null (like an
            // unset variable), so gates stay forgiving.
            private Val ParsePostfix()
            {
                var v = ParsePrimary();
                while (true)
                {
                    SkipWs();
                    if (Peek('['))
                    {
                        _i++;
                        var idx = ParseOr();
                        SkipWs();
                        if (!Peek(']')) throw new LvnException($"expr: missing ']' in \"{_s}\"");
                        _i++;
                        v = Index(v, idx);
                    }
                    else if (Peek('.') && _i + 1 < _s.Length && (char.IsLetter(_s[_i + 1]) || _s[_i + 1] == '_'))
                    {
                        _i++;
                        int start = _i;
                        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                        v = Index(v, Val.Of(_s.Substring(start, _i - start)));
                    }
                    else return v;
                }
            }

            private static Val Index(Val v, Val key)
            {
                if (v.Kind == Kind.Json && v.Arr != null)
                {
                    int i = (int)key.AsNum();
                    return (i >= 0 && i < v.Arr.Count) ? Val.From(v.Arr[i]) : Val.Null;
                }
                if (v.Kind == Kind.Json && v.Obj != null)
                    return Val.From(v.Obj[key.AsStr()]);
                if (v.Kind == Kind.Str)
                {
                    int i = (int)key.AsNum();
                    return (i >= 0 && i < v.S.Length) ? Val.Of(v.S[i].ToString()) : Val.Null;
                }
                return Val.Null;
            }

            private Val ParsePrimary()
            {
                SkipWs();
                if (_i >= _s.Length)
                    throw new LvnException($"expr: unexpected end of \"{_s}\"");

                var c = _s[_i];
                if (c == '(')
                {
                    _i++;
                    var v = ParseOr();
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != ')')
                        throw new LvnException($"expr: missing ')' in \"{_s}\"");
                    _i++;
                    return v;
                }
                if (c == '[') // list literal: [a, b, c]
                {
                    _i++;
                    var arr = new JArray();
                    SkipWs();
                    if (!Peek(']'))
                    {
                        arr.Add(ParseOr().ToJToken());
                        SkipWs();
                        while (Peek(',')) { _i++; arr.Add(ParseOr().ToJToken()); SkipWs(); }
                    }
                    if (!Peek(']')) throw new LvnException($"expr: missing ']' in \"{_s}\"");
                    _i++;
                    return Val.Of(arr);
                }
                if (c == '{') // map literal: { potion: 3, "sword": 1 }
                {
                    _i++;
                    var obj = new JObject();
                    SkipWs();
                    if (!Peek('}'))
                    {
                        ParsePair(obj);
                        SkipWs();
                        while (Peek(',')) { _i++; ParsePair(obj); SkipWs(); }
                    }
                    if (!Peek('}')) throw new LvnException($"expr: missing '}}' in \"{_s}\"");
                    _i++;
                    return Val.Of(obj);
                }
                if (c == '"' || c == '\'')
                {
                    var end = _s.IndexOf(c, _i + 1);
                    if (end < 0) throw new LvnException($"expr: unterminated string in \"{_s}\"");
                    var s = _s.Substring(_i + 1, end - _i - 1);
                    _i = end + 1;
                    return Val.Of(s);
                }
                if (char.IsDigit(c) || (c == '.' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1])))
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                    var num = _s.Substring(start, _i - start);
                    if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        throw new LvnException($"expr: bad number '{num}'");
                    return Val.Of(d);
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                    var word = _s.Substring(start, _i - start);
                    switch (word)
                    {
                        case "true": return Val.Of(true);
                        case "false": return Val.Of(false);
                        case "null": return Val.Null;
                    }
                    // function call: word( arg, arg, … )
                    SkipWs();
                    if (Peek('('))
                    {
                        _i++;
                        var args = new System.Collections.Generic.List<Val>();
                        SkipWs();
                        if (!Peek(')'))
                        {
                            args.Add(ParseOr());
                            SkipWs();
                            while (Peek(',')) { _i++; args.Add(ParseOr()); SkipWs(); }
                        }
                        if (!Peek(')')) throw new LvnException($"expr: missing ')' after {word}( in \"{_s}\"");
                        _i++;
                        return CallFunc(word, args);
                    }
                    if (_vars != null && _vars.TryGetValue(word, out var t)) return Val.From(t);
                    return Val.Null; // unset var reads as null
                }
                throw new LvnException($"expr: unexpected '{c}' in \"{_s}\"");
            }

            // One "key: value" pair of a map literal. Key is a bare identifier or a
            // quoted string; the value is any expression.
            private void ParsePair(JObject obj)
            {
                SkipWs();
                if (_i >= _s.Length) throw new LvnException($"expr: unterminated map in \"{_s}\"");
                string key;
                var ch = _s[_i];
                if (ch == '"' || ch == '\'')
                {
                    var end = _s.IndexOf(ch, _i + 1);
                    if (end < 0) throw new LvnException($"expr: unterminated key in \"{_s}\"");
                    key = _s.Substring(_i + 1, end - _i - 1);
                    _i = end + 1;
                }
                else
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                    if (_i == start) throw new LvnException($"expr: expected a key in \"{_s}\"");
                    key = _s.Substring(start, _i - start);
                }
                SkipWs();
                if (!Peek(':')) throw new LvnException($"expr: missing ':' after key '{key}' in \"{_s}\"");
                _i++;
                obj[key] = ParseOr().ToJToken();
            }

            // Built-in expression functions. rand()/chance() are the only stateful
            // ones — they draw from an <see cref="LvnRandom"/> stream (the ambient
            // LvnExpression.Random unless the caller passed its own); everything
            // else is pure. rand(a,b)=int in [a,b]; rand(n)=int in [0,n]; rand()=0..1.
            //
            // That stream is part of the save (LvnSnapshot.RngState), so a reload
            // replays the SAME rolls it showed before the crash instead of
            // re-rolling the fight. It used to be a private static System.Random:
            // one per process, unseedable, invisible to the snapshot — which made
            // a seeded soak run un-reproducible (the flake hunter could not tell a
            // flake from the content rolling a different number) and let a reload
            // re-roll a lost battle.
            private Val CallFunc(string name, System.Collections.Generic.List<Val> a)
            {
                double N(int i) => i < a.Count ? a[i].AsNum() : 0;
                Val A(int i) => i < a.Count ? a[i] : Val.Null;
                switch (name)
                {
                    // ── numbers / chance ──
                    case "rand":
                        if (a.Count == 0) return Val.Of(_rng.NextDouble());
                        if (a.Count == 1) { int n = (int)System.Math.Round(N(0)); return Val.Of((double)_rng.NextInt(0, n < 0 ? 0 : n)); }
                        // NextInt is inclusive on both ends and orders its bounds.
                        return Val.Of((double)_rng.NextInt((int)System.Math.Round(N(0)), (int)System.Math.Round(N(1))));
                    case "chance": return Val.Of(_rng.NextDouble() < (a.Count > 0 ? N(0) : 0.5));
                    case "min": return Val.Of(a.Count == 0 ? 0 : (a.Count == 1 ? N(0) : System.Math.Min(N(0), N(1))));
                    case "max": return Val.Of(a.Count == 0 ? 0 : (a.Count == 1 ? N(0) : System.Math.Max(N(0), N(1))));
                    case "abs": return Val.Of(System.Math.Abs(N(0)));
                    case "floor": return Val.Of(System.Math.Floor(N(0)));
                    case "round": return Val.Of(System.Math.Round(N(0)));

                    // ── collections: length / membership / read ──
                    case "len":
                    {
                        var v = A(0);
                        if (v.Arr != null) return Val.Of((double)v.Arr.Count);
                        if (v.Obj != null) return Val.Of((double)v.Obj.Count);
                        if (v.Kind == Kind.Str) return Val.Of((double)(v.S?.Length ?? 0));
                        return Val.Of(0);
                    }
                    case "has":
                    {
                        var v = A(0); var x = A(1);
                        if (v.Arr != null) { foreach (var e in v.Arr) if (Val.From(e).EqualTo(x)) return Val.Of(true); return Val.Of(false); }
                        if (v.Obj != null) return Val.Of(v.Obj[x.AsStr()] != null);
                        if (v.Kind == Kind.Str) return Val.Of((v.S ?? "").Contains(x.AsStr()));
                        return Val.Of(false);
                    }
                    case "get": // get(coll, key[, default]) — safe read
                    {
                        var v = A(0); var key = A(1); var def = a.Count > 2 ? a[2] : Val.Null;
                        var r = Index(v, key);
                        return r.Kind == Kind.Null ? def : r;
                    }
                    case "indexof":
                    {
                        var arr = A(0).Arr; var x = A(1);
                        if (arr != null) for (int i = 0; i < arr.Count; i++) if (Val.From(arr[i]).EqualTo(x)) return Val.Of((double)i);
                        return Val.Of(-1);
                    }
                    case "count":
                    {
                        var arr = A(0).Arr; var x = A(1); int n = 0;
                        if (arr != null) foreach (var e in arr) if (Val.From(e).EqualTo(x)) n++;
                        return Val.Of((double)n);
                    }
                    case "sum":
                    {
                        var arr = A(0).Arr; double s = 0;
                        if (arr != null) foreach (var e in arr) s += Val.From(e).AsNum();
                        return Val.Of(s);
                    }
                    case "first": { var arr = A(0).Arr; return (arr != null && arr.Count > 0) ? Val.From(arr[0]) : Val.Null; }
                    case "last": { var arr = A(0).Arr; return (arr != null && arr.Count > 0) ? Val.From(arr[arr.Count - 1]) : Val.Null; }
                    case "keys":
                    {
                        var obj = A(0).Obj; var outA = new JArray();
                        if (obj != null) foreach (var p in obj.Properties()) outA.Add(p.Name);
                        return Val.Of(outA);
                    }
                    case "vals":
                    {
                        var obj = A(0).Obj; var outA = new JArray();
                        if (obj != null) foreach (var p in obj.Properties()) outA.Add(p.Value.DeepClone());
                        return Val.Of(outA);
                    }

                    // ── collections: pure builders (return a NEW value; reassign with `key = …`) ──
                    case "list": { var outA = new JArray(); foreach (var v in a) outA.Add(v.ToJToken()); return Val.Of(outA); }
                    case "push": { var arr = CloneArr(A(0)); arr.Add(A(1).ToJToken()); return Val.Of(arr); }
                    case "pop": { var arr = CloneArr(A(0)); if (arr.Count > 0) arr.RemoveAt(arr.Count - 1); return Val.Of(arr); }
                    case "removeat": { var arr = CloneArr(A(0)); int i = (int)N(1); if (i >= 0 && i < arr.Count) arr.RemoveAt(i); return Val.Of(arr); }
                    case "remove": // drop first element equal to x
                    {
                        var arr = CloneArr(A(0)); var x = A(1);
                        for (int i = 0; i < arr.Count; i++) if (Val.From(arr[i]).EqualTo(x)) { arr.RemoveAt(i); break; }
                        return Val.Of(arr);
                    }
                    case "slice": // slice(list, start[, end])
                    {
                        var src = A(0).Arr; var outA = new JArray();
                        if (src != null)
                        {
                            int s = (int)N(1), e = a.Count > 2 ? (int)N(2) : src.Count;
                            if (s < 0) s = 0; if (e > src.Count) e = src.Count;
                            for (int i = s; i < e; i++) outA.Add(src[i].DeepClone());
                        }
                        return Val.Of(outA);
                    }
                    case "concat":
                    {
                        var outA = new JArray();
                        foreach (var v in a) { if (v.Arr != null) foreach (var e in v.Arr) outA.Add(e.DeepClone()); else outA.Add(v.ToJToken()); }
                        return Val.Of(outA);
                    }
                    case "put": // put(map, key, val) — new map with key set (also creates a map)
                    {
                        var obj = CloneObj(A(0)); obj[A(1).AsStr()] = A(2).ToJToken(); return Val.Of(obj);
                    }
                    case "del": // del(map, key) — new map without key
                    {
                        var obj = CloneObj(A(0)); obj.Remove(A(1).AsStr()); return Val.Of(obj);
                    }

                    default:
                    {
                        var host = HostFunction;
                        if (host != null)
                        {
                            var args = new System.Collections.Generic.List<object>(a.Count);
                            for (int i = 0; i < a.Count; i++) args.Add(a[i].Boxed());
                            object res;
                            try { res = host(name, args); }
                            catch (System.Exception e)
                            {
                                // Сервисный слой ходит в сеть и в кэш. Его сбой
                                // не должен ронять главу: считаем, что условие
                                // не выполнено, и говорим об этом в лог.
                                UnityEngine.Debug.LogWarning($"[lvn-expr] {name}(): {e.Message}");
                                return Val.Null;
                            }
                            if (res != NotHandled) return Val.From(res);
                        }
                        throw new LvnException($"expr: unknown function {name}()");
                    }
                }
            }

            private static JArray CloneArr(Val v) => v.Arr != null ? (JArray)v.Arr.DeepClone() : new JArray();
            private static JObject CloneObj(Val v) => v.Obj != null ? (JObject)v.Obj.DeepClone() : new JObject();

            // ── lexing helpers ──────────────────────────────────────────────

            private void SkipWs()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private bool Peek(char c) => _i < _s.Length && _s[_i] == c;

            private bool PeekBinaryMinus()
            {
                SkipWs();
                return Peek('-');
            }

            private bool TakeOp(string op)
            {
                SkipWs();
                if (_i + op.Length > _s.Length || string.CompareOrdinal(_s, _i, op, 0, op.Length) != 0)
                    return false;
                // don't take ">" out of ">=", "<" out of "<=".
                if ((op == ">" || op == "<") && _i + 1 < _s.Length && _s[_i + 1] == '=')
                    return false;
                _i += op.Length;
                return true;
            }

            private bool TakeWord(string w)
            {
                SkipWs();
                if (_i + w.Length > _s.Length || string.CompareOrdinal(_s, _i, w, 0, w.Length) != 0)
                    return false;
                int after = _i + w.Length;
                if (after < _s.Length && (char.IsLetterOrDigit(_s[after]) || _s[after] == '_'))
                    return false;
                _i = after;
                return true;
            }
        }
    }

    /// <summary>
    /// The random stream behind <c>rand()</c> and <c>chance()</c>: an instance
    /// with a seed, a position, and a state string a save can carry.
    ///
    /// <para>Three things the old <c>private static System.Random</c> could not
    /// do, and this can:</para>
    /// <list type="bullet">
    ///   <item>be SEEDED — <c>new LvnRandom(seed)</c> replays a run exactly, so
    ///   the nightly flake hunter (qa/stability.sh) can tell a flaky test from
    ///   content that simply rolled a different number;</item>
    ///   <item>be SAVED — <see cref="SaveState"/> / <see cref="TryLoadState"/>
    ///   ride along in <c>LvnPlayer.LvnSnapshot.RngState</c>, so a reload
    ///   continues the same stream instead of re-rolling the fight the player
    ///   just lost;</item>
    ///   <item>be OWNED — a host running two stories at once gives each its own
    ///   instance instead of interleaving draws in one global.</item>
    /// </list>
    ///
    /// <para>The generator is splitmix64: 64 bits of state, no warm-up, and —
    /// unlike <see cref="System.Random"/>, whose sequence is explicitly not
    /// contractual across runtimes — the same seed yields the same sequence on
    /// every platform and every .NET/Mono/IL2CPP version. A save moved from an
    /// editor run to a phone therefore resumes the same rolls.</para>
    ///
    /// <para>Not thread-safe: like the rest of the player, drive it from one
    /// thread.</para>
    /// </summary>
    public sealed class LvnRandom
    {
        private const ulong Gamma = 0x9E3779B97F4A7C15UL; // splitmix64 increment
        private const string Tag = "lvnrng1";             // state-string version

        private static long _born; // distinguishes instances made in the same tick

        private ulong _state;

        /// <summary>The seed this stream started from — carried through save/load
        /// for diagnostics ("which seed produced this bug report?").</summary>
        public ulong Seed { get; private set; }

        /// <summary>How many numbers have been drawn since the seed. Together
        /// with <see cref="Seed"/> it identifies the position exactly, and it is
        /// what makes a QA report reproducible.</summary>
        public long Draws { get; private set; }

        /// <summary>A stream seeded from the clock — different every run, which
        /// is what a shipped game wants.</summary>
        public LvnRandom() : this(FreshSeed()) { }

        /// <summary>A stream seeded explicitly — the same seed always replays the
        /// same sequence.</summary>
        public LvnRandom(ulong seed) { Reseed(seed); }

        /// <summary>Restart the stream from <paramref name="seed"/>.</summary>
        public void Reseed(ulong seed)
        {
            Seed = seed;
            _state = seed;
            Draws = 0;
        }

        private static ulong FreshSeed()
        {
            unchecked
            {
                var n = (ulong)System.Threading.Interlocked.Increment(ref _born);
                return Mix((ulong)DateTime.UtcNow.Ticks + n * Gamma);
            }
        }

        private static ulong Mix(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>The next 64 raw bits.</summary>
        public ulong NextULong()
        {
            unchecked
            {
                _state += Gamma;
                Draws++;
                return Mix(_state);
            }
        }

        /// <summary>Uniform double in [0, 1) — the 53 bits a double can hold.</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);

        /// <summary>Uniform int in [<paramref name="lo"/>, <paramref name="hi"/>],
        /// INCLUSIVE on both ends (that is what <c>rand(a, b)</c> promises) and
        /// tolerant of reversed bounds. Rejection-sampled, so no modulo bias.</summary>
        public int NextInt(int lo, int hi)
        {
            if (hi < lo) { var t = lo; lo = hi; hi = t; }
            ulong span = (ulong)((long)hi - lo) + 1UL; // ≤ 2^32, never 0
            ulong limit = ulong.MaxValue - ulong.MaxValue % span; // largest multiple of span
            ulong r;
            do { r = NextULong(); } while (r >= limit);
            return (int)((long)lo + (long)(r % span));
        }

        /// <summary>The stream's position as a string a save can hold.
        ///
        /// <para>A string, not a number, on purpose: the state is 64 bits, and a
        /// save blob round-trips through JSON parsers that decode every number as
        /// a double (Go's <c>map[string]interface{}</c>, every browser). Anything
        /// above 2^53 would come back silently wrong — the stream would resume in
        /// the wrong place, which is worse than not resuming it at all.</para></summary>
        public string SaveState() =>
            Tag + ":" + Seed.ToString("x16", CultureInfo.InvariantCulture) +
            ":" + _state.ToString("x16", CultureInfo.InvariantCulture) +
            ":" + Draws.ToString(CultureInfo.InvariantCulture);

        /// <summary>Move this stream to a saved position. Returns false — leaving
        /// the stream untouched — for anything it cannot read (null, an older or
        /// newer format, a truncated blob), so a corrupt save costs the player a
        /// re-rolled fight, never a crash on load.</summary>
        public bool TryLoadState(string state)
        {
            if (string.IsNullOrEmpty(state)) return false;
            var parts = state.Split(':');
            if (parts.Length != 4 || !string.Equals(parts[0], Tag, StringComparison.Ordinal)) return false;
            if (!ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var seed)) return false;
            if (!ulong.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var st)) return false;
            if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var draws)) return false;
            Seed = seed;
            _state = st;
            Draws = draws;
            return true;
        }

        /// <summary>An independent stream at this one's current position — for a
        /// "what if" simulation that must not disturb the player's rolls.</summary>
        public LvnRandom Clone()
        {
            var c = new LvnRandom(Seed);
            c._state = _state;
            c.Draws = Draws;
            return c;
        }

        public override string ToString() => SaveState();
    }
}
