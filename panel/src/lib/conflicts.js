// Pure helpers for the import-conflict screen. No React, no fetch — the
// screen's whole decision logic lives here so it can be tested directly.
//
// A conflict row is what GET /v1/admin/import-conflicts returns:
//   { rel, incoming_rel, mine:{exists,size,modified,sha}, incoming:{…},
//     text, titles:[…], diff, diff_note, undoable }

// parseUnifiedDiff turns the server's unified diff into rows a table can
// render. Kinds: "file" (the ---/+++ header), "hunk" (@@), "add", "del",
// "ctx". Counts are of real +/- lines, not of header lines that start with
// the same character — the "+++" header line is NOT an addition, and getting
// that wrong reports "+1" on every single file.
export function parseUnifiedDiff(text) {
  const out = { rows: [], added: 0, removed: 0 };
  if (!text) return out;
  const lines = String(text).replace(/\n$/, "").split("\n");
  for (const line of lines) {
    if (line.startsWith("--- ") || line.startsWith("+++ ")) {
      out.rows.push({ kind: "file", text: line });
      continue;
    }
    if (line.startsWith("@@")) {
      out.rows.push({ kind: "hunk", text: line });
      continue;
    }
    if (line.startsWith("+")) {
      out.added++;
      out.rows.push({ kind: "add", text: line.slice(1) });
      continue;
    }
    if (line.startsWith("-")) {
      out.removed++;
      out.rows.push({ kind: "del", text: line.slice(1) });
      continue;
    }
    out.rows.push({ kind: "ctx", text: line.replace(/^ /, "") });
  }
  return out;
}

const IMAGE_RE = /\.(png|jpe?g|gif|webp|avif|bmp|svg)$/i;

// isImagePath: can the browser show this file as a picture? The parked copy
// carries the ORIGINAL extension plus ".incoming", so the test runs against
// the author's path and both sides are previewable (the server sniffs the
// content type for the suffixed file — verified against a real PNG).
export function isImagePath(rel) {
  return IMAGE_RE.test(String(rel || ""));
}

// kindOf classifies a conflict for the list's icon/label. Scripts are the
// author's own text, art is binary, "манифест" is the shared index — the
// three behave differently on resolve (only the first two are per-novel, and
// only text ones have a diff).
export function kindOf(rel) {
  const p = String(rel || "");
  if (/\.lvns$/i.test(p)) return "source";
  if (/\.lvn$/i.test(p)) return "script";
  if (p === "manifest.json") return "manifest";
  if (isImagePath(p)) return "art";
  if (/\.(json|txt|md|csv|po|pot)$/i.test(p)) return "data";
  return "binary";
}

export const KIND_LABEL = {
  source: "исходник .lvns",
  script: "глава .lvn",
  manifest: "манифест",
  art: "арт",
  data: "данные",
  binary: "файл",
};

// titleOf: which novel a conflicting path belongs to. The server answers this
// from the import baselines (`titles`), which is authoritative; the path
// fallback only covers a file no import ever tracked (a hand-made script the
// author dropped into scripts/<id>/ themselves).
export function titleOf(conflict) {
  const t = (conflict && conflict.titles) || [];
  if (t.length) return t[0];
  const m = /^scripts\/([^/]+)\//.exec((conflict && conflict.rel) || "");
  return m ? m[1] : "";
}

// groupByTitle: [{title, rows}] sorted by title, untitled last. The conflicts
// screen leads with this because "which novel is in an undefined state" is
// the question an author actually has.
export function groupByTitle(conflicts) {
  const m = new Map();
  for (const c of conflicts || []) {
    const t = titleOf(c);
    if (!m.has(t)) m.set(t, []);
    m.get(t).push(c);
  }
  return [...m.entries()]
    .map(([title, rows]) => ({ title, rows }))
    .sort((a, b) => (a.title === "" ? 1 : b.title === "" ? -1 : a.title.localeCompare(b.title)));
}

// countByTitle: {titleId: n} — what the novels list stamps on each row.
export function countByTitle(conflicts) {
  const out = {};
  for (const c of conflicts || []) {
    const t = titleOf(c);
    out[t] = (out[t] || 0) + 1;
  }
  return out;
}

// sizeDelta: "+128 b" / "−1 240 b" / "0" — the one number that is available
// for every conflict, binary art included.
export function sizeDelta(conflict) {
  const mine = ((conflict && conflict.mine) || {}).size || 0;
  const inc = ((conflict && conflict.incoming) || {}).size || 0;
  return inc - mine;
}

// canKeepMine: false when the author's file is gone (deleted between imports).
// The server answers such a resolve with 400 ErrMineMissing, so the button
// must be disabled rather than offered and rejected.
export function canKeepMine(conflict) {
  return !!(conflict && conflict.mine && conflict.mine.exists);
}

// previewUrl: the static content URL for one side. The parked version is a
// plain file under content/ too, so both sides preview from the same origin.
// The sha is the cache-buster: art is served immutable, and resolving a
// conflict must not leave the old picture on screen.
export function previewUrl(conflict, side) {
  const c = conflict || {};
  const rel = side === "incoming" ? c.incoming_rel : c.rel;
  const meta = (side === "incoming" ? c.incoming : c.mine) || {};
  if (!rel) return "";
  const path = String(rel).split("/").map(encodeURIComponent).join("/");
  return "/content/" + path + (meta.sha ? "?v=" + meta.sha.slice(0, 12) : "");
}

// shortSha: enough to tell two versions apart in a table, short enough to fit.
export const shortSha = (s) => String(s || "").slice(0, 8);

// ── the write report an import answers with ─────────────────────────────────
// Both import endpoints return `files` = importer.WriteReport:
//   { files: [{rel, status, incoming}], conflicts: [rel, …] }
// with status ∈ new | updated | unchanged | kept_local | conflict.

export const STATUS_LABEL = {
  new: "новых",
  updated: "обновлено",
  unchanged: "без изменений",
  kept_local: "оставлено ваших",
  conflict: "конфликтов",
};

// summarizeWrite counts what the import actually did. `conflicts` is read from
// the report's own list (the server fills it), falling back to the per-file
// statuses so an older response shape still reports honestly.
export function summarizeWrite(report) {
  const files = (report && report.files) || [];
  const counts = { new: 0, updated: 0, unchanged: 0, kept_local: 0, conflict: 0 };
  for (const f of files) {
    if (counts[f.status] == null) counts[f.status] = 0;
    counts[f.status]++;
  }
  const listed = (report && report.conflicts) || [];
  const conflicts = listed.length
    ? listed
    : files.filter((f) => f.status === "conflict").map((f) => f.rel);
  return { counts, conflicts, total: files.length };
}

// changedOnly: the statuses worth showing after an import. "unchanged" is the
// majority on a re-import and drowns the two lines that matter.
export const CHANGED_STATUSES = ["conflict", "new", "updated", "kept_local", "unchanged"];
