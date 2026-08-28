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
  const staged = [];
  let player, guard = 0, fail = null;
  try {
    // Постановочные команды плеер не трактует — он их ПЕРЕСЫЛАЕТ, и корпус
    // проверяет именно поток пересланного (expect.stage).
    player = new Player(c.doc, { onStage: (cmd) => staged.push(cmd) });
    let ev = player.advance();
    while (guard++ < 5000) {
      if (ev.type === "say") {
        // Поля дублируются намеренно: корпус описывает реплику то строкой
        // (тогда сверяется `say`), то объектом {who, text} — тогда нужны имена
        // полей как в языке.
        stops.push({ say: ev.text, text: ev.text, who: ev.who ?? "" });
        ev = player.advance();
      } else if (ev.type === "choice") {
        // РАЗВОРАЧИВАЕМ СКЛЕЙКУ. Браузерный плеер намеренно отдаёт реплику
        // перед выбором ВМЕСТЕ с ним («a choice directly after shows together
        // with its prompt line») — это подача UI, а не другой язык: на одном
        // экране и вопрос, и варианты. Корпус описывает ЯЗЫК, где это две
        // остановки, поэтому здесь склейку раскрываем обратно.
        if (ev.text) stops.push({ say: ev.text, text: ev.text, who: ev.who ?? "" });
        stops.push({ choice: ev.options.map((o) => o.text) });
        if (!picks.length) { fail = "выборы кончились, а choice открыт"; break; }
        const p = picks.shift();
        if (p && typeof p === "object" && p.timeout) {
          // Метод называется timeoutChoice — «выбор истёк, идём по его ветке».
          ev = player.timeoutChoice();
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
        // Ожидание — тоже остановка языка: корпус пишет её как {wait:{ms}}.
        stops.push({ wait: { ms: ev.ms } });
        ev = player.advance();
      } else {
        stops.push({ end: true });
        break;
      }
    }
  } catch (e) {
    fail = "исключение: " + String((e && e.message) || e);
  }
  out.push({ id: c.id, stops, staged, vars: player ? player.vars : {}, fail });
}
process.stdout.write(JSON.stringify(out));
