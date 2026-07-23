// Thin client over the Go LVN server's content + admin endpoints.
// Paths are proxied by Vite to the running server (see vite.config.js).

export async function getManifest() {
  const r = await fetch("/v1/content/manifest", { cache: "no-store" });
  if (!r.ok) throw new Error("manifest " + r.status);
  return r.json();
}

// The project's optional host-op declaration (content/ext-grammar.json) — the
// same file the validator's -ext-grammar auto-detects. Absent → null (the
// closed core grammar applies); present-but-broken → throws, callers surface it.
export async function getExtGrammar() {
  const r = await fetch("/content/ext-grammar.json", { cache: "no-store" });
  if (r.status === 404) return null;
  if (!r.ok) throw new Error("ext-grammar " + r.status);
  return r.json();
}

// encodePath URL-encodes each segment of a content-relative path while keeping
// the '/' separators — a filename with '#', '?' or '%' must not break the URL.
const encodePath = (rel) => String(rel).split("/").map(encodeURIComponent).join("/");

// PUT a file through the token-gated admin route. `body` is a string (script /
// manifest JSON) or a File/Blob (uploaded art). Returns { path, bytes }.
export async function putAsset(path, body, token, contentType) {
  const rel = encodePath(String(path).replace(/^\/+content\/+/, "").replace(/^\/+/, ""));
  const r = await fetch("/v1/admin/assets/" + rel, {
    method: "PUT",
    headers: {
      Authorization: "Bearer " + (token || ""),
      "Content-Type": contentType || "application/octet-stream",
    },
    body,
  });
  if (!r.ok) throw new Error(r.status + ": " + (await r.text()).trim());
  return r.json();
}

// uploadStaged PUTs a File to the server in chunks, resuming from wherever the
// server says it left off — a dropped connection re-queries the offset and
// continues instead of restarting the whole (possibly multi-hundred-MB)
// upload from zero. `id` should be stable for the same logical file (name +
// size) so re-picking it after a reload resumes rather than reuploading.
// Resolves to the staged file's absolute server path (fed straight into
// import-bundle's JSON {dir} fields — see importBundleFromPaths below).
export async function uploadStaged(file, id, token, onProgress, chunkSize = 8 * 1024 * 1024) {
  const headers = { Authorization: "Bearer " + (token || "") };
  let offset = 0;
  {
    const r = await fetch("/v1/admin/staged-upload/" + encodeURIComponent(id), { headers });
    if (r.ok) offset = (await r.json()).offset || 0;
  }
  if (offset > file.size) offset = 0; // stale/mismatched staged file — start over
  while (offset < file.size) {
    const end = Math.min(offset + chunkSize, file.size);
    const chunk = file.slice(offset, end);
    const r = await fetch("/v1/admin/staged-upload/" + encodeURIComponent(id), {
      method: "PUT",
      headers: { ...headers, "Content-Range": `bytes ${offset}-${end - 1}/${file.size}` },
      body: chunk,
    });
    const body = await r.json().catch(() => ({}));
    if (r.status === 409 && typeof body.offset === "number") {
      offset = body.offset; // server resynced us — resume from where it actually is
      continue;
    }
    if (!r.ok) throw new Error(r.status + ": server rejected chunk at " + offset);
    offset = body.offset;
    if (onProgress) onProgress(offset / file.size);
  }
  const r = await fetch("/v1/admin/staged-upload/" + encodeURIComponent(id), { headers });
  const body = await r.json();
  return body.path;
}

// uploadStagedWithRetry wraps uploadStaged with resume-on-failure: a network
// drop (wifi hiccup, tab throttled in the background) throws mid-chunk —
// catch it, back off, and call uploadStaged again, which re-queries the
// server's offset and continues rather than starting over.
export async function uploadStagedWithRetry(file, id, token, onProgress, maxAttempts = 20) {
  let lastErr;
  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    try {
      return await uploadStaged(file, id, token, onProgress);
    } catch (e) {
      lastErr = e;
      await new Promise((res) => setTimeout(res, Math.min(1000 * attempt, 10000)));
    }
  }
  throw lastErr;
}

// Run a bundle import from files ALREADY staged on the server (see
// uploadStaged) — hits import-bundle's JSON {dir} mode instead of a fresh
// multipart upload. Near-instant: the server just reads paths it already has.
export async function importBundleFromPaths(paths, meta, token) {
  const r = await fetch("/v1/admin/import-bundle", {
    method: "POST",
    headers: { Authorization: "Bearer " + (token || ""), "Content-Type": "application/json" },
    body: JSON.stringify({
      articy: paths.articy || "", backgrounds: paths.backgrounds || "", heroine: paths.heroine || "",
      characters: paths.characters || "", vars: paths.vars || "",
      id: meta.id || "", name: meta.name || "", subtitle: meta.subtitle || "", template: meta.template || "",
    }),
  });
  const body = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(r.status + ": " + (body.error || JSON.stringify(body)));
  return body;
}

// Register a Spine character from its editor export: the three files land in
// content/spine/<id>/ and the entity is spliced into manifest.sprites.
export async function uploadSpine(meta, files, token) {
  const fd = new FormData();
  fd.append("id", meta.id);
  if (meta.name) fd.append("name", meta.name);
  if (meta.auto) fd.append("auto", meta.auto);
  if (meta.scale) fd.append("scale", String(meta.scale));
  fd.append("json", files.json);
  fd.append("atlas", files.atlas);
  fd.append("texture", files.texture);
  const res = await fetch("/v1/admin/spine", {
    method: "POST",
    headers: { Authorization: "Bearer " + (token || "") },
    body: fd,
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json();
}

// ── Admin dashboard API ──────────────────────────────────────────────────────
// The product-backend endpoints the old raw /admin/ page used, now called from
// the unified React app. Every call is token-gated; a 401 is surfaced so the UI
// can prompt for the admin token.

// adminFetch is the shared request helper: attaches the bearer token, throws a
// typed error on failure (message "401" on auth so callers can special-case), and
// returns parsed JSON (or text for non-JSON responses).
export async function adminFetch(path, token, opt = {}) {
  opt.headers = Object.assign({ Authorization: "Bearer " + (token || "") }, opt.headers || {});
  const r = await fetch(path, opt);
  if (r.status === 401) throw new Error("401");
  if (!r.ok) throw new Error(((await r.text()) || r.status).toString().trim());
  const ct = r.headers.get("content-type") || "";
  return ct.includes("json") ? r.json() : r.text();
}

// GET /v1/admin/users → { users: [{ user_id, name, created, providers, balances }] }
export const adminUsers = (token) =>
  adminFetch("/v1/admin/users", token).then((d) => d.users || []);

// GET /v1/admin/users/<id> → { name, wallet: { balances, inventory, history } }
export const adminUserDetail = (id, token) =>
  adminFetch("/v1/admin/users/" + encodeURIComponent(id), token);

// POST /v1/admin/grant — credit/debit a wallet currency (amount may be negative).
export const adminGrant = (body, token) =>
  adminFetch("/v1/admin/grant", token, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

// GET /v1/admin/orders → { orders: [{ ts, user_id, type, sku, amount, currency, reason }] }
export const adminOrders = (token) =>
  adminFetch("/v1/admin/orders", token).then((d) => d.orders || []);

// GET /v1/admin/saves → { saves: [{ key, size, modified }] }
export const adminSaves = (token) =>
  adminFetch("/v1/admin/saves", token).then((d) => d.saves || []);

// GET /v1/admin/saves/<key> → the raw save blob (JSON).
export const adminSaveDetail = (key, token) =>
  adminFetch("/v1/admin/saves/" + encodeURIComponent(key), token);

// DELETE /v1/admin/saves/<key> — irreversible.
export const adminDeleteSave = (key, token) =>
  adminFetch("/v1/admin/saves/" + encodeURIComponent(key), token, { method: "DELETE" });

// GET/PUT /v1/admin/config/<name> — a live-reloaded server config (iap-catalog,
// ads, daily-rewards). PUT validates JSON server-side and applies immediately.
export const adminConfig = (name, token) =>
  adminFetch("/v1/admin/config/" + encodeURIComponent(name), token);
export const adminPutConfig = (name, doc, token) =>
  adminFetch("/v1/admin/config/" + encodeURIComponent(name), token, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(doc),
  });

// GET /v1/admin/history?file=<rel> → { versions: [{ ts, size }] } (newest first).
export const adminHistory = (file, token) =>
  adminFetch("/v1/admin/history?file=" + encodeURIComponent(file), token);

// POST /v1/admin/rollback {file, ts} — restore a saved version (the rollback
// itself is versioned too, so it's always reversible).
export const adminRollback = (file, ts, token) =>
  adminFetch("/v1/admin/rollback", token, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ file, ts }),
  });

// GET /v1/admin/files?dir=<rel> → { files: [{ name, size, dir }] } (dirs first).
export const adminFiles = (dir, token) =>
  adminFetch("/v1/admin/files?dir=" + encodeURIComponent(dir || ""), token);

// DELETE /v1/admin/assets/<path> — scripts go to history, art is gone for good.
export const adminDeleteAsset = (path, token) =>
  adminFetch("/v1/admin/assets/" + encodePath(path), token, { method: "DELETE" });

// GET /v1/admin/manifest[?draft=1] — the manifest (or its unpublished draft).
export const adminManifest = (token, draft) =>
  adminFetch("/v1/admin/manifest" + (draft ? "?draft=1" : ""), token);

// PUT /v1/admin/manifest[?draft=1] — save (players see it live unless draft).
export const adminPutManifest = (doc, token, draft) =>
  adminFetch("/v1/admin/manifest" + (draft ? "?draft=1" : ""), token, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(doc),
  });

// POST /v1/admin/manifest/publish — the draft becomes the live manifest.
export const adminPublishManifest = (token) =>
  adminFetch("/v1/admin/manifest/publish", token, { method: "POST" });

// DELETE /v1/admin/manifest?draft=1 — discard the draft.
export const adminDiscardDraft = (token) =>
  adminFetch("/v1/admin/manifest?draft=1", token, { method: "DELETE" });

// GET /v1/analytics/summary?day=YYYY-MM-DD → { total, unique_users, by_name }.
export const adminAnalytics = (day, token) =>
  adminFetch("/v1/analytics/summary?day=" + encodeURIComponent(day), token);
