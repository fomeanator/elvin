// КОНТРАКТ ДИСПЕТЧЕРИЗАЦИИ ДЛЯ БРАУЗЕРНОГО ПЛЕЕРА — зеркало C#-стража
// OpDispatchContractTests, которого у четвёртой реализации языка не было.
//
// Правило одно на все рантаймы: операцию потока плеер ПОТРЕБЛЯЕТ сам,
// постановочную — ПЕРЕСЫЛАЕТ сцене дословно. Таблица владения
// (ops-owners.json) говорит, кто есть кто; проверять это надо у каждой
// реализации, иначе «неизвестная операция» тихо превращается в пропуск
// ровно там, где её никто не ждёт.
import { readFileSync } from "node:fs";

const [, , playerPath, probesJson] = process.argv;
const { Player } = await import(playerPath);
const probes = JSON.parse(readFileSync(probesJson, "utf8"));

const out = [];
for (const p of probes) {
  const staged = [];
  let error = null, stop = "";
  try {
    const player = new Player({ script: p.script }, { onStage: (cmd) => staged.push(cmd) });
    let ev = player.advance(), guard = 0;
    // Доигрываем до конца или до первой остановки: нам важно лишь то, что
    // команда УСПЕЛА пройти через диспетчер.
    while (guard++ < 200 && ev && ev.type !== "end" && ev.type !== "say"
           && ev.type !== "choice" && ev.type !== "input" && ev.type !== "wait") {
      ev = player.advance();
    }
    // Тип остановки важен не меньше пересылки: браузерный плеер подаёт ввод и
    // ожидание СОБЫТИЕМ, тогда как C# рисует их сценой. Разная подача одной
    // операции законна; потерять операцию — нет.
    stop = ev && ev.type ? ev.type : "";
  } catch (e) {
    error = String(e && e.message ? e.message : e);
  }
  out.push({ op: p.op, forwarded: staged.some((c) => c && c.op === p.op), stop, error });
}
process.stdout.write(JSON.stringify(out));
