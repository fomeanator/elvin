// Slide-in reference: the engine ops authors can use, grouped, plus a copyable
// prompt for drafting chapters with an LLM.
import ResizeHandle from "./ResizeHandle.jsx";

const GROUPS = [
  {
    title: "Visuals & camera",
    rows: [
      ["bg", "sprite_url, id"],
      ["actor", "id, sprite_url, show, position, emotion"],
      ["obj", "id, sprite_url, x, y, width, height, on_click"],
      ["fade", "to (black/white/clear), duration"],
      ["dim", "alpha, duration"],
      ["flash", "color, duration"],
      ["tint", "color, alpha, duration"],
      ["blur", "alpha, duration"],
      ["camera", "action (shake/zoom/pan/reset), amplitude, factor, x, y, duration"],
      ["particles", "type (rain/snow), on"],
    ],
  },
  {
    title: "Audio & timing",
    rows: [
      ["audio", "channel, url, action"],
      ["wait", "ms"],
      ["text_pace", "cps"],
    ],
  },
  {
    title: "Flow",
    rows: [
      ["goto", "label (special: __end)"],
      ["if", "expr, then, else"],
      ["call / return", "subroutine jump"],
    ],
  },
  {
    title: "State",
    rows: [
      ["set", "key, value"],
      ["inc", "key, by"],
      ["hint", "text, show"],
    ],
  },
];

const SYNTAX = [
  [":label", "a jump target"],
  ["Mara [smile]: Hi.", "speech + emotion"],
  ["- Stay -> inside", "a choice"],
  ['bg sprite_url="…"', "an engine op"],
];

const AI_PROMPT = `# LVNScript (.lvns) — generation rules
Write narrative scripts in LVNScript. Grammar:
- \`scene name\` and \`actor_map Display=asset_id\` at the top.
- \`:label\` is a jump target; \`goto label\`, \`call\`/\`return\`.
- Plain line = narration; \`Name: text\` = speech; \`Name [emo]: text\` = speech + emotion.
- Choices: consecutive \`- Text -> target\` lines (+ optional \`cost=\`, \`min=\`, \`requires_stat=\`).
- Ops: \`set key="k" value=v\`, \`inc key="k" by=1\`, \`bg sprite_url="…"\`,
  \`actor id="x" show=true position="left"\`, \`fade to="black" duration=0.8\`,
  \`flash color="white"\`, \`tint color="cold" alpha=0.4\`, \`particles type="rain" on=true\`,
  \`camera action="shake" duration=0.5\`, \`if expr="score>=10" then="win" else="lose"\`.
- End paths with \`goto __end\`.`;

export default function DocsPanel({ onClose }) {
  return (
    <aside className="docs enter">
      <ResizeHandle storageKey="ide-w-docs" />
      <div className="docs-head">
        <h2>Reference</h2>
        <button className="btn-ghost sm" onClick={onClose}>✕</button>
      </div>

      <p className="docs-lede">
        Write in <strong>LVNScript</strong> on the left; it compiles to the
        engine's <code>.lvn</code> live. <em>Save to app</em> pushes it to the
        running game in ~2s.
      </p>

      <div className="syntax-mini">
        {SYNTAX.map(([c, d]) => (
          <div key={c}><code>{c}</code><span>{d}</span></div>
        ))}
      </div>

      {GROUPS.map((g) => (
        <div key={g.title} className="ref-group">
          <div className="ref-cat">{g.title}</div>
          {g.rows.map(([op, fields]) => (
            <div key={op} className="ref-row">
              <code>{op}</code><span>{fields}</span>
            </div>
          ))}
        </div>
      ))}

      <div className="ref-group">
        <div className="ref-cat ref-cat-row">
          AI author prompt
          <button className="btn-ghost sm" onClick={() => navigator.clipboard.writeText(AI_PROMPT)}>Copy</button>
        </div>
        <pre className="ai-prompt">{AI_PROMPT}</pre>
      </div>
    </aside>
  );
}
