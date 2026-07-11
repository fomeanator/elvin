import Editor from "@monaco-editor/react";
import "../lib/monacoSetup.js"; // workers (editor+json) + loader — shared with the lvns editor

// A compact Monaco JSON editor for the admin config/manifest cards: syntax
// colours, folding, bracket matching and live JSON validation (squiggles on a
// typo BEFORE the save button round-trips). Controlled: value + onChange.
// `editorRef` (optional plain ref) receives the Monaco editor instance so the
// toolbar can drive undo/redo.
export default function JsonEditor({ value, onChange, height = 240, editorRef }) {
  return (
    <div className="admin-monaco" style={{ height }}>
      <Editor
        language="json"
        theme="vs-dark"
        value={value}
        onChange={(v) => onChange(v ?? "")}
        onMount={(editor) => { if (editorRef) editorRef.current = editor; }}
        options={{
          minimap: { enabled: false },
          fontSize: 12.5,
          lineNumbers: "on",
          folding: true,
          wordWrap: "on",
          scrollBeyondLastLine: false,
          automaticLayout: true,
          tabSize: 2,
          renderLineHighlight: "none",
          overviewRulerLanes: 0,
          scrollbar: { verticalScrollbarSize: 8, horizontalScrollbarSize: 8 },
        }}
      />
    </div>
  );
}
