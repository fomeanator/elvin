// The browser playground's interpreter (server/website/play) — driven here in
// node against hand-built .lvn docs, so the play-in-browser path has the same
// CI gate as everything else. Covers the full pause loop: say, choice
// (filtered + timed), input, if/set/inc, call/return, and stage forwarding.

import { test } from "node:test";
import assert from "node:assert";

async function load() {
  const { Player } = await import("../../../panel/public/play/core.js");
  const expr = await import("../../../panel/public/play/expr.js");
  return { Player, ...expr };
}

test("expr: arithmetic, comparisons, lists, functions", async () => {
  const { evalExpr, interpolate } = await load();
  const vars = { gold: 12, inv: ["ключ"], hp: 7.5 };
  assert.equal(evalExpr("gold >= 10 && has(inv, \"ключ\")", vars), true);
  assert.equal(evalExpr("gold - 6 * 2", vars), 0);
  assert.equal(evalExpr("max(1, hp, 3)", vars), 7.5);
  assert.deepEqual(evalExpr("inv + [\"меч\"]", vars), ["ключ", "меч"]);
  assert.deepEqual(evalExpr("inv - \"ключ\"", vars), []);
  assert.equal(evalExpr("unknown_var + 1", vars), 1, "unset vars read as 0");
  assert.equal(interpolate("HP: {hp}, {{x}}", vars), "HP: 7.5, {x}");
});

test("player: say → filtered choice → branch state", async () => {
  const { Player } = await load();
  const doc = { script: [
    { op: "set", key: "gold", expr: "5" },
    { op: "say", text: "Лавка. Золота: {gold}." },
    { op: "choice", options: [
      { text: "Купить меч", goto: "buy", expr: "gold >= 10" },
      { text: "Уйти", goto: "leave" },
    ] },
    { op: "label", id: "buy" }, { op: "say", text: "куплено" }, { op: "goto", label: "__end" },
    { op: "label", id: "leave" }, { op: "say", text: "ушёл" },
  ] };
  const p = new Player(doc);
  const ev = p.advance();
  assert.equal(ev.type, "choice", "say+choice pause together");
  assert.equal(ev.text, "Лавка. Золота: 5.");
  assert.equal(ev.options.length, 1, "the unaffordable option is hidden");
  assert.equal(ev.options[0].text, "Уйти");
  const next = p.choose(ev.options[0].index);
  assert.equal(next.text, "ушёл");
});

test("player: timed choice exposes the countdown and expiry jumps", async () => {
  const { Player } = await load();
  const doc = { script: [
    { op: "choice", timeout: 5, timeout_goto: "late", options: [{ text: "Да", goto: "y" }] },
    { op: "label", id: "y" }, { op: "say", text: "успел" }, { op: "goto", label: "__end" },
    { op: "label", id: "late" }, { op: "say", text: "поздно" },
  ] };
  const p = new Player(doc);
  const ev = p.advance();
  assert.equal(ev.timeout, 5);
  assert.equal(ev.hasTimeoutBranch, true);
  const after = p.timeoutChoice();
  assert.equal(after.text, "поздно");
});

test("player: input lands in vars and interpolates", async () => {
  const { Player } = await load();
  const doc = { script: [
    { op: "input", var: "name", prompt: "Кто ты?", default: "Гость" },
    { op: "say", text: "Привет, {name}!" },
  ] };
  const p = new Player(doc);
  const ev = p.advance();
  assert.equal(ev.type, "input");
  assert.equal(ev.default, "Гость");
  const next = p.submitInput("Вася");
  assert.equal(next.text, "Привет, Вася!");
});

test("player: call/return + inc + stage forwarding", async () => {
  const { Player } = await load();
  const staged = [];
  const doc = { script: [
    { op: "bg", sprite_url: "/x.jpg" },
    { op: "call", label: "sub" },
    { op: "say", text: "score={score}" },
    { op: "goto", label: "__end" },
    { op: "label", id: "sub" },
    { op: "inc", key: "score", by: 3 },
    { op: "return" },
  ] };
  const p = new Player(doc, { onStage: (c) => staged.push(c.op) });
  const ev = p.advance();
  assert.equal(ev.text, "score=3");
  assert.deepEqual(staged, ["bg"]);
  assert.equal(p.advance().type, "end");
});
