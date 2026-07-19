import { useEffect, useMemo, useState } from "react";
import { parseGIF, decompressFrames } from "gifuct-js";
import { getManifest, putAsset, uploadSpine } from "../lib/api.js";
import {
  uid, slug, partPath, toEditor, toEntity, fill, framesFromAxis, TEMPLATES, PRESETS,
} from "../lib/sprites.js";

const BLANK = { id: "", name: "", color: "", kind: "", axes: {}, picked: {}, parts: [], anim: [] };

// Tween properties a track can drive (frame = swap the layer's sprite by an axis
// value; the rest are transform tweens).
const TWEEN_PROPS = ["frame", "scale", "scalex", "scaley", "rotation", "alpha", "x", "y", "screen_x", "screen_y"];
const EASES = ["", "linear", "inOutSine", "outCubic", "outBack", "inBack"];

function uniqueAnimName(list) {
  const names = new Set(list.map((a) => a.name));
  if (!names.has("idle")) return "idle";
  let i = 2;
  while (names.has("anim" + i)) i++;
  return "anim" + i;
}

function loadImage(src) {
  return new Promise((res, rej) => { const i = new Image(); i.onload = () => res(i); i.onerror = rej; i.src = src; });
}

// Decode every frame of an animated GIF into composited PNG blobs.
async function gifToFrames(file) {
  const buf = await file.arrayBuffer();
  const gif = parseGIF(buf);
  const frames = decompressFrames(gif, true);
  const W = gif.lsd.width, H = gif.lsd.height;
  const full = document.createElement("canvas"); full.width = W; full.height = H;
  const fctx = full.getContext("2d");
  const tmp = document.createElement("canvas"); const tctx = tmp.getContext("2d");
  const out = [];
  for (const fr of frames) {
    const { width, height, top, left } = fr.dims;
    tmp.width = width; tmp.height = height;
    const data = tctx.createImageData(width, height);
    data.data.set(fr.patch);
    tctx.putImageData(data, 0, 0);
    fctx.drawImage(tmp, left, top);
    out.push(await new Promise((res) => full.toBlob(res, "image/png")));
    if (fr.disposalType === 2) fctx.clearRect(left, top, width, height);
  }
  return out;
}

// Slice a spritesheet grid (columns×rows, prompted) into PNG blobs.
async function sheetToFrames(file) {
  const spec = prompt("Sheet grid as columns×rows (e.g. 4x2):", "4x1");
  if (!spec) return null;
  const m = spec.toLowerCase().match(/(\d+)\s*[x×,\s]\s*(\d+)/);
  if (!m) throw new Error("bad grid — use e.g. 4x2");
  const cols = +m[1], rows = +m[2];
  const img = await loadImage(URL.createObjectURL(file));
  const cw = Math.floor(img.width / cols), ch = Math.floor(img.height / rows);
  const canvas = document.createElement("canvas"); canvas.width = cw; canvas.height = ch;
  const g = canvas.getContext("2d");
  const out = [];
  for (let r = 0; r < rows; r++) {
    for (let c = 0; c < cols; c++) {
      g.clearRect(0, 0, cw, ch);
      g.drawImage(img, c * cw, r * ch, cw, ch, 0, 0, cw, ch);
      out.push(await new Promise((res) => canvas.toBlob(res, "image/png")));
    }
  }
  return out;
}

// A friendlier label for the common axes; anything else keeps its own name.
const AXIS_LABEL = { pose: "Poses", emotion: "Moods" };
const MOOD_ICON = { neutral: "😐", happy: "😊", smile: "🙂", sad: "😢", angry: "😠", blush: "☺️" };

export default function SpritesView({ creds, notify, titleId }) {
  const [catalog, setCatalog] = useState({});
  const [currentId, setCurrentId] = useState(null);
  const [ed, setEd] = useState(BLANK);
  // Cache-buster: EMPTY until an upload actually changes art — a Date.now()
  // per mount forced every roster thumb to redownload on every visit (the
  // "no cache in the admin" complaint). Uploads bump it to show fresh art.
  const [bust, setBust] = useState(0);
  const [chooser, setChooser] = useState(false);
  const [spineDlg, setSpineDlg] = useState(false);
  const [advanced, setAdvanced] = useState(false);
  const [preview, setPreview] = useState(null); // anim key being played on stage
  // id → index of first appearance across the novel's chapters (see below) —
  // the roster orders characters the way the STORY introduces them.
  const [scriptOrder, setScriptOrder] = useState(null);
  // Roster browsing: a photo GRID (art as "files") or the compact list, with
  // search and pagination — a partner novel lands with hundreds of entities.
  const [rosterView, setRosterView] = useState(() => localStorage.getItem("lvn_roster_view") || "grid");
  useEffect(() => localStorage.setItem("lvn_roster_view", rosterView), [rosterView]);
  const [rosterQuery, setRosterQuery] = useState("");
  // Only entities the OPEN novel's chapters actually reference (via
  // scriptOrder). The catalog is app-wide — a dev server carries every demo's
  // cast; a partner working on one title must not wade through the rest.
  const [onlyTitle, setOnlyTitle] = useState(() => (localStorage.getItem("lvn_roster_only_title") ?? "1") === "1");
  useEffect(() => localStorage.setItem("lvn_roster_only_title", onlyTitle ? "1" : "0"), [onlyTitle]);
  const [rosterCount, setRosterCount] = useState(48); // infinite scroll: how many tiles are mounted

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

  // Script order: walk the open novel's chapters once and record where each
  // actor id (or spoken who/who_id) first appears. Characters then sort by
  // the story, not by the accident of catalog-object key order.
  useEffect(() => {
    if (!titleId) { setScriptOrder(null); return; }
    let dead = false;
    (async () => {
      try {
        const m = await getManifest();
        const t = (m.titles || []).find((x) => x.id === titleId);
        const urls = [];
        (t?.seasons || []).forEach((s) => (s.chapters || []).forEach((c) => c.script_url && urls.push(c.script_url)));
        const order = new Map();
        let n = 0;
        const note = (key) => { if (key && !order.has(key)) order.set(key, n++); };
        for (const u of urls) {
          try {
            const doc = await (await fetch(u)).json();
            for (const c of doc.script || []) {
              if (!c || typeof c !== "object") continue;
              if (c.op === "actor" || c.op === "obj") note(c.id);
              else if (c.op === "say") { note(c.who_id); note(c.who); }
            }
          } catch { /* глава может быть ещё не скомпилирована — пропускаем */ }
        }
        if (!dead) setScriptOrder(order);
      } catch { if (!dead) setScriptOrder(null); }
    })();
    return () => { dead = true; };
  }, [titleId]);

  // Preview player: cycle a frame track's axis values on the stage over the anim's
  // duration (only re-renders when the visible frame changes).
  useEffect(() => {
    if (!preview) return undefined;
    const a = (ed.anim || []).find((x) => x.key === preview);
    const ft = a && a.tracks.find((t) => t.prop === "frame" && (t.keys || []).length);
    if (!ft) return undefined;
    const dur = Number(a.duration) || 1;
    const start = performance.now();
    let last = null, raf = 0;
    const tick = (now) => {
      const t = ((now - start) / 1000) % dur;
      let v = ft.keys[0][1];
      for (const [kt, kv] of ft.keys) if (kt <= t) v = kv;
      if (v !== last) { last = v; setPicked(ft.axis, v); }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
    // setPicked is recreated every render; depending on it would restart the
    // preview loop each frame. The loop only needs preview/anim identity.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [preview, ed.anim]);

  function selectEntity(id) {
    setChooser(false);
    setPreview(null);
    setCurrentId(id);
    const ent = id ? catalog[id] : null;
    if (ent && ent.kind === "spine") {
      // Spine entities have no layer editor — they are driven from scripts.
      setEd(BLANK);
      notify(`«${id}» — Spine-персонаж. В сценах: actor id=${id} play="Idle"`, "ok");
      return;
    }
    setEd(ent ? toEditor(ent, id) : BLANK);
  }
  function newEntity(kind) {
    setChooser(false);
    if (kind === "spine") { setSpineDlg(true); return; }
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
        const np = { key: uid(), name: partName || axisName, layerId: slug(partName || axisName), axis: axisName, when: null, url: "" };
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

  // ── animation mutations ──────────────────────────────────────────────────
  const updateAnims = (fn) => patch((s) => ({ ...s, anim: fn(s.anim || []) }));

  function addAnim() {
    // Seed a frame animation from the first axis that has a layer, if any.
    const axis = Object.keys(ed.axes).find((a) => (ed.axes[a] || []).length && ed.parts.some((p) => p.axis === a));
    const part = axis ? ed.parts.find((p) => p.axis === axis) : null;
    const dur = axis ? Math.max(0.3, (ed.axes[axis] || []).length * 0.12) : 1;
    const track = axis
      ? { key: uid(), layer: part?.layerId || "", prop: "frame", axis, ease: "", interp: "", keys: framesFromAxis(ed.axes[axis], dur) }
      : { key: uid(), layer: ed.parts[0]?.layerId || "", prop: "scale", axis: "", ease: "outBack", interp: "", keys: [[0, 1], [dur, 1.1]] };
    const name = uniqueAnimName(ed.anim || []);
    updateAnims((a) => [...a, { key: uid(), name, loop: !!axis, auto: !!axis, duration: dur, tracks: [track] }]);
    // rigged is required for named/idle animations to run
    if (!ed.kind) patch((s) => ({ ...s, kind: "rigged" }));
  }
  function removeAnim(k) { updateAnims((a) => a.filter((x) => x.key !== k)); }
  function updateAnim(k, patchObj) { updateAnims((a) => a.map((x) => (x.key === k ? { ...x, ...patchObj } : x))); }

  function addTrack(animKey) {
    updateAnims((a) => a.map((x) => x.key === animKey
      ? { ...x, tracks: [...x.tracks, { key: uid(), layer: ed.parts[0]?.layerId || "", prop: "scale", axis: "", ease: "", interp: "", keys: [[0, 1], [x.duration || 1, 1.1]] }] }
      : x));
  }
  function removeTrack(animKey, trackKey) {
    updateAnims((a) => a.map((x) => x.key === animKey ? { ...x, tracks: x.tracks.filter((t) => t.key !== trackKey) } : x));
  }
  function updateTrack(animKey, trackKey, patchObj) {
    updateAnims((a) => a.map((x) => x.key === animKey
      ? { ...x, tracks: x.tracks.map((t) => (t.key === trackKey ? { ...t, ...patchObj } : t)) }
      : x));
  }
  // Regenerate a frame track's keys evenly from its axis's current values.
  function syncFrameKeys(animKey, trackKey) {
    updateAnims((a) => a.map((x) => {
      if (x.key !== animKey) return x;
      return { ...x, tracks: x.tracks.map((t) => {
        if (t.key !== trackKey || t.prop !== "frame") return t;
        return { ...t, keys: framesFromAxis(ed.axes[t.axis], x.duration) };
      }) };
    }));
  }

  // Import frames from an animated GIF or a spritesheet grid: decode/slice into
  // per-frame PNGs, upload each as an axis value (so they drive a `frame`
  // animation). New supported source types, all client-side.
  function importSheet(axis) {
    const part = partForAxis(axis);
    if (!part) { notify("Add art to this set first, then import frames.", "err"); return; }
    const picker = document.createElement("input");
    picker.type = "file"; picker.accept = "image/*,.gif";
    picker.onchange = async () => {
      const f = picker.files && picker.files[0];
      if (!f) return;
      const isGif = f.type === "image/gif" || /\.gif$/i.test(f.name);
      try {
        const blobs = isGif ? await gifToFrames(f) : await sheetToFrames(f);
        if (!blobs) return; // cancelled
        notify(`Uploading ${blobs.length} frames…`);
        const values = [];
        for (let i = 0; i < blobs.length; i++) {
          const value = String(i);
          const url = part.url.replace(new RegExp("\\{" + axis + "\\}"), value);
          await putAsset(url, new File([blobs[i]], value + ".png", { type: "image/png" }), creds.token, "image/png");
          values.push(value);
        }
        patch((s) => {
          const cur = (s.axes[axis] || []).slice();
          values.forEach((v) => { if (!cur.includes(v)) cur.push(v); });
          const picked = { ...s.picked };
          if (!picked[axis] && cur.length) picked[axis] = cur[0];
          return { ...s, axes: { ...s.axes, [axis]: cur }, picked };
        });
        setBust(Date.now());
        notify(`✓ Imported ${values.length} frames — add an animation to play them`, "ok");
      } catch (e) { notify("✗ " + (e.message || e), "err"); }
    };
    picker.click();
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

  // Roster in two tiers: CHARACTERS (layered entities — the heroes) first, in
  // the order the story introduces them; then everything else (plain objects,
  // imported look-variants) below, same story order, alphabetical tail for
  // ids the script never mentions.
  const roster = useMemo(() => {
    const ids = Object.keys(catalog);
    const isChar = (id) =>
      (catalog[id]?.layers || []).some((l) => (typeof l === "string" ? l : l.url || "").includes("{"));
    const ord = (id) => {
      if (!scriptOrder) return Infinity;
      const e = catalog[id] || {};
      const byId = scriptOrder.get(id);
      const byName = e.name != null ? scriptOrder.get(e.name) : undefined;
      return Math.min(byId ?? Infinity, byName ?? Infinity);
    };
    const cmp = (a, b) => ord(a) - ord(b) || a.localeCompare(b, "ru");
    // Look-variants share one display name (Матвей / Matvey_bloody /
    // Matvey_bandage…) — the roster shows ONE tile per character, variants
    // fold inside it. Grouping is by display name within each tier.
    const grouped = (list) => {
      const by = new Map();
      for (const id of list.sort(cmp)) {
        const name = String(catalog[id]?.name || id);
        if (!by.has(name)) by.set(name, []);
        by.get(name).push(id);
      }
      return [...by.entries()].map(([name, gids]) => ({ name, ids: gids }));
    };
    return {
      characters: grouped(ids.filter(isChar)),
      objects: grouped(ids.filter((id) => !isChar(id))),
    };
  }, [catalog, scriptOrder]);

  return (
    <div className="cast">
      {/* roster */}
      <aside className={"roster enter" + (rosterView === "grid" ? " wide" : "")}>
        <div className="roster-head">
          <span className="section-label">Cast</span>
          <div className="roster-tools">
            <button
              className={"btn-ghost sm" + (rosterView === "grid" ? " on" : "")}
              title={rosterView === "grid" ? "Списком" : "Плиткой (арт как файлы)"}
              onClick={() => setRosterView(rosterView === "grid" ? "list" : "grid")}
            >{rosterView === "grid" ? "☰" : "▦"}</button>
            <button className="btn-ghost sm" onClick={() => setChooser(true)}>+ New</button>
          </div>
        </div>
        <input
          className="field roster-search"
          placeholder="Поиск по касту…"
          value={rosterQuery}
          onChange={(e) => { setRosterQuery(e.target.value); setRosterCount(48); }}
        />
        {titleId && (
          <div className="roster-scope">
            <button className={"roster-scope-chip" + (onlyTitle ? " active" : "")}
              onClick={() => { setOnlyTitle(true); setRosterCount(48); }}>эта новелла</button>
            <button className={"roster-scope-chip" + (!onlyTitle ? " active" : "")}
              onClick={() => { setOnlyTitle(false); setRosterCount(48); }}>все ассеты</button>
          </div>
        )}
        {(() => {
          const q = rosterQuery.trim().toLowerCase();
          const inTitle = (g) => !onlyTitle || !scriptOrder || !titleId
            || scriptOrder.has(g.name)
            || g.ids.some((id) => scriptOrder.has(id));
          const gmatch = (g) => inTitle(g) && (!q || g.name.toLowerCase().includes(q)
            || g.ids.some((id) => id.toLowerCase().includes(q)));
          const chars = roster.characters.filter(gmatch);
          const objs = roster.objects.filter(gmatch);
          const flat = [...chars, ...objs];
          const slice = flat.slice(0, rosterCount);
          const firstObj = objs.length ? objs[0] : null;
          const Item = rosterView === "grid" ? RosterCard : RosterItem;
          return (
            <>
              <div
                className={rosterView === "grid" ? "roster-grid" : "roster-list"}
                onScroll={(e) => {
                  const el = e.currentTarget;
                  if (el.scrollTop + el.clientHeight > el.scrollHeight - 420 && slice.length < flat.length)
                    setRosterCount((c) => c + 48);
                }}
              >
                {flat.length === 0 && (
                  <div className="roster-empty">{q ? "Ничего не найдено." : <>Nobody yet.<br />Add someone →</>}</div>
                )}
                {slice.map((g) => (
                  <span key={g.name + g.ids[0]} style={{ display: "contents" }}>
                    {g === firstObj && chars.length > 0 && (
                      <div className="roster-divider">Objects & variants</div>
                    )}
                    <Item
                      group={g}
                      catalog={catalog}
                      bust={bust}
                      activeId={currentId}
                      onPick={selectEntity}
                    />
                  </span>
                ))}
              </div>
              {slice.length < flat.length && (
                <div className="roster-more">показано {slice.length} из {flat.length} — прокрути ниже…</div>
              )}
            </>
          );
        })()}
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
            {isCharacter && (
              <button className="btn-ghost sm" onClick={addAnim} title="Анимации: idle-луп на показе, остальные из скрипта (actor id play=name)">
                + Анимация
              </button>
            )}
            <button className={"btn-ghost sm" + (advanced ? " on" : "")} title="Advanced: id, цвет, условные слои"
              onClick={() => setAdvanced((a) => !a)}>⚙</button>
            <button className="btn-ghost sm" onClick={deleteEntity}>Delete</button>
            <button className="btn btn-primary" onClick={save}>Save — put in game ▸</button>
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
                  onImport={() => importSheet(axis)}
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

        {isCharacter && (ed.anim || []).length > 0 && (
          <AnimEditor
            ed={ed}
            preview={preview}
            onRemove={removeAnim}
            onUpdate={updateAnim}
            onAddTrack={addTrack}
            onRemoveTrack={removeTrack}
            onUpdateTrack={updateTrack}
            onSyncFrames={syncFrameKeys}
            onPreview={(k) => setPreview((p) => (p === k ? null : k))}
          />
        )}

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

      </main>

      {chooser && <Chooser onPick={newEntity} onCancel={() => setChooser(false)} />}
      {spineDlg && (
        <SpineUpload
          token={creds.token}
          notify={notify}
          onDone={async (id) => {
            setSpineDlg(false);
            try {
              const sprites = (await getManifest()).sprites || {};
              setCatalog(sprites);
              setCurrentId(id);
            } catch { /* manifest refresh is best-effort */ }
          }}
          onCancel={() => setSpineDlg(false)}
        />
      )}
    </div>
  );
}

/* ── pieces ──────────────────────────────────────────────────────────────── */

// Every default-resolved layer of the entity, in stage order — the roster
// tile stacks them so the preview is the ASSEMBLED character (body + clothes
// + face + hair), not whichever single layer resolved first.
function entityLayers(entity, bust) {
  const e = entity || {};
  const def = e.defaults || {};
  const out = [];
  for (const l of e.layers || []) {
    let u = typeof l === "string" ? l : l.url;
    if (!u) continue;
    u = u.replace(/\{([^}]+)\}/g, (_, k) => def[k] || "");
    if (!u.includes("{")) out.push(bust ? u + "?v=" + bust : u);
  }
  return out;
}

// The stacked-layers thumbnail; falls back to the first letter when nothing
// resolves. Layers that 404 hide themselves and never break the stack.
function AssembledThumb({ entity, bust, letter, className }) {
  const urls = entityLayers(entity, bust);
  if (!urls.length) return <span className="roster-card-letter">{letter}</span>;
  return (
    <span className={className || "thumb-stack"}>
      {urls.map((u) => (
        <img key={u} src={u} alt="" loading="lazy"
          onError={(e) => { e.currentTarget.style.display = "none"; }} />
      ))}
    </span>
  );
}

// A group = one display name (a character) and every catalog id behind it:
// the main entity first, story-state variants (dead / bloody / …) after.
function RosterItem({ group, catalog, bust, activeId, onPick }) {
  const activeInGroup = group.ids.includes(activeId);
  const shownId = activeInGroup ? activeId : group.ids[0];
  const entity = catalog[shownId];
  const cast = (entity?.layers || []).some((l) => (typeof l === "string" ? l : l.url || "").includes("{"));
  return (
    <>
      <button className={"roster-item" + (activeInGroup ? " active" : "")} onClick={() => onPick(shownId)}>
        <span className="roster-portrait thumb-stack" style={entity?.color ? { "--tint": entity.color } : undefined}>
          <AssembledThumb entity={entity} bust={bust} letter={group.name[0]?.toUpperCase()} className="" />
        </span>
        <span className="roster-meta">
          <span className="roster-name">{group.name}{group.ids.length > 1 ? ` ×${group.ids.length}` : ""}</span>
          <span className="roster-kind">{cast ? "character" : "object"}</span>
        </span>
      </button>
      {activeInGroup && group.ids.length > 1 && (
        <div className="roster-variants list">
          {group.ids.map((vid) => (
            <button key={vid} className={"roster-variant" + (vid === activeId ? " active" : "")}
              title={vid} onClick={() => onPick(vid)}>{variantLabel(vid, group)}</button>
          ))}
        </div>
      )}
    </>
  );
}

// The grid tile — art as a "file": a big portrait with the name underneath,
// like a file manager, so a 300-entity partner cast is scannable by eye.
// Variants of the active character unfold as a chip row inside the tile.
function RosterCard({ group, catalog, bust, activeId, onPick }) {
  const activeInGroup = group.ids.includes(activeId);
  const shownId = activeInGroup ? activeId : group.ids[0];
  const entity = catalog[shownId];
  return (
    <div className={"roster-card" + (activeInGroup ? " active" : "")} title={shownId}
      role="button" tabIndex={0} onClick={() => onPick(shownId)}
      onKeyDown={(e) => e.key === "Enter" && onPick(shownId)}>
      <span className="roster-card-art thumb-stack" style={entity?.color ? { "--tint": entity.color } : undefined}>
        <AssembledThumb entity={entity} bust={bust} letter={group.name[0]?.toUpperCase()} className="" />
        {group.ids.length > 1 && <span className="roster-card-badge">×{group.ids.length}</span>}
      </span>
      <span className="roster-card-name">{group.name}</span>
      {activeInGroup && group.ids.length > 1 && (
        <div className="roster-variants">
          {group.ids.map((vid) => (
            <button key={vid} className={"roster-variant" + (vid === activeId ? " active" : "")}
              title={vid} onClick={(e) => { e.stopPropagation(); onPick(vid); }}>
              {variantLabel(vid, group)}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

// Chip label for a variant id: strip the segments every variant of the group
// shares (project prefix + the character: Cold_Matvey_mechanic → "mechanic");
// the main entity shows as «основной».
function variantLabel(vid, group) {
  if (vid === group.ids[0]) return "основной";
  const others = group.ids.filter((x) => x !== group.ids[0]).map((x) => x.split("_"));
  let common = 0;
  if (others.length > 1) {
    const first = others[0];
    while (common < first.length - 1 &&
      others.every((seg) => seg[common]?.toLowerCase() === first[common].toLowerCase())) common++;
  } else {
    // single variant: drop segments that also occur in the main id
    const main = new Set(group.ids[0].toLowerCase().split("_"));
    return vid.split("_").filter((s) => !main.has(s.toLowerCase())).join(" ") || vid;
  }
  return vid.split("_").slice(common).join(" ") || vid;
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
            src={bust ? l.url + "?v=" + bust : l.url}
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

function LookGroup({ label, hint, onRemove, onImport, children }) {
  return (
    <section className="look-group">
      <div className="look-head">
        <span className="look-label">{label}</span>
        {hint && <span className="look-hint">{hint}</span>}
        {onImport && <button className="look-remove" onClick={onImport} title="import frames from a GIF or spritesheet (→ per-frame images)">⊞</button>}
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
        {url && <img src={bust ? url + "?v=" + bust : url} alt="" style={empty ? { display: "none" } : undefined} onLoad={() => setEmpty(false)} onError={() => setEmpty(true)} />}
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

/* A Spine editor export in: three files + an id. The server stores them under
   content/spine/<id>/ and splices a kind:"spine" entity into the catalog. */
function SpineUpload({ token, notify, onDone, onCancel }) {
  const [id, setId] = useState("");
  const [name, setName] = useState("");
  const [auto, setAuto] = useState("Idle");
  const [files, setFiles] = useState({});
  const [busy, setBusy] = useState(false);
  const ok = id.trim() && files.json && files.atlas && files.texture;

  async function submit() {
    if (!ok || busy) return;
    setBusy(true);
    try {
      const r = await uploadSpine({ id: id.trim(), name: name.trim(), auto: auto.trim() }, files, token);
      notify(`Spine character “${r.id}” added — use it as actor id=${r.id}`, "ok");
      onDone(r.id);
    } catch (e) {
      notify("Spine upload failed: " + (e.message || e), "err");
      setBusy(false);
    }
  }


  return (
    <div className="sp-chooser" onClick={onCancel}>
      <div className="sp-chooser-box" onClick={(e) => e.stopPropagation()}>
        <h3>Add a Spine character</h3>
        <label className="adv-field"><span>Script id</span>
          <input className="field wide mono" value={id} onChange={(e) => setId(e.target.value)} placeholder="knight" />
        </label>
        <label className="adv-field"><span>Name (speaker label, optional)</span>
          <input className="field wide" value={name} onChange={(e) => setName(e.target.value)} placeholder="Рыцарь" />
        </label>
        <label className="adv-field"><span>Idle animation (auto-plays on show)</span>
          <input className="field wide mono" value={auto} onChange={(e) => setAuto(e.target.value)} placeholder="Idle" />
        </label>
        <SpineFile label="Skeleton (.json)" accept=".json" k="json" setFiles={setFiles} />
        <SpineFile label="Atlas (.atlas / .atlas.txt)" accept=".atlas,.txt" k="atlas" setFiles={setFiles} />
        <SpineFile label="Texture (.png)" accept=".png" k="texture" setFiles={setFiles} />
        <div style={{ display: "flex", gap: 8, marginTop: 10 }}>
          <button className="btn" disabled={!ok || busy} onClick={submit}>{busy ? "Uploading…" : "Add"}</button>
          <button className="btn-ghost" onClick={onCancel}>Cancel</button>
        </div>
        <div className="adv-note">Runtime side needs the spine-unity integration (see howto/EMBEDDING).</div>
      </div>
    </div>
  );
}

// Top-level on purpose: defined inside SpineUpload it would be a NEW component
// every render, remounting the input and losing the picked file.
function SpineFile({ label, accept, k, setFiles }) {
  return (
    <label className="adv-field">
      <span>{label}</span>
      <input type="file" accept={accept}
        onChange={(e) => setFiles((f) => ({ ...f, [k]: e.target.files[0] }))} />
    </label>
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
        <button className="sp-choice" onClick={() => onPick("spine")}>
          <b>🦴 A Spine character</b>
          <span>an export from the Spine editor — json + atlas + texture, real skeletal animation</span>
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

/* ── animation editor ──────────────────────────────────────────────────────── */

function AnimEditor(props) {
  const { ed, preview, onRemove, onUpdate, onAddTrack, onRemoveTrack, onUpdateTrack, onSyncFrames, onPreview } = props;
  const layers = [...new Set(ed.parts.map((p) => p.layerId).filter(Boolean))];
  const axes = Object.keys(ed.axes || {}).filter((a) => (ed.axes[a] || []).length);
  const anims = ed.anim || [];
  return (
    <div className="anim-editor enter">
      {/* The section header + empty state moved into the studio top bar
          ("+ Анимация" next to the name) — this editor only appears once an
          animation exists, so it needs no banner of its own. */}
      {anims.map((a) => {
        const playing = preview === a.key;
        return (
          <div className="anim-row" key={a.key}>
            <div className="anim-row-head">
              <input className="field anim-name" value={a.name}
                onChange={(e) => onUpdate(a.key, { name: e.target.value })} placeholder="idle" />
              <label className="anim-flag"><input type="checkbox" checked={a.loop}
                onChange={(e) => onUpdate(a.key, { loop: e.target.checked })} /> loop</label>
              <label className="anim-flag"><input type="checkbox" checked={a.auto}
                onChange={(e) => onUpdate(a.key, { auto: e.target.checked })} /> auto</label>
              <label className="anim-flag">dur
                <input className="field anim-num" type="number" min="0.05" step="0.05" value={a.duration}
                  onChange={(e) => onUpdate(a.key, { duration: parseFloat(e.target.value) || 0 })} />s</label>
              <button className={"btn-ghost sm" + (playing ? " on" : "")} onClick={() => onPreview(a.key)}>
                {playing ? "■ Stop" : "▶ Play"}
              </button>
              <button className="look-remove" onClick={() => onRemove(a.key)} title="remove animation">✕</button>
            </div>
            <div className="anim-tracks">
              {a.tracks.map((t) => (
                <AnimTrackRow key={t.key} anim={a} track={t} layers={layers} axes={axes}
                  onChange={(patchObj) => onUpdateTrack(a.key, t.key, patchObj)}
                  onRemove={() => onRemoveTrack(a.key, t.key)}
                  onSync={() => onSyncFrames(a.key, t.key)} />
              ))}
              <button className="opt-add-new" onClick={() => onAddTrack(a.key)}>＋<em>track</em></button>
            </div>
          </div>
        );
      })}
    </div>
  );
}

function AnimTrackRow({ track, layers, axes, onChange, onRemove, onSync }) {
  const isFrame = track.prop === "frame";
  const startV = track.keys[0]?.[1] ?? (isFrame ? "" : 1);
  const endV = track.keys[track.keys.length - 1]?.[1] ?? (isFrame ? "" : 1);
  const setTween = (which, val) => {
    const keys = track.keys.length >= 2 ? track.keys.map((k) => k.slice()) : [[0, 1], [1, 1]];
    keys[which === "start" ? 0 : keys.length - 1][1] = parseFloat(val);
    onChange({ keys });
  };
  return (
    <div className="anim-track">
      <select className="field anim-sel" value={track.prop} onChange={(e) => onChange({ prop: e.target.value })}>
        {TWEEN_PROPS.map((p) => <option key={p} value={p}>{p}</option>)}
      </select>
      <select className="field anim-sel" value={track.layer} onChange={(e) => onChange({ layer: e.target.value })}>
        <option value="">(whole actor)</option>
        {layers.map((l) => <option key={l} value={l}>{l}</option>)}
      </select>
      {isFrame ? (
        <>
          <select className="field anim-sel" value={track.axis} onChange={(e) => onChange({ axis: e.target.value })}>
            <option value="">axis…</option>
            {axes.map((a) => <option key={a} value={a}>{a}</option>)}
          </select>
          <button className="btn-ghost sm" onClick={onSync} title="rebuild frame keys evenly from the axis's values">
            ↻ {track.keys.length} frames
          </button>
        </>
      ) : (
        <span className="anim-tween">
          <input className="field anim-num" type="number" step="0.05" value={startV}
            onChange={(e) => setTween("start", e.target.value)} title="start value" />
          →
          <input className="field anim-num" type="number" step="0.05" value={endV}
            onChange={(e) => setTween("end", e.target.value)} title="end value" />
          <select className="field anim-sel" value={track.ease} onChange={(e) => onChange({ ease: e.target.value })}>
            {EASES.map((x) => <option key={x} value={x}>{x || "ease…"}</option>)}
          </select>
        </span>
      )}
      <button className="opt-mini" onClick={onRemove} title="remove track">✕</button>
    </div>
  );
}
