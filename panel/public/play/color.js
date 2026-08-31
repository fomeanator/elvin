// ЦВЕТ ПО ИМЕНИ — тот же словарь, что у движка (UiColor.Named в C#).
//
// Плеер отдавал слово цвета ПРЯМО В CSS: `veil.style.background = cmd.color`.
// «accent», «warm», «sepia» для браузера — не цвета, он их молча игнорирует, а
// «green» в CSS означает ТЁМНЫЙ #008000, тогда как в движке зелёный яркий.
// Один и тот же скрипт в приложении и по ссылке давал разный зелёный — ровно
// то расхождение рантаймов, ради которого существует корпус соответствия.
//
// Токены темы отданы палитре ПЛОЩАДКИ: у неё свой облик, и «accent» здесь
// значит её акцент — это и есть тема. Слова те же, оттенки свои.
const TOKENS = {
  bg: "#0e0e13",
  surface: "#1c1c24",
  surface_hi: "#26262e",
  panel: "rgba(13,13,20,.84)",
  text: "#e8e4da",
  dim: "#8f8a80",
  accent: "#c8a050",
  on_accent: "#14141a",
  gold: "#ffd166",
  warn: "#e07a6a",
  border: "#2c2c36",
  veil: "rgba(0,0,0,.6)",
  clear: "transparent",
};

// Имена движка стоят ПЕРЕД разбором браузера намеренно: «green» в HTML
// тёмно-зелёный, а в движке яркий, и молча сменить его значило бы играть
// написанные главы другим цветом.
const ENGINE = {
  white: "#ffffff",
  black: "#000000",
  red: "#ff0000",
  blue: "#0000ff",
  green: "#00ff00",
  // НЕ #ffff00: у движка это Unity-шный Color.yellow — (1, 0.922, 0.016).
  // Разница видна глазом, и её уже успели не заметить: одна и та же вспышка
  // в приложении шла зеленовато-жёлтой, а по ссылке чисто жёлтой.
  yellow: "#ffeb04",
  cyan: "#00ffff",
  magenta: "#ff00ff",
};

// Мнемоники настроения — готовые оттенки, которые автор зовёт словом.
const MOOD = {
  cold: "rgb(153,179,255)",
  tint_cold: "rgb(153,179,255)",
  warm: "rgb(255,217,179)",
  tint_warm: "rgb(255,217,179)",
  sepia: "rgb(194,153,107)",
};

/** Цвет по слову словаря, hex или имени CSS. Регистр не важен. */
export function lvnColor(word, fallback) {
  if (word === null || word === undefined || word === "") return fallback;
  const w = String(word).trim().toLowerCase();
  if (w in TOKENS) return TOKENS[w];
  if (w in ENGINE) return ENGINE[w];
  if (w in MOOD) return MOOD[w];
  // «ff0000» без решётки — то же, что «#ff0000»: движок принимает обе записи.
  if (/^[0-9a-f]{3,8}$/.test(w)) return "#" + w;
  // Незакрытая подстановка — не опечатка: её ещё не подставили.
  if (String(word).includes("{")) return fallback;
  return String(word);
}

/** Слова словаря — для сверки со стражем. */
export const LVN_COLOR_WORDS = Object.keys(TOKENS)
  .concat(Object.keys(ENGINE))
  .concat(Object.keys(MOOD));
