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

const FUNCS = {
  has: (list, x) => Array.isArray(list) && list.some((e) => eq(e, x)),
  rand: (n) => Math.floor(Math.random() * Math.max(1, Math.trunc(num(n)))),
  min: (...a) => Math.min(...a.map(num)),
  max: (...a) => Math.max(...a.map(num)),
  abs: (a) => Math.abs(num(a)),
  floor: (a) => Math.floor(num(a)),
  round: (a) => Math.round(num(a)),
  int: (a) => Math.trunc(num(a)),
  len: (a) => (Array.isArray(a) || typeof a === "string" ? a.length : 0),
};

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
    let v = this.atom();
    for (;;) {
      if (this.eat("*")) v = num(v) * num(this.atom());
      else if (this.eat("/")) { const r = num(this.atom()); v = r === 0 ? 0 : num(v) / r; }
      else if (this.eat("%")) { const r = num(this.atom()); v = r === 0 ? 0 : num(v) % r; }
      else return v;
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
      try { return fmt(evalExpr(e, vars)); } catch { return "{" + e + "}"; }
    })
    .replace(/\u0001/g, "{").replace(/\u0002/g, "}");
}

function fmt(v) {
  if (Array.isArray(v)) return v.map(fmt).join(", ");
  if (typeof v === "number") return Number.isInteger(v) ? String(v) : String(Math.round(v * 100) / 100);
  return String(v);
}
