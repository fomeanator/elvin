// The docs site map. Each page's `file` is fetched from content/ (the deploy
// copies howto/ and docs/ there, flattened with a prefix). Sections group the
// sidebar; `search` pages are indexed for the client-side search.
export const SECTIONS = [
  {
    title: "Начало",
    pages: [
      { id: "tutorial", title: "Быстрый старт (15 мин)", file: "howto-TUTORIAL.md" },
      { id: "novella-core", title: "Основа новеллы (5 кирпичей)", file: "howto-novella-core-README.md" },
      { id: "time-romance", title: "Хаб, сборники, типы новелл", file: "howto-time-romance-README.md" },
      { id: "agents", title: "Для ИИ-агента", file: "howto-AGENTS.md" },
      { id: "capabilities", title: "Что умеет / не умеет", file: "howto-CAPABILITIES.md" },
    ],
  },
  {
    title: "Язык",
    pages: [
      { id: "cheatsheet", title: "Шпаргалка", file: "howto-CHEATSHEET.md" },
      { id: "language", title: "Справочник языка", file: "howto-LANGUAGE.md" },
      { id: "recipes", title: "Книга рецептов", file: "howto-recipes.md" },
      { id: "format", title: ".lvn — формат контейнера", file: "docs-lvn-format.md" },
    ],
  },
  {
    title: "Постановка",
    pages: [
      { id: "cast", title: "Каст и персонажи", file: "docs-cast.md" },
      { id: "placement", title: "Размещение", file: "docs-placement.md" },
      { id: "staging", title: "Теги постановки", file: "docs-staging-tags.md" },
      { id: "animation", title: "Анимация", file: "docs-animation-system.md" },
    ],
  },
  {
    title: "Жанры",
    pages: [
      { id: "genres", title: "Обзор 12 жанров", file: "howto-README.md" },
      { id: "g-visual-novel", title: "Визуальная новелла", file: "howto-visual-novel-README.md" },
      { id: "g-gamebook", title: "Геймбук / CYOA", file: "howto-gamebook-README.md" },
      { id: "g-point-and-click", title: "Point-and-click", file: "howto-point-and-click-README.md" },
      { id: "g-rpg", title: "RPG", file: "howto-rpg-README.md" },
      { id: "g-quiz", title: "Викторина", file: "howto-quiz-README.md" },
      { id: "g-detective", title: "Детектив", file: "howto-detective-README.md" },
      { id: "g-dating-sim", title: "Дэйтинг-сим", file: "howto-dating-sim-README.md" },
      { id: "g-clicker", title: "Кликер / idle", file: "howto-clicker-README.md" },
      { id: "g-roguelike", title: "Roguelike", file: "howto-roguelike-README.md" },
      { id: "g-tycoon", title: "Тайкун", file: "howto-tycoon-README.md" },
      { id: "g-puzzle", title: "Головоломка", file: "howto-puzzle-README.md" },
      { id: "g-kinetic-novel", title: "Кинетическая новелла", file: "howto-kinetic-novel-README.md" },
    ],
  },
  {
    title: "Встраивание и сервисы",
    pages: [
      { id: "unity", title: "Старт в Unity", file: "docs-unity-getting-started.md" },
      { id: "embedding", title: "Встраивание движка", file: "docs-embedding.md" },
      { id: "services", title: "Продуктовые сервисы", file: "docs-services.md" },
      { id: "mcp", title: "MCP-сервер", file: "docs-mcp.md" },
      { id: "playground", title: "Playground", file: "docs-playground.md" },
      { id: "releasing", title: "Релизы и совместимость", file: "docs-releasing.md" },
    ],
  },
  {
    title: "О проекте",
    pages: [
      { id: "strategy", title: "Стратегия языка", file: "docs-language-strategy.ru.md" },
    ],
  },
];

export const PAGES = SECTIONS.flatMap((s) => s.pages);
export const byId = Object.fromEntries(PAGES.map((p) => [p.id, p]));

// Map a doc-relative link (howto/x.md, ../docs/y.md, foo.md) to a page id, so
// in-page links navigate the SPA instead of 404ing.
const fileToId = Object.fromEntries(PAGES.map((p) => [p.file, p.id]));
export function linkToId(href) {
  const base = href.split("/").pop().replace(/#.*$/, "");
  const parts = href.replace(/^\.\.?\//, "").split("#")[0].split("/");
  // try prefix-joined form first (howto/quiz/README.md → howto-quiz-README.md)
  const joined = parts.join("-");
  if (fileToId[joined]) return "#" + fileToId[joined];
  if (fileToId["howto-" + base]) return "#" + fileToId["howto-" + base];
  if (fileToId["docs-" + base]) return "#" + fileToId["docs-" + base];
  return null;
}
