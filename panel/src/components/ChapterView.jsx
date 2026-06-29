import { useEffect, useRef, useState } from "react";
import { ensureWasm, compileLvns } from "../lib/wasm.js";
import { putAsset } from "../lib/api.js";
import { highlightLvns } from "../lib/highlight.js";
import DocsPanel from "./DocsPanel.jsx";

const splitLines = (s) => (s ? s.split("\n").map((x) => x.trim()).filter(Boolean) : []);

const DEFAULT_SCRIPT = `scene the-last-guest
actor_map Mara=mara

bg sprite_url="/content/bg/porch.jpg"
fade to="clear" duration=0.6
particles type="rain" on=true
Rain ticked on the porch roof.
Mara: You came back.
Mara: I wasn't sure you would.

- I did. -> warmth
- I can't stay. -> leave

:warmth
inc key="warmth" by=1
Mara [smile]: Then come in out of the rain.
particles type="rain" on=false
tint color="warm" alpha=0.25 duration=0.6
Mara: The kettle's still warm.
goto __end

:leave
hint text="Some doors don't open twice." show=true
Mara: She watched the dark take you back.
fade to="black" duration=0.8
goto __end`;

const SAMPLES = [
  {
    label: "Narration & speech",
    code: `scene intro_scene
actor_map Mara=mara

This is narration and has no speaker nameplate.
Mara: This is a speech line. Nameplate shows "Mara".
Mara [happy]: I am smiling now!
goto __end`,
  },
  {
    label: "Branching & variables",
    code: `scene variables_branching
set key="friendship" value=0

:start
Mara: Hello! Have we met?
- Yes, we have. -> met_before
- No, first time. -> first_meeting

:met_before
inc key="friendship" by=5
goto check

:first_meeting
Mara: Nice to meet you!
goto check

:check
if expr="friendship >= 5" then="best_buds" else="strangers"

:best_buds
Mara [smile]: We are already great friends!
goto __end

:strangers
Mara: I hope we get to know each other better.
goto __end`,
  },
  {
    label: "Gated choices",
    code: `scene choices_gates
:room
Mara: Do you want to try the forbidden door?

- Break it down -> enter min=5 requires_stat="courage"
- Pay the lockpicker -> enter cost="50 gold"
- Walk away -> leave

:enter
You step through the door.
goto __end

:leave
You walk away safely.
goto __end`,
  },
];

export default function ChapterView({ creds, setStatus, notify, embedded }) {
  const [src, setSrc] = useState(DEFAULT_SCRIPT);
  const [output, setOutput] = useState("");
  const [error, setError] = useState(false);
  const [docs, setDocs] = useState(false);
  const [diags, setDiags] = useState({ errors: [], warnings: [] });
  const lastJson = useRef("");
  const highlightRef = useRef(null);

  // Compile on every keystroke once the WASM converter is up.
  function compile(text) {
    const r = compileLvns(text);
    setDiags({ errors: splitLines(r && r.errors), warnings: splitLines(r && r.warnings) });
    if (!r || !r.ok) {
      const first = r && r.errors ? r.errors.split("\n")[0] : "Compilation error";
      setOutput(r && r.errors ? r.errors : "Compilation error");
      setError(true);
      setStatus({ kind: "error", text: "✗ " + first, title: r?.errors || "" });
      lastJson.current = "";
      return;
    }
    lastJson.current = r.json;
    setOutput(r.json);
    setError(false);
    if (r.warnings) {
      const n = r.warnings.split("\n").filter(Boolean).length;
      setStatus({ kind: "warn", text: `⚠ ${n} warning${n > 1 ? "s" : ""}`, title: r.warnings });
    } else {
      setStatus({ kind: "success", text: "✓ Compiled" });
    }
  }

  useEffect(() => {
    let alive = true;
    setStatus({ kind: "warn", text: "… loading compiler" });
    ensureWasm()
      .then(() => alive && compile(src))
      .catch((e) => alive && setStatus({ kind: "error", text: "✗ " + e.message }));
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function onEdit(text) {
    setSrc(text);
    compile(text);
  }

  function loadSample(s) {
    setSrc(s.code);
    compile(s.code);
  }

  async function save() {
    if (!lastJson.current) { notify("Nothing compiled to save.", "err"); return; }
    const path = (creds.path || "scripts/ch1.lvn").trim();
    notify("Saving…");
    try {
      const d = await putAsset(path, lastJson.current, creds.token, "application/json");
      notify(`✓ Saved ${d.path} (${d.bytes} B) — live in the app within ~2s`, "ok");
    } catch (e) {
      notify("✗ " + e.message, "err");
    }
  }

  return (
    <div className={"chapter" + (docs ? " with-docs" : "")}>
      <div className="ch-toolbar enter">
        <div className="ch-tools-left">
          <button className={"btn-ghost" + (docs ? " on" : "")} onClick={() => setDocs((d) => !d)}>
            ✦ Reference
          </button>
          <span className="divider" />
          <span className="section-label">Samples</span>
          {SAMPLES.map((s) => (
            <button key={s.label} className="btn-ghost sm" onClick={() => loadSample(s)}>
              {s.label}
            </button>
          ))}
        </div>
        <div className="ch-tools-right">
          {!embedded && (
            <input
              className="field path"
              placeholder="scripts/ch1.lvn"
              value={creds.path}
              onChange={(e) => creds.setPath(e.target.value)}
            />
          )}
          <button className="btn-ghost" onClick={() => navigator.clipboard.writeText(output)}>
            Copy JSON
          </button>
          <button className="btn btn-primary" onClick={save}>Save to app ▸</button>
        </div>
      </div>

      <div className="ch-body">
        {docs && <DocsPanel onClose={() => setDocs(false)} />}
        <section className="pane enter d1">
          <div className="pane-head">
            <span className="pane-title">LVNScript</span>
            <span className="pane-sub">.lvns — your language</span>
          </div>
          <div className="code-editor">
            <pre className="code-highlight" aria-hidden="true" ref={highlightRef}>
              <code dangerouslySetInnerHTML={{ __html: highlightLvns(src) + "\n" }} />
            </pre>
            <textarea
              className="code-input"
              spellCheck={false}
              wrap="off"
              value={src}
              onChange={(e) => onEdit(e.target.value)}
              onScroll={(e) => {
                if (highlightRef.current) {
                  highlightRef.current.scrollTop = e.target.scrollTop;
                  highlightRef.current.scrollLeft = e.target.scrollLeft;
                }
              }}
            />
          </div>
        </section>
        <section className="pane enter d2">
          <div className="pane-head">
            <span className="pane-title">Compiled</span>
            <span className="pane-sub">.lvn — engine format</span>
          </div>
          <pre className={"code-output" + (error ? " error" : "")}>{output}</pre>
        </section>
      </div>
      {(diags.errors.length > 0 || diags.warnings.length > 0) && (
        <DiagnosticsPanel errors={diags.errors} warnings={diags.warnings} />
      )}
    </div>
  );
}

// Parse a validator line like `script[2] label: msg` or `doc: msg` into parts.
function parseMsg(m) {
  let mm = m.match(/^script\[(\d+)\]\s+(\S+?):\s*(.*)$/);
  if (mm) return { idx: mm[1], op: mm[2], msg: mm[3] };
  mm = m.match(/^doc:\s*(.*)$/);
  if (mm) return { idx: null, op: null, msg: mm[1] };
  return { idx: null, op: null, msg: m };
}

// An IDE-style Problems list under the editor: errors first, then warnings.
function DiagnosticsPanel({ errors, warnings }) {
  const rows = [
    ...errors.map((m) => ({ sev: "error", ...parseMsg(m) })),
    ...warnings.map((m) => ({ sev: "warn", ...parseMsg(m) })),
  ];
  return (
    <div className="diagnostics">
      <div className="diag-head">
        <span className="diag-title">Problems</span>
        {errors.length > 0 && <span className="diag-count err">{errors.length} error{errors.length > 1 ? "s" : ""}</span>}
        {warnings.length > 0 && <span className="diag-count warn">{warnings.length} warning{warnings.length > 1 ? "s" : ""}</span>}
        {errors.length === 0 && warnings.length === 0 && <span className="diag-count ok">no problems</span>}
      </div>
      <div className="diag-list">
        {rows.map((r, i) => (
          <div key={i} className={"diag-row " + r.sev}>
            <span className="diag-dot" />
            {r.idx != null && <span className="diag-loc">#{r.idx} {r.op}</span>}
            <span className="diag-msg">{r.msg}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
