import { useState, useMemo, useEffect } from "react";
import ResizeHandle from "./ResizeHandle.jsx";

// The translation workbench. It reads the chapter's *compiled* .lvn for every
// localizable string (say lines + choice option labels), then lets you fill a
// target-language catalog. Keys are the source strings themselves (gettext /
// Ren'Py style) — exactly what the engine's player looks up at runtime — so the
// same .lvn plays in any language by swapping <script>.<lang>.json.

function sourceStrings(json) {
  let doc;
  try { doc = JSON.parse(json); } catch { return []; }
  const out = [], seen = new Set();
  const add = (s) => { if (typeof s === "string" && s && !seen.has(s)) { seen.add(s); out.push(s); } };
  for (const c of doc.script || []) {
    if (c.op === "say") add(c.text);
    if (c.op === "choice") for (const o of c.options || []) add(o.text);
  }
  return out;
}

const catalogUrl = (scriptUrl, lang) =>
  String(scriptUrl || "scripts/ch.lvn").replace(/\.lvn$/, "") + "." + lang + ".json";

export default function TranslatePanel({ compiledJson, scriptUrl, sourceLang, token, notify, onClose }) {
  const strings = useMemo(() => sourceStrings(compiledJson), [compiledJson]);
  const [lang, setLang] = useState("en");
  const [tr, setTr] = useState({}); // source string → translation
  const [busy, setBusy] = useState(false);

  // Load the existing catalog when the target language (or chapter) changes.
  useEffect(() => {
    let off = false;
    setTr({});
    fetch(catalogUrl(scriptUrl, lang), { cache: "no-store" })
      .then((r) => (r.ok ? r.json() : {}))
      .then((d) => { if (!off) setTr(d && typeof d === "object" ? d : {}); })
      .catch(() => {});
    return () => { off = true; };
  }, [lang, scriptUrl]);

  const done = strings.filter((s) => (tr[s] || "").trim()).length;

  async function save() {
    if (!lang) return;
    setBusy(true);
    try {
      const clean = {}; // keep only live strings → drops stale keys
      for (const s of strings) if ((tr[s] || "").trim()) clean[s] = tr[s];
      const rel = catalogUrl(scriptUrl, lang).replace(/^\/+content\/+/, "").replace(/^\/+/, "");
      const r = await fetch("/v1/admin/assets/" + rel, {
        method: "PUT",
        headers: { Authorization: "Bearer " + (token || ""), "Content-Type": "application/json" },
        body: JSON.stringify(clean, null, 1),
      });
      if (!r.ok) throw new Error(r.status + ": " + (await r.text()).trim());
      notify?.(`Saved ${Object.keys(clean).length} ${lang} string(s)`, "");
    } catch (e) {
      notify?.("Save failed: " + e.message, "error");
    } finally {
      setBusy(false);
    }
  }

  return (
    <aside className="ide-pane ide-translate enter">
      <ResizeHandle storageKey="ide-w-translate" side="left" min={340} max={820} />
      <div className="ide-tr-head">
        <span className="section-label">Translate</span>
        <span className="ide-tr-arrow">{sourceLang || "source"} →</span>
        <input className="ide-lang-input" value={lang} spellCheck={false}
          onChange={(e) => setLang(e.target.value.trim().toLowerCase())}
          title="Target language code, e.g. en, de, fr" />
        <span className="ide-tr-count" title="translated / total">{done}/{strings.length}</span>
        <button className="btn-ghost sm" onClick={onClose} title="Close">✕</button>
      </div>

      <div className="ide-tr-list">
        {strings.length === 0 && (
          <div className="ide-empty">No translatable lines.<br />Compile a chapter first.</div>
        )}
        {strings.map((s, i) => (
          <div key={i} className="ide-tr-row">
            <div className="ide-tr-src" title={s}>{s}</div>
            <textarea className="ide-tr-dst" rows={1} spellCheck={false}
              placeholder={"… " + (lang || "?")}
              value={tr[s] || ""}
              onChange={(e) => setTr((m) => ({ ...m, [s]: e.target.value }))} />
          </div>
        ))}
      </div>

      <div className="ide-tr-foot">
        <span className="ide-tr-hint">key = source line · saves <code>{lang || "lang"}.json</code></span>
        <button className="btn btn-primary" onClick={save} disabled={busy || !lang || strings.length === 0}>
          {busy ? "Saving…" : `Save ${lang || "lang"}.json ▸`}
        </button>
      </div>
    </aside>
  );
}
