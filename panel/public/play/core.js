// The playground's story interpreter — a faithful JS mini-port of LvnPlayer
// for the .lvn subset a browser demo needs: say / choice (with timeout) /
// input / label / goto / call / return / if / set / inc / wait, plus every
// staging command forwarded to the host via onStage (bg, actor, text, audio,
// fade, …) so the renderer draws what it supports and ignores the rest.
//
// Pure and DOM-free: advance()/choose()/submitInput() return a pause event
// ({type: say|choice|input|wait|end, …}); the UI renders it and calls back.

// ИМПОРТ — ОДНОЙ СТРОКОЙ, и это не стиль. Экспорт в самостоятельный .html
// снимает модульные строки построчно (`^import .*$`), поэтому перенос строки
// внутри импорта оставляет его хвост в упакованном коде и ломает файл.
import { evalExpr, evalBool, interpolate, getVarPath, setVarPath, evalStructuredCond, seedRandom, randomState, restoreRandom } from "./expr.js";

// СОСТОЯНИЕ СЦЕНЫ ИЗ ПЕРЕСЛАННЫХ КОМАНД — что стоит в кадре прямо сейчас.
//
// Плеер команды постановки не трактует, он их пересылает; но и песочнице, и
// экспортированной игре нужно ЗАПОМНИТЬ кадр — для автосохранения и возврата.
// Правило было списано дважды: в app.js и в шаблоне экспорта, — и копии
// разошлись. В экспорте не было ветки `clear`, поэтому после восстановления
// сохранения там возвращались актёры, которых сцена убрала.
//
// Живёт здесь, потому что core.js инлайнится в экспорт целиком: одно правило
// доезжает обоим само, без переписывания.
export function trackStage(state, cmd) {
  if (!cmd || !state) return state;
  state.actors = state.actors || {};
  state.hud = state.hud || {};
  if (cmd.op === "bg" && cmd.sprite_url) state.bg = cmd.sprite_url;
  // `clear` empties the cast and nothing else — the backdrop and the HUD are
  // deliberately left in place, so a scene change stays `clear` + a new `bg`.
  else if (cmd.op === "clear") state.actors = {};
  else if (cmd.op === "actor" || cmd.op === "obj") {
    if (!cmd.id) return state;
    if (!flag(cmd.show, true)) delete state.actors[cmd.id];
    else state.actors[cmd.id] = Object.assign({}, state.actors[cmd.id] || {}, cmd);
  } else if (cmd.op === "text" && cmd.id) {
    if (flag(cmd.hide, false)) delete state.hud[cmd.id];
    else state.hud[cmd.id] = Object.assign({}, state.hud[cmd.id] || {}, cmd);
  }
  return state;
}

// ГЛУБИНА ОТКАТА И САМ ОТКАТ — правило одно на песочницу и на экспорт.
//
// Толчок в историю выглядит на три строки, и потому его писали дважды: в
// песочнице и в самостоятельном файле. Копии уже начали расходиться — у одной
// глубина стояла именованной постоянной, у другой числом. Совпадали они по
// везению, а цена расхождения ровно та, ради которой заведён страж экспорта:
// автор проверяет откат в песочнице и отдаёт игроку игру, которая ведёт себя
// иначе.
export const HISTORY_MAX = 100;

// Снимок берётся ВМЕСТЕ с кадром сцены и глубокой копией: кадр меняется на
// месте, и ссылка на него превратила бы историю в сто ссылок на одно
// состояние — откат «работает», но возвращает всегда последнее.
export function pushBeat(history, player, stagedState) {
  if (!player || player.finished) return;
  history.push({ snap: player.snapshot(), stage: JSON.parse(JSON.stringify(stagedState)) });
  if (history.length > HISTORY_MAX) history.shift();
}

// ПЕРЕИГРАТЬ КАДР — вернуть на экран то, что сохранено: фон, состав, надписи.
//
// Порядок важен и потому записан один раз: сперва фон (он под всеми), затем
// тела, затем HUD (он поверх). Обряд стоял двумя копиями — в песочнице и в
// шаблоне экспорта, — и держался тем, что четыре строки легко переписать
// одинаково. Ровно так же «легко» разошлись соседние копии: ветка `clear`
// пропала и в состоянии кадра, и в рисовании.
//
// Рисовальщик передаётся снаружи: у песочницы и у самостоятельного файла свой
// DOM, и это законно — общим остаётся ПОРЯДОК, а не способ рисовать.
export function replayStage(state, apply) {
  if (!state || typeof apply !== "function") return;
  if (state.bg) apply({ op: "bg", sprite_url: state.bg });
  for (const cmd of Object.values(state.actors || {})) apply(cmd);
  for (const cmd of Object.values(state.hud || {})) apply(cmd);
}

// СЛОВАРЬ «ДА-НЕТ» — тот же, что у движка (Lvn.LvnBool) и у повтора кадра
// (lvn.flagOn). Компилятор булевых значений НЕ приводит: `show=no` доезжает до
// плеера СТРОКОЙ «no», `show=0` — числом, и разобрать это обязан плеер.
//
// Возвращает null для слова НЕ ИЗ СЛОВАРЯ: это «не понял», а не «нет», и
// различать обязан вызывающий — полю команды правильнее взять своё умолчание.
function parseFlag(v) {
  if (v === null || v === undefined) return null;
  if (typeof v === "boolean") return v;
  if (typeof v === "number") return v !== 0;
  if (typeof v !== "string") return null;
  switch (v.trim().toLowerCase()) {
    case "1": case "true": case "yes": case "y": case "on": case "да":
      return true;
    case "0": case "false": case "no": case "n": case "off": case "нет":
      return false;
    default:
      return null;
  }
}

// ЗНАЧЕНИЕ ПОЛЯ: не понял — берём умолчание. Зеркало LvnBool.Of.
//
// Читать `cmd.hide` голой истинностью JS нельзя: строка «no» истинна, и
// `hide=no` прятал бы надпись, которую движок оставляет. Так три копии
// рендерера и разошлись с рантаймом на полях, решающих, кто на экране.
export function flag(v, fallback) {
  const parsed = parseFlag(v);
  return parsed === null ? fallback : parsed;
}

// Согласие там, где умолчание — «нет»: незнакомое слово вернее считать
// опечаткой автора, чем его решением.
export function consent(v) {
  return flag(v, false);
}

// ЧИСЛО ИЗ ЗНАЧЕНИЯ СОСТОЯНИЯ — то же правило, что у движка (Lvn.LvnNum.Value):
// число как есть, ЛОГИЧЕСКОЕ как единица или ноль, число-строкой разбирается
// (так приходит ввод игрока), всё прочее — ноль.
//
// Здесь стояло `parseFloat(cur) || 0`, и логическое значение превращалось в
// ноль: `set flag=true` + `inc flag` давали 1 вместо 2. Корпус это ловит
// (values-number-from-string), но до 28.08 браузер им не проверялся.
export function stateNumber(v) {
  if (typeof v === "number") return Number.isFinite(v) ? v : 0;
  if (typeof v === "boolean") return v ? 1 : 0;
  if (typeof v === "string") {
    const n = parseFloat(v.trim());
    return Number.isFinite(n) ? n : 0;
  }
  return 0;
}

export class Player {
  // У ПРОГОНА ЕСТЬ СВОЁ СЕМЯ. Без него поток засевался бы от часов при первом
  // же броске — то есть воспроизводимость существовала бы в генераторе и не
  // существовала на практике. `seed` принимают и тесты, и корпус, и ссылка
  // «поиграй в мою историю»: одно число делает прогон повторимым для всех.
  constructor(doc, { onStage, seed } = {}) {
    seedRandom(seed);
    this.seed = seed;
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
          // `default` means INITIALISE-ONLY: a chapter-entry default must not
          // stomp a value carried in from an earlier chapter or a save.
          //
          // Сравнение шло с `true` — то есть понимался ТОЛЬКО настоящий
          // логический литерал. Но компилятор булевы не нормализует:
          // `default=yes` доезжает строкой «yes», `default=1` числом, и в
          // браузере такой default молча не срабатывал — значение
          // перезаписывалось вопреки написанному. То же правило в движке живёт
          // у Чтеца «да-нет» (Lvn.LvnBool); здесь оно повторено по словарю,
          // который закреплён корпусом (values-boolean-forms).
          if (c.key && !(consent(c.default) && getVarPath(this.vars, c.key) !== null))
            setVarPath(this.vars, c.key, v);
          this.ip++;
          break;
        }
        case "inc": {
          // `by` is a NUMBER, not an expression: LvnPlayer coerces only
          // numeric/boolean and falls back to 1 otherwise, so evaluating a
          // string here made the same script step differently in the two
          // runtimes. The validator warns about a non-numeric `by`.
          const by = typeof c.by === "number" ? c.by
            : typeof c.by === "boolean" ? (c.by ? 1 : 0) : 1;
          const cur = getVarPath(this.vars, c.key);
          setVarPath(this.vars, c.key, stateNumber(cur) + by);
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
          // `expr` wins; the STRUCTURED form {key,op,value} is what the articy
          // importer emits, and this player used to ignore it entirely — every
          // imported condition silently took the else branch here while working
          // in the app. Mirrors LvnPlayer.EvalCond.
          let cond = false;
          try {
            if (c.expr !== undefined && c.expr !== null) cond = evalBool(c.expr, this.vars);
            else if (c.cond) cond = evalStructuredCond(c.cond, this.vars);
          } catch { cond = false; }
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
          if (this.ip < this.script.length && this.script[this.ip].op === "choice"
              && this.visibleOptions(this.script[this.ip]).length > 0) {
            const ch = this.pauseChoice(this.script[this.ip]);
            ch.who = who; ch.text = text; ch.style = c.style;
            return ch;
          }
          return { type: "say", who, text, style: c.style };
        }
        case "choice":
          // НИ ОДНОГО ВАРИАНТА — НЕ ВСТАЁМ. Все закрыты порогом стата или
          // условием: показывать нечего, а прежний код всё равно показывал —
          // пустую стопку и ожидание выбора, которого игрок сделать не может.
          // Тот же закон, что и в LvnPlayer: идём дальше по скрипту.
          if (this.visibleOptions(c).length === 0) {
            if (typeof console !== "undefined" && console.warn) {
              console.warn(`[lvn-play] выбор на шаге ${this.ip}: ни один из ` +
                           `${(c.options || []).length} вариантов не доступен — иду дальше`);
            }
            this.ip++;
            break;
          }
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
    const options = this.visibleOptions(c);
    return {
      type: "choice",
      options,
      timeout: typeof c.timeout === "number" ? c.timeout : 0,
      hasTimeoutBranch: !!c.timeout_goto,
    };
  }

  /** Варианты, прошедшие условие: порог стата и выражение. Пустой список
   *  значит «показывать нечего» — см. case "choice" в advance(). */
  visibleOptions(c) {
    const options = [];
    (c.options || []).forEach((o, i) => {
      if (o.expr !== undefined && o.expr !== "") {
        try { if (!evalBool(o.expr, this.vars)) return; } catch { return; }
      }
      if (o.requires_stat) {
        const v = getVarPath(this.vars, o.requires_stat);
        const n = typeof v === "number" ? v : parseFloat(v) || 0;
        // The importer writes `requires_min`; `min` is the hand-authored spelling.
        // Absent threshold is 0, not 1 — with 1 this player hid options the app
        // showed. Mirrors LvnPlayer.BuildOptions.
        const need = o.requires_min ?? o.min ?? 0;
        const needN = typeof need === "number" ? need : parseFloat(need) || 0;
        if (n < needN) return;
      }
      options.push({ index: i, text: interpolate(o.text, this.vars), cost: o.cost });
    });
    return options;
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
            if (b.key) setVarPath(this.vars, b.key, v);
          } else {
            const cur = getVarPath(this.vars, b.key);
            const by = typeof b.by === "number" ? b.by
              : typeof b.by === "boolean" ? (b.by ? 1 : 0) : 1;
            setVarPath(this.vars, b.key, (typeof cur === "number" ? cur : parseFloat(cur) || 0) + by);
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
  // ПОЗИЦИЯ КОСТЕЙ — ЧАСТЬ ПРОГОНА, а не окружения. Без неё «продолжить»
  // переигрывает случайное заново: сохранился перед броском, вернулся — выпало
  // другое. Движок кладёт состояние потока в сейв ровно по этой причине.
  snapshot() {
    return {
      ip: this.pausedIp ?? this.ip,
      vars: JSON.parse(JSON.stringify(this.vars)),
      callStack: [...this.callStack],
      rng: randomState(),
    };
  }

  restore(snap) {
    if (!snap || typeof snap.ip !== "number") return { type: "noop" };
    this.ip = Math.max(0, Math.min(snap.ip, this.script.length));
    this.vars = Object.assign(Object.create(null), snap.vars || {});
    this.callStack = [...(snap.callStack || [])];
    // Кости возвращаются туда же, откуда продолжается история: снимок без них
    // воспроизводит текст, но не броски — а игрок видит именно броски.
    restoreRandom(snap.rng);
    this.finished = false;
    this._choice = null;
    this._awaitInput = null;
    return this.advance();
  }

  /** Commit the input overlay's text. */
  submitInput(text) {
    const c = this._awaitInput;
    this._awaitInput = null;
    if (c && c.var) setVarPath(this.vars, c.var, String(text ?? ""));
    return this.advance();
  }
}
