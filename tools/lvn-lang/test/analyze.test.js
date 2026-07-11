import { test } from "node:test";
import assert from "node:assert/strict";
import {
  completionAt, labelsIn, predictGhost, hoverAt, definitionAt,
  documentSymbols, describeLine, offsetToPos, posToOffset,
} from "../src/analyze.js";

// shared fixture: one cast member with emotion + pose axes
const catalog = {
  mara: {
    name: "Mara", axes: { emotion: ["neutral", "happy", "sad"], pose: ["standing"] },
    defaults: { pose: "standing", emotion: "neutral" },
    anim: { idle: {}, wave: {}, nod: {} },
  },
  porch: {},
};
const actorMap = { Mara: "mara" };
const ctx = { catalog, actorMap };

const SCRIPT = `scene demo
bg id="porch"
Mara [happy]: Hi {gold}.
set key="gold" value=3
- Go -> done
goto done
:done
goto __end`;

test("completion: op list filtered by prefix", () => {
  const r = completionAt("fa", [], catalog, actorMap);
  assert.ok(r && r.items.some((i) => i.text === "fade" && i.kind === "op"));
});

test("completion: line start suggests cast names (case-insensitive)", () => {
  const r = completionAt("Mar", [], catalog, actorMap);
  assert.ok(r && r.items.some((i) => i.text === "Mara" && i.kind === "speaker"));
  const r2 = completionAt("mar", [], catalog, actorMap);
  assert.ok(r2 && r2.items.some((i) => i.text === "Mara"), "matches regardless of case");
});

test("completion: character with emotions offers a [emotion] variant", () => {
  const r = completionAt("Mar", [], catalog, actorMap);
  const emote = r.items.find((i) => i.kind === "speaker" && i.emote);
  assert.ok(emote, "a discoverable 'Name [emotion]' item is offered");
  assert.equal(emote.label, "Mara [emotion]");
});

test("completion: id= suggests catalog entities", () => {
  const r = completionAt('bg id="', [], catalog, actorMap);
  assert.ok(r.items.some((i) => i.text === "mara" && i.kind === "entity"));
});

test("completion: emotion bracket lists the character's emotions", () => {
  const r = completionAt("Mara [", [], catalog, actorMap);
  assert.deepEqual(r.items.map((i) => i.text), ["neutral", "happy", "sad"]);
});

test("completion: play= suggests the character's animations", () => {
  const r = completionAt('actor id="mara" play="', [], catalog, actorMap);
  assert.ok(r && r.items.some((i) => i.text === "wave" && i.kind === "value"));
  assert.deepEqual(r.items.map((i) => i.text).sort(), ["idle", "nod", "wave"]);
});

test("completion: goto suggests labels", () => {
  const r = completionAt("goto d", ["done", "intro"], catalog, actorMap);
  assert.ok(r.items.some((i) => i.text === "done" && i.kind === "label"));
});

test("completion: empty attr token defers to ghost (null)", () => {
  assert.equal(completionAt("bg ", [], catalog, actorMap), null);
});

test("ghost: op template after 'op '", () => {
  assert.equal(predictGhost("bg ", ctx), 'id=""');
});

test("ghost: name prefix completes to 'a: '", () => {
  assert.equal(predictGhost("Mar", ctx), "a: ");
});

test("ghost: full known name → ': '", () => {
  assert.equal(predictGhost("Mara", ctx), ": ");
});

test("ghost: key= → common value", () => {
  assert.equal(predictGhost("fade to=", ctx), '"black"');
});

test("hover: op word resolves to its doc", () => {
  // 'goto' on line 6, col within the word
  const h = hoverAt(SCRIPT, 6, 1, ctx);
  assert.equal(h.kind, "op");
  assert.equal(h.word, "goto");
});

test("hover: valid vs invalid emotion", () => {
  // line 3: "Mara [happy]: Hi {gold}." — 'happy' at col ~7
  const h = hoverAt(SCRIPT, 3, 7, ctx);
  assert.equal(h.kind, "emotion");
  assert.equal(h.ok, true);
});

test("hover: variable in interpolation", () => {
  // line 3 has {gold}; 'gold' inside braces (~col 19)
  const h = hoverAt(SCRIPT, 3, 19, ctx);
  assert.equal(h.kind, "var");
  assert.equal(h.sets, 1);
  assert.equal(h.uses, 1);
});

test("definition: goto target → its :def line", () => {
  // line 6 "goto done" — 'done' at col ~6
  const d = definitionAt(SCRIPT, 6, 6, ctx);
  assert.equal(d.line, 7); // :done is line 7
  assert.equal(d.name, "done");
});

test("documentSymbols: scene + labels", () => {
  const syms = documentSymbols(SCRIPT);
  assert.deepEqual(syms, [
    { kind: "scene", name: "demo", line: 1 },
    { kind: "label", name: "done", line: 7 },
  ]);
});

test("describeLine: op signature + active arg", () => {
  const r = describeLine('fade to="black" duration=0.8', 28, ctx);
  assert.equal(r.kind, "op");
  assert.equal(r.op, "fade");
  assert.equal(r.active, "duration");
});

test("labelsIn finds label definitions", () => {
  assert.deepEqual(labelsIn(SCRIPT), ["done"]);
});

test("position helpers round-trip", () => {
  const off = posToOffset(SCRIPT, { line: 2, character: 3 });
  assert.deepEqual(offsetToPos(SCRIPT, off), { line: 2, character: 3 });
});

// ── ext-grammar: host-op declarations power ext completion/hover/ghost ──────
const extGrammar = {
  ops: {
    minigame: {
      doc: "Runs a host mini-game; the story waits for Resume().",
      fields: ["difficulty", "timeout"],
      required: ["id"],
      enums: { difficulty: ["easy", "normal", "hard"] },
      snippet: 'ext minigame id="river" difficulty=normal',
    },
    confetti: { fields: ["amount"] },
  },
};

test("ext completion: `ext <partial>` lists declared host ops", () => {
  const r = completionAt("ext mini", [], catalog, actorMap, extGrammar);
  assert.ok(r && r.items.some((i) => i.text === "minigame" && i.kind === "op"));
  assert.ok(!r.items.some((i) => i.text === "confetti"), "prefix-filtered");
});

test("ext completion: fields come from the declaration (fields ∪ required)", () => {
  const r = completionAt('ext minigame id="river" di', [], catalog, actorMap, extGrammar);
  assert.ok(r && r.items.some((i) => i.text === "difficulty" && i.kind === "attr"));
  const r2 = completionAt("ext minigame i", [], catalog, actorMap, extGrammar);
  assert.ok(r2 && r2.items.some((i) => i.text === "id"), "required fields complete too");
});

test("ext completion: enum values complete inside key=", () => {
  const r = completionAt("ext minigame difficulty=ha", [], catalog, actorMap, extGrammar);
  assert.ok(r && r.items.some((i) => i.text === "hard" && i.kind === "value"));
});

test("ext completion: without a grammar `ext` stays quiet", () => {
  assert.equal(completionAt("ext mini", [], catalog, actorMap), null);
});

test("ext hover: a declared op shows its doc and snippet signature", () => {
  const src = 'ext minigame id="river"';
  const h = hoverAt(src, 1, 5, { catalog, actorMap, extGrammar });
  assert.ok(h && h.kind === "op" && h.desc.includes("mini-game"));
  assert.ok(h.sig.includes("minigame"));
});

test("ext ghost: the snippet tail follows `ext <op> `", () => {
  const g = predictGhost("ext minigame ", { catalog, actorMap, extGrammar });
  assert.equal(g, 'id="river" difficulty=normal');
  const g2 = predictGhost("ext confetti ", { catalog, actorMap, extGrammar });
  assert.equal(g2, 'amount=""', "no snippet → fields seeded");
});
