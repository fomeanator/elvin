// ЭКСПОРТИРОВАННАЯ ИГРА ЗНАЕТ ТОТ ЖЕ СЛОВАРЬ ЦВЕТА.
//
// Экспорт — путь доставки, а не ещё один плеер: `export.js` вшивает в
// самостоятельный .html те же core.js/expr.js/color.js, сняв строки
// import/export. Если словарь туда не доедет, скачанный файл вернётся к
// старому пути — слово цвета ПРЯМО В CSS, — и «tint color=warm» в нём не
// покрасит ничего, а «green» покрасит тёмным. Игрок при этом видит ту же
// новеллу: расхождение молчаливое.
//
// Собираем без браузера так же, как conformance/export-check.mjs: подменяем
// `fetch` чтением с диска. ПОСТРОЧНОЕ совпадение упаковки с исходником держит
// тот страж (его гоняет go test через TestExportPacksTheSameLanguage); здесь —
// именно цвет: что словарь внутри и что старого пути в CSS больше нет.
import { beforeAll, describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const playDir = join(dirname(fileURLToPath(import.meta.url)), "..", "public", "play");

let html;

beforeAll(async () => {
  // buildHtml тянет исходники относительными путями через fetch — в node
  // такого нет, поэтому читаем с диска (ровно как в export-check.mjs).
  globalThis.fetch = async (u) => ({
    ok: true,
    text: async () => readFileSync(join(playDir, String(u)), "utf8"),
    blob: async () => { throw new Error("картинки здесь не выкачиваются"); },
  });
  const { buildHtml } = await import(pathToFileURL(join(playDir, "export.js")).href);
  const doc = { scene: "проверка", script: [{ op: "say", text: "раз" }] };
  html = await buildHtml("проверка", JSON.stringify(doc), {}, {}, (u) => u);
});

describe("словарь цвета доезжает в самостоятельный .html", () => {
  it("внутри файла есть сама функция словаря", () => {
    expect(html).toContain("function lvnColor(");
  });

  it("внутри файла есть все три трети словаря", () => {
    // Не «файл большой», а «слова на месте»: токен площадки, имя движка,
    // мнемоника настроения. Пропади одна таблица — молчать будет треть языка.
    expect(html, "нет токенов площадки").toMatch(/^ {2}accent: /m);
    expect(html, "нет имён движка").toMatch(/^ {2}green: "#00ff00",$/m);
    expect(html, "нет мнемоник настроения").toMatch(/^ {2}sepia: /m);
  });

  it("словарь приехал СНЯТЫМ с модулей — файл самостоятелен", () => {
    // Строка `export function lvnColor` в файле без модулей — синтаксическая
    // ошибка: игра не открылась бы вовсе.
    expect(html).not.toContain("export function lvnColor");
    expect(html).not.toContain("export const LVN_COLOR_WORDS");
    expect(html).not.toMatch(/^import .*color\.js/m);
  });
});

describe("рендерер экспорта красит СЛОВАРЁМ", () => {
  it("вуаль и подписи HUD спрашивают цвет у словаря", () => {
    expect(html, "«tint» красит мимо словаря").toContain("lvnColor(cmd.color");
    expect(html, "«fade» красит мимо словаря").toContain("lvnColor(to,");
  });

  it("старого пути «слово прямо в CSS» в файле не осталось", () => {
    // Именно эта запись и была расхождением: браузер молча выбрасывал
    // «accent»/«warm»/«sepia», а «green» красил тёмным.
    expect(html).not.toContain("cmd.color || ");
    expect(html).not.toMatch(/background\s*=\s*cmd\.color\s*[;,)]/);
    expect(html).not.toMatch(/style\.color\s*=\s*cmd\.color\s*[;,)]/);
  });
});
