// One-file HTML export — the Twine lesson in full: pack the compiled .lvn,
// the interpreter (core/expr sources fetched and inlined, import/export lines
// stripped) and a lean stage renderer into a single self-contained .html.
// Text, choices, timers, input and variables play anywhere the file opens;
// art urls keep pointing wherever they pointed (absolute urls travel well).

/** Collect every art/audio url the script can touch (bg / sprite urls /
 * catalog layers for the actors it stages, across the axis values it uses),
 * fetch each and return {url → dataURL}. Unreachable art is skipped — the
 * exported story degrades to text instead of failing. */
async function inlineAssets(doc, catalog, extraAssets = {}, resolve = (u) => u) {
  const urls = new Set();
  const actorAxes = new Map(); // id → Set of "axis=value" combos seen
  for (const c of doc.script || []) {
    if (c.op === "bg" && c.sprite_url) urls.add(c.sprite_url);
    if ((c.op === "actor" || c.op === "obj")) {
      if (c.sprite_url) urls.add(c.sprite_url);
      if (c.body_url) urls.add(c.body_url);
      if (!c.sprite_url && !c.body_url && c.id && catalog[c.id]) {
        if (!actorAxes.has(c.id)) actorAxes.set(c.id, [{}]);
        actorAxes.get(c.id).push({ ...c });
      }
    }
    if (c.op === "audio" && c.url) urls.add(c.url);
    if (c.op === "say" && c.voice) urls.add(c.voice);
  }
  const usedCatalog = {};
  for (const [id, cmds] of actorAxes) {
    const entity = catalog[id];
    usedCatalog[id] = entity;
    const axes = entity.axes || {};
    const defs = entity.defaults || {};
    for (const cmd of cmds) {
      for (const raw of entity.layers || []) {
        const l = typeof raw === "string" ? { url: raw } : raw;
        if (!l.url) continue;
        urls.add(l.url.replace(/\{(\w+)\}/g, (_, a) =>
          cmd[a] ?? defs[a] ?? (axes[a] && axes[a][0]) ?? ""));
      }
    }
  }
  const map = {};
  await Promise.all([...urls].map(async (u) => {
    if (extraAssets[u]) { map[u] = extraAssets[u]; return; } // uploaded image — already a data URI
    try {
      const r = await fetch(resolve(u));
      if (!r.ok) return;
      const blob = await r.blob();
      if (blob.size > 4 * 1024 * 1024) return; // one oversized asset won't balloon the file
      map[u] = await new Promise((res) => {
        const fr = new FileReader();
        fr.onload = () => res(fr.result);
        fr.readAsDataURL(blob);
      });
    } catch { /* offline/missing art → text-only beat */ }
  }));
  return { assetMap: map, usedCatalog };
}

export async function exportHtml(title, lvnJson, catalog = {}, extraAssets = {}, resolve = (u) => u) {
  const [core, expr] = await Promise.all([
    fetch("core.js").then((r) => r.text()),
    fetch("expr.js").then((r) => r.text()),
  ]);
  const doc = JSON.parse(lvnJson);
  const { assetMap, usedCatalog } = await inlineAssets(doc, catalog, extraAssets, resolve);
  const strip = (s) => s
    .replace(/^import .*$/gm, "")
    .replace(/^export default /gm, "")
    .replace(/^export /gm, "");

  const html = TEMPLATE
    .replaceAll("__TITLE__", escapeHtml(title || "LVN story"))
    .replace("__EXPR__", strip(expr))
    .replace("__CORE__", strip(core))
    .replace("__DOC__", "<" + "script id=\"lvn-doc\" type=\"application/json\">"
      + JSON.stringify({ doc, assets: assetMap, catalog: usedCatalog }).replace(/<\/script/gi, "<\\/script")
      + "</" + "script>");

  const blob = new Blob([html], { type: "text/html" });
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = (title || "game").replace(/[^\wА-Яа-яЁё-]+/g, "_") + ".html";
  a.click();
  URL.revokeObjectURL(a.href);
}

function escapeHtml(s) {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;");
}

const TEMPLATE = `<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>__TITLE__</title>
<style>
* { box-sizing: border-box; } [hidden] { display: none !important; }
html, body { height: 100%; margin: 0; }
body { font: 16px/1.5 -apple-system, "Segoe UI", Roboto, sans-serif; background: #0a0a0f; color: #e8e4da; }
.stage { position: relative; width: 100%; height: 100%; overflow: hidden;
  background: radial-gradient(ellipse at 50% 30%, #181822 0%, #0a0a0f 75%); }
.bg { position: absolute; inset: 0; background-size: cover; background-position: center; }
.actors { position: absolute; inset: 0; pointer-events: none; }
.actors img { position: absolute; bottom: 0; transform: translateX(-50%); max-height: 85%; }
.actors .actor-box { position: absolute; bottom: 0; transform: translateX(-50%); }
.actors .actor-box img { position: absolute; max-height: none; transform: none; object-fit: contain; bottom: auto; }
.hud { position: absolute; inset: 0; pointer-events: none; }
.hud .hud-label { position: absolute; font-weight: 600; text-shadow: 0 1px 3px #000; }
.veil { position: absolute; inset: 0; pointer-events: none; background: #000; opacity: 0; transition: opacity .5s; }
.dialogue { position: absolute; left: 3%; right: 3%; bottom: 3%; background: rgba(13,13,20,.84);
  border-radius: 12px; padding: 14px 18px 18px; cursor: pointer; box-shadow: 0 6px 24px rgba(0,0,0,.5); }
.speaker { color: #ffd166; font-weight: 700; font-size: 14px; margin-bottom: 4px; }
.speaker:empty { display: none; }
.line { font-size: 17px; min-height: 1.5em; white-space: pre-wrap; }
.advance-hint { position: absolute; right: 14px; bottom: 8px; color: #8f8a80; font-size: 12px; animation: pulse 1.2s infinite; }
@keyframes pulse { 50% { opacity: .25; } }
.choices { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center;
  justify-content: center; gap: 10px; padding: 20px; background: rgba(0,0,0,.25); }
.choices .timerbar { width: 58%; height: 6px; border-radius: 3px; background: rgba(255,255,255,.15); overflow: hidden; margin-bottom: 6px; }
.choices .timerbar > div { height: 100%; background: #e6a33b; width: 100%; }
.choices button { min-width: 58%; max-width: 86%; padding: 12px 20px; background: rgba(31,31,41,.95);
  color: #f5f5f5; border: 0; border-radius: 10px; font-size: 16px; cursor: pointer; }
.inputbox { position: absolute; left: 12%; right: 12%; top: 50%; transform: translateY(-50%);
  background: rgba(20,20,26,.97); border-radius: 12px; padding: 20px; display: flex; flex-direction: column; gap: 12px; }
.inputbox input { background: #101016; color: #e8e4da; border: 1px solid #2c2c36; border-radius: 8px; padding: 10px 12px; font-size: 16px; outline: none; }
.inputbox button, .endcard button { background: #c8a050; color: #14141a; font-weight: 600; border: 0; border-radius: 8px; padding: 10px 16px; font-size: 15px; cursor: pointer; }
.endcard { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center;
  justify-content: center; gap: 16px; background: rgba(0,0,0,.55); font-size: 28px; }
.made { position: absolute; top: 8px; right: 12px; font-size: 11px; color: #6d6962; z-index: 5; }
.titlecard { position: absolute; inset: 0; z-index: 6; display: flex; flex-direction: column;
  align-items: center; justify-content: center; gap: 24px;
  background: radial-gradient(ellipse at 50% 35%, #1c1c28 0%, #0a0a0f 80%); }
.tc-name { font-size: 34px; font-weight: 700; letter-spacing: .5px; color: #f4ecd8;
  text-align: center; padding: 0 24px; }
.titlecard button { background: #c8a050; color: #14141a; font-weight: 700; border: 0;
  border-radius: 12px; padding: 14px 34px; font-size: 18px; cursor: pointer; }
.backbtn { position: absolute; top: 8px; left: 10px; z-index: 5; background: rgba(38,38,46,.8);
  color: #e8e4da; border: 0; border-radius: 8px; padding: 6px 10px; font-size: 14px; cursor: pointer; }
.made a { color: #8f8a80; }
</style>
</head>
<body>
<div class="stage">
  <div id="bg" class="bg"></div>
  <div id="actors" class="actors"></div>
  <div id="hud" class="hud"></div>
  <div id="veil" class="veil"></div>
  <div id="dialogue" class="dialogue" hidden>
    <div id="speaker" class="speaker"></div>
    <div id="line" class="line"></div>
    <div class="advance-hint">▼</div>
  </div>
  <div id="choices" class="choices" hidden></div>
  <div id="inputbox" class="inputbox" hidden>
    <div id="input-prompt"></div>
    <input id="input-field" type="text" />
    <button id="input-ok">OK</button>
  </div>
  <div id="endcard" class="endcard" hidden>
    <div>The End</div>
    <button id="restart">↻ Restart</button>
  </div>
  <div id="titlecard" class="titlecard">
    <div class="tc-name">__TITLE__</div>
    <button id="play-btn">▶ Play</button>
  </div>
  <button id="back" class="backbtn" title="Step back">↩</button>
  <span class="made">made with <a href="https://github.com/fomeanator/unity-lvn-vn-engine">LVN</a></span>
</div>
__DOC__
<script>
__EXPR__
__CORE__

// ── lean standalone renderer (a distilled app.js without the editor) ──
const $id = (x) => document.getElementById(x);
const bundle = JSON.parse($id("lvn-doc").textContent);
const doc = bundle.doc, ASSETS = bundle.assets || {}, CATALOG = bundle.catalog || {};
const art = (u) => ASSETS[u] || u;
let player, typeTimer, choiceTimer, fullLine = "", revealing = false;
const hudLabels = new Map();
const SAVE_KEY = "lvn-html-save:" + (document.title || "story");
let stagedState = { bg: null, actors: {}, hud: {} };

function trackStage(cmd) {
  if (cmd.op === "bg" && cmd.sprite_url) stagedState.bg = cmd.sprite_url;
  else if (cmd.op === "actor" || cmd.op === "obj") {
    if (!cmd.id) return;
    if (cmd.show === false) delete stagedState.actors[cmd.id];
    else stagedState.actors[cmd.id] = Object.assign({}, stagedState.actors[cmd.id] || {}, cmd);
  } else if (cmd.op === "text" && cmd.id) {
    if (cmd.hide) delete stagedState.hud[cmd.id];
    else stagedState.hud[cmd.id] = Object.assign({}, stagedState.hud[cmd.id] || {}, cmd);
  }
}

function autosave() {
  if (!player || player.finished) return;
  try { localStorage.setItem(SAVE_KEY, JSON.stringify({ snap: player.snapshot(), stage: stagedState })); } catch {}
}

let history = [];
function pushHistory() {
  if (!player || player.finished) return;
  history.push({ snap: player.snapshot(), stage: JSON.parse(JSON.stringify(stagedState)) });
  if (history.length > 100) history.shift();
}
function rollback() {
  if (history.length < 2) return;
  clearInterval(typeTimer); clearInterval(choiceTimer);
  history.pop();
  const prev = history.pop();
  for (const x of ["choices", "inputbox", "endcard"]) $id(x).hidden = true;
  stagedState = prev.stage;
  $id("actors").innerHTML = ""; $id("hud").innerHTML = ""; hudLabels.clear();
  if (stagedState.bg) applyStage({ op: "bg", sprite_url: stagedState.bg });
  for (const cmd of Object.values(stagedState.actors)) applyStage(cmd);
  for (const cmd of Object.values(stagedState.hud)) applyStage(cmd);
  render(player.restore(prev.snap));
}

function start() {
  clearInterval(typeTimer); clearInterval(choiceTimer);
  $id("bg").style.backgroundImage = "";
  $id("actors").innerHTML = ""; $id("hud").innerHTML = ""; hudLabels.clear();
  $id("veil").style.opacity = 0;
  for (const x of ["dialogue", "choices", "inputbox", "endcard"]) $id(x).hidden = true;
  hudLabels.clear();
  stagedState = { bg: null, actors: {}, hud: {} };
  history = [];
  player = new Player(doc, { onStage: (c) => { trackStage(c); applyStage(c); } });

  let saved = null;
  try { saved = JSON.parse(localStorage.getItem(SAVE_KEY) || "null"); } catch {}
  if (saved && saved.snap && saved.snap.ip > 0 && saved.snap.ip < doc.script.length) {
    const box = $id("choices"); box.innerHTML = ""; box.hidden = false;
    const note = document.createElement("div");
    note.style.color = "#cfc8bd"; note.textContent = "A save exists — continue?";
    box.appendChild(note);
    const go = document.createElement("button"); go.textContent = "▶ Continue";
    go.addEventListener("click", () => {
      box.hidden = true;
      stagedState = saved.stage || stagedState;
      if (stagedState.bg) applyStage({ op: "bg", sprite_url: stagedState.bg });
      for (const cmd of Object.values(stagedState.actors)) applyStage(cmd);
      for (const cmd of Object.values(stagedState.hud)) applyStage(cmd);
      render(player.restore(saved.snap));
    });
    box.appendChild(go);
    const anew = document.createElement("button"); anew.textContent = "↻ Start over";
    anew.addEventListener("click", () => { try { localStorage.removeItem(SAVE_KEY); } catch {}
      box.hidden = true; render(player.advance()); });
    box.appendChild(anew);
    return;
  }
  render(player.advance());
}

function applyStage(cmd) {
  switch (cmd.op) {
    case "bg": if (cmd.sprite_url) $id("bg").style.backgroundImage = 'url("' + art(cmd.sprite_url) + '")'; break;
    case "actor": case "obj": {
      if (!cmd.id) break;
      let node = $id("actors").querySelector('[data-id="' + cmd.id + '"]');
      if (cmd.show === false) { node && node.remove(); break; }
      const entity = !cmd.sprite_url && !cmd.body_url ? CATALOG[cmd.id] : null;
      if (entity && entity.layers) {
        if (!node || node.tagName !== "DIV") { node && node.remove();
          node = document.createElement("div"); node.className = "actor-box"; node.dataset.id = cmd.id; $id("actors").appendChild(node); }
        node.innerHTML = "";
        const axes = entity.axes || {}, defs = entity.defaults || {};
        const val = (a) => cmd[a] ?? defs[a] ?? (axes[a] && axes[a][0]) ?? "";
        for (const raw of entity.layers) {
          const l = typeof raw === "string" ? { url: raw } : raw;
          if (!l.url) continue;
          const img = document.createElement("img");
          img.src = art(l.url.replace(/\\{(\\w+)\\}/g, (_, a) => val(a)));
          if (typeof l.x === "number") { img.style.left = (l.x * 100) + "%"; img.style.top = ((l.y ?? 0) * 100) + "%";
            img.style.width = ((l.w ?? 1) * 100) + "%"; img.style.height = ((l.h ?? 1) * 100) + "%"; }
          else { img.style.left = "0"; img.style.top = "0"; img.style.width = "100%"; img.style.height = "100%"; }
          node.appendChild(img);
        }
        const bx = typeof cmd.x === "number" ? cmd.x : cmd.position === "left" ? 0.22 : cmd.position === "right" ? 0.78 : 0.5;
        node.style.left = (bx * 100) + "%";
        node.style.height = ((typeof cmd.height === "number" ? cmd.height : 0.8) * 100) + "%";
        node.style.aspectRatio = String(entity.aspect || 0.6);
        break;
      }
      const url = cmd.sprite_url || cmd.body_url;
      if (!node && url) { node = document.createElement("img"); node.dataset.id = cmd.id; $id("actors").appendChild(node); }
      if (!node) break;
      if (url && node.tagName === "IMG") node.src = art(url);
      const x = typeof cmd.x === "number" ? cmd.x : cmd.position === "left" ? 0.22 : cmd.position === "right" ? 0.78 : 0.5;
      node.style.left = (x * 100) + "%";
      if (typeof cmd.width === "number") node.style.maxWidth = (cmd.width * 100) + "%";
      break;
    }
    case "text": {
      if (!cmd.id) break;
      if (cmd.hide) { const e = hudLabels.get(cmd.id); e && e.el.remove(); hudLabels.delete(cmd.id); break; }
      let entry = hudLabels.get(cmd.id);
      if (!entry) { const el = document.createElement("div"); el.className = "hud-label"; $id("hud").appendChild(el); entry = { el, template: "" }; hudLabels.set(cmd.id, entry); }
      if (cmd.text) entry.template = cmd.text;
      entry.el.style.left = (cmd.x ?? 4) + "%"; entry.el.style.top = (cmd.y ?? 4) + "%";
      entry.el.style.fontSize = ((cmd.size ?? 24) * 0.6) + "px"; entry.el.style.color = cmd.color || "#f1e4c9";
      break;
    }
    case "fade": { const v = $id("veil"); const to = cmd.to || "black";
      v.style.background = to === "white" ? "#fff" : "#000";
      v.style.opacity = to === "clear" ? 0 : 1;
      if (to !== "clear") setTimeout(() => { v.style.opacity = 0; }, 650); break; }
    case "dim": { const v = $id("veil"); v.style.background = "#000"; v.style.opacity = cmd.alpha ?? 0.4; break; }
    case "tint": { const v = $id("veil"); v.style.background = cmd.color || "#000"; v.style.opacity = cmd.alpha ?? 0.3; break; }
    case "audio": if (cmd.channel === "sfx" && cmd.action !== "stop" && cmd.url) { new Audio(art(cmd.url)).play().catch(() => {}); break; }
      if (cmd.channel === "music") {
      if (cmd.action === "stop") { window.__m && window.__m.pause(); break; }
      if (cmd.url) { window.__m && window.__m.pause(); const a = new Audio(art(cmd.url)); a.loop = cmd.loop !== false; a.play().catch(() => {}); window.__m = a; }
    } break;
  }
}

function refreshHud() { for (const { el, template } of hudLabels.values()) el.textContent = interpolate(template, player.vars); }

function render(ev) {
  if (ev.type === "say" || ev.type === "choice" || ev.type === "input") pushHistory();
  autosave();
  refreshHud();
  $id("choices").hidden = true; $id("inputbox").hidden = true;
  if (ev.type === "say") showLine(ev, false);
  else if (ev.type === "choice") { if (ev.text !== undefined) showLine(ev, true); showChoices(ev); }
  else if (ev.type === "input") { $id("inputbox").hidden = false; $id("input-prompt").textContent = ev.prompt || ""; const f = $id("input-field"); f.value = ev.default || ""; if (ev.max > 0) f.maxLength = ev.max; f.focus(); f.select(); }
  else if (ev.type === "wait") setTimeout(() => render(player.advance()), ev.ms);
  else if (ev.type === "end") { $id("dialogue").hidden = true; $id("endcard").hidden = false;
    try { localStorage.removeItem(SAVE_KEY); } catch {} }
}

function showLine(ev, noAdvance) {
  const d = $id("dialogue"); d.hidden = false;
  $id("speaker").textContent = ev.who || "";
  fullLine = ev.text || ""; $id("line").textContent = ""; revealing = true;
  let i = 0; clearInterval(typeTimer);
  typeTimer = setInterval(() => { i += 2; $id("line").textContent = fullLine.slice(0, i);
    if (i >= fullLine.length) { clearInterval(typeTimer); revealing = false; } }, 24);
  d.dataset.noadvance = noAdvance ? "1" : "";
}

$id("dialogue").addEventListener("click", () => {
  if (!$id("inputbox").hidden || !$id("choices").hidden) return;
  if (revealing) { clearInterval(typeTimer); $id("line").textContent = fullLine; revealing = false; return; }
  if ($id("dialogue").dataset.noadvance === "1") return;
  if (player && !player.finished) render(player.advance());
});

function showChoices(ev) {
  const box = $id("choices"); box.innerHTML = ""; box.hidden = false;
  if (ev.timeout > 0 && ev.hasTimeoutBranch) {
    const bar = document.createElement("div"); bar.className = "timerbar";
    const fill = document.createElement("div"); bar.appendChild(fill); box.appendChild(bar);
    const deadline = performance.now() + ev.timeout * 1000;
    choiceTimer = setInterval(() => { const left = deadline - performance.now();
      fill.style.width = Math.max(0, left / (ev.timeout * 10)) + "%";
      if (left <= 0) { clearInterval(choiceTimer); render(player.timeoutChoice()); } }, 80);
  }
  for (const o of ev.options) {
    const b = document.createElement("button"); b.textContent = o.text;
    b.addEventListener("click", () => { clearInterval(choiceTimer); render(player.choose(o.index)); });
    box.appendChild(b);
  }
}

function submit() { if ($id("inputbox").hidden) return; $id("inputbox").hidden = true; render(player.submitInput($id("input-field").value)); }
$id("input-ok").addEventListener("click", submit);
$id("input-field").addEventListener("keydown", (e) => { if (e.key === "Enter") submit(); });
$id("restart").addEventListener("click", start);
$id("back").addEventListener("click", rollback);
document.addEventListener("wheel", (e) => { if (e.deltaY < 0) rollback(); });
// The title card doubles as the user gesture the autoplay policy wants —
// music started after this click is allowed to sound.
$id("play-btn").addEventListener("click", () => {
  $id("titlecard").remove();
  start();
});
</script>
</body>
</html>`;
