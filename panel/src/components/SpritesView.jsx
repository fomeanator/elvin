import { useEffect, useMemo, useState } from "react";
import { getManifest, putAsset } from "../lib/api.js";
import {
  uid, slug, tokenOf, partPath, toEditor, toEntity, fill, TEMPLATES, PRESETS,
} from "../lib/sprites.js";

const BLANK = { id: "", name: "", color: "", axes: {}, picked: {}, parts: [] };

// A friendlier label for the common axes; anything else keeps its own name.
const AXIS_LABEL = { pose: "Poses", emotion: "Moods" };
const MOOD_ICON = { neutral: "😐", happy: "😊", smile: "🙂", sad: "😢", angry: "😠", blush: "☺️" };

export default function SpritesView({ creds, notify }) {
  const [catalog, setCatalog] = useState({});
  const [currentId, setCurrentId] = useState(null);
  const [ed, setEd] = useState(BLANK);
  const [bust, setBust] = useState(() => Date.now());
  const [chooser, setChooser] = useState(false);
  const [advanced, setAdvanced] = useState(false);

  useEffect(() => {
    (async () => {
      let sprites = {};
      try { sprites = (await getManifest()).sprites || {}; } catch { sprites = {}; }
      setCatalog(sprites);
      const first = Object.keys(sprites)[0] || null;
      setCurrentId(first);
      setEd(first ? toEditor(sprites[first], first) : BLANK);
    })();
  }, []);

  function selectEntity(id) {
    setChooser(false);
    setCurrentId(id);
    setEd(id && catalog[id] ? toEditor(catalog[id], id) : BLANK);
  }
  function newEntity(kind) {
    setChooser(false);
    setAdvanced(false);
    setCurrentId(null);
    setEd(toEditor(TEMPLATES[kind] || TEMPLATES.simple, ""));
  }

  // ── mutations ─────────────────────────────────────────────────────────
  const regenParts = (parts, id) => parts.map((p) => ({ ...p, url: partPath(p, id) }));

  function patch(fn) { setEd((s) => fn(s)); }

  function setName(name) {
    patch((s) => {
      const next = { ...s, name };
      if (!currentId && (!s.id || s.id === slug(s.name))) {
        next.id = slug(name);
        next.parts = regenParts(s.parts, next.id);
      }
      return next;
    });
  }
  function setId(id) { patch((s) => ({ ...s, id, parts: regenParts(s.parts, id) })); }
  function setColor(color) { patch((s) => ({ ...s, color })); }
  function setPicked(axis, value) { patch((s) => ({ ...s, picked: { ...s.picked, [axis]: value } })); }

  // Add a "look category" (an axis) and make sure a layer varies by it.
  function addLookCategory(axisName, values, partName) {
    patch((s) => {
      const axes = { ...s.axes };
      const cur = (axes[axisName] || []).slice();
      (values || []).forEach((v) => { if (!cur.includes(v)) cur.push(v); });
      axes[axisName] = cur;
      const picked = { ...s.picked };
      if (!picked[axisName] && cur.length) picked[axisName] = cur[0];
      let parts = s.parts;
      if (!parts.some((p) => p.axis === axisName)) {
        const np = { key: uid(), name: partName || axisName, axis: axisName, when: null, url: "" };
        np.url = partPath(np, s.id);
        // Only when this is the FIRST category (a plain object becoming a
        // character) does its lone static image become the category's first frame.
        // A character that already has a base + other axes KEEPS its base layer.
        if (Object.keys(s.axes).length === 0) {
          const base = parts.find((p) => !p.axis && p.url);
          if (base) parts = parts.filter((p) => p !== base);
        }
        parts = [...parts, np];
      }
      return { ...s, axes, picked, parts };
    });
  }
  function addOption(axis, value) {
    const v = (value || "").trim();
    if (!v) return;
    patch((s) => {
      if ((s.axes[axis] || []).includes(v)) return s;
      const axes = { ...s.axes, [axis]: [...(s.axes[axis] || []), v] };
      const picked = { ...s.picked, [axis]: s.picked[axis] || v };
      return { ...s, axes, picked };
    });
  }
  function removeOption(axis, value) {
    patch((s) => ({ ...s, axes: { ...s.axes, [axis]: (s.axes[axis] || []).filter((v) => v !== value) } }));
  }
  function removeCategory(axis) {
    patch((s) => {
      const axes = { ...s.axes }; delete axes[axis];
      const picked = { ...s.picked }; delete picked[axis];
      const parts = s.parts.filter((p) => p.axis !== axis);
      return { ...s, axes, picked, parts };
    });
  }

  // effective preview selection (first value as fallback)
  const effPicked = useMemo(() => {
    const out = {};
    Object.keys(ed.axes).forEach((ax) => {
      const vals = ed.axes[ax] || [];
      out[ax] = ed.picked[ax] && vals.includes(ed.picked[ax]) ? ed.picked[ax] : (vals[0] || ed.picked[ax] || "");
    });
    return out;
  }, [ed.axes, ed.picked]);

  function partForAxis(axis) { return ed.parts.find((p) => p.axis === axis); }

  async function uploadResolved(resolvedUrl) {
    return new Promise((resolve) => {
      const picker = document.createElement("input");
      picker.type = "file";
      picker.accept = "image/*";
      picker.onchange = async () => {
        const f = picker.files && picker.files[0];
        if (!f) return resolve(false);
        notify("Uploading…");
        try {
          const d = await putAsset(resolvedUrl, f, creds.token, f.type || "application/octet-stream");
          setBust(Date.now());
          notify(`✓ Uploaded ${d.path} (${(d.bytes / 1024).toFixed(1)} KB)`, "ok");
          resolve(true);
        } catch (e) { notify("✗ " + e.message, "err"); resolve(false); }
      };
      picker.click();
    });
  }
  async function uploadForOption(axis, value) {
    const part = partForAxis(axis);
    if (!part) return;
    setPicked(axis, value);
    await uploadResolved(part.url.replace(/\{[^}]+\}/, value));
  }

  async function save() {
    const id = ed.id.trim() || slug(ed.name);
    if (!id) { notify("Give it a name first.", "err"); return; }
    const entity = toEntity({ ...ed, id, picked: effPicked });
    const nextCatalog = { ...catalog };
    if (currentId && currentId !== id) delete nextCatalog[currentId];
    nextCatalog[id] = entity;
    setCatalog(nextCatalog);
    setCurrentId(id);
    try {
      const m = await getManifest();
      m.sprites = nextCatalog;
      await putAsset("manifest.json", JSON.stringify(m, null, 2), creds.token, "application/json");
      notify(`✓ Saved — live in the game in ~2s`, "ok");
    } catch (e) { notify("✗ " + e.message, "err"); }
  }
  function deleteEntity() {
    if (!currentId || !catalog[currentId]) { selectEntity(Object.keys(catalog)[0] || null); return; }
    const next = { ...catalog }; delete next[currentId];
    setCatalog(next);
    selectEntity(Object.keys(next)[0] || null);
  }

  const axisNames = Object.keys(ed.axes);
  const basePart = ed.parts.find((p) => !p.axis && p.when == null);
  const isCharacter = axisNames.length > 0;

  return (
    <div className="cast">
      {/* roster */}
      <aside className="roster enter">
        <div className="roster-head">
          <span className="section-label">Cast</span>
          <button className="btn-ghost sm" onClick={() => setChooser(true)}>+ New</button>
        </div>
        <div className="roster-list">
          {Object.keys(catalog).length === 0 && <div className="roster-empty">Nobody yet.<br />Add someone →</div>}
          {Object.keys(catalog).map((id) => (
            <RosterItem
              key={id}
              id={id}
              entity={catalog[id]}
              bust={bust}
              active={id === currentId}
              onClick={() => selectEntity(id)}
            />
          ))}
        </div>
      </aside>

      {/* stage + wardrobe */}
      <main className="studio enter d1">
        <div className="studio-top">
          <input
            className="char-name"
            placeholder="Name your character…"
            value={ed.name}
            onChange={(e) => setName(e.target.value)}
          />
          <div className="studio-top-actions">
            <button className={"btn-ghost" + (advanced ? " on" : "")} onClick={() => setAdvanced((a) => !a)}>
              ⚙ Advanced
            </button>
          </div>
        </div>

        <div className="studio-body">
          <Stage parts={ed.parts} picked={effPicked} bust={bust} />

          <div className="wardrobe">
            {basePart && (
              <LookGroup label={isCharacter ? "Base" : "Artwork"} hint={isCharacter ? "always-on layer (the body)" : "the picture for this object"}>
                <OptionCard
                  label={isCharacter ? "body" : "image"}
                  url={basePart.url}
                  bust={bust}
                  selected
                  onSelect={() => uploadResolved(basePart.url)}
                  onUpload={() => uploadResolved(basePart.url)}
                />
              </LookGroup>
            )}

            {axisNames.map((axis) => {
              const part = partForAxis(axis);
              const label = AXIS_LABEL[axis] || (axis[0].toUpperCase() + axis.slice(1));
              return (
                <LookGroup
                  key={axis}
                  label={label}
                  hint={`click an option to see it on stage · click an empty card to add art`}
                  onRemove={() => removeCategory(axis)}
                >
                  {(ed.axes[axis] || []).map((v) => (
                    <OptionCard
                      key={v}
                      label={v}
                      icon={axis === "emotion" ? MOOD_ICON[v] : null}
                      url={part ? (fill(part.url, { ...effPicked, [axis]: v }) || "") : ""}
                      bust={bust}
                      selected={effPicked[axis] === v}
                      onSelect={() => setPicked(axis, v)}
                      onUpload={() => uploadForOption(axis, v)}
                      onRemove={() => removeOption(axis, v)}
                    />
                  ))}
                  <AddOption onAdd={(name) => addOption(axis, name)} />
                </LookGroup>
              );
            })}

            <div className="add-look">
              {!ed.axes.pose && (
                <button className="add-look-btn" onClick={() => addLookCategory("pose", PRESETS.pose, "body")}>
                  <b>🧍 Add poses</b><span>standing, sitting…</span>
                </button>
              )}
              {!ed.axes.emotion && (
                <button className="add-look-btn" onClick={() => addLookCategory("emotion", PRESETS.emotion, "face")}>
                  <b>😊 Add moods</b><span>neutral, happy, sad…</span>
                </button>
              )}
              <button
                className="add-look-btn add-look-dim"
                onClick={() => {
                  let n = "look", i = 1; while (ed.axes[n]) n = "look" + ++i;
                  addLookCategory(n, [], n);
                }}
              >
                <b>＋ Custom set</b><span>your own category</span>
              </button>
            </div>
          </div>
        </div>

        {advanced && (
          <Advanced
            ed={ed}
            setId={setId}
            setColor={setColor}
            onPartWhen={(key, when) => patch((s) => ({ ...s, parts: s.parts.map((p) => (p.key === key ? { ...p, when } : p)) }))}
            onAddBlush={() => patch((s) => ({
              ...s,
              parts: [...s.parts, { key: uid(), name: "blush", axis: "", when: "warmth >= 1", url: partPath({ name: "blush", axis: "" }, s.id) }],
            }))}
          />
        )}

        <div className="studio-bar">
          <button className="btn btn-primary" onClick={save}>Save — put in game ▸</button>
          <button className="btn-ghost" onClick={deleteEntity}>Delete</button>
        </div>
      </main>

      {chooser && <Chooser onPick={newEntity} onCancel={() => setChooser(false)} />}
    </div>
  );
}

/* ── pieces ──────────────────────────────────────────────────────────────── */

function entityThumb(entity, bust) {
  const e = entity || {};
  const def = e.defaults || {};
  for (const l of e.layers || []) {
    let u = typeof l === "string" ? l : l.url;
    if (!u) continue;
    u = u.replace(/\{([^}]+)\}/g, (_, k) => def[k] || "");
    if (!u.includes("{")) return u + "?v=" + bust;
  }
  return null;
}

function RosterItem({ id, entity, bust, active, onClick }) {
  const thumb = entityThumb(entity, bust);
  const cast = (entity?.layers || []).some((l) => (typeof l === "string" ? l : l.url || "").includes("{"));
  const [ok, setOk] = useState(true);
  return (
    <button className={"roster-item" + (active ? " active" : "")} onClick={onClick}>
      <span className="roster-portrait" style={entity?.color ? { "--tint": entity.color } : undefined}>
        {thumb && ok ? <img src={thumb} alt="" onError={() => setOk(false)} /> : <span>{(entity?.name || id)[0]?.toUpperCase()}</span>}
      </span>
      <span className="roster-meta">
        <span className="roster-name">{entity?.name || id}</span>
        <span className="roster-kind">{cast ? "character" : "object"}</span>
      </span>
    </button>
  );
}

function Stage({ parts, picked, bust }) {
  const layers = [];
  parts.forEach((p) => {
    if (!p.url) return;
    const url = fill(p.url, picked);
    if (!url) return;
    layers.push({ key: p.key, url, when: p.when });
  });

  return (
    <div className="stage">
      <div className="stage-spot" />
      <div className="stage-figure">
        {layers.map((l) => (
          <img
            key={l.url}
            className="stage-layer"
            src={l.url + "?v=" + bust}
            style={l.when ? { opacity: 0.65 } : undefined}
            alt=""
            onError={(e) => { e.currentTarget.style.visibility = "hidden"; }}
            onLoad={(e) => { e.currentTarget.style.visibility = "visible"; }}
          />
        ))}
        {layers.length === 0 && (
          <div className="stage-empty">
            <span className="stage-empty-mark">＋</span>
            Pick a look on the right, then drop in its picture
          </div>
        )}
      </div>
      <div className="stage-pedestal" />
    </div>
  );
}

function LookGroup({ label, hint, onRemove, children }) {
  return (
    <section className="look-group">
      <div className="look-head">
        <span className="look-label">{label}</span>
        {hint && <span className="look-hint">{hint}</span>}
        {onRemove && <button className="look-remove" onClick={onRemove} title={"remove " + label}>✕</button>}
      </div>
      <div className="look-cards">{children}</div>
    </section>
  );
}

function OptionCard({ label, icon, url, bust, selected, onSelect, onUpload, onRemove }) {
  const [empty, setEmpty] = useState(true);
  return (
    <div className={"opt" + (selected ? " selected" : "") + (empty ? " empty" : "")}>
      <button className="opt-face" onClick={empty ? onUpload : onSelect} title={empty ? "add a picture" : "show on stage"}>
        {url && <img src={url + "?v=" + bust} alt="" style={empty ? { display: "none" } : undefined} onLoad={() => setEmpty(false)} onError={() => setEmpty(true)} />}
        {empty && <span className="opt-add">{icon || "＋"}<em>add art</em></span>}
      </button>
      <div className="opt-foot">
        <span className="opt-label">{icon && !empty ? icon + " " : ""}{label}</span>
        {!empty && <button className="opt-mini" onClick={onUpload} title="replace picture">⟳</button>}
        {onRemove && <button className="opt-mini" onClick={onRemove} title="remove option">✕</button>}
      </div>
    </div>
  );
}

function AddOption({ onAdd }) {
  const [open, setOpen] = useState(false);
  const [val, setVal] = useState("");
  if (!open) return <button className="opt-add-new" onClick={() => setOpen(true)}>＋<em>option</em></button>;
  return (
    <div className="opt-add-form">
      <input
        autoFocus
        className="field"
        placeholder="name, e.g. sitting"
        value={val}
        onChange={(e) => setVal(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") { onAdd(val); setVal(""); setOpen(false); }
          if (e.key === "Escape") { setOpen(false); setVal(""); }
        }}
        onBlur={() => { if (val.trim()) onAdd(val); setOpen(false); setVal(""); }}
      />
    </div>
  );
}

function Chooser({ onPick, onCancel }) {
  return (
    <div className="sp-chooser" onClick={onCancel}>
      <div className="sp-chooser-box" onClick={(e) => e.stopPropagation()}>
        <h3>What are you adding?</h3>
        <button className="sp-choice" onClick={() => onPick("character")}>
          <b>🧍 A character</b>
          <span>someone with poses &amp; moods — you'll drop in a picture for each</span>
        </button>
        <button className="sp-choice" onClick={() => onPick("simple")}>
          <b>🖼 A background or object</b>
          <span>just one picture — a scene, a prop, a UI piece</span>
        </button>
        <button className="btn-ghost" onClick={onCancel}>Cancel</button>
      </div>
    </div>
  );
}

function Advanced({ ed, setId, setColor, onPartWhen, onAddBlush }) {
  const conds = ed.parts.filter((p) => p.when != null);
  return (
    <div className="advanced enter">
      <div className="adv-grid">
        <label className="adv-field">
          <span>Script id <em>(how the story refers to this)</em></span>
          <input className="field wide mono" value={ed.id} onChange={(e) => setId(e.target.value)} placeholder="mara" />
        </label>
        <label className="adv-field">
          <span>Name colour <em>(speech nameplate)</em></span>
          <input className="field wide" value={ed.color} onChange={(e) => setColor(e.target.value)} placeholder="#e9bcd9" />
        </label>
      </div>
      <div className="adv-conds">
        <div className="adv-conds-head">
          <span>Conditional extras <em>(a layer shown only when a story value is true — e.g. a blush)</em></span>
          <button className="btn-ghost sm" onClick={onAddBlush}>+ Add a blush layer</button>
        </div>
        {conds.length === 0 && <div className="adv-note">None. These are optional and advanced.</div>}
        {conds.map((p) => (
          <div className="adv-cond" key={p.key}>
            <span className="adv-cond-name">{p.name}</span>
            <span className="adv-cond-when">show when</span>
            <input className="field mono" value={p.when} onChange={(e) => onPartWhen(p.key, e.target.value)} placeholder="warmth >= 1" />
          </div>
        ))}
      </div>
    </div>
  );
}
