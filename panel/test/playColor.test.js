// СЛОВАРЬ ЦВЕТА ВЕБ-ПЛЕЕРА — ТОТ ЖЕ ЯЗЫК, ЧТО У ДВИЖКА.
//
// Плеер отдавал слово ПРЯМО В CSS: `veil.style.background = cmd.color`. Для
// браузера «accent», «warm», «sepia» — не цвета, он их молча игнорирует; а
// «green» в CSS означает ТЁМНЫЙ #008000, тогда как в движке зелёный яркий.
// Один и тот же скрипт в приложении и по Share-ссылке красился по-разному —
// расхождение рантаймов, которое видит только игрок.
//
// СОСТАВ словаря (те же слова, что у C#) держит го-страж
// `TestВебПлеерЗнаетТеЖеСловаЦвета` в tools/lvnconv/lvn/screen_set_guard_test.go.
// Он сверяет КЛЮЧИ таблиц, но не значения — «green: #008000» он пропускает.
// Значения и поведение закреплены здесь.
import { describe, expect, it } from "vitest";
import { lvnColor, LVN_COLOR_WORDS } from "../public/play/color.js";

/** Числа из «rgb(r,g,b)» или «#rrggbb» — чтобы говорить о цвете, а не о строке. */
function rgb(css) {
  const m = /^rgba?\(([^)]+)\)$/.exec(css);
  if (m) return m[1].split(",").slice(0, 3).map((n) => parseFloat(n));
  const h = /^#([0-9a-f]{6})$/i.exec(css);
  if (h) return [0, 2, 4].map((i) => parseInt(h[1].slice(i, i + 2), 16));
  throw new Error(`не цвет: ${css}`);
}

describe("имена движка", () => {
  it("«green» — ЯРКИЙ зелёный движка, а не тёмный HTML-ный", () => {
    // ГЛАВНОЕ ПРАВИЛО ЭТОГО ФАЙЛА, и оно про уже написанные главы. Отдай слово
    // браузеру — и каждая вспышка, каждая вуаль, написанная словом, перекрасится
    // задним числом: в приложении яркая, по ссылке тёмная.
    const зелёный = lvnColor("green", "#fallback");

    expect(зелёный).toBe("#00ff00");
    expect(зелёный).not.toBe("#008000"); // столько стоит «отдать слово в CSS»
    expect(зелёный).not.toBe("green");   // и столько же — отдать его как есть
  });

  it("остальные семь имён движка — те же краски, что в C#", () => {
    expect(lvnColor("white", "#fb")).toBe("#ffffff");
    expect(lvnColor("black", "#fb")).toBe("#000000");
    expect(lvnColor("red", "#fb")).toBe("#ff0000");
    expect(lvnColor("blue", "#fb")).toBe("#0000ff");
    expect(lvnColor("cyan", "#fb")).toBe("#00ffff");
    expect(lvnColor("magenta", "#fb")).toBe("#ff00ff");

    // «yellow» — НЕ чистый #ffff00: у движка это Unity-шный Color.yellow,
    // RGBA(1, 0.92, 0.016) = #ffeb04, «жёлтый, приятный глазу». Стороны
    // разошлись на нём молча — та же болезнь, что и с «green», только тише:
    // одна и та же вспышка в приложении зеленовато-жёлтая, а по ссылке чистая.
    // Сведено; го-страж теперь сверяет и ЗНАЧЕНИЯ, а не только слова.
    expect(lvnColor("yellow", "#fb")).toBe("#ffeb04");
  });
});

describe("токены темы", () => {
  it("токен отдаёт цвет ПАЛИТРЫ ПЛОЩАДКИ, а не имя обратно в CSS", () => {
    // Облик у площадки свой — слова те же. «accent» здесь значит её акцент;
    // важно, что это КРАСКА, а не строка «accent», которую браузер выбросит.
    for (const слово of ["bg", "surface", "surface_hi", "panel", "text", "dim",
                         "accent", "on_accent", "gold", "warn", "border", "veil"]) {
      const цвет = lvnColor(слово, "#fallback");
      expect(цвет, `«${слово}» вернулось словом — браузер его выбросит`).not.toBe(слово);
      expect(цвет, `«${слово}» не дало цвета`).not.toBe("#fallback");
      expect(цвет).toMatch(/^(#[0-9a-f]{3,8}|rgba?\()/i);
    }
  });

  it("«clear» — отсутствие краски, а не краска", () => {
    expect(lvnColor("clear", "#000")).toBe("transparent");
  });
});

describe("мнемоники настроения", () => {
  it("тепло тёплое, холод холодный, сепия коричневатая", () => {
    const [tr, , tb] = rgb(lvnColor("warm", "#fb"));
    const [cr, , cb] = rgb(lvnColor("cold", "#fb"));
    const [sr, , sb] = rgb(lvnColor("sepia", "#fb"));

    expect(tr, "«тепло» обязано быть тёплым").toBeGreaterThan(tb);
    expect(cb, "«холодно» обязано быть холодным").toBeGreaterThan(cr);
    expect(sr, "«сепия» обязана быть коричневатой").toBeGreaterThan(sb);
  });

  it("синоним даёт ТОТ ЖЕ цвет: cold/tint_cold, warm/tint_warm", () => {
    // Разойдись они хоть на единицу — автор получит два разных кадра за то,
    // что счёл одним и тем же приёмом.
    expect(lvnColor("tint_cold", "#fb")).toBe(lvnColor("cold", "#fb"));
    expect(lvnColor("tint_warm", "#fb")).toBe(lvnColor("warm", "#fb"));
  });
});

describe("как слово написано", () => {
  it("регистр не важен", () => {
    // В C# словарь регистронезависим с самого начала; разойдись здесь — и
    // «Accent» в манифесте работал бы в приложении и молчал по ссылке.
    expect(lvnColor("Accent", "#fb")).toBe(lvnColor("accent", "#fb"));
    expect(lvnColor("ACCENT", "#fb")).toBe(lvnColor("accent", "#fb"));
    expect(lvnColor("Surface_Hi", "#fb")).toBe(lvnColor("surface_hi", "#fb"));
    expect(lvnColor("WARM", "#fb")).toBe(lvnColor("warm", "#fb"));
    expect(lvnColor("Green", "#fb")).toBe("#00ff00"); // заглавная не возвращает HTML-ный
  });

  it("пробелы по краям не мешают", () => {
    expect(lvnColor("  accent  ", "#fb")).toBe(lvnColor("accent", "#fb"));
  });

  it("решётка необязательна — движок принимает обе записи", () => {
    expect(lvnColor("#ff0000", "#fb")).toBe("#ff0000");
    expect(lvnColor("ff0000", "#fb")).toBe("#ff0000");
    expect(lvnColor("FF0000", "#fb")).toBe("#ff0000");
    expect(lvnColor("fff", "#fb")).toBe("#fff");
    expect(lvnColor("ffffff80", "#fb")).toBe("#ffffff80"); // прозрачность восьмой цифрой
  });
});

describe("когда цвета нет", () => {
  it("пустота — это отсутствие, а не мусор: берётся умолчание вызывающего", () => {
    expect(lvnColor("", "#f1e4c9")).toBe("#f1e4c9");
    expect(lvnColor(null, "#f1e4c9")).toBe("#f1e4c9");
    expect(lvnColor(undefined, "#f1e4c9")).toBe("#f1e4c9");
  });

  it("неподставленная подстановка — не опечатка, а «ещё не подставили»", () => {
    // Отдай «{skin.accent}» в CSS — браузер выбросит правило целиком, и вуаль
    // останется от предыдущей команды. Умолчание честнее.
    expect(lvnColor("{skin.accent}", "#000")).toBe("#000");
    expect(lvnColor("#{hex}", "#000")).toBe("#000");
  });

  it("незнакомое слово уходит как есть — имена CSS разбирает браузер", () => {
    // Словарь ЗНАЕТ свои слова и не мешает остальным: «rebeccapurple» браузер
    // разберёт сам, а функциональные записи автор пишет прямо.
    expect(lvnColor("rebeccapurple", "#fb")).toBe("rebeccapurple");
    expect(lvnColor("rgba(0,0,0,.5)", "#fb")).toBe("rgba(0,0,0,.5)");
  });
});

describe("список слов", () => {
  it("каждое слово списка действительно красит — ни эха, ни умолчания", () => {
    // Список существует ради сверки с движком (го-страж). Слово, попавшее в
    // список мимо таблиц, сделало бы сверку зелёной на пустом месте.
    for (const слово of LVN_COLOR_WORDS) {
      const цвет = lvnColor(слово, "#fallback");
      expect(цвет, `«${слово}» есть в списке, но цвета не даёт`).not.toBe("#fallback");
      expect(цвет, `«${слово}» вернулось само собой`).not.toBe(слово);
    }
  });

  it("в списке нет повторов и есть все три трети словаря", () => {
    expect(new Set(LVN_COLOR_WORDS).size).toBe(LVN_COLOR_WORDS.length);
    expect(LVN_COLOR_WORDS).toContain("accent");     // токен темы
    expect(LVN_COLOR_WORDS).toContain("green");      // имя движка
    expect(LVN_COLOR_WORDS).toContain("sepia");      // мнемоника настроения
    expect(LVN_COLOR_WORDS).toContain("tint_warm");  // и синоним мнемоники
  });
});
