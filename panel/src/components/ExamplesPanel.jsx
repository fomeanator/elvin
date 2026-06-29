import { EXAMPLES } from "../lib/examples.js";
import ResizeHandle from "./ResizeHandle.jsx";

// A browsable library of LVNScript examples. Clicking one loads it into the
// editor (as a single undoable step — Ctrl+Z brings your own code back).
export default function ExamplesPanel({ onApply, onClose }) {
  const groups = [];
  const byCat = {};
  EXAMPLES.forEach((e) => {
    if (!byCat[e.cat]) { byCat[e.cat] = []; groups.push(e.cat); }
    byCat[e.cat].push(e);
  });

  return (
    <aside className="docs enter">
      <ResizeHandle storageKey="ide-w-examples" />
      <div className="docs-head">
        <h2>Examples</h2>
        <button className="btn-ghost sm" onClick={onClose}>✕</button>
      </div>
      <p className="docs-lede">
        {EXAMPLES.length} ready snippets — click one to load it into the editor.
        <strong> Ctrl/Cmd+Z</strong> brings your own code back.
      </p>
      {groups.map((cat) => (
        <div key={cat} className="ex-group">
          <div className="ref-cat">{cat}</div>
          {byCat[cat].map((e, i) => (
            <button key={i} className="ex-item" onClick={() => onApply(e.code)} title="Load into editor">
              {e.title}
            </button>
          ))}
        </div>
      ))}
    </aside>
  );
}
