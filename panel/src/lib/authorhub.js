// Pure helpers for the author's cabinet (admin/Novels.jsx): which files belong
// to a novel, and how the server's per-file version history folds into one
// readable "what changed, when, undo it" feed.
//
// WHAT THE SERVER ACTUALLY RECORDS (checked against server/admin.go and
// tools/lvnconv/importer/apply.go, not against a description):
//
//   • .history/<rel>/<ms>.bak is written by snapshotHistory, and snapshotHistory
//     is called ONLY on the editorial write paths — asset PUT, config PUT,
//     manifest PUT/publish, rollback, and conflict resolve. An IMPORT writes
//     through importer.WriteToContentDir, which never calls it.
//     ⇒ every history entry is a change made INSIDE the engine, never an import.
//   • A .bak holds the bytes as they were BEFORE that write. So the newest
//     entry for a file is "the version from just before the last change", and
//     rolling back to it is exactly "switch the last change off".
//   • No operator identity is stored anywhere — see reportGap below.

// relOf turns a manifest script_url ("/content/scripts/x/ch1.lvn") into the
// content-relative path the admin API speaks ("scripts/x/ch1.lvn").
export const relOf = (url) =>
  String(url || "").replace(/^\/+content\/+/, "").replace(/^\/+/, "");

// chapterFilesOf lists the history-eligible files of one manifest title: each
// chapter's compiled .lvn AND its .lvns source (the importer writes both, and
// the author edits the source), newest-chapter order preserved.
// Only scripts/** is included — historyEligible() rejects everything else
// under a novel (art has no history at all), so asking would just 404.
export function chapterFilesOf(title) {
  const out = [];
  const seen = new Set();
  const push = (rel, chapter) => {
    if (!rel || seen.has(rel)) return;
    if (!/^scripts\/.+\.(lvn|lvns)$/.test(rel)) return;
    seen.add(rel);
    out.push({ rel, chapter });
  };
  for (const s of (title && title.seasons) || []) {
    for (const c of s.chapters || []) {
      const rel = relOf(c.script_url);
      push(rel, c.name || c.id || "");
      if (/\.lvn$/.test(rel)) push(rel.replace(/\.lvn$/, ".lvns"), c.name || c.id || "");
    }
  }
  return out;
}

export const chapterCountOf = (title) =>
  ((title && title.seasons) || []).reduce((n, s) => n + ((s.chapters || []).length), 0);

// buildFeed folds {rel: [{ts,size}]} into one newest-first timeline.
// `files` carries the chapter label for each rel so a row reads "Глава 3"
// rather than a path only its own author can decode.
//
// prevSize is the size of the NEXT-OLDER snapshot of the same file, so a row
// can show the direction of the edit. The newest snapshot per file is flagged
// `undoLast`: that is the one-click "switch the last change off".
export function buildFeed(historyByFile, files) {
  const label = new Map((files || []).map((f) => [f.rel, f.chapter]));
  const rows = [];
  for (const [rel, versions] of Object.entries(historyByFile || {})) {
    const sorted = [...(versions || [])].sort((a, b) => Number(b.ts) - Number(a.ts));
    sorted.forEach((v, i) => {
      const older = sorted[i + 1];
      rows.push({
        rel,
        chapter: label.get(rel) || "",
        ts: String(v.ts),
        when: Number(v.ts),
        size: Number(v.size) || 0,
        prevSize: older ? Number(older.size) || 0 : null,
        undoLast: i === 0,
      });
    });
  }
  rows.sort((a, b) => b.when - a.when || a.rel.localeCompare(b.rel));
  return rows;
}

// importedAtOf reads the per-title baseline mtime out of a listing of
// content/.lvn-import (GET /v1/admin/files?dir=.lvn-import). The baseline file
// is rewritten by every import AND by every conflict resolution, so the honest
// label is "база импорта обновлена", not "последний импорт" — a resolution
// moves it too.
export function importedAtOf(baselineFiles, titleId) {
  const want = String(titleId || "") + ".json";
  for (const f of baselineFiles || []) {
    if (!f.dir && f.name === want) return f.modified || "";
  }
  return "";
}

// novelRows joins the three sources the cabinet reads — the manifest, the
// conflict scan and the baseline listing — into the table's row model.
export function novelRows(titles, conflictCounts, baselineFiles) {
  return (titles || []).map((t) => ({
    id: t.id,
    name: t.name || t.id,
    subtitle: t.subtitle || "",
    author: t.author || "",
    chapters: chapterCountOf(t),
    conflicts: (conflictCounts || {})[t.id] || 0,
    importedAt: importedAtOf(baselineFiles, t.id),
    title: t,
  }));
}

// state: the one word the list puts in the «состояние» column.
// "conflicts" outranks everything — while a file is parked, what the players
// get and what the export says disagree, and no other status is meaningful.
export function stateOf(row) {
  if (!row) return "empty";
  if (row.conflicts > 0) return "conflicts";
  if (!row.chapters) return "empty";
  if (!row.author) return "unattributed";
  return "ok";
}

export const STATE_LABEL = {
  conflicts: "конфликты",
  empty: "нет глав",
  unattributed: "без автора",
  ok: "в порядке",
};

// sourceOfLastChange infers WHERE a file's current bytes came from, from two
// facts the server does expose: the file's mtime, and the newest .history
// snapshot for it.
//
// An editorial write snapshots the previous bytes and then writes, so mtime
// and the newest snapshot land in the same instant. An import writes straight
// through and never snapshots, so it moves mtime and leaves history behind.
// Therefore:
//
//   mtime ≈ newest snapshot  → the last change was made in the engine
//   mtime ≫ newest snapshot  → the last change came from an import
//   no history at all        → nothing was ever edited here (import only)
//
// `tolerance` covers the gap between the snapshot and the rename (and the
// filesystem's mtime resolution). Returns "editor" | "import" | "unknown".
export function sourceOfLastChange(modified, newestTs, tolerance = 5000) {
  const mtime = modified ? Date.parse(modified) : NaN;
  const hist = Number(newestTs) || 0;
  if (!Number.isFinite(mtime)) return hist ? "editor" : "unknown";
  if (!hist) return "import";
  return mtime - hist > tolerance ? "import" : "editor";
}

export const SOURCE_LABEL = {
  editor: "правка в движке",
  import: "импорт articy",
  unknown: "неизвестно",
};

// runPool runs `job` over `items` with at most `limit` in flight. The cabinet
// asks the server for one novel's history file by file (there is no batch
// endpoint), and 50 parallel fetches from one tab is a self-inflicted DoS.
export async function runPool(items, limit, job) {
  const list = [...(items || [])];
  const out = [];
  let next = 0;
  const workers = Array.from({ length: Math.max(1, Math.min(limit, list.length)) }, async () => {
    for (;;) {
      const i = next++;
      if (i >= list.length) return;
      out[i] = await job(list[i], i);
    }
  });
  await Promise.all(workers);
  return out;
}

// reportGap: the sentence the cabinet shows where "кто" should be. The owner
// asked for who/when/what; the server stores when and what, and nothing at all
// about who — /v1/admin/* is a single shared bearer token, so even a recorded
// actor would be "whoever holds the admin token". Stating that beats inventing
// a name, and the panel says exactly what the server would have to add.
export const AUTHOR_UNKNOWN_NOTE =
  "Сервер не записывает, КТО сделал правку: /v1/admin/* закрыт одним общим " +
  "admin-токеном, отдельных учёток нет. Известно достоверно другое — правка " +
  "сделана внутри движка (импорт в историю не пишет вовсе).";
