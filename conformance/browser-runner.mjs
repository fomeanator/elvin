// Прогон conformance-корпуса через НАСТОЯЩИЙ браузерный плеер (core.js).
// Правила те же, что у C#-прогона: `picks` — очередь выборов по индексу
// ПОКАЗАННЫХ вариантов, «остановка» — say/choice/input/end.
import { readFileSync } from "node:fs";

const [, , playerPath, casesJson] = process.argv;
const { Player } = await import(playerPath);
const cases = JSON.parse(readFileSync(casesJson, "utf8"));

const out = [];
for (const c of cases) {
  const picks = [...(c.picks || [])];
  const inputs = [...(c.inputs || [])];
  const stops = [];
  let player, guard = 0, fail = null;
  try {
    player = new Player(c.doc, { onStage: () => {} });
    let ev = player.advance();
    while (guard++ < 5000) {
      if (ev.type === "say") {
        stops.push({ say: ev.text, who: ev.who ?? null });
        ev = player.advance();
      } else if (ev.type === "choice") {
        stops.push({ choice: ev.options.map((o) => o.text) });
        if (!picks.length) { fail = "выборы кончились, а choice открыт"; break; }
        const p = picks.shift();
        if (p && typeof p === "object" && p.timeout) {
          ev = player.timeout ? player.timeout() : player.advance();
        } else {
          if (p >= ev.options.length) { fail = `pick ${p} вне показанных (${ev.options.length})`; break; }
          ev = player.choose(ev.options[p].index);
        }
      } else if (ev.type === "input") {
        // Значение берёт СЛУЧАЙ (поле `inputs`), как и в C#-прогоне: игрок
        // печатает своё, а не соглашается с подсказкой. Подставив `default`,
        // прогонщик мерил бы не плеер, а собственную выдумку.
        stops.push({ input: ev.var });
        const typed = inputs.length ? inputs.shift() : (ev.default ?? "");
        ev = player.submitInput ? player.submitInput(typed) : player.advance();
      } else if (ev.type === "wait") {
        ev = player.advance();
      } else {
        stops.push({ end: true });
        break;
      }
    }
  } catch (e) {
    fail = "исключение: " + String((e && e.message) || e);
  }
  out.push({ id: c.id, stops, vars: player ? player.vars : {}, fail });
}
process.stdout.write(JSON.stringify(out));
