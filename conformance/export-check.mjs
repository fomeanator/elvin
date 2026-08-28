// ЭКСПОРТ — ПУТЬ ДОСТАВКИ, А НЕ ЕЩЁ ОДИН ЯЗЫК.
//
// `export.js` собирает из новеллы самостоятельный .html: внутрь попадают тот же
// `core.js` и `expr.js`, только со снятыми строками import/export. Значит
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
const core = read("core.js"), expr = read("expr.js");

if (/^\s*import /m.test(html)) problems.push("в упакованном виде остался import — файл не самостоятелен");

for (const [name, src] of [["core.js", core], ["expr.js", expr]]) {
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
  if (/^\s*function trackStage\s*\(/m.test(src)) {
    problems.push(`${name}: своё определение trackStage — правило кадра должно приходить из core.js`);
  }
}

process.stdout.write(JSON.stringify(problems));
