// Playground glue: the real lvnconv compiler (wasm) turns the left pane's
// .lvns into a .lvn doc; core.js plays it; this file renders pauses into DOM
// and shares scripts through the URL hash — open the link, the game runs.

import { Player } from "./core.js";
import { interpolate } from "./expr.js";
import { attach as attachHighlight } from "./highlight.js";
import { exportHtml } from "./export.js";

const $ = (id) => document.getElementById(id);
const els = {
  editor: $("editor"), problems: $("problems"), status: $("status"),
  bg: $("bg"), actors: $("actors"), hud: $("hud"),
  dialogue: $("dialogue"), speaker: $("speaker"), line: $("line"),
  choices: $("choices"), inputbox: $("inputbox"),
  inputPrompt: $("input-prompt"), inputField: $("input-field"), inputOk: $("input-ok"),
  endcard: $("endcard"),
};

// ── wasm compiler ──────────────────────────────────────────────────────────
let wasmReady = false;
(async () => {
  try {
    const go = new window.Go();
    const res = await WebAssembly.instantiateStreaming(fetch("lvns.wasm"), go.importObject); // relative: works at / and under GitHub Pages subpaths
    go.run(res.instance);
    wasmReady = true;
    setStatus("ready", "ok");
    boot();
  } catch (e) {
    setStatus("compiler failed to load: " + e.message, "err");
  }
})();

function setStatus(text, cls) {
  els.status.textContent = text;
  els.status.className = "status" + (cls ? " " + cls : "");
}

// ── product services from the browser ─────────────────────────────────────
// The playground is same-origin with the content server, so shared stories
// get REAL analytics and leaderboards: a device account is minted on demand
// (localStorage), then ext ops from the script just work.
let svcToken = null;
async function svcAuth() {
  if (svcToken) return svcToken;
  let device = localStorage.getItem("lvn-play-device");
  if (!device) {
    device = crypto.randomUUID().replaceAll("-", "") + crypto.randomUUID().replaceAll("-", "");
    localStorage.setItem("lvn-play-device", device);
  }
  try {
    const r = await fetch("/v1/auth/register", { method: "POST", body: JSON.stringify({ device_id: device }) });
    if (!r.ok) return null;
    svcToken = (await r.json()).token;
  } catch { return null; }
  return svcToken;
}

async function svcOp(cmd, vars) {
  const tok = await svcAuth();
  if (!tok) return;
  const auth = { "Authorization": "Bearer " + tok };
  const num = (field, varField) => {
    if (typeof cmd[field] === "number") return cmd[field];
    const n = parseFloat(vars[cmd[varField]]);
    return Number.isFinite(n) ? n : 0;
  };
  try {
    if (cmd.op === "track" && cmd.name) {
      await fetch("/v1/analytics/events", { method: "POST", headers: auth,
        body: JSON.stringify([{ name: cmd.name }]) });
    } else if (cmd.op === "leaderboard_submit" && cmd.board) {
      const payload = { score: num("score", "score_var") };
      const nm = vars[cmd.name_var];
      if (nm) payload.name = String(nm);
      const r = await fetch("/v1/leaderboard/" + cmd.board, { method: "POST", headers: auth,
        body: JSON.stringify(payload) });
      if (r.ok) {
        const { rank } = await r.json();
        setStatus(`score submitted to "${cmd.board}" — rank ${rank}`, "ok");
        void showBoard(cmd.board, tok);
      }
    }
  } catch { /* offline playground stays a playground */ }
}

// A small overlay with the board's top — the competition made visible.
async function showBoard(board, tok) {
  try {
    const r = await fetch(`/v1/leaderboard/${board}?n=5`, { headers: { "Authorization": "Bearer " + tok } });
    if (!r.ok) return;
    const d = await r.json();
    let box = document.getElementById("lb-overlay");
    if (!box) {
      box = document.createElement("div");
      box.id = "lb-overlay";
      box.className = "lb-overlay";
      document.getElementById("stage").appendChild(box);
      box.addEventListener("click", () => box.remove());
    }
    const rows = (d.top || []).map((e, i) =>
      `<div class="lb-row"><span>${i + 1}. ${escapeHtml(e.name || "Anonymous")}</span><b>${e.score}</b></div>`).join("");
    const me = d.me ? `<div class="lb-me">You — rank ${d.me.rank} (${d.me.score})</div>` : "";
    box.innerHTML = `<div class="lb-title">🏆 ${escapeHtml(board)}</div>${rows}${me}<div class="lb-hint">tap to close</div>`;
  } catch { }
}

function escapeHtml(s) {
  return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;");
}

// ── sprite catalog (manifest) — layered actors render honestly ────────────
// The manifest probe doubles as server detection: same-origin with the
// content server, /content/... asset paths resolve as-is; on a static host
// (GitHub Pages) the probe fails and absolute /content/ paths remap onto the
// demo pack shipped beside the playground (./content/...), so example art
// still renders without any server.
let catalog = {};
let contentBase = ""; // "" → same-origin /content/ works untouched
const art = (u) => {
  if (typeof u !== "string") return u;
  if (userAssets[u]) return userAssets[u]; // an uploaded image, by file name
  return contentBase && u.startsWith("/content/")
    ? contentBase + u.slice("/content/".length) : u;
};
fetch("/v1/content/manifest").then((r) => {
  if (!r.ok) throw new Error("no content server");
  return r.json();
}).then((m) => {
  catalog = (m && m.sprites) || {};
}).catch(() => { contentBase = new URL("content/", location.href).href; });

// ── user images — test your own visuals without any server ────────────────
// Uploads become data URIs keyed by file name: `bg sprite_url="my_bg.png"`
// just works, persists in this browser (best-effort — quota may say no) and
// rides along into the single-file HTML export.
const USER_ASSETS_KEY = "lvn-user-assets";
let userAssets = {};
try { userAssets = JSON.parse(localStorage.getItem(USER_ASSETS_KEY)) || {}; } catch { /* fresh start */ }
function saveUserAssets() {
  try { localStorage.setItem(USER_ASSETS_KEY, JSON.stringify(userAssets)); }
  catch { setStatus("image kept for this tab only (browser storage is full)", ""); }
}

function resolveLayers(entity, cmd) {
  // Normalize the three catalog shapes: ["url"], [{url}], and full layer
  // objects with per-layer geometry; {axis} templates fill from the command
  // (falling back to the entity defaults).
  const axes = entity.axes || {};
  const defs = entity.defaults || {};
  const val = (axis) => cmd[axis] ?? defs[axis] ?? (axes[axis] && axes[axis][0]) ?? "";
  const out = [];
  for (const raw of entity.layers || []) {
    const l = typeof raw === "string" ? { url: raw } : raw;
    if (!l.url) continue;
    const url = l.url.replace(/\{(\w+)\}/g, (_, a) => val(a));
    out.push({ ...l, url });
  }
  return out;
}

// ── examples ───────────────────────────────────────────────────────────────
const EXAMPLES = {
  "First scene": `scene playground

A dark room. It smells of dust and old books.
input var=name prompt="What's your name?" default="Guest" max=20
Voice: At last, {name}. I've been waiting.
Voice: You have five seconds to decide.

choice timeout=5 timeout_goto=frozen
- Look for the light switch -> light
- Run for the door -> door

:light
courage = 1
You found the switch. The room turns out to be a library.
-> finale

:door
courage = 0
The door wouldn't budge. But your eyes got used to the dark.
-> finale

:frozen
courage = 0
You froze. Sometimes that's a choice too.
-> finale

:finale
if courage >= 1 -> brave
Voice: Caution is not weakness, {name}.
-> __end
:brave
Voice: Bold, {name}. I like it.`,

  "Shop & inventory": `scene shop

gold = 12
inv = []
text hud x=4 y=6 size=20 color=#ffd166 «💰 {gold}  🎒 {inv}»

Merchant: What'll it be? The sword is 10, the apple is 3.
:menu
- Sword (10 gold) -> sword expr="gold >= 10"
- Apple (3) -> apple expr="gold >= 3"
- Turn out your pockets -> pockets
- Leave -> bye

:sword
gold = gold - 10
inv = inv + ["sword"]
Merchant: Fine steel!
-> menu
:apple
gold = gold - 3
inv = inv + ["apple"]
Merchant: Fresh!
-> menu
:pockets
You turn out your pockets — {inv}.
-> menu
:bye
if has(inv, "sword") -> armed
Merchant: Come again.
-> __end
:armed
Merchant: Feels safer with a sword, doesn't it?`,

  "Scene with art": `scene art_demo

bg sprite_url="/content/sprites/doll/bg.png"
obj id=apple sprite_url="/content/sprites/doll/apple.png" x=0.3 width=0.1
obj id=bag sprite_url="/content/sprites/doll/bag.png" x=0.75 width=0.18

The doll's room. An apple and a bag on the floor.
- Put the apple in the bag -> tidy
- Leave it be -> leave

:tidy
obj id=apple show=false
fade to=black
Tidy! The apple is in the bag.
-> __end

:leave
dim alpha=0.35
Let it lie. It's picturesque.`,

  "Paper doll (catalog layers)": `scene doll_pg

bg sprite_url="/content/sprites/doll/bg.png"
actor doll x=0.5 height=0.85
A doll built from four layers — body, arm, head, hair — with per-layer geometry from the catalog.
In the Unity runtime she also breathes, nods and sways her hair on spring bones.
- Got it -> fin
:fin
Check out the Unity sandbox for the full version!`,

  "Codel: emotions from the catalog": `scene codel_demo

actor codel x=0.5 height=0.85
Codel: Hi! I'm a character from the manifest catalog.
actor codel emotion=happy
Codel: One word in the script — and my emotion changes.
actor codel emotion=annoyed
Codel: emotion=annoyed. Noticeable, right?
actor codel emotion=shy
Codel: And this is shy... don't stare.
- Smile! -> smile
- Enough -> bye
:smile
actor codel emotion=happy
Codel: There we go, much better!
-> __end
:bye
actor codel emotion=sad
Codel: Aw. Bye then.`,

  "Quiz blitz": `scene quiz

score = 0
input var=player_name prompt="Your name for the leaderboard?" default="Anonymous" max=20
A quiz! Two questions, very little time.

Question 1. How many bytes in a kilobyte?
choice timeout=6 timeout_goto=miss1
- 1000 -> w1
- 1024 -> r1
:r1
score = score + 1
Correct!
-> q2
:w1
Nope — 1024.
-> q2
:miss1
Time! The answer is 1024.
-> q2

:q2
Question 2. The largest planet?
choice timeout=6 timeout_goto=miss2
- Jupiter -> r2
- Saturn -> w2
:r2
score = score + 1
Exactly.
-> res
:w2
It's Jupiter.
-> res
:miss2
Too late! Jupiter.
-> res

:res
ext leaderboard_submit board=playground_quiz score_var=score name_var=player_name
Your score is {score} out of 2 — already on the shared leaderboard!
if score >= 2 -> ace
Not bad. Rematch?
-> __end
:ace
Flawless!`,
};

// ── compile & run ──────────────────────────────────────────────────────────
let player = null;
window.__lvn = { get player() { return player; } };
let typeTimer = null;
let choiceTimer = null;

function compileAndRun() {
  if (!wasmReady) return;
  stopTimers();
  const src = els.editor.value;
  const out = window.lvnsCompile(src);
  if (!out.ok) {
    showProblems("Compile error:\n" + out.errors);
    setStatus("compile error", "err");
    return;
  }
  showProblems(out.warnings ? "Warnings:\n" + out.warnings : "");
  setStatus(out.warnings ? "running (with warnings)" : "running ✓", out.warnings ? "" : "ok");

  const doc = JSON.parse(out.json);
  const sceneName = (/^\s*scene\s+(\S+)/m.exec(src) || [])[1] || "scene";
  saveKey = "lvn-play-save:" + sceneName;
  resetStage();
  history = [];
  player = new Player(doc, { onStage: applyStage });

  // A save from an earlier visit: offer to continue (the whole point of
  // playing a shared story in more than one sitting).
  let saved = null;
  try { saved = JSON.parse(localStorage.getItem(saveKey) || "null"); } catch {}
  if (saved && saved.snap && saved.snap.ip > 0 && saved.snap.ip < doc.script.length) {
    showResume(saved);
    return;
  }
  render(player.advance());
}

function showResume(saved) {
  els.choices.innerHTML = "";
  els.choices.hidden = false;
  const note = document.createElement("div");
  note.style.color = "#cfc8bd";
  note.textContent = "A save exists — continue?";
  els.choices.appendChild(note);
  const btnGo = document.createElement("button");
  btnGo.textContent = "▶ Continue";
  btnGo.addEventListener("click", () => {
    els.choices.hidden = true;
    stagedState = saved.stage || stagedState;
    if (stagedState.bg) applyStageDom({ op: "bg", sprite_url: stagedState.bg }, player.vars);
    for (const cmd of Object.values(stagedState.actors)) applyStageDom(cmd, player.vars);
    for (const cmd of Object.values(stagedState.hud)) applyStageDom(cmd, player.vars);
    render(player.restore(saved.snap));
  });
  els.choices.appendChild(btnGo);
  const btnNew = document.createElement("button");
  btnNew.textContent = "↻ Start over";
  btnNew.addEventListener("click", () => {
    try { localStorage.removeItem(saveKey); } catch {}
    els.choices.hidden = true;
    render(player.advance());
  });
  els.choices.appendChild(btnNew);
}

function showProblems(text) {
  els.problems.hidden = !text;
  els.problems.textContent = text || "";
}

// ── stage rendering (the subset a browser can honestly draw) ──────────────
function resetStage() {
  els.bg.style.backgroundImage = "";
  els.actors.innerHTML = "";
  els.hud.innerHTML = "";
  hudLabels.clear();
  els.dialogue.hidden = true;
  els.choices.hidden = true;
  els.choices.innerHTML = "";
  stagedState = { bg: null, actors: {}, hud: {} };
  els.inputbox.hidden = true;
  els.endcard.hidden = true;
  $("veil").style.opacity = 0;
}

const hudLabels = new Map();
// A plain mirror of what's on stage — travels inside saves so a restore can
// redraw bg/actors/HUD without replaying branching history.
let stagedState = { bg: null, actors: {}, hud: {} };

function applyStage(cmd, vars) {
  trackStage(cmd);
  applyStageDom(cmd, vars);
}

function trackStage(cmd) {
  if (cmd.op === "bg" && cmd.sprite_url) stagedState.bg = cmd.sprite_url;
  else if (cmd.op === "actor" || cmd.op === "obj") {
    if (!cmd.id) return;
    if (cmd.show === false) delete stagedState.actors[cmd.id];
    else stagedState.actors[cmd.id] = { ...(stagedState.actors[cmd.id] || {}), ...cmd };
  } else if (cmd.op === "text" && cmd.id) {
    if (cmd.hide) delete stagedState.hud[cmd.id];
    else stagedState.hud[cmd.id] = { ...(stagedState.hud[cmd.id] || {}), ...cmd };
  }
}

function applyStageDom(cmd, vars) {
  switch (cmd.op) {
    case "bg":
      if (cmd.sprite_url) els.bg.style.backgroundImage = `url("${art(cmd.sprite_url)}")`;
      break;
    case "actor":
    case "obj": {
      if (!cmd.id) break;
      let node = els.actors.querySelector(`[data-id="${cmd.id}"]`);
      if (cmd.show === false) { node?.remove(); break; }

      const entity = !cmd.sprite_url && !cmd.body_url ? catalog[cmd.id] : null;
      if (entity && entity.layers) {
        // Layered catalog actor: a positioned box with stacked layer images.
        if (!node || node.tagName !== "DIV") {
          node?.remove();
          node = document.createElement("div");
          node.className = "actor-box";
          node.dataset.id = cmd.id;
          els.actors.appendChild(node);
        }
        node.innerHTML = "";
        for (const l of resolveLayers(entity, cmd)) {
          const img = document.createElement("img");
          img.onerror = () => img.remove();
          img.src = art(l.url);
          if (typeof l.x === "number") {
            img.style.left = (l.x * 100) + "%";
            img.style.top = ((l.y ?? 0) * 100) + "%";
            img.style.width = ((l.w ?? 1) * 100) + "%";
            img.style.height = ((l.h ?? 1) * 100) + "%";
          } else {
            img.style.left = "0"; img.style.top = "0";
            img.style.width = "100%"; img.style.height = "100%";
          }
          node.appendChild(img);
        }
        const bx = typeof cmd.x === "number" ? cmd.x
          : cmd.position === "left" ? 0.22 : cmd.position === "right" ? 0.78 : 0.5;
        node.style.left = (bx * 100) + "%";
        const h = typeof cmd.height === "number" ? cmd.height : 0.8;
        node.style.height = (h * 100) + "%";
        const aspect = entity.aspect || (typeof cmd.width === "number" && typeof cmd.height === "number"
          ? cmd.width / cmd.height : 0.6);
        node.style.aspectRatio = String(aspect);
        if (typeof cmd.opacity === "number") node.style.opacity = cmd.opacity;
        break;
      }

      const url = cmd.sprite_url || cmd.body_url;
      if (!node && url) {
        node = document.createElement("img");
        node.dataset.id = cmd.id;
        node.onerror = () => node.remove(); // no content server (e.g. Pages) → text-only beat
        els.actors.appendChild(node);
      }
      if (!node) break;
      if (url && node.tagName === "IMG") node.src = art(url);
      const x = typeof cmd.x === "number" ? cmd.x
        : cmd.position === "left" ? 0.22 : cmd.position === "right" ? 0.78 : 0.5;
      node.style.left = (x * 100) + "%";
      if (typeof cmd.width === "number") node.style.maxWidth = (cmd.width * 100) + "%";
      if (typeof cmd.opacity === "number") node.style.opacity = cmd.opacity;
      break;
    }
    case "text": {
      if (!cmd.id) break;
      if (cmd.hide) { hudLabels.get(cmd.id)?.el.remove(); hudLabels.delete(cmd.id); break; }
      let entry = hudLabels.get(cmd.id);
      if (!entry) {
        const el = document.createElement("div");
        el.className = "hud-label";
        els.hud.appendChild(el);
        entry = { el, template: "" };
        hudLabels.set(cmd.id, entry);
      }
      if (cmd.text) entry.template = cmd.text;
      entry.el.style.left = (cmd.x ?? 4) + "%";
      entry.el.style.top = (cmd.y ?? 4) + "%";
      entry.el.style.fontSize = ((cmd.size ?? 24) * 0.6) + "px";
      entry.el.style.color = cmd.color || "#f1e4c9";
      break;
    }
    case "audio": {
      if (cmd.channel === "sfx" && cmd.action !== "stop" && cmd.url) {
        new Audio(cmd.url).play().catch(() => {});
        break;
      }
      if (cmd.channel === "music") {
        if (cmd.action === "stop") { window.__lvnMusic?.pause(); break; }
        if (cmd.url) {
          window.__lvnMusic?.pause();
          const a = new Audio(cmd.url);
          a.loop = cmd.loop !== false; a.volume = cmd.volume ?? 1;
          a.play().catch(() => {});
          window.__lvnMusic = a;
        }
      }
      break;
    }
    case "fade": {
      const veil = $("veil");
      const to = cmd.to || "black";
      veil.style.background = to === "white" ? "#fff" : "#000";
      veil.style.opacity = to === "clear" ? 0 : 1;
      if (to !== "clear") setTimeout(() => { veil.style.opacity = 0; }, 650);
      break;
    }
    case "dim": {
      const veil = $("veil");
      veil.style.background = "#000";
      veil.style.opacity = cmd.alpha ?? 0.4;
      break;
    }
    case "tint": {
      const veil = $("veil");
      veil.style.background = cmd.color || "#000";
      veil.style.opacity = cmd.alpha ?? 0.3;
      break;
    }
    case "track":
    case "leaderboard_submit":
      void svcOp(cmd, vars || (player ? player.vars : {}));
      break;
    // camera/particles/anim: quietly skipped — the note under the stage says
    // the full staging lives in the Unity runtime.
  }
}

function refreshHud(vars) {
  for (const { el, template } of hudLabels.values())
    el.textContent = interpolate(template, vars);
}

// ── pause-event rendering ──────────────────────────────────────────────────
let saveKey = null;
// Rollback history: one {snap, stage} pair per pause, engine-style.
let history = [];
const HISTORY_MAX = 100;

function pushHistory() {
  if (!player || player.finished) return;
  history.push({ snap: player.snapshot(), stage: JSON.parse(JSON.stringify(stagedState)) });
  if (history.length > HISTORY_MAX) history.shift();
}

function rollback() {
  if (history.length < 2) return;
  stopTimers();
  history.pop(); // the beat on screen
  const prev = history.pop(); // re-pushed when its beat re-runs
  els.choices.hidden = true; els.inputbox.hidden = true; els.endcard.hidden = true;
  stagedState = prev.stage;
  els.actors.innerHTML = ""; els.hud.innerHTML = ""; hudLabels.clear();
  if (stagedState.bg) applyStageDom({ op: "bg", sprite_url: stagedState.bg }, player.vars);
  for (const cmd of Object.values(stagedState.actors)) applyStageDom(cmd, player.vars);
  for (const cmd of Object.values(stagedState.hud)) applyStageDom(cmd, player.vars);
  render(player.restore(prev.snap));
}

function autosave() {
  if (!saveKey || !player || player.finished) return;
  try {
    localStorage.setItem(saveKey, JSON.stringify({ snap: player.snapshot(), stage: stagedState }));
  } catch {}
}

function render(ev) {
  if (ev.type === "say" || ev.type === "choice" || ev.type === "input") pushHistory();
  autosave();
  if (window.__lvnDebug) console.log("[render]", ev.type, ev.text ?? "", new Error().stack.split("\n")[2]?.trim());
  refreshHud(player.vars);
  els.choices.hidden = true;
  els.inputbox.hidden = true;

  switch (ev.type) {
    case "say":
      showLine(ev);
      break;
    case "choice":
      if (ev.text !== undefined) showLine(ev, /*noAdvance*/ true);
      showChoices(ev);
      break;
    case "input":
      showInput(ev);
      break;
    case "wait":
      setTimeout(() => render(player.advance()), ev.ms);
      break;
    case "end":
      els.dialogue.hidden = true;
      els.endcard.hidden = false;
      try { if (saveKey) localStorage.removeItem(saveKey); } catch {} // finished = clean slate
      break;
  }
}

let fullLine = "", revealing = false;

function showLine(ev, noAdvance) {
  els.dialogue.hidden = false;
  els.speaker.textContent = ev.who || "";
  els.line.className = "line" + (ev.style ? " " + ev.style : "") + (ev.who ? "" : " narration");
  fullLine = ev.text || "";
  els.line.textContent = "";
  revealing = true;
  let i = 0;
  clearInterval(typeTimer);
  typeTimer = setInterval(() => {
    i += 2;
    els.line.textContent = fullLine.slice(0, i);
    if (i >= fullLine.length) { clearInterval(typeTimer); revealing = false; }
  }, 24);
  els.dialogue.dataset.noadvance = noAdvance ? "1" : "";
}

els.dialogue.addEventListener("click", () => {
  if (!els.inputbox.hidden || !els.choices.hidden) return; // an overlay owns the beat
  if (revealing) { clearInterval(typeTimer); els.line.textContent = fullLine; revealing = false; return; }
  if (els.dialogue.dataset.noadvance === "1") return; // a choice owns this beat
  if (player && !player.finished) render(player.advance());
});

function showChoices(ev) {
  els.choices.innerHTML = "";
  els.choices.hidden = false;

  if (ev.timeout > 0 && ev.hasTimeoutBranch) {
    const bar = document.createElement("div");
    bar.className = "timerbar";
    const fill = document.createElement("div");
    bar.appendChild(fill);
    els.choices.appendChild(bar);
    const deadline = performance.now() + ev.timeout * 1000;
    choiceTimer = setInterval(() => {
      const left = deadline - performance.now();
      fill.style.width = Math.max(0, (left / (ev.timeout * 1000)) * 100) + "%";
      if (left <= 0) {
        stopTimers();
        render(player.timeoutChoice());
      }
    }, 80);
  }

  for (const o of ev.options) {
    const btn = document.createElement("button");
    btn.textContent = o.text;
    if (o.cost) {
      const c = document.createElement("span");
      c.className = "cost";
      c.textContent = o.cost;
      btn.appendChild(c);
    }
    btn.addEventListener("click", () => {
      stopTimers();
      render(player.choose(o.index));
    });
    els.choices.appendChild(btn);
  }
}

function showInput(ev) {
  els.inputbox.hidden = false;
  els.inputPrompt.textContent = ev.prompt || "";
  els.inputField.value = ev.default || "";
  if (ev.max > 0) els.inputField.maxLength = ev.max;
  els.inputField.focus();
  els.inputField.select();
}

function submitInput() {
  if (els.inputbox.hidden) return;
  els.inputbox.hidden = true;
  render(player.submitInput(els.inputField.value));
}
els.inputOk.addEventListener("click", submitInput);
els.inputField.addEventListener("keydown", (e) => { if (e.key === "Enter") submitInput(); });

function stopTimers() {
  clearInterval(choiceTimer);
  clearInterval(typeTimer);
  revealing = false;
}

$("restart").addEventListener("click", compileAndRun);
$("rollback").addEventListener("click", rollback);
document.getElementById("stage").addEventListener("wheel", (e) => {
  if (e.deltaY < 0) { e.preventDefault(); rollback(); }
}, { passive: false });

// ── toolbar ────────────────────────────────────────────────────────────────
$("run").addEventListener("click", compileAndRun);
document.addEventListener("keydown", (e) => {
  if ((e.metaKey || e.ctrlKey) && e.key === "Enter") { e.preventDefault(); compileAndRun(); }
});

$("download").addEventListener("click", () => {
  if (!wasmReady) return;
  const out = window.lvnsCompile(els.editor.value);
  if (!out.ok) { showProblems("Compile error:\n" + out.errors); setStatus("compile error", "err"); return; }
  const blob = new Blob([out.json], { type: "application/json" });
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = "game.lvn";
  a.click();
  URL.revokeObjectURL(a.href);
});

$("export-html").addEventListener("click", async () => {
  if (!wasmReady) return;
  const out = window.lvnsCompile(els.editor.value);
  if (!out.ok) { showProblems("Compile error:\n" + out.errors); setStatus("compile error", "err"); return; }
  const m = /^\s*scene\s+(\S+)/m.exec(els.editor.value);
  // scene ids are snake_case tech names; the card deserves a human title.
  const raw = m ? m[1] : "game";
  const pretty = raw.replace(/[_-]+/g, " ").replace(/^./, (c) => c.toUpperCase());
  await exportHtml(pretty, out.json, catalog, userAssets, art);
  setStatus("HTML saved — the file plays on its own", "ok");
});

$("share").addEventListener("click", async () => {
  const _b = new TextEncoder().encode(els.editor.value);
  let _s = "";
  for (let _i = 0; _i < _b.length; _i += 0x8000) _s += String.fromCharCode.apply(null, _b.subarray(_i, _i + 0x8000));
  const packed = btoa(_s);
  const url = location.origin + location.pathname + "#s=" + packed;
  window.history.replaceState(null, "", "#s=" + packed);
  try {
    await navigator.clipboard.writeText(url);
    setStatus("link copied — it plays right away for whoever opens it", "ok");
  } catch {
    setStatus("the link is in the address bar", "ok");
  }
});

const examplesSel = $("examples");
for (const name of Object.keys(EXAMPLES)) {
  const opt = document.createElement("option");
  opt.value = name; opt.textContent = name;
  examplesSel.appendChild(opt);
}
examplesSel.addEventListener("change", () => {
  els.editor.value = EXAMPLES[examplesSel.value];
  repaint();
  compileAndRun();
});

// ── boot: URL script → example ─────────────────────────────────────────────
let lintTimer = null;
els.editor.addEventListener("input", () => {
  try { localStorage.setItem("lvn-play-draft", els.editor.value); } catch {}
  // Live lint (debounced): same compiler, just not restarting the story.
  clearTimeout(lintTimer);
  lintTimer = setTimeout(() => {
    if (!wasmReady) return;
    const out = window.lvnsCompile(els.editor.value);
    if (!out.ok) { showProblems("Compile error:\n" + out.errors); setStatus("error — fix and press ▶", "err"); }
    else { showProblems(out.warnings ? "Warnings:\n" + out.warnings : ""); setStatus("compiles ✓ (▶ to rerun)", "ok"); }
  }, 400);
});
const repaint = attachHighlight(els.editor, document.getElementById("backdrop"));

function boot() {
  const m = /#s=(.+)/.exec(location.hash);
  if (m) {
    try {
      const bytes = Uint8Array.from(atob(m[1]), (c) => c.charCodeAt(0));
      els.editor.value = new TextDecoder().decode(bytes);
    } catch {
      els.editor.value = EXAMPLES["First scene"];
    }
  } else {
    let draft = null;
    try { draft = localStorage.getItem("lvn-play-draft"); } catch {}
    els.editor.value = draft || EXAMPLES["First scene"];
  }
  repaint();
  compileAndRun();
}

// ── assets drawer — see the demo art, add your own ─────────────────────────
// The demo list mirrors what ships beside the playground (and what the
// content server serves); user uploads land in `userAssets` above.
const DEMO_ASSETS = [
  "/content/bg/Autumn_street.jpg",
  "/content/sprites/doll/bg.png",
  "/content/sprites/doll/apple.png",
  "/content/sprites/doll/bag.png",
  "/content/sprites/doll/body.png",
  "/content/sprites/doll/arm.png",
  "/content/sprites/doll/head.png",
  "/content/sprites/doll/hair.png",
];

function insertAtCursor(text) {
  const ta = els.editor;
  const a = ta.selectionStart ?? ta.value.length;
  const b = ta.selectionEnd ?? ta.value.length;
  ta.value = ta.value.slice(0, a) + text + ta.value.slice(b);
  ta.focus();
  ta.selectionStart = ta.selectionEnd = a + text.length;
  ta.dispatchEvent(new Event("input", { bubbles: true }));
}

function assetTile(ref, src, onRemove) {
  const tile = document.createElement("button");
  tile.className = "asset-tile";
  tile.title = `insert ${ref}`;
  const img = document.createElement("img");
  img.src = src;
  img.loading = "lazy";
  const cap = document.createElement("span");
  cap.textContent = ref.split("/").pop();
  tile.append(img, cap);
  tile.addEventListener("click", () => insertAtCursor(ref));
  if (onRemove) {
    const x = document.createElement("span");
    x.className = "asset-x";
    x.textContent = "✕";
    x.title = "remove";
    x.addEventListener("click", (e) => { e.stopPropagation(); onRemove(); });
    tile.appendChild(x);
  }
  return tile;
}

function renderAssets() {
  const demo = $("assets-demo");
  demo.innerHTML = "";
  for (const u of DEMO_ASSETS) demo.appendChild(assetTile(u, art(u)));
  const mine = $("assets-user");
  mine.innerHTML = "";
  for (const name of Object.keys(userAssets).sort()) {
    mine.appendChild(assetTile(name, userAssets[name], () => {
      delete userAssets[name];
      saveUserAssets();
      renderAssets();
    }));
  }
}

$("assets").addEventListener("click", () => {
  const d = $("assets-drawer");
  d.hidden = !d.hidden;
  if (!d.hidden) renderAssets();
});
$("assets-close").addEventListener("click", () => { $("assets-drawer").hidden = true; });

$("assets-upload").addEventListener("change", async (e) => {
  for (const file of e.target.files) {
    const dataUri = await new Promise((res, rej) => {
      const fr = new FileReader();
      fr.onload = () => res(fr.result);
      fr.onerror = rej;
      fr.readAsDataURL(file);
    });
    userAssets[file.name] = dataUri;
  }
  e.target.value = "";
  saveUserAssets();
  renderAssets();
  setStatus("image(s) added — reference by file name", "ok");
});
