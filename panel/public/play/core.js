// The playground's story interpreter — a faithful JS mini-port of LvnPlayer
// for the .lvn subset a browser demo needs: say / choice (with timeout) /
// input / label / goto / call / return / if / set / inc / wait, plus every
// staging command forwarded to the host via onStage (bg, actor, text, audio,
// fade, …) so the renderer draws what it supports and ignores the rest.
//
// Pure and DOM-free: advance()/choose()/submitInput() return a pause event
// ({type: say|choice|input|wait|end, …}); the UI renders it and calls back.

import { evalExpr, evalBool, interpolate, truthy } from "./expr.js";

export class Player {
  constructor(doc, { onStage } = {}) {
    this.script = (doc && doc.script) || [];
    this.vars = Object.create(null);
    this.ip = 0;
    this.callStack = [];
    this.finished = false;
    this.onStage = onStage || (() => {});
    this.labels = Object.create(null);
    this.script.forEach((c, i) => {
      if (c.op === "label" && c.id) this.labels[c.id] = i;
    });
  }

  jump(label) {
    if (label === "__end") { this.ip = this.script.length; return; }
    if (label in this.labels) { this.ip = this.labels[label]; return; }
    this.ip = this.script.length; // dangling jump ends the story (validator catches it upstream)
  }

  /** Run until the next pause; returns the pause event. */
  advance() {
    // A stray advance while a stop is open must not skip it — re-issue the
    // same pause instead (the UI's overlay stays the source of truth).
    if (this._awaitInput) {
      const c = this._awaitInput;
      return { type: "input", var: c.var, prompt: interpolate(c.prompt, this.vars), default: c.default ?? "", max: c.max ?? 0 };
    }
    if (this._choice) return this.pauseChoice(this._choice);
    let budget = this.script.length + 10000; // goto-cycle guard
    while (!this.finished && this.ip >= 0 && this.ip < this.script.length) {
      if (--budget < 0) throw new Error("infinite loop: a goto cycle with no say/choice");
      const c = this.script[this.ip];
      switch (c.op) {
        case "label":
          this.ip++;
          break;
        case "set": {
          let v;
          if (c.expr !== undefined) { try { v = evalExpr(c.expr, this.vars); } catch { v = 0; } }
          else v = c.value;
          if (c.key) this.vars[c.key] = v;
          this.ip++;
          break;
        }
        case "inc": {
          let by = 1;
          if (c.by !== undefined) {
            try { by = typeof c.by === "number" ? c.by : evalExpr(c.by, this.vars); } catch { by = 0; }
          }
          const cur = this.vars[c.key];
          this.vars[c.key] = (typeof cur === "number" ? cur : parseFloat(cur) || 0) + by;
          this.ip++;
          break;
        }
        case "goto":
          this.jump(c.label);
          break;
        case "call":
          this.callStack.push(this.ip + 1);
          this.jump(c.label);
          break;
        case "return":
          this.ip = this.callStack.length ? this.callStack.pop() : this.script.length;
          break;
        case "if": {
          let cond = false;
          try { cond = evalBool(c.expr, this.vars); } catch { cond = false; }
          const branch = cond ? c.then : c.else;
          if (branch) this.jump(branch);
          else this.ip++;
          break;
        }
        case "say": {
          this.pausedIp = this.ip; // the save anchor: restore re-runs this beat
          const who = interpolate(c.who, this.vars);
          const text = interpolate(c.text, this.vars);
          this.ip++;
          // A choice directly after shows together with its prompt line.
          if (this.ip < this.script.length && this.script[this.ip].op === "choice") {
            const ch = this.pauseChoice(this.script[this.ip]);
            ch.who = who; ch.text = text; ch.style = c.style;
            return ch;
          }
          return { type: "say", who, text, style: c.style };
        }
        case "choice":
          this.pausedIp = this.ip;
          return this.pauseChoice(c);
        case "input":
          this.pausedIp = this.ip;
          this.ip++;
          this._awaitInput = c;
          return {
            type: "input",
            var: c.var,
            prompt: interpolate(c.prompt, this.vars),
            default: c.default ?? "",
            max: c.max ?? 0,
          };
        case "wait":
          this.ip++;
          return { type: "wait", ms: typeof c.ms === "number" ? c.ms : 1000 };
        default:
          this.onStage(c, this.vars);
          this.ip++;
          break;
      }
    }
    this.finished = true;
    return { type: "end" };
  }

  pauseChoice(c) {
    this._choice = c;
    const options = [];
    (c.options || []).forEach((o, i) => {
      if (o.expr !== undefined && o.expr !== "") {
        try { if (!evalBool(o.expr, this.vars)) return; } catch { return; }
      }
      if (o.requires_stat) {
        const v = this.vars[o.requires_stat];
        const n = typeof v === "number" ? v : parseFloat(v) || 0;
        if (n < (o.min ?? 1)) return;
      }
      options.push({ index: i, text: interpolate(o.text, this.vars), cost: o.cost });
    });
    return {
      type: "choice",
      options,
      timeout: typeof c.timeout === "number" ? c.timeout : 0,
      hasTimeoutBranch: !!c.timeout_goto,
    };
  }

  /** Resolve a shown choice by the option's original index. */
  choose(index) {
    const c = this._choice;
    this._choice = null;
    if (!c) return this.advance();
    const opt = (c.options || [])[index];
    if (!opt) { this.ip++; return this.advance(); }
    if (Array.isArray(opt.body)) {
      for (const b of opt.body) {
        if (b.op === "set" || b.op === "inc") {
          const saveIp = this.ip;
          this.ip = -1; // guard: run the data op inline without moving
          if (b.op === "set") {
            let v; try { v = b.expr !== undefined ? evalExpr(b.expr, this.vars) : b.value; } catch { v = 0; }
            if (b.key) this.vars[b.key] = v;
          } else {
            const cur = this.vars[b.key];
            let by = 1; try { by = b.by !== undefined ? (typeof b.by === "number" ? b.by : evalExpr(b.by, this.vars)) : 1; } catch {}
            this.vars[b.key] = (typeof cur === "number" ? cur : parseFloat(cur) || 0) + by;
          }
          this.ip = saveIp;
        } else if (b.op === "goto") {
          this.jump(b.label);
          return this.advance();
        } else this.onStage(b, this.vars);
      }
      this.ip++;
      return this.advance();
    }
    if (opt.goto) this.jump(opt.goto);
    else this.ip++;
    return this.advance();
  }

  /** The timed choice expired. */
  timeoutChoice() {
    const c = this._choice;
    this._choice = null;
    if (!c || !c.timeout_goto) return { type: "noop" };
    this.jump(c.timeout_goto);
    return this.advance();
  }

  /** Save anchor: everything needed to come back to the CURRENT pause.
   * Restore rewinds to the paused command and re-runs it, so the beat
   * (line/options/input) re-presents itself — the engine's own recipe. */
  snapshot() {
    return {
      ip: this.pausedIp ?? this.ip,
      vars: JSON.parse(JSON.stringify(this.vars)),
      callStack: [...this.callStack],
    };
  }

  restore(snap) {
    if (!snap || typeof snap.ip !== "number") return { type: "noop" };
    this.ip = Math.max(0, Math.min(snap.ip, this.script.length));
    this.vars = Object.assign(Object.create(null), snap.vars || {});
    this.callStack = [...(snap.callStack || [])];
    this.finished = false;
    this._choice = null;
    this._awaitInput = null;
    return this.advance();
  }

  /** Commit the input overlay's text. */
  submitInput(text) {
    const c = this._awaitInput;
    this._awaitInput = null;
    if (c && c.var) this.vars[c.var] = String(text ?? "");
    return this.advance();
  }
}
