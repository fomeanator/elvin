// The docs SPA: a sidebar of sections, hash-routed pages fetched from
// content/ and rendered by md.js, a per-page table of contents, and a
// client-side search over titles + fetched bodies. Mobile: the sidebar is a
// drawer. Zero dependencies.

import { render, setLinkResolver, slug } from "./md.js";
import { SECTIONS, PAGES, byId, linkToId } from "./pages.js";

setLinkResolver(linkToId);

const $ = (id) => document.getElementById(id);
const cache = new Map();

// ── sidebar ────────────────────────────────────────────────────────────────
function buildSidebar() {
  const nav = $("sidebar");
  nav.innerHTML = "";
  for (const sec of SECTIONS) {
    const h = document.createElement("div");
    h.className = "nav-section";
    h.textContent = sec.title;
    nav.appendChild(h);
    for (const p of sec.pages) {
      const a = document.createElement("a");
      a.className = "nav-link";
      a.href = "#" + p.id;
      a.textContent = p.title;
      a.dataset.id = p.id;
      nav.appendChild(a);
    }
  }
}

function markActive(id) {
  for (const a of document.querySelectorAll(".nav-link"))
    a.classList.toggle("active", a.dataset.id === id);
}

// ── page load ──────────────────────────────────────────────────────────────
async function fetchPage(p) {
  if (cache.has(p.id)) return cache.get(p.id);
  let text;
  try {
    const r = await fetch("content/" + p.file);
    text = r.ok ? await r.text() : `# ${p.title}\n\n_Page unavailable._`;
  } catch {
    text = `# ${p.title}\n\n_Failed to load._`;
  }
  cache.set(p.id, text);
  return text;
}

async function showPage(id, anchor) {
  const p = byId[id] || PAGES[0];
  const md = await fetchPage(p);
  const { html, headings } = render(md);
  $("article").innerHTML = html;
  buildToc(headings);
  markActive(p.id);
  closeDrawer();
  if (anchor) {
    const el = document.getElementById(anchor);
    if (el) { el.scrollIntoView(); return; }
  }
  $("article").scrollIntoView();
  document.querySelector(".content").scrollTop = 0;
  window.scrollTo(0, 0);
}

function buildToc(headings) {
  const toc = $("toc");
  const items = headings.filter((h) => h.level <= 3);
  if (items.length < 3) { toc.innerHTML = ""; toc.style.display = "none"; return; }
  toc.style.display = "";
  toc.innerHTML = "<div class='toc-title'>On this page</div>" +
    items.map((h) => `<a class="toc-l${h.level}" href="#${location.hash.slice(1).split("::")[0]}::${h.id}">${h.text}</a>`).join("");
}

// ── routing (#pageId or #pageId::anchor) ───────────────────────────────────
function route() {
  const raw = location.hash.slice(1);
  const [id, anchor] = raw.split("::");
  showPage(id || PAGES[0].id, anchor);
}
window.addEventListener("hashchange", route);

// ── search ─────────────────────────────────────────────────────────────────
let searchIndex = null;
async function buildSearchIndex() {
  if (searchIndex) return searchIndex;
  searchIndex = [];
  await Promise.all(PAGES.map(async (p) => {
    const md = await fetchPage(p);
    searchIndex.push({ id: p.id, title: p.title, body: md.toLowerCase() });
  }));
  return searchIndex;
}

const searchBox = $("search");
let searchPopup = null;
searchBox.addEventListener("input", async () => {
  const q = searchBox.value.trim().toLowerCase();
  if (q.length < 2) { closeSearch(); return; }
  const idx = await buildSearchIndex();
  const hits = [];
  for (const e of idx) {
    const inTitle = e.title.toLowerCase().includes(q);
    const pos = e.body.indexOf(q);
    if (inTitle || pos >= 0) {
      const snippet = pos >= 0 ? e.body.slice(Math.max(0, pos - 30), pos + 50).replace(/\s+/g, " ") : "";
      hits.push({ id: e.id, title: e.title, snippet, score: inTitle ? 0 : 1 });
    }
  }
  hits.sort((a, b) => a.score - b.score);
  renderSearch(hits.slice(0, 12));
});

function renderSearch(hits) {
  closeSearch();
  if (!hits.length) return;
  searchPopup = document.createElement("div");
  searchPopup.className = "search-popup";
  searchPopup.innerHTML = hits.map((h) =>
    `<a href="#${h.id}"><b>${h.title}</b>${h.snippet ? `<span>…${escapeHtml(h.snippet)}…</span>` : ""}</a>`).join("");
  searchPopup.addEventListener("click", () => { searchBox.value = ""; closeSearch(); });
  document.querySelector(".topbar").appendChild(searchPopup);
}
function closeSearch() { searchPopup?.remove(); searchPopup = null; }
function escapeHtml(s) { return s.replace(/&/g, "&amp;").replace(/</g, "&lt;"); }
document.addEventListener("click", (e) => {
  if (!searchPopup) return;
  if (!searchPopup.contains(e.target) && e.target !== searchBox) closeSearch();
});

// ── mobile drawer ──────────────────────────────────────────────────────────
function openDrawer() { document.body.classList.add("drawer-open"); }
function closeDrawer() { document.body.classList.remove("drawer-open"); }
$("menu-toggle").addEventListener("click", () =>
  document.body.classList.toggle("drawer-open"));
$("scrim").addEventListener("click", closeDrawer);

// ── boot ────────────────────────────────────────────────────────────────────
buildSidebar();
if (!location.hash) location.hash = PAGES[0].id;
route();
