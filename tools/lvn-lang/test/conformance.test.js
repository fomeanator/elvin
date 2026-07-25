// The playground half of the cross-runtime conformance corpus. Cases live in
// /conformance/cases as data (see /conformance/README.md); this file is only a
// driver — it reads a case, plays it with the browser player, and diffs the
// observable effects against what the case declares.
//
// Nothing here decides what the playground supports: a case the web player is
// not expected to pass simply does not list "js" in its `runtimes`. Keep it that
// way — the moment a runner starts skipping fields by op name, the corpus stops
// being the contract and starts being a rubber stamp.

import { test } from "node:test";
import assert from "node:assert";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, "..", "..", "..");
const casesDir = join(repoRoot, "conformance", "cases");

async function load() {
  const { Player } = await import("../../../panel/public/play/core.js");
  const { evalBool } = await import("../../../panel/public/play/expr.js");
  return { Player, evalBool };
}

function readCases() {
  return readdirSync(casesDir)
    .filter((f) => f.endsWith(".json"))
    .sort()
    .map((f) => ({ file: f, ...JSON.parse(readFileSync(join(casesDir, f), "utf8")) }))
    .filter((c) => (c.runtimes || []).includes("js"));
}

// ── the driver ─────────────────────────────────────────────────────────────
// Advance until the next stop, react by kind, record what was observed. The
// same loop the C# runner implements — see conformance/README.md §Driving.
function run(Player, c) {
  const stops = [];
  const stage = [];
  const picks = [...(c.picks || [])];
  const inputs = [...(c.inputs || [])];
  const p = new Player(c.doc, { onStage: (cmd) => stage.push(cmd) });

  let ev = p.advance();
  for (let step = 0; step < (c.max_steps || 500); step++) {
    // A say immediately followed by a choice arrives as ONE event carrying the
    // line; the contract calls it two stops, so split it here.
    if (ev.type === "choice" && ev.text !== undefined) {
      stops.push({ kind: "say", who: ev.who, text: ev.text, style: ev.style });
      ev = { ...ev, text: undefined };
    }
    switch (ev.type) {
      case "say":
        stops.push({ kind: "say", who: ev.who, text: ev.text, style: ev.style });
        ev = p.advance();
        break;
      case "choice": {
        stops.push({
          kind: "choice",
          options: ev.options.map((o) => o.text),
          timeout: ev.timeout || 0,
        });
        assert.ok(picks.length > 0, `${c.id}: a choice is open but picks ran out`);
        const pick = picks.shift();
        if (pick && typeof pick === "object" && pick.timeout) {
          assert.ok(ev.hasTimeoutBranch, `${c.id}: pick says timeout but the choice has no timeout_goto`);
          ev = p.timeoutChoice();
        } else {
          const shown = ev.options[pick];
          assert.ok(shown, `${c.id}: pick ${pick} is out of range of the ${ev.options.length} presented options`);
          ev = p.choose(shown.index);
        }
        break;
      }
      case "input":
        stops.push({ kind: "input", prompt: ev.prompt, default: ev.default, max: ev.max });
        assert.ok(inputs.length > 0, `${c.id}: an input is open but inputs ran out`);
        ev = p.submitInput(inputs.shift());
        break;
      case "wait":
        stops.push({ kind: "wait", ms: ev.ms });
        ev = p.advance();
        break;
      case "end":
        stops.push({ kind: "end" });
        return { stops, stage, vars: p.vars };
      default:
        assert.fail(`${c.id}: unexpected pause type ${ev.type}`);
    }
  }
  assert.fail(`${c.id}: ran past max_steps without ending`);
}

// ── expectation matching ───────────────────────────────────────────────────

const str = (v) => (v === undefined || v === null ? "" : String(v));

// Normalise a declared stop (short or long form) into the driver's shape.
function normalizeExpected(stop, id) {
  const [kind, body] = Object.entries(stop)[0];
  switch (kind) {
    case "say":
      return typeof body === "string"
        ? { kind: "say", text: body }
        : { kind: "say", ...body };
    case "choice":
      return Array.isArray(body)
        ? { kind: "choice", options: body }
        : { kind: "choice", ...body };
    case "input":
      return { kind: "input", ...body };
    case "wait":
      return { kind: "wait", ...body };
    case "end":
      return { kind: "end" };
    default:
      assert.fail(`${id}: unknown stop kind ${kind}`);
  }
}

function assertStops(id, want, got) {
  // Two phases. First the SHAPE — which kinds of stop, in which order — because a
  // structurally diverged trace is unreadable as a per-field failure halfway down.
  // Only then the details, which a case may pin selectively.
  const render = (s) =>
    s.kind === "choice" ? `choice[${(s.options || []).join(" | ")}]` : `${s.kind}(${str(s.text ?? s.prompt ?? s.ms)})`;
  const wantNorm = want.map((w) => normalizeExpected(w, id));
  assert.deepEqual(
    got.map((s) => s.kind),
    wantNorm.map((s) => s.kind),
    `${id}: stop trace diverged\n  expected: ${wantNorm.map(render).join(" → ")}\n  actual:   ${got.map(render).join(" → ")}`,
  );
  want.forEach((raw, i) => {
    const w = normalizeExpected(raw, id);
    const g = got[i];
    const at = `${id}: stop #${i} (${w.kind})`;
    if (w.kind === "say") {
      if ("who" in w) assert.equal(str(g.who), str(w.who), `${at}: speaker`);
      if ("text" in w) assert.equal(str(g.text), str(w.text), `${at}: line`);
      if ("style" in w) assert.equal(str(g.style), str(w.style), `${at}: style`);
    } else if (w.kind === "choice") {
      if (w.options) assert.deepEqual(g.options, w.options, `${at}: presented options`);
      if ("timeout" in w) assert.equal(g.timeout, w.timeout, `${at}: countdown seconds`);
    } else if (w.kind === "input") {
      for (const f of ["prompt", "default", "max"])
        if (f in w) assert.equal(str(g[f]), str(w[f]), `${at}: ${f}`);
    } else if (w.kind === "wait") {
      if ("ms" in w) assert.equal(g.ms, w.ms, `${at}: ms`);
    }
  });
}

function assertVars(id, want, got) {
  for (const [k, v] of Object.entries(want)) {
    const actual = got[k];
    if (typeof v === "number") assert.equal(Number(actual), v, `${id}: var ${k}`);
    else if (typeof v === "boolean") assert.equal(Boolean(actual), v, `${id}: var ${k}`);
    else assert.equal(str(actual), str(v), `${id}: var ${k}`);
  }
}

// Each expected staging command is matched as a SUBSET of the real one, so a case
// pins the fields it cares about without freezing every unrelated key.
function assertStage(id, want, got) {
  assert.equal(got.length, want.length,
    `${id}: ${want.length} staging commands expected, ${got.length} reached the renderer ` +
    `(${got.map((c) => c.op).join(", ")})`);
  want.forEach((w, i) => {
    for (const [k, v] of Object.entries(w))
      assert.equal(str(got[i][k]), str(v), `${id}: stage #${i} field ${k}`);
  });
}

// ── the tests ──────────────────────────────────────────────────────────────

const cases = readCases();

test("conformance corpus: the playground has cases to run", () => {
  assert.ok(cases.length > 0, "no case in /conformance/cases lists the js runtime");
});

for (const c of cases) {
  test(`conformance/${c.file}: ${c.title}`, async () => {
    const { Player, evalBool } = await load();
    const got = run(Player, c);
    const want = c.expect || {};
    if (want.stops) assertStops(c.id, want.stops, got.stops);
    if (want.vars) assertVars(c.id, want.vars, got.vars);
    if (want.stage) assertStage(c.id, want.stage, got.stage);
    for (const e of want.expr_true || [])
      assert.equal(evalBool(e, got.vars), true, `${c.id}: expected «${e}» to hold`);
    for (const e of want.expr_false || [])
      assert.equal(evalBool(e, got.vars), false, `${c.id}: expected «${e}» not to hold`);
    // scene/labels are not observable in the playground; the Go guard forbids a
    // js-listed case from declaring them, so there is nothing to skip here.
  });
}
