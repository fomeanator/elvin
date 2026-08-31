// ЭКСПОРТ — ПУТЬ ДОСТАВКИ, А НЕ ЕЩЁ ОДИН ЯЗЫК.
//
// `export.js` собирает из новеллы самостоятельный .html: внутрь попадают тот же
// `core.js`, `expr.js` и `color.js`, только со снятыми строками import/export. Значит
// экспортированная игра обязана играть ровно так же, как песочница, — и
// доказывается это не прогоном (в файле нет модулей, его не импортируешь), а
// тем, что упакованный код ПОСТРОЧНО совпадает с исходником.
//
// Проверяются три вещи: объявления пережили снятие `export` (правило `^export `
// вырезает слово, а не строку — но regexp легко испортить), внутри нет ни
// одного оставшегося `import`, и каждая значащая строка исходника присутствует
// в упакованном виде.
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";

const [, , playDir] = process.argv;
const read = (n) => readFileSync(join(playDir, n), "utf8");

// exportHtml тянет исходники через fetch по относительному пути — в node такого
// нет, поэтому подменяем на чтение с диска.
globalThis.fetch = async (u) => ({
  ok: true,
  text: async () => read(String(u)),
  blob: async () => { throw new Error("assets are not fetched in this check"); },
});

const { buildHtml } = await import(join(playDir, "export.js"));
const doc = { scene: "check", script: [{ op: "say", text: "раз" }] };
const html = await buildHtml("check", JSON.stringify(doc), {}, {}, (u) => u);

const problems = [];
const core = read("core.js"), expr = read("expr.js"), color = read("color.js");

if (/^\s*import /m.test(html)) problems.push("в упакованном виде остался import — файл не самостоятелен");

// ХВОСТ МНОГОСТРОЧНОГО ИМПОРТА СЛОВА «import» НЕ СОДЕРЖИТ. Снятие идёт
// построчно (`^import .*$`), поэтому перенос строки внутри списка имён оставляет
// в упакованном коде обрывок вида `  foo, bar } from "./expr.js";` — синтаксис
// ломается, а проверка по слову этого не видит. Проверено на живом случае:
// импорт разбили на две строки, страж сказал «ок», а экспортированный файл не
// открывался вовсе.
if (/^[^\S\n]*[\w,\s]*\}\s*from\s+["']/m.test(html))
  problems.push("в упакованном виде остался ХВОСТ импорта (перенос строки внутри import) — держите import одной строкой");

for (const [name, src] of [["core.js", core], ["expr.js", expr], ["color.js", color]]) {
  for (const raw of src.split("\n")) {
    const line = raw.trim();
    if (!line || line.startsWith("//")) continue;
    if (line.startsWith("import ")) continue;            // вырезается намеренно
    const packed = line.replace(/^export default /, "").replace(/^export /, "");
    if (!html.includes(packed)) {
      problems.push(`${name}: строка не доехала в экспорт → ${packed.slice(0, 70)}`);
      if (problems.length > 6) break;
    }
  }
}

// ПРАВИЛО КАДРА — ОДНО. `trackStage` живёт в плеере и приезжает в экспорт
// вместе с ним. Отдельное определение в песочнице или в шаблоне значит, что
// копия вернулась: ровно так в экспорте однажды пропала ветка `clear`, и
// восстановление сохранения возвращало актёров, которых сцена убрала.
for (const [name, src] of [["app.js", read("app.js")], ["export.js", read("export.js")]]) {
  for (const fn of ["trackStage", "replayStage"]) {
    if (new RegExp(`^\\s*function ${fn}\\s*\\(`, "m").test(src)) {
      problems.push(`${name}: своё определение ${fn} — правило кадра должно приходить из core.js`);
    }
  }
}

// PLAYGROUND ЕДЕТ ЦЕЛИКОМ. Перенос его исходников под версии (28.08) взял код
// и забыл демо-пак — девять картинок, на которые ремапятся примеры при
// статическом хостинге. Следующая сборка стёрла бы их вместе с папкой вывода,
// и примеры в песочнице потеряли бы арт: страница есть, картинок нет.
for (const must of ["content/bg/Autumn_street.jpg", "content/sprites/doll/body.png"]) {
  try {
    readFileSync(join(playDir, must));
  } catch {
    problems.push(`playground неполон: нет ${must} — примеры останутся без арта после сборки`);
  }
}

// ДВА РЕНДЕРЕРА РИСУЮТ ОДИН НАБОР КОМАНД.
//
// Рендереры законно разные: в песочнице редактор и подсветка, в экспорте —
// облегчённый. Но НАБОР команд, на которые они отзываются, обязан совпадать:
// расхождение здесь значит, что автор видит одно, а игрок — другое. Так в
// экспорте не оказалось ветки `clear`: сцена не очищалась, актёры оставались
// стоять поверх новой.
const opsOf = (src, fnName) => {
  const i = src.indexOf(fnName);
  if (i < 0) return null;
  const body = src.slice(i, i + 6000);
  return new Set([...body.matchAll(/case "([a-z_0-9]+)"/g)].map((m) => m[1]));
};
const sandboxOps = opsOf(read("app.js"), "function applyStageDom");
const exportOps = opsOf(read("export.js"), "function applyStage(");
// Команды, которых в экспорте нет НАМЕРЕННО, и почему.
const exportSkips = new Map([
  ["ui", "дерево ui рисует редактор песочницы; в самостоятельном файле его нет"],
  // Сервисные операции песочница отправляет на сервер (svcOp). Экспортированная
  // игра — один файл, который открывают где угодно, в том числе без сети: ей
  // некуда их слать, и это не пропажа, а свойство доставки.
  ["track", "метка конверсии уходит на сервер; самостоятельный файл его не имеет"],
  ["leaderboard_submit", "таблица рекордов требует сервера, которого у файла нет"],
]);
if (sandboxOps && exportOps) {
  for (const op of sandboxOps) {
    if (exportOps.has(op) || exportSkips.has(op)) continue;
    problems.push(`команду "${op}" рисует песочница, но не экспорт — игрок увидит не то, что автор`);
  }
}

process.stdout.write(JSON.stringify(problems));
