import { useMemo, useRef, useState } from "react";
import { useDebounced } from "../adminShared.jsx";
import JsonEditor from "../JsonEditor.jsx";

// JSON-document editing kit for the admin config/manifest cards: tracks the
// edited text against the server copy (dirty), validates JSON on a 300ms
// debounce, and renders the editor with the spec'd toolbar — «Формат», a dirty
// dot, and a save button that is DISABLED until the doc is both dirty and
// valid. A parse error shows as a line under the editor (line:col), so a typo
// never round-trips to the server to be found out.

// useJsonDoc: local text state over the loaded server document.
export function useJsonDoc(serverData) {
  const [text, setText] = useState(null); // null = mirror the server copy
  const serverText = serverData != null ? JSON.stringify(serverData, null, 2) : "";
  const value = text != null ? text : serverText;
  const dirty = text != null && serverData != null && text !== serverText;

  const debounced = useDebounced(value, 300);
  const parse = useMemo(() => {
    // Untouched → the server copy is valid by definition (no flicker while the
    // debounce catches up with an async load).
    if (text == null) return { ok: true, doc: serverData };
    try { return { ok: true, doc: JSON.parse(debounced) }; }
    catch (e) { return { ok: false, error: prettyJsonError(e, debounced) }; }
  }, [debounced, serverData, text]);

  // format: re-indent the CURRENT text if it parses (uses live value, not the
  // debounced one, so the button acts on what the operator sees).
  function format() {
    try { setText(JSON.stringify(JSON.parse(value), null, 2)); }
    catch { /* invalid — the error line is already visible */ }
  }
  const reset = () => setText(null);

  // parseNow: click-time parse of the LIVE text — the debounced `valid` above
  // may lag 300ms behind fast typing, so a save must never trust it for data.
  function parseNow() {
    try { return { ok: true, doc: JSON.parse(value) }; }
    catch (e) { return { ok: false, error: prettyJsonError(e, value) }; }
  }

  return { value, setText, dirty, valid: parse.ok, parseError: parse.ok ? "" : parse.error, format, reset, parseNow };
}

// prettyJsonError: "line N, col M — message" out of a V8 SyntaxError.
function prettyJsonError(e, text) {
  const m = /position (\d+)/.exec(e.message || "");
  if (m) {
    const pos = Number(m[1]);
    const before = text.slice(0, pos);
    const line = before.split("\n").length;
    const col = pos - before.lastIndexOf("\n");
    return `строка ${line}, колонка ${col} — ${e.message}`;
  }
  return e.message || "битый JSON";
}

// JsonCard: toolbar + editor + error strip. `onSave(doc)` gets the parsed doc
// of the LIVE text (parseNow — never the debounced snapshot).
export function JsonCard({ doc, onSave, busy, height = 260, saveLabel = "Сохранить", extraActions }) {
  const [clickError, setClickError] = useState("");
  const editorRef = useRef(null);
  const canSave = doc.dirty && doc.valid && !busy;
  function save() {
    const p = doc.parseNow();
    if (!p.ok) { setClickError(p.error); return; }
    setClickError("");
    onSave(p.doc);
  }
  // undo/redo drive Monaco's own history; its onChange feeds the text back.
  const hist = (cmd) => { const ed = editorRef.current; if (ed) { ed.focus(); ed.trigger("toolbar", cmd, null); } };
  const err = clickError || (!doc.valid ? doc.parseError : "");
  return (
    <div className="adm-jsoncard">
      <div className="adm-jsonbar">
        <button className="btn-ghost sm" onClick={doc.format} disabled={!doc.valid} title="переформатировать JSON">Формат</button>
        <button className="adm-iconbtn" onClick={() => hist("undo")} title="отменить (Cmd/Ctrl+Z)">↶</button>
        <button className="adm-iconbtn" onClick={() => hist("redo")} title="повторить">↷</button>
        {extraActions}
        <span className="adm-jsonbar-spring" />
        {doc.dirty && (
          <>
            <span className="adm-dirty-dot" />
            <span className="adm-jsonbar-note">{doc.valid ? "не сохранено" : "битый JSON"}</span>
          </>
        )}
        <button className="btn btn-primary sm" onClick={save} disabled={!canSave}
                title={!doc.dirty ? "нет изменений" : !doc.valid ? "исправьте JSON" : undefined}>
          {busy ? "Сохраняю…" : saveLabel}
        </button>
      </div>
      <JsonEditor value={doc.value} onChange={doc.setText} height={height} editorRef={editorRef} />
      {err && <div className="adm-jsonerr">⚠ {err}</div>}
    </div>
  );
}
