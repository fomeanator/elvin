// The docs site map. Each page's `file` is fetched from content/ (the deploy
// copies howto/ and docs/ there, flattened with a prefix). Sections group the
// sidebar; `search` pages are indexed for the client-side search.
export const SECTIONS = [
  {
    title: "Getting started",
    pages: [
      { id: "tutorial", title: "Quickstart (15 min)", file: "howto-TUTORIAL.md" },
      { id: "novella-core", title: "Novel core (5 bricks)", file: "howto-novella-core-README.md" },
      { id: "time-romance", title: "Hub, collections, novel types", file: "howto-time-romance-README.md" },
      { id: "agents", title: "For an AI agent", file: "howto-AGENTS.md" },
      { id: "capabilities", title: "Capabilities & limits", file: "howto-CAPABILITIES.md" },
    ],
  },
  {
    title: "The language",
    pages: [
      { id: "cheatsheet", title: "Cheatsheet", file: "howto-CHEATSHEET.md" },
      { id: "language", title: "Language reference", file: "howto-LANGUAGE.md" },
      { id: "recipes", title: "Recipe book", file: "howto-recipes.md" },
      { id: "format", title: ".lvn — the container format", file: "docs-lvn-format.md" },
    ],
  },
  {
    title: "Staging",
    pages: [
      { id: "cast", title: "Cast & characters", file: "docs-cast.md" },
      { id: "placement", title: "Placement", file: "docs-placement.md" },
      { id: "staging", title: "Staging tags", file: "docs-staging-tags.md" },
      { id: "animation", title: "Animation", file: "docs-animation-system.md" },
    ],
  },
  {
    title: "Genres",
    pages: [
      { id: "genres", title: "The 12 genres", file: "howto-README.md" },
      { id: "g-visual-novel", title: "Visual novel", file: "howto-visual-novel-README.md" },
      { id: "g-gamebook", title: "Gamebook / CYOA", file: "howto-gamebook-README.md" },
      { id: "g-point-and-click", title: "Point-and-click", file: "howto-point-and-click-README.md" },
      { id: "g-rpg", title: "RPG", file: "howto-rpg-README.md" },
      { id: "g-quiz", title: "Quiz", file: "howto-quiz-README.md" },
      { id: "g-detective", title: "Detective", file: "howto-detective-README.md" },
      { id: "g-dating-sim", title: "Dating sim", file: "howto-dating-sim-README.md" },
      { id: "g-clicker", title: "Clicker / idle", file: "howto-clicker-README.md" },
      { id: "g-roguelike", title: "Roguelike", file: "howto-roguelike-README.md" },
      { id: "g-tycoon", title: "Tycoon", file: "howto-tycoon-README.md" },
      { id: "g-puzzle", title: "Puzzle", file: "howto-puzzle-README.md" },
      { id: "g-kinetic-novel", title: "Kinetic novel", file: "howto-kinetic-novel-README.md" },
    ],
  },
  {
    title: "Embedding & services",
    pages: [
      { id: "unity", title: "Unity getting started", file: "docs-unity-getting-started.md" },
      { id: "embedding", title: "Embedding the engine", file: "docs-embedding.md" },
      { id: "services", title: "Product services", file: "docs-services.md" },
      { id: "mcp", title: "MCP server", file: "docs-mcp.md" },
      { id: "playground", title: "Playground", file: "docs-playground.md" },
      { id: "releasing", title: "Releases & compatibility", file: "docs-releasing.md" },
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
