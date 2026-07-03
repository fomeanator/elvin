// Playground glue: the real lvnconv compiler (wasm) turns the left pane's
// .lvns into a .lvn doc; core.js plays it; this file renders pauses into DOM
// and shares scripts through the URL hash — open the link, the game runs.

import { Player } from "./core.js";
import { interpolate } from "./expr.js";

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
    const res = await WebAssembly.instantiateStreaming(fetch("/lvns.wasm"), go.importObject);
    go.run(res.instance);
    wasmReady = true;
    setStatus("готов", "ok");
    boot();
  } catch (e) {
    setStatus("компилятор не загрузился: " + e.message, "err");
  }
})();

function setStatus(text, cls) {
  els.status.textContent = text;
  els.status.className = "status" + (cls ? " " + cls : "");
}

// ── examples ───────────────────────────────────────────────────────────────
const EXAMPLES = {
  "Первая сцена": `scene playground

Тёмная комната. Пахнет пылью и старыми книгами.
input var=name prompt="Как тебя зовут?" default="Гость" max=20
Голос: Наконец-то, {name}. Я ждал тебя.
Голос: У тебя пять секунд, чтобы решить.

choice timeout=5 timeout_goto=frozen
- Искать выключатель -> light
- Бежать к двери -> door

:light
courage = 1
Ты нашёл выключатель. Комната оказалась библиотекой.
-> finale

:door
courage = 0
Дверь не поддалась. Зато глаза привыкли к темноте.
-> finale

:frozen
courage = 0
Ты замер. Иногда это тоже выбор.
-> finale

:finale
if courage >= 1 -> brave
Голос: Осторожность — не слабость, {name}.
-> __end
:brave
Голос: Смело, {name}. Мне это нравится.`,

  "Лавка и инвентарь": `scene shop

gold = 12
inv = []
text hud x=4 y=6 size=20 color=#ffd166 «💰 {gold}  🎒 {inv}»

Торговец: Что берём? Меч — 10, яблоко — 3.
:menu
- Меч (10 золота) -> sword expr="gold >= 10"
- Яблоко (3) -> apple expr="gold >= 3"
- Показать карманы -> pockets
- Уйти -> bye

:sword
gold = gold - 10
inv = inv + ["меч"]
Торговец: Отличная сталь!
-> menu
:apple
gold = gold - 3
inv = inv + ["яблоко"]
Торговец: Свежее!
-> menu
:pockets
Ты выворачиваешь карманы — {inv}.
-> menu
:bye
if has(inv, "меч") -> armed
Торговец: Заходи ещё.
-> __end
:armed
Торговец: С мечом-то оно спокойнее, да?`,

  "Викторина-блиц": `scene quiz

score = 0
Викторина! Три вопроса, времени мало.

Вопрос 1. Сколько байт в килобайте?
choice timeout=6 timeout_goto=miss1
- 1000 -> w1
- 1024 -> r1
:r1
score = score + 1
Верно!
-> q2
:w1
Нет — 1024.
-> q2
:miss1
Время! Правильный ответ — 1024.
-> q2

:q2
Вопрос 2. Самая большая планета?
choice timeout=6 timeout_goto=miss2
- Юпитер -> r2
- Сатурн -> w2
:r2
score = score + 1
Точно.
-> res
:w2
Это Юпитер.
-> res
:miss2
Поздно! Юпитер.
-> res

:res
Твой счёт — {score} из 2.
if score >= 2 -> ace
Неплохо. Реванш?
-> __end
:ace
Безупречно!`,
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
    showProblems("Ошибка компиляции:\n" + out.errors);
    setStatus("ошибка компиляции", "err");
    return;
  }
  showProblems(out.warnings ? "Предупреждения:\n" + out.warnings : "");
  setStatus(out.warnings ? "запущено (есть предупреждения)" : "запущено ✓", out.warnings ? "" : "ok");

  const doc = JSON.parse(out.json);
  resetStage();
  player = new Player(doc, { onStage: applyStage });
  render(player.advance());
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
  els.dialogue.hidden = true;
  els.choices.hidden = true;
  els.choices.innerHTML = "";
  els.inputbox.hidden = true;
  els.endcard.hidden = true;
}

const hudLabels = new Map();

function applyStage(cmd, vars) {
  switch (cmd.op) {
    case "bg":
      if (cmd.sprite_url) els.bg.style.backgroundImage = `url("${cmd.sprite_url}")`;
      break;
    case "actor":
    case "obj": {
      if (!cmd.id) break;
      let img = els.actors.querySelector(`[data-id="${cmd.id}"]`);
      if (cmd.show === false) { img?.remove(); break; }
      const url = cmd.sprite_url || cmd.body_url;
      if (!img && url) {
        img = document.createElement("img");
        img.dataset.id = cmd.id;
        els.actors.appendChild(img);
      }
      if (!img) break;
      if (url) img.src = url;
      const x = typeof cmd.x === "number" ? cmd.x
        : cmd.position === "left" ? 0.22 : cmd.position === "right" ? 0.78 : 0.5;
      img.style.left = (x * 100) + "%";
      if (typeof cmd.width === "number") img.style.maxWidth = (cmd.width * 100) + "%";
      if (typeof cmd.opacity === "number") img.style.opacity = cmd.opacity;
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
      // One looping music channel is enough for a demo pane.
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
    // fade/dim/camera/particles/anim: quietly skipped — the note under the
    // stage says the full staging lives in the Unity runtime.
  }
}

function refreshHud(vars) {
  for (const { el, template } of hudLabels.values())
    el.textContent = interpolate(template, vars);
}

// ── pause-event rendering ──────────────────────────────────────────────────
function render(ev) {
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

// ── toolbar ────────────────────────────────────────────────────────────────
$("run").addEventListener("click", compileAndRun);
document.addEventListener("keydown", (e) => {
  if ((e.metaKey || e.ctrlKey) && e.key === "Enter") { e.preventDefault(); compileAndRun(); }
});

$("share").addEventListener("click", async () => {
  const packed = btoa(String.fromCharCode(...new TextEncoder().encode(els.editor.value)));
  const url = location.origin + location.pathname + "#s=" + packed;
  history.replaceState(null, "", "#s=" + packed);
  try {
    await navigator.clipboard.writeText(url);
    setStatus("ссылка скопирована — у открывшего сразу играет", "ok");
  } catch {
    setStatus("ссылка в адресной строке", "ok");
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
  compileAndRun();
});

// ── boot: URL script → example ─────────────────────────────────────────────
function boot() {
  const m = /#s=(.+)/.exec(location.hash);
  if (m) {
    try {
      const bytes = Uint8Array.from(atob(m[1]), (c) => c.charCodeAt(0));
      els.editor.value = new TextDecoder().decode(bytes);
    } catch {
      els.editor.value = EXAMPLES["Первая сцена"];
    }
  } else {
    els.editor.value = EXAMPLES["Первая сцена"];
  }
  compileAndRun();
}
