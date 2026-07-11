import { useEffect, useMemo, useRef, useState } from "react";
import { getManifest, putAsset } from "../lib/api.js";
import { ensureWasm, compileLvns } from "../lib/wasm.js";
import DocsPanel from "./DocsPanel.jsx";
import ExamplesPanel from "./ExamplesPanel.jsx";
import ExportPanel from "./ExportPanel.jsx";
import ThemePanel from "./ThemePanel.jsx";
import TranslatePanel from "./TranslatePanel.jsx";
import ResizeHandle from "./ResizeHandle.jsx";
import MonacoEditor from "./MonacoEditor.jsx";

const splitLines = (s) => (s ? s.split("\n").map((x) => x.trim()).filter(Boolean) : []);

// Banner shown in the editor for a server-side compiled chapter (an articy import).
// Plain .lvns comments, so it never breaks anything if the chapter is later edited.
function importedBanner(id, n) {
  return `# ─────────────────────────────────────────────────────────────
# «${id}» — импортировано из articy:draft (.adpd)
#
# Глава скомпилирована напрямую в .lvn (${n} команд) и лежит на
# сервере — она уже играбельна в движке и видна в библиотеке.
# Редактор здесь read-only: формат слишком большой для ручного
# .lvns. Справа — реальный компилированный .lvn. Перевод строк —
# через кнопку «🌐 Languages».
# ─────────────────────────────────────────────────────────────
`;
}

function defaultSrc(scene) {
  return `scene ${scene || "chapter"}

The chapter opens here.
Mara: Hello.

- Continue -> next
- Leave -> __end

:next
Mara [smile]: Glad you stayed.
goto __end
`;
}

// "New file" templates. `code: null` means a blank chapter (defaultSrc).
const SAMPLES = [
  { label: "Blank chapter", code: null },
  {
    label: "Narration & speech",
    code: `scene intro
actor_map Mara=mara

This is narration — no speaker.
Mara: This is a speech line.
Mara [happy]: I am smiling now!
goto __end
`,
  },
  {
    label: "Branching & variables",
    code: `scene branching
set key="friendship" value=0

:start
Mara: Have we met?
- Yes -> met
- No -> first

:met
inc key="friendship" by=5
goto check
:first
Mara: Nice to meet you!
goto check

:check
if expr="friendship >= 5" then="friends" else="strangers"
:friends
Mara [smile]: Already great friends!
goto __end
:strangers
Mara: Let's get to know each other.
goto __end
`,
  },
  {
    label: "Gated choices",
    code: `scene gates
:room
Mara: Try the forbidden door?
- Break it -> enter min=5 requires_stat="courage"
- Pay the lockpick -> enter cost="50 gold"
- Walk away -> leave

:enter
You step through.
goto __end
:leave
You walk away.
goto __end
`,
  },
];

// A novel's Script, as a small web IDE: an Explorer of chapters, a gutter+syntax
// editor, a compiled-.lvn preview, a Problems dock and a status bar. No local
// drafts — the server is the single source of truth: open re-reads the chapter's
// .lvns, "Save to app" writes both the .lvns source and the compiled .lvn back.
export default function ScriptSection({ creds, notify, titleId, setStatus }) {
  const [title, setTitle] = useState(null);
  const [published, setPublished] = useState(() => new Set()); // chapter ids live on the server
  const [selId, setSelId] = useState(null);
  const [catalog, setCatalog] = useState({}); // manifest.sprites — for id/axes autocomplete
  const [bust, setBust] = useState(() => Date.now());

  const [src, setSrc] = useState("");
  const [output, setOutput] = useState("");
  const [imported, setImported] = useState(false); // chapter is a read-only server-side .lvn (articy import)
  const [error, setError] = useState(false);
  const [diags, setDiags] = useState([]); // [{ sev, line, op, msg }]
  const [jump, setJump] = useState({ line: 0, n: 0 });
  const [stat, setStat] = useState({ kind: "warn", text: "…", title: "" });
  const [showPreview, setShowPreview] = useState(true);
  const [showProblems, setShowProblems] = useState(true);
  const [showDocs, setShowDocs] = useState(false);
  const [showExamples, setShowExamples] = useState(false);
  const [showExport, setShowExport] = useState(false);
  const [showTheme, setShowTheme] = useState(false);
  const [showTranslate, setShowTranslate] = useState(false);
  const [newMenu, setNewMenu] = useState(false);
  const [caretPos, setCaretPos] = useState({ line: 1, col: 1 });
  const lastJson = useRef("");
  const importedRef = useRef(false); // sync mirror of `imported` for the editor's mount-echo guard
  const openEpoch = useRef(0); // bumped per openChapter call; a stale async open bails out
  const editorRef = useRef(null);
  const wasmReady = useRef(false);
  const saveRef = useRef(null);

  // Global shortcuts: Ctrl/Cmd+S saves to the app; Ctrl/Cmd+P opens the
  // chapter quick-open; Ctrl/Cmd+Shift+F searches across every chapter.
  const [quickOpen, setQuickOpen] = useState(false);
  const [searchAll, setSearchAll] = useState(false);
  useEffect(() => {
    const h = (e) => {
      if ((e.metaKey || e.ctrlKey) && (e.key === "s" || e.key === "S")) {
        e.preventDefault();
        saveRef.current && saveRef.current();
      }
      if ((e.metaKey || e.ctrlKey) && (e.key === "p" || e.key === "P") && !e.shiftKey) {
        e.preventDefault();
        setQuickOpen(true);
      }
      if ((e.metaKey || e.ctrlKey) && e.shiftKey && (e.key === "f" || e.key === "F")) {
        e.preventDefault();
        setSearchAll(true);
      }
      if (e.key === "Escape") { setQuickOpen(false); setSearchAll(false); }
    };
    window.addEventListener("keydown", h);
    return () => window.removeEventListener("keydown", h);
  }, []);

  // ── chapters ──────────────────────────────────────────────────────────
  useEffect(() => {
    (async () => {
      let t = null;
      try {
        const m = await getManifest();
        setCatalog(m.sprites || {});
        t = (m.titles || []).find((x) => x.id === titleId) || null;
      } catch {}
      if (!t) t = { id: titleId, seasons: [{ chapters: [] }] };
      if (!t.seasons || t.seasons.length === 0) t.seasons = [{ chapters: [] }];
      setTitle(t);
      // everything that came from the manifest is already live
      const ids = [];
      (t.seasons || []).forEach((s) => (s.chapters || []).forEach((c) => ids.push(c.id)));
      setPublished(new Set(ids));
      const first = (t.seasons[0].chapters || [])[0];
      await ensureWasm().then(() => (wasmReady.current = true)).catch(() => {});
      if (first) openChapter(first); else { setSrc(""); compile(""); }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [titleId]);

  const chapters = useMemo(() => {
    if (!title) return [];
    const out = [];
    (title.seasons || []).forEach((s) => (s.chapters || []).forEach((c) => out.push(c)));
    out.sort((a, b) => (a.number || 0) - (b.number || 0));
    return out;
  }, [title]);

  // ── unsaved-work safety ───────────────────────────────────────────────
  // Every IDE keeps your typing safe; "the server is the single source of
  // truth" must not mean "a closed tab eats an hour of writing". The editor
  // keeps a per-chapter DRAFT in localStorage while the text differs from the
  // last server copy; opening the chapter restores the draft (Reload from
  // server discards it), saving clears it, and closing the tab with unsaved
  // changes asks first.
  const savedSrc = useRef(""); // the last server-agreed source for this chapter
  const draftKey = (chapterId) => `lvn_draft_${titleId}_${chapterId}`;
  const dirty = !imported && !!selId && src !== savedSrc.current;

  useEffect(() => {
    document.title = (dirty ? "● " : "") + "ELVIN IDE";
    if (!dirty) return;
    const h = (e) => { e.preventDefault(); e.returnValue = ""; };
    window.addEventListener("beforeunload", h);
    return () => window.removeEventListener("beforeunload", h);
  }, [dirty]);

  // Adopt server text as the agreed baseline, then let a stashed draft win.
  function adoptSource(chapterId, serverText) {
    savedSrc.current = serverText;
    const draft = localStorage.getItem(draftKey(chapterId));
    if (draft != null && draft !== serverText) {
      setSrc(draft);
      compile(draft);
      notify("Restored an unsaved draft — «Reload from server» discards it", "");
      return true;
    }
    return false;
  }

  async function openChapter(c) {
    // Guard against a slow fetch from a previous open clobbering the chapter the
    // user has since switched to (and leaving importedRef stuck → dropped keys).
    const epoch = ++openEpoch.current;
    setSelId(c.id);
    if (c.script_url) creds.setPath(String(c.script_url).replace(/^\/+content\/+/, "").replace(/^\/+/, ""));
    // Read the source fresh from the server; a local unsaved draft, when one
    // exists, wins over it (see adoptSource).
    // Prefer a sibling .lvns SOURCE next to the compiled .lvn; it's editable, so
    // hand-made novels open as language, never as read-only bytecode.
    if (c.script_url && /\.lvn$/.test(c.script_url)) {
      const lvnsUrl = c.script_url.replace(/\.lvn$/, ".lvns");
      try {
        const r = await fetch(lvnsUrl + "?v=" + Date.now(), { cache: "no-store" });
        if (openEpoch.current !== epoch) return; // a newer openChapter is in charge
        if (r.ok) {
          const txt = await r.text();
          if (openEpoch.current !== epoch) return;
          // .lvns is plain text; guard against a static server falling back to
          // the compiled .lvn (JSON) when the source is missing.
          if (txt && !txt.trimStart().startsWith("{")) {
            importedRef.current = false;
            setImported(false);
            if (adoptSource(c.id, txt)) return;
            setSrc(txt);
            compile(txt);
            return;
          }
        }
      } catch { /* no .lvns source — fall through to the compiled .lvn */ }
    }
    // No .lvns source either. If the server holds a compiled .lvn for this chapter
    // (e.g. an articy:draft import), show that real content rather than a blank
    // template — read-only, since it isn't .lvns source.
    if (c.script_url) {
      try {
        const r = await fetch(c.script_url + "?v=" + Date.now(), { cache: "no-store" });
        if (openEpoch.current !== epoch) return;
        if (r.ok) {
          const txt = await r.text();
          if (openEpoch.current !== epoch) return;
          const obj = JSON.parse(txt);
          if (obj && Array.isArray(obj.script)) {
            const n = obj.script.length;
            const pretty = JSON.stringify(obj, null, 2);
            lastJson.current = pretty;
            importedRef.current = true;
            setImported(true);
            setOutput(pretty);
            setError(false);
            setDiags([]);
            setSrc(importedBanner(c.id, n));
            const s = { kind: "success", text: `✓ Imported · ${n} commands (read-only)` };
            setStat(s); setStatus?.(s);
            return;
          }
        }
      } catch { /* not a compiled import — fall through to a fresh template */ }
    }
    importedRef.current = false;
    setImported(false);
    if (adoptSource(c.id, "")) return; // a draft of a never-published chapter
    const text = defaultSrc(c.id);
    savedSrc.current = text; // a fresh template is "clean" until edited
    setSrc(text);
    compile(text);
  }

  // ── compile (WASM) ────────────────────────────────────────────────────
  function compile(text) {
    const r = compileLvns(text);
    setDiags(Array.isArray(r && r.diags) ? r.diags : []);
    if (!r || !r.ok) {
      const first = r && r.errors ? r.errors.split("\n")[0] : "Compilation error";
      setOutput(r && r.errors ? r.errors : "Compilation error");
      setError(true);
      const s = { kind: "error", text: "✗ " + first, title: r?.errors || "" };
      setStat(s); setStatus?.(s);
      lastJson.current = "";
      return;
    }
    lastJson.current = r.json;
    setOutput(r.json);
    setError(false);
    let s;
    if (r.warnings) {
      const n = splitLines(r.warnings).length;
      s = { kind: "warn", text: `⚠ ${n} warning${n > 1 ? "s" : ""}`, title: r.warnings };
    } else {
      s = { kind: "success", text: "✓ Compiled" };
    }
    setStat(s); setStatus?.(s);
  }

  // Compiling on EVERY keystroke froze the editor on real chapters (a 1.5k-line
  // articy episode = a full WASM compile + a giant JSON re-render per key).
  // The text state updates immediately (typing stays instant); the compile —
  // diagnostics, Problems, the Compiled pane — settles ~200ms after the pause.
  const compileTimer = useRef(0);
  useEffect(() => () => clearTimeout(compileTimer.current), []);

  function onEdit(text) {
    // Imported chapters are read-only — ignore the editor's mount-time echo so we
    // don't clobber the server .lvn shown in the Compiled pane.
    // Use the ref (not state) — the echo can fire before the state commit lands.
    if (importedRef.current) return;
    setSrc(text);
    // Draft stash: unsaved typing survives a closed tab / crashed browser.
    if (selId) {
      try {
        if (text !== savedSrc.current) localStorage.setItem(draftKey(selId), text);
        else localStorage.removeItem(draftKey(selId));
      } catch { /* quota — the beforeunload guard still protects */ }
    }
    if (!wasmReady.current) return;
    clearTimeout(compileTimer.current);
    compileTimer.current = setTimeout(() => compile(text), 200);
  }

  // ── chapter CRUD / meta ───────────────────────────────────────────────
  async function persist(nextTitle) {
    setTitle(nextTitle);
    try {
      const m = await getManifest();
      const titles = m.titles || [];
      const idx = titles.findIndex((t) => t.id === nextTitle.id);
      if (idx >= 0) titles[idx] = nextTitle; else titles.push(nextTitle);
      m.titles = titles;
      await putAsset("manifest.json", JSON.stringify(m, null, 2), creds.token, "application/json");
      notify("✓ Chapters saved — live in ~2s", "ok");
    } catch (e) { notify("✗ " + e.message, "err"); }
  }
  function addToSeasonOne(ch) {
    return { ...title, seasons: title.seasons.map((s, i) => (i === 0 ? { ...s, chapters: [...(s.chapters || []), ch] } : s)) };
  }
  function uniqueId(base) {
    let id = base, k = 1;
    while (chapters.some((x) => x.id === id)) id = base + "-" + ++k;
    return id;
  }
  // Create a new chapter file, optionally seeding its draft from a sample. A new
  // file never touches the file you're editing — picking a sample can't erase
  // your code.
  // New files are LOCAL DRAFTS — they never touch the live game until you
  // "Save to app". So creating a file can't break a running game, and you can
  // throw drafts away freely.
  // Load a brand-new chapter straight into the editor (no server file yet, no
  // drafts) — it becomes real on "Save to app", which writes its .lvns + .lvn.
  function seedNewChapter(id, text, bg) {
    const ch = { id, number: (chapters.length ? Math.max(...chapters.map((x) => x.number || 0)) : 0) + 1, script_url: `/content/scripts/${id}.lvn`, bg_url: bg || "" };
    setTitle(addToSeasonOne(ch));
    setSelId(id);
    creds.setPath(`scripts/${id}.lvn`);
    importedRef.current = false; setImported(false);
    setSrc(text);
    if (wasmReady.current) compile(text);
    notify("New chapter — Save to app to publish", "");
  }
  function createChapter(seed) {
    setNewMenu(false);
    const id = uniqueId(`${titleId}-ch${(chapters.length ? Math.max(...chapters.map((x) => x.number || 0)) : 0) + 1}`);
    seedNewChapter(id, seed != null ? seed : defaultSrc(id), "");
  }
  const addChapter = () => createChapter(null);

  function duplicateChapter(c) {
    const text = c.id === selId ? src : defaultSrc(c.id);
    seedNewChapter(uniqueId(`${c.id}-copy`), text, c.bg_url || "");
  }
  function patchChapter(id, patch) {
    setTitle((t) => ({ ...t, seasons: t.seasons.map((s) => ({ ...s, chapters: (s.chapters || []).map((c) => (c.id === id ? { ...c, ...patch } : c)) })) }));
  }
  function commitChapter(id, patch) {
    const next = { ...title, seasons: title.seasons.map((s) => ({ ...s, chapters: (s.chapters || []).map((c) => (c.id === id ? { ...c, ...patch } : c)) })) };
    // a draft's metadata edits stay local until it's published
    if (published.has(id)) persist(next); else setTitle(next);
  }
  function removeChapter(id) {
    const next = { ...title, seasons: title.seasons.map((s) => ({ ...s, chapters: (s.chapters || []).filter((c) => c.id !== id) })) };
    // an unpublished chapter just vanishes; a published one is removed on the server
    if (published.has(id)) {
      persist(next);
      setPublished((p) => { const q = new Set(p); q.delete(id); return q; });
    } else {
      setTitle(next);
    }
    if (selId === id) { const first = (next.seasons[0].chapters || [])[0]; if (first) openChapter(first); else { setSelId(null); setSrc(""); compile(""); } }
  }
  async function uploadBg(ch) {
    const target = ch.bg_url || `/content/ui/loading/${ch.id}.png`;
    const picker = document.createElement("input");
    picker.type = "file"; picker.accept = "image/*";
    picker.onchange = async () => {
      const f = picker.files && picker.files[0];
      if (!f) return;
      notify("Uploading loading screen…");
      try {
        await putAsset(target, f, creds.token, f.type || "application/octet-stream");
        setBust(Date.now());
        if (!ch.bg_url) commitChapter(ch.id, { bg_url: target });
        notify("✓ Loading bg uploaded", "ok");
      } catch (e) { notify("✗ " + e.message, "err"); }
    };
    picker.click();
  }

  async function save() {
    // The compile is debounced behind typing — flush it so we never save a
    // stale .lvn against fresh .lvns source.
    clearTimeout(compileTimer.current);
    if (wasmReady.current && !importedRef.current) compile(src);
    if (!lastJson.current) { notify("Fix the errors before saving.", "err"); return; }
    const lvnPath = (creds.path || "scripts/ch1.lvn").trim();
    const lvnsPath = lvnPath.replace(/\.lvn$/, ".lvns");
    notify("Saving…");
    try {
      // Persist BOTH the editable source (.lvns) and the compiled bytecode (.lvn)
      // to the server — the source is what the editor re-reads on open, so this is
      // what makes the no-drafts model work.
      await putAsset(lvnsPath, src, creds.token, "text/plain; charset=utf-8");
      await putAsset(lvnPath, lastJson.current, creds.token, "application/json");
      // a new chapter is published on first save: push its manifest entry too.
      if (selId && !published.has(selId)) {
        await persist(title);
        setPublished((p) => new Set(p).add(selId));
        notify(`✓ Published ${lvnsPath} (+ .lvn) — live in ~2s`, "ok");
      } else {
        notify(`✓ Saved ${lvnsPath} (+ .lvn) — live in ~2s`, "ok");
      }
      savedSrc.current = src; // the server now agrees — clean
      if (selId) try { localStorage.removeItem(draftKey(selId)); } catch { }
    } catch (e) { notify("✗ " + e.message, "err"); }
  }

  saveRef.current = save;

  const sel = chapters.find((c) => c.id === selId) || null;

  // Re-read the current chapter's .lvns from the server (drops in-editor unsaved
  // changes) — handy when the source was edited out-of-band, e.g. on disk.
  function reloadFromServer() {
    if (!sel) return;
    try { localStorage.removeItem(draftKey(sel.id)); } catch { } // an explicit reload discards the draft
    openChapter(sel);
    notify("Перечитано с сервера (черновик сброшен)", "ok");
  }
  const cmdCount = (output.match(/"op":/g) || []).length;
  const errCount = diags.filter((d) => d.sev === "error").length;
  const warnCount = diags.filter((d) => d.sev === "warning").length;
  const goLine = (line) => { if (line > 0) setJump((j) => ({ line, n: j.n + 1 })); };
  const outline = useMemo(() => {
    const items = [];
    src.split("\n").forEach((l, i) => {
      let m;
      if ((m = l.match(/^\s*scene\s+(\S+)/))) items.push({ kind: "scene", name: m[1], line: i + 1 });
      else if ((m = l.match(/^\s*:(\S+)/))) items.push({ kind: "label", name: m[1], line: i + 1 });
    });
    return items;
  }, [src]);
  const curOutline = (() => {
    let cur = -1;
    for (let i = 0; i < outline.length; i++) { if (outline[i].line <= caretPos.line) cur = i; else break; }
    return cur;
  })();

  return (
    <div className="ide">
      {quickOpen && (
        <QuickOpen
          chapters={chapters}
          currentId={selId}
          onPick={(c) => { setQuickOpen(false); openChapter(c); }}
          onClose={() => setQuickOpen(false)}
        />
      )}
      {searchAll && (
        <SearchAll
          chapters={chapters}
          onPick={async (c, line) => {
            setSearchAll(false);
            await openChapter(c);
            if (line > 0) goLine(line);
          }}
          onClose={() => setSearchAll(false)}
        />
      )}
      <div className="ide-top">
        <div className="ide-file">
          <span className={"ide-file-dot" + (dirty ? " dirty" : "")} title={dirty ? "Unsaved changes (drafted locally)" : "Saved"} />
          <span className="ide-file-name">{sel ? sel.id : "—"}<em>.lvns</em>{dirty ? " •" : ""}</span>
        </div>
        <div className="ide-top-actions">
          <button className={"btn-ghost sm" + (showExamples ? " on" : "")} onClick={() => { setShowExamples((v) => !v); setShowDocs(false); }}>❖ Examples</button>
          <button className={"btn-ghost sm" + (showDocs ? " on" : "")} onClick={() => { setShowDocs((v) => !v); setShowExamples(false); }}>✦ Reference</button>
          <button className={"btn-ghost sm" + (showPreview ? " on" : "")} onClick={() => setShowPreview((v) => !v)}>⌗ Compiled</button>
          <button className={"btn-ghost sm" + (showTheme ? " on" : "")} onClick={() => { setShowTheme((v) => !v); setShowDocs(false); setShowExamples(false); setShowExport(false); }}>◐ Theme</button>
          <button className={"btn-ghost sm" + (showExport ? " on" : "")} onClick={() => { setShowExport((v) => !v); setShowDocs(false); setShowExamples(false); setShowTheme(false); }}>⤓ Export</button>
          <button className={"btn-ghost sm" + (showTranslate ? " on" : "")} onClick={() => setShowTranslate((v) => !v)}>🌐 Languages</button>
          <button className="btn-ghost sm" onClick={reloadFromServer} title="Перечитать .lvns с сервера (сбросить несохранённые правки)">↻ Reload</button>
          <button className="btn-ghost sm" onClick={() => navigator.clipboard.writeText(output)}>Copy .lvn</button>
          <button className="btn btn-primary" onClick={save} disabled={!!error}>{selId && !published.has(selId) ? "Publish to app ▸" : "Save to app ▸"}</button>
        </div>
      </div>

      <div className="ide-body">
        <aside className="ide-explorer enter">
          <ResizeHandle storageKey="ide-w-explorer" side="right" min={190} max={900} />
          <div className="ide-explorer-head">
            <span className="section-label">Files</span>
            <div className="ide-new">
              <button className="btn-ghost sm" onClick={() => setNewMenu((v) => !v)}>+ New ▾</button>
              {newMenu && (
                <div className="ide-new-menu" onMouseLeave={() => setNewMenu(false)}>
                  {SAMPLES.map((s) => (
                    <button key={s.label} onClick={() => createChapter(s.code)}>{s.label}</button>
                  ))}
                </div>
              )}
            </div>
          </div>
          <div className="ide-files">
            {chapters.length === 0 && <div className="ide-empty">No files.<br />+ New →</div>}
            {chapters.map((c) => {
              const isDraft = !published.has(c.id);
              const hasError = c.id === selId && errCount > 0;
              const status = hasError ? "error" : isDraft ? "draft" : "live";
              return (
              <div key={c.id} className={"ide-file-row" + (c.id === selId ? " active" : "")}>
                <button className="ide-file-open" onClick={() => openChapter(c)} title={c.id + ".lvns"}>
                  <span className={"ide-file-ico st-" + status} title={status === "error" ? "has errors" : status === "draft" ? "draft — not in the game yet" : "live in the game"} />
                  <span className="ide-file-num">{c.number}</span>
                  <span className="ide-file-label">{c.name ? c.name : <>{c.id}<em>.lvns</em></>}</span>
                  {isDraft && <span className="ide-file-tag">draft</span>}
                </button>
                <span className="ide-file-acts">
                  <button onClick={() => duplicateChapter(c)} title="Duplicate file">⧉</button>
                  <button onClick={() => removeChapter(c.id)} title="Delete file">✕</button>
                </span>
              </div>
              );
            })}
          </div>

          {sel && outline.length > 0 && (
            <div className="ide-outline">
              <div className="section-label">Outline</div>
              <div className="ide-outline-list">
                {outline.map((o, i) => (
                  <button key={i} className={"ide-out-row k-" + o.kind + (i === curOutline ? " cur" : "")}
                    onClick={() => goLine(o.line)} title={`line ${o.line}`}>
                    <span className="ide-out-ico">{o.kind === "scene" ? "▤" : "⌖"}</span>
                    <span className="ide-out-name">{o.name}</span>
                    <span className="ide-out-line">{o.line}</span>
                  </button>
                ))}
              </div>
            </div>
          )}

          {sel && (
            <div className="ide-chapter-settings">
              <div className="section-label">Chapter</div>
              <label className="ide-set-row">
                <span>Name</span>
                <input className="field" type="text" placeholder="Эпизод…" value={sel.name ?? ""}
                  onChange={(e) => patchChapter(sel.id, { name: e.target.value })}
                  onBlur={(e) => commitChapter(sel.id, { name: e.target.value })} />
              </label>
              <label className="ide-set-row">
                <span>Number</span>
                <input className="field" type="number" value={sel.number ?? 0}
                  onChange={(e) => patchChapter(sel.id, { number: parseInt(e.target.value, 10) || 0 })}
                  onBlur={(e) => commitChapter(sel.id, { number: parseInt(e.target.value, 10) || 0 })} />
              </label>
              <button className="ide-bg" onClick={() => uploadBg(sel)} title="Loading-screen background">
                {sel.bg_url ? <img src={sel.bg_url + "?v=" + bust} alt="" onError={(e) => { e.currentTarget.style.display = "none"; }} /> : <span>＋ loading bg</span>}
              </button>
              <code className="ide-set-path">{sel.script_url}</code>
              <button className="btn-ghost sm wide-btn" onClick={() => removeChapter(sel.id)}>Remove chapter</button>
            </div>
          )}
        </aside>

        {showExport && <ExportPanel defaultName={title ? title.name : ""} notify={notify} onClose={() => setShowExport(false)} />}
        {showTheme && <ThemePanel token={creds.token} notify={notify} titleId={titleId} onClose={() => setShowTheme(false)} />}
        {showTranslate && <TranslatePanel compiledJson={output} scriptUrl={sel ? sel.script_url : null} sourceLang="source" token={creds.token} notify={notify} onClose={() => setShowTranslate(false)} />}
        {showDocs && <DocsPanel onClose={() => setShowDocs(false)} />}
        {showExamples && (
          <ExamplesPanel
            onApply={(code) => editorRef.current && editorRef.current.applyText(code)}
            onClose={() => setShowExamples(false)}
          />
        )}

        <main className="ide-main">
          {sel ? (
            <>
              <div className="ide-editor-row">
                <section className="ide-pane">
                  <MonacoEditor ref={editorRef} key={selId} src={src} onChange={onEdit} diags={diags} jump={jump} catalog={catalog} onCaret={setCaretPos} readOnly={imported} />
                </section>
                {showPreview && (
                  <section className="ide-pane ide-preview">
                    <ResizeHandle storageKey="ide-w-preview" side="left" min={300} max={900} />
                    <div className="ide-pane-head"><span>Compiled · {sel.id}.lvn</span></div>
                    <pre className={"code-output" + (error ? " error" : "")}>{output}</pre>
                  </section>
                )}
              </div>
              {showProblems && (
                <ProblemsDock diags={diags} onJump={goLine} onClose={() => setShowProblems(false)} />
              )}
            </>
          ) : (
            <div className="ide-blank">
              <p>This novel has no chapters yet.</p>
              <button className="btn btn-primary" onClick={addChapter}>+ Add the first chapter</button>
            </div>
          )}
        </main>
      </div>

      <div className="ide-status">
        <span className={"ide-stat " + stat.kind} title={stat.title}>{stat.text}</span>
        <span className="ide-status-sep" />
        <span className="ide-status-dim">{cmdCount} command{cmdCount === 1 ? "" : "s"}</span>
        {sel && <span className="ide-status-dim mono">{creds.path}</span>}
        <span className="grow" />
        {sel && <span className="ide-status-dim mono">Ln {caretPos.line}, Col {caretPos.col}</span>}
        <span className="ide-status-sep" />
        <button className={"ide-status-toggle" + (showProblems ? " on" : "")} onClick={() => setShowProblems((v) => !v)}>
          {errCount > 0 && <span className="dot err" />}
          {warnCount > 0 && <span className="dot warn" />}
          Problems {errCount + warnCount > 0 ? `(${errCount + warnCount})` : ""}
        </button>
      </div>
    </div>
  );
}

/* ── Problems dock — click a row to jump to its source line ────────────── */
function ProblemsDock({ diags, onJump, onClose }) {
  const errCount = diags.filter((d) => d.sev === "error").length;
  const warnCount = diags.filter((d) => d.sev === "warning").length;
  const rows = [...diags].sort((a, b) => (a.sev === b.sev ? (a.line || 0) - (b.line || 0) : a.sev === "error" ? -1 : 1));
  return (
    <div className="diagnostics">
      <div className="diag-head">
        <span className="diag-title">Problems</span>
        {errCount > 0 && <span className="diag-count err">{errCount} error{errCount > 1 ? "s" : ""}</span>}
        {warnCount > 0 && <span className="diag-count warn">{warnCount} warning{warnCount > 1 ? "s" : ""}</span>}
        {diags.length === 0 && <span className="diag-count ok">no problems</span>}
        <span className="grow" />
        <button className="btn-ghost sm" onClick={onClose}>✕</button>
      </div>
      <div className="diag-list">
        {diags.length === 0 && <div className="diag-clean">Nothing to fix — the chapter compiles clean.</div>}
        {rows.map((d, i) => (
          <button
            key={i}
            className={"diag-row " + (d.sev === "error" ? "error" : "warn")}
            onClick={() => d.line > 0 && onJump(d.line)}
            title={d.line > 0 ? "Go to line " + d.line : ""}
          >
            <span className="diag-dot" />
            <span className={"diag-loc" + (d.line > 0 ? "" : " dim")}>{d.line > 0 ? "line " + d.line : "—"}</span>
            <span className="diag-msg">{d.op ? <em>{d.op}</em> : null}{d.op ? " · " : ""}{d.msg}</span>
          </button>
        ))}
      </div>
    </div>
  );
}

// Quick Open (Ctrl/Cmd+P): fuzzy-jump to any chapter by id, episode name or
// number — the "go to file" every IDE has. Arrow keys + Enter, Esc closes.
function QuickOpen({ chapters, currentId, onPick, onClose }) {
  const [q, setQ] = useState("");
  const [idx, setIdx] = useState(0);

  const needle = q.trim().toLowerCase();
  const hits = chapters.filter((c) => {
    if (!needle) return true;
    const hay = `${c.id} ${c.name || ""} ${c.number || ""}`.toLowerCase();
    // every space-separated term must appear somewhere (order-free)
    return needle.split(/\s+/).every((t) => hay.includes(t));
  });
  const sel = Math.min(idx, Math.max(0, hits.length - 1));

  function onKey(e) {
    if (e.key === "ArrowDown") { e.preventDefault(); setIdx((i) => Math.min(i + 1, hits.length - 1)); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setIdx((i) => Math.max(i - 1, 0)); }
    else if (e.key === "Enter") { e.preventDefault(); if (hits[sel]) onPick(hits[sel]); }
    else if (e.key === "Escape") { e.preventDefault(); onClose(); }
    e.stopPropagation();
  }

  return (
    <div className="qo-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="qo-box">
        <input
          autoFocus
          className="qo-input"
          placeholder="Chapter… (id, name or number)"
          value={q}
          onChange={(e) => { setQ(e.target.value); setIdx(0); }}
          onKeyDown={onKey}
        />
        <div className="qo-list">
          {hits.map((c, i) => (
            <button
              key={c.id}
              className={"qo-item" + (i === sel ? " active" : "") + (c.id === currentId ? " current" : "")}
              onMouseEnter={() => setIdx(i)}
              onClick={() => onPick(c)}
            >
              <span className="qo-item-id">{c.id}</span>
              {c.name ? <span className="qo-item-name">{c.name}</span> : null}
            </button>
          ))}
          {hits.length === 0 && <div className="qo-empty">No chapters match</div>}
        </div>
      </div>
    </div>
  );
}

// Search across every chapter (Ctrl/Cmd+Shift+F): fetches each chapter's
// .lvns source once, greps case-insensitively, and jumps straight to the
// matched line in the right chapter — the workspace search every IDE has.
function SearchAll({ chapters, onPick, onClose }) {
  const [q, setQ] = useState("");
  const [hits, setHits] = useState([]);
  const [busy, setBusy] = useState(false);
  const cache = useRef({}); // chapter id → source text (per overlay session)
  const runTimer = useRef(0);

  useEffect(() => () => clearTimeout(runTimer.current), []);

  function schedule(text) {
    setQ(text);
    clearTimeout(runTimer.current);
    if (text.trim().length < 2) { setHits([]); return; }
    runTimer.current = setTimeout(() => run(text), 250);
  }

  async function run(text) {
    const needle = text.toLowerCase();
    setBusy(true);
    const out = [];
    for (const c of chapters) {
      if (out.length >= 200) break; // enough to act on
      let src = cache.current[c.id];
      if (src == null) {
        try {
          const url = String(c.script_url || "").replace(/\.lvn$/, ".lvns");
          const r = await fetch(url + "?v=" + Date.now(), { cache: "no-store" });
          src = r.ok ? await r.text() : "";
          if (src.trimStart().startsWith("{")) src = ""; // compiled import — no source to grep
        } catch { src = ""; }
        cache.current[c.id] = src;
      }
      if (!src) continue;
      const lines = src.split("\n");
      for (let i = 0; i < lines.length && out.length < 200; i++) {
        if (lines[i].toLowerCase().includes(needle))
          out.push({ ch: c, line: i + 1, text: lines[i].trim().slice(0, 120) });
      }
    }
    setHits(out);
    setBusy(false);
  }

  return (
    <div className="qo-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="qo-box">
        <input
          autoFocus
          className="qo-input"
          placeholder="Search in all chapters… (2+ characters)"
          value={q}
          onChange={(e) => schedule(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Escape") { e.preventDefault(); onClose(); } e.stopPropagation(); }}
        />
        <div className="qo-list">
          {busy && <div className="qo-empty">Searching…</div>}
          {!busy && hits.map((h, i) => (
            <button key={i} className="qo-item" onClick={() => onPick(h.ch, h.line)}>
              <span className="qo-item-id">{h.ch.id}:{h.line}</span>
              <span className="qo-item-name">{h.text}</span>
            </button>
          ))}
          {!busy && q.trim().length >= 2 && hits.length === 0 && <div className="qo-empty">No matches</div>}
        </div>
      </div>
    </div>
  );
}
