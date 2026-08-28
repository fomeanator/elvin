// Expression evaluator for the browser playground — the same surface the
// engine's LvnExpression covers for the recipes that matter: numbers,
// strings, lists, vars, arithmetic, comparisons, && || !, and the built-in
// functions the docs use. Pure and tiny by design; unknown identifiers
// evaluate to 0/"" like the engine treats unset vars.

export function evalExpr(src, vars) {
  return new Parser(String(src ?? ""), vars).parse();
}

export function evalBool(src, vars) {
  return truthy(evalExpr(src, vars));
}

export function truthy(v) {
  if (Array.isArray(v)) return v.length > 0;
  if (typeof v === "string") return v.length > 0;
  return !!v && v !== 0;
}

// Dotted keys are NESTED paths, not flat names with a dot in them: `set
// key="Way.Moral"` writes vars.Way.Moral, and the expression `Way.Moral` reads
// it back through member access. Both halves must agree — mirrors
// LvnPlayer.GetVarPath/SetVarPath and LvnExpression's postfix `.name`. A missing
// segment reads as null (which compares equal to 0/""/false, ink-style).
export function getVarPath(vars, key) {
  if (!key) return null;
  const segs = String(key).split(".");
  let cur = vars;
  for (const s of segs) {
    if (cur === null || cur === undefined || typeof cur !== "object") return null;
    cur = Array.isArray(cur) ? cur[Math.trunc(num(s))] : cur[s];
    if (cur === undefined) return null;
  }
  return cur === undefined ? null : cur;
}

// Writes the path, creating intermediate maps. A non-map segment in the way is
// replaced, exactly as SetVarPath does — the alternative is a silent no-op.
export function setVarPath(vars, key, value) {
  if (!key) return;
  const segs = String(key).split(".");
  let cur = vars;
  for (let i = 0; i < segs.length - 1; i++) {
    const s = segs[i];
    if (cur[s] === null || cur[s] === undefined || typeof cur[s] !== "object" || Array.isArray(cur[s])) cur[s] = {};
    cur = cur[s];
  }
  cur[segs[segs.length - 1]] = value;
}

// The built-in set is CLOSED and must match LvnExpression.cs case-for-case:
// an author who tests a scene here and ships it to Unity has to get the same
// numbers. Divergence here is worse than a missing feature — it is a bug that
// only appears after the content leaves the playground. (This file used to
// carry 8 of the 26 and silently threw on the rest, so every list/map recipe
// worked in the app and died in the browser.)
const isMap = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
const asArr = (v) => (Array.isArray(v) ? v.slice() : []);
const asMap = (v) => (isMap(v) ? { ...v } : {});
const randInt = (lo, hi) => lo + Math.floor(Math.random() * (hi - lo + 1)); // inclusive

const FUNCS = {
  // numbers
  rand: (...a) => {
    if (a.length === 0) return Math.random();
    if (a.length === 1) { const n = Math.round(num(a[0])); return randInt(0, n < 0 ? 0 : n); }
    let lo = Math.round(num(a[0])), hi = Math.round(num(a[1]));
    if (lo > hi) { const t = lo; lo = hi; hi = t; }
    return randInt(lo, hi);
  },
  chance: (...a) => Math.random() < (a.length > 0 ? num(a[0]) : 0.5),

  // Функции хоста: их значения приходят из кошелька и гардероба, которых у
  // веб-плеера нет. Отвечаем безопасным пустым ответом, а не падаем: ветка за
  // покупку в песочнице просто не открывается, и это правильный ответ —
  // покупок здесь и правда нет. Расхождение с C# намеренное и записано в
  // conformance; молчать о нём нельзя, поэтому оно названо здесь.
  has_item: () => false,
  balance: () => 0,
  worn: () => "",
  // В песочнице игрок один и он же автор: держим его в первой группе, чтобы
  // сцена была предсказуемой. Делить одного человека пополам бессмысленно.
  abtest: () => "a",
  // min/max read only their first two arguments — mirrors LvnExpression, which
  // ignores the rest. Extra args are silently dropped in BOTH runtimes.
  min: (...a) => (a.length === 0 ? 0 : a.length === 1 ? num(a[0]) : Math.min(num(a[0]), num(a[1]))),
  max: (...a) => (a.length === 0 ? 0 : a.length === 1 ? num(a[0]) : Math.max(num(a[0]), num(a[1]))),
  abs: (a) => Math.abs(num(a)),
  floor: (a) => Math.floor(num(a)),
  round: (a) => Math.round(num(a)),

  // reads — work on lists, maps and (for has) strings
  len: (a) => (Array.isArray(a) || typeof a === "string" ? a.length : isMap(a) ? Object.keys(a).length : 0),
  has: (coll, x) => {
    if (Array.isArray(coll)) return coll.some((e) => eq(e, x));
    if (isMap(coll)) return Object.prototype.hasOwnProperty.call(coll, String(x));
    if (typeof coll === "string") return coll.includes(String(x));
    return false;
  },
  get: (coll, key, ...def) => {
    const fallback = def.length > 0 ? def[0] : null;
    let r = null;
    if (Array.isArray(coll)) { const i = Math.trunc(num(key)); r = i >= 0 && i < coll.length ? coll[i] : null; }
    else if (isMap(coll)) r = Object.prototype.hasOwnProperty.call(coll, String(key)) ? coll[String(key)] : null;
    return r === null || r === undefined ? fallback : r;
  },
  indexof: (arr, x) => (Array.isArray(arr) ? arr.findIndex((e) => eq(e, x)) : -1),
  count: (arr, x) => (Array.isArray(arr) ? arr.filter((e) => eq(e, x)).length : 0),
  sum: (arr) => (Array.isArray(arr) ? arr.reduce((s, e) => s + num(e), 0) : 0),
  first: (arr) => (Array.isArray(arr) && arr.length > 0 ? arr[0] : null),
  last: (arr) => (Array.isArray(arr) && arr.length > 0 ? arr[arr.length - 1] : null),
  keys: (m) => (isMap(m) ? Object.keys(m) : []),
  vals: (m) => (isMap(m) ? Object.values(m) : []),

  // builders — all PURE: they return a new collection, never mutate in place
  list: (...a) => a,
  push: (arr, x) => { const o = asArr(arr); o.push(x); return o; },
  pop: (arr) => { const o = asArr(arr); o.pop(); return o; }, // the list WITHOUT its last item
  removeat: (arr, i) => { const o = asArr(arr); const k = Math.trunc(num(i)); if (k >= 0 && k < o.length) o.splice(k, 1); return o; },
  remove: (arr, x) => { const o = asArr(arr); const k = o.findIndex((e) => eq(e, x)); if (k >= 0) o.splice(k, 1); return o; },
  slice: (arr, s, ...e) => {
    const src = Array.isArray(arr) ? arr : [];
    let from = Math.trunc(num(s)), to = e.length > 0 ? Math.trunc(num(e[0])) : src.length;
    if (from < 0) from = 0;
    if (to > src.length) to = src.length;
    return from < to ? src.slice(from, to) : [];
  },
  concat: (...a) => { const o = []; for (const v of a) { if (Array.isArray(v)) o.push(...v); else o.push(v); } return o; },
  put: (m, k, v) => { const o = asMap(m); o[String(k)] = v; return o; },
  del: (m, k) => { const o = asMap(m); delete o[String(k)]; return o; },
};

// The structured condition form {key, op, value} the articy importer emits.
// eq/ne compare BY VALUE (strings and bools too, with ink "unset == 0 == '' ==
// false" semantics); the orderings coerce to number. Mirrors LvnPlayer.EvalCond
// — an unknown op there falls back to "left is non-zero", so it does here too.
export function evalStructuredCond(cond, vars) {
  if (!cond) return false;
  const left = getVarPath(vars, cond.key);
  const right = cond.value === undefined ? null : cond.value;
  switch (cond.op) {
    case "eq": return eq(left, right);
    case "ne": return !eq(left, right);
    case "lt": return num(left) < num(right);
    case "lte": return num(left) <= num(right);
    case "gt": return num(left) > num(right);
    case "gte": return num(left) >= num(right);
    default: return num(left) !== 0;
  }
}

function num(v) {
  if (typeof v === "number") return v;
  if (typeof v === "boolean") return v ? 1 : 0;
  const n = parseFloat(v);
  return Number.isFinite(n) ? n : 0;
}

function eq(a, b) {
  if (typeof a === "number" || typeof b === "number") return num(a) === num(b);
  return String(a) === String(b);
}

// One element out of a list (numeric), a map (string key) or a string
// (character) — out of range / missing reads as null. Mirrors LvnExpression.Index.
function index(v, key) {
  if (Array.isArray(v)) { const i = Math.trunc(num(key)); return i >= 0 && i < v.length ? v[i] : null; }
  if (isMap(v)) { const r = v[String(key)]; return r === undefined ? null : r; }
  if (typeof v === "string") { const i = Math.trunc(num(key)); return i >= 0 && i < v.length ? v[i] : null; }
  return null;
}

class Parser {
  constructor(src, vars) {
    this.src = src;
    this.pos = 0;
    this.vars = vars || {};
  }

  parse() {
    const v = this.or();
    this.ws();
    if (this.pos < this.src.length) throw new Error(`unexpected '${this.src.slice(this.pos, this.pos + 8)}'`);
    return v;
  }

  ws() { while (this.pos < this.src.length && /\s/.test(this.src[this.pos])) this.pos++; }
  peek(s) { this.ws(); return this.src.startsWith(s, this.pos); }
  eat(s) { if (this.peek(s)) { this.pos += s.length; return true; } return false; }

  or() {
    let v = this.and();
    while (this.eat("||") || this.eatWord("or")) v = truthy(v) || truthy(this.and());
    return v;
  }

  and() {
    let v = this.not();
    while (this.eat("&&") || this.eatWord("and")) v = truthy(v) && truthy(this.not());
    return v;
  }

  not() {
    if (this.eat("!") || this.eatWord("not")) return !truthy(this.not());
    return this.cmp();
  }

  cmp() {
    let v = this.add();
    for (;;) {
      if (this.eat(">=")) v = num(v) >= num(this.add());
      else if (this.eat("<=")) v = num(v) <= num(this.add());
      else if (this.eat("==")) v = eq(v, this.add());
      else if (this.eat("!=")) v = !eq(v, this.add());
      else if (this.eat(">")) v = num(v) > num(this.add());
      else if (this.eat("<")) v = num(v) < num(this.add());
      else return v;
    }
  }

  add() {
    let v = this.mul();
    for (;;) {
      if (this.eat("+")) {
        const r = this.mul();
        if (Array.isArray(v) || Array.isArray(r)) v = [].concat(v ?? [], r ?? []);
        else if (typeof v === "string" || typeof r === "string") v = String(v) + String(r);
        else v = num(v) + num(r);
      } else if (this.peekMinusBinary()) {
        this.eat("-");
        const r = this.mul();
        if (Array.isArray(v)) v = v.filter((e) => !eq(e, r)); // list minus element
        else v = num(v) - num(r);
      } else return v;
    }
  }

  // A '-' here is binary (we just produced a value); unary minus lives in atom.
  peekMinusBinary() { return this.peek("-") && !this.peek("->"); }

  mul() {
    let v = this.postfix();
    for (;;) {
      if (this.eat("*")) v = num(v) * num(this.postfix());
      // ДЕЛЕНИЕ НА НОЛЬ — ОШИБКА, а не тихий ноль. Здесь стояло `r === 0 ? 0`,
      // и это расходилось с движком, который бросает "expr: division by zero".
      // Автор пробует формулу в playground, видит ноль и считает её рабочей —
      // а в приложении та же строка останавливает главу. Правило одно на все
      // рантаймы: неизвестное и невозможное — ошибка, никогда не пропуск.
      else if (this.eat("/")) {
        const r = num(this.postfix());
        if (r === 0) throw new Error("expr: division by zero");
        v = num(v) / r;
      } else if (this.eat("%")) {
        const r = num(this.postfix());
        if (r === 0) throw new Error("expr: modulo by zero");
        v = num(v) % r;
      }
      else return v;
    }
  }

  // Postfix chain on a value: `.name` member access and `[expr]` indexing, the
  // same pair LvnExpression applies. `Way.Moral` is therefore read as "the var
  // Way, indexed by Moral" — which is how `set key="Way.Moral"` stored it. The
  // articy importer emits hundreds of such stats, and without this they were a
  // hard parse error in the browser while working in Unity.
  postfix() {
    let v = this.atom();
    for (;;) {
      if (this.peek(".") && /[A-Za-zА-Яа-яЁё_]/.test(this.src[this.pos + 1] || "")) {
        this.pos++;
        const m = /^[A-Za-zА-Яа-яЁё_][A-Za-zА-Яа-яЁё_0-9]*/.exec(this.src.slice(this.pos));
        this.pos += m[0].length;
        v = index(v, m[0]);
      } else if (this.peek("[")) {
        this.eat("[");
        const k = this.or();
        if (!this.eat("]")) throw new Error("missing ] in index");
        v = index(v, k);
      } else return v;
    }
  }

  atom() {
    this.ws();
    const c = this.src[this.pos];
    if (c === undefined) throw new Error("unexpected end of expression");

    if (this.eat("(")) {
      const v = this.or();
      if (!this.eat(")")) throw new Error("missing )");
      return v;
    }
    if (this.eat("[")) {
      const items = [];
      if (!this.eat("]")) {
        do { items.push(this.or()); } while (this.eat(","));
        if (!this.eat("]")) throw new Error("missing ]");
      }
      return items;
    }
    if (c === '"' || c === "'" || c === "«") return this.string(c === "«" ? "»" : c);
    if (c === "-") { this.pos++; return -num(this.atom()); }
    if (/[0-9.]/.test(c)) return this.number();
    if (/[A-Za-zА-Яа-яЁё_]/.test(c)) return this.ident();
    throw new Error(`unexpected '${c}'`);
  }

  string(close) {
    this.pos++; // opening quote
    let out = "";
    while (this.pos < this.src.length && this.src[this.pos] !== close) {
      if (this.src[this.pos] === "\\" && this.pos + 1 < this.src.length) this.pos++;
      out += this.src[this.pos++];
    }
    this.pos++; // closing quote
    return out;
  }

  number() {
    const m = /^[0-9]*\.?[0-9]+/.exec(this.src.slice(this.pos));
    this.pos += m[0].length;
    return parseFloat(m[0]);
  }

  eatWord(w) {
    this.ws();
    const re = new RegExp(`^${w}(?![A-Za-zА-Яа-яЁё_0-9])`);
    if (re.test(this.src.slice(this.pos))) { this.pos += w.length; return true; }
    return false;
  }

  ident() {
    const m = /^[A-Za-zА-Яа-яЁё_][A-Za-zА-Яа-яЁё_0-9]*/.exec(this.src.slice(this.pos));
    const name = m[0];
    this.pos += name.length;
    if (name === "true") return true;
    if (name === "false") return false;
    if (this.peek("(")) {
      this.eat("(");
      const args = [];
      if (!this.eat(")")) {
        do { args.push(this.or()); } while (this.eat(","));
        if (!this.eat(")")) throw new Error("missing ) in call");
      }
      const fn = FUNCS[name];
      if (!fn) throw new Error(`unknown function ${name}()`);
      return fn(...args);
    }
    return name in this.vars ? this.vars[name] : 0;
  }
}

// {expr} interpolation for say/text templates; {{ }} are literal braces.
export function interpolate(template, vars) {
  if (!template) return "";
  return String(template)
    .replace(/\{\{/g, "\u0001").replace(/\}\}/g, "\u0002")
    .replace(/\{([^{}]+)\}/g, (_, e) => {
      // A bare name that is not set renders as the literal "{key}" so missing
      // data is VISIBLE — that is the typo signal the engine documents. Only
      // interpolation does this: in a condition an unset var stays 0/""/false.
      const bare = e.trim();
      if (/^[A-Za-zА-Яа-яЁё_][A-Za-zА-Яа-яЁё_0-9]*(\.[A-Za-zА-Яа-яЁё_0-9]+)*$/.test(bare)
          && getVarPath(vars, bare) === null) {
        return "{" + e + "}";
      }
      try { return fmt(evalExpr(e, vars)); } catch { return "{" + e + "}"; }
    })
    // Deliberate control-char sentinels: `{{`/`}}` are parked on U+0001/U+0002
    // above so the interpolation pass cannot mistake them for a template, then
    // restored here. Author text never contains these bytes.
    // eslint-disable-next-line no-control-regex
    .replace(/\u0001/g, "{").replace(/\u0002/g, "}");
}

function fmt(v) {
  if (Array.isArray(v)) return v.map(fmt).join(", ");
  if (typeof v === "number") return Number.isInteger(v) ? String(v) : String(Math.round(v * 100) / 100);
  return String(v);
}
