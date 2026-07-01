// Pure helpers for the sprite/entity model. An entity is a catalog id the
// manifest & scripts reference; the client resolves it to layer urls and
// composites them. A "part" is the editor's friendly view of one layer.

let _uid = 0;
export const uid = () => "p" + ++_uid;

export function slug(s) {
  return (s || "").trim().toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "") || "part";
}

export function deriveName(path) {
  const f = (path || "").split("/").pop() || "";
  return f.replace(/_?\{[^}]+\}/, "").replace(/\.[^.]+$/, "");
}

export function tokenOf(url) {
  const m = (url || "").match(/\{([^}]+)\}/);
  return m ? m[1] : "";
}

// Build a part's file path from the entity id + part name + the axis it varies
// by. Keeps a hand-authored folder/extension; otherwise follows /content/sprites/<id>/.
export function partPath(part, entityId) {
  const id = slug(entityId) || "entity";
  const name = slug(part.name);
  const cur = part.url || "";
  let dir = "/content/sprites/" + id;
  let ext = ".png";
  const slash = cur.lastIndexOf("/");
  if (slash > 0 && !cur.startsWith("/content/sprites/")) {
    dir = cur.slice(0, slash);
    const dot = cur.lastIndexOf(".");
    if (dot > slash) ext = cur.slice(dot);
  }
  return dir + "/" + name + (part.axis ? "_{" + part.axis + "}" : "") + ext;
}

// Catalog entity (or a guided template) -> editor state.
export function toEditor(entity, id) {
  const e = entity || { layers: [{}] };
  const axes = {};
  Object.keys(e.axes || {}).forEach((k) => (axes[k] = (e.axes[k] || []).slice()));
  const picked = { ...(e.defaults || {}) };
  const source = e.parts || (e.layers && e.layers.length ? e.layers : [{}]);
  const parts = source.map((l) => {
    const lay = typeof l === "string" ? { url: l } : l;
    const name = lay.name || (lay.url ? deriveName(lay.url) : "");
    const part = {
      key: uid(),
      name,
      // layer id (targeted by frame/tween tracks); default to the part name.
      layerId: lay.id || slug(name),
      axis: lay.axis != null ? lay.axis : tokenOf(lay.url),
      when: lay.when || null,
      url: lay.url || "",
    };
    if (!part.url) part.url = partPath(part, id || slug(e.name));
    return part;
  });
  // ensure axes referenced by tokens exist
  parts.forEach((p) => { if (p.axis && !axes[p.axis]) axes[p.axis] = []; });
  return {
    id: id || "", name: e.name || "", color: e.color || "",
    kind: e.kind || "", axes, picked, parts, anim: animToEditor(e.anim),
  };
}

// Editor state -> catalog entity (manifest shape).
export function toEntity(ed) {
  const anim = animToEntity(ed.anim);
  // Layers carry an `id` only when a track needs to target them (keeps the common
  // static entity's layers as plain url strings).
  const targeted = new Set();
  (ed.anim || []).forEach((a) => (a.tracks || []).forEach((t) => t.layer && targeted.add(t.layer)));
  const layers = ed.parts
    .filter((p) => p.url)
    .map((p) => {
      const needId = targeted.has(p.layerId);
      if (!p.when && !needId) return p.url;
      const o = { url: p.url };
      if (needId) o.id = p.layerId;
      if (p.when) o.when = p.when;
      return o;
    });
  const out = { layers };
  if (ed.name) out.name = ed.name;
  if (ed.color) out.color = ed.color;
  if (ed.kind) out.kind = ed.kind;
  const axes = {};
  Object.keys(ed.axes).forEach((k) => { if ((ed.axes[k] || []).length) axes[k] = ed.axes[k]; });
  if (Object.keys(axes).length) out.axes = axes;
  const defaults = {};
  Object.keys(ed.picked).forEach((k) => { if (ed.picked[k]) defaults[k] = ed.picked[k]; });
  if (Object.keys(defaults).length) out.defaults = defaults;
  if (anim && Object.keys(anim).length) out.anim = anim;
  return out;
}

// ── animation round-trip ─────────────────────────────────────────────────────
// Editor keeps anims as an ordered array (stable UI); the manifest stores a
// name→def map. A track is { layer, prop, axis?, ease?, interp?, keys:[[t,v],…] }.

function animToEditor(anim) {
  if (!anim || typeof anim !== "object") return [];
  return Object.keys(anim).map((name) => {
    const a = anim[name] || {};
    return {
      key: uid(),
      name,
      loop: !!a.loop,
      auto: a.auto === true || a.auto === "true",
      duration: typeof a.duration === "number" ? a.duration : 1,
      tracks: (a.tracks || []).map((t) => ({
        key: uid(),
        layer: t.layer || "",
        prop: t.prop || "frame",
        axis: t.axis || "",
        ease: t.ease || "",
        interp: t.interp || "",
        keys: (t.keys || []).map((k) => [k[0], k[1]]),
      })),
    };
  });
}

function animToEntity(list) {
  const out = {};
  (list || []).forEach((a) => {
    if (!a.name || !(a.tracks || []).length) return;
    const def = { loop: !!a.loop, duration: Number(a.duration) || 1 };
    if (a.auto) def.auto = "true";
    def.tracks = a.tracks
      .filter((t) => t.prop && (t.keys || []).length)
      .map((t) => {
        const tr = { prop: t.prop };
        if (t.layer) tr.layer = t.layer;
        if (t.prop === "frame" && t.axis) tr.axis = t.axis;
        if (t.ease) tr.ease = t.ease;
        if (t.interp) tr.interp = t.interp;
        tr.keys = t.keys.map((k) => [Number(k[0]) || 0, t.prop === "frame" ? String(k[1]) : Number(k[1])]);
        return tr;
      });
    if (def.tracks.length) out[a.name] = def;
  });
  return out;
}

// Build evenly-timed frame keys for an axis's values over a total duration.
export function framesFromAxis(values, duration) {
  const vals = (values || []).filter(Boolean);
  if (!vals.length) return [];
  const d = Number(duration) || vals.length * 0.1;
  const step = vals.length > 1 ? d / vals.length : d;
  return vals.map((v, i) => [Math.round(i * step * 1000) / 1000, v]);
}

// Fill {axis} tokens from the picked preview state. Returns null if unresolved.
export function fill(url, picked) {
  let missing = false;
  const out = (url || "").replace(/\{([^}]+)\}/g, (_, k) => {
    const v = picked[k];
    if (!v) { missing = true; return "{" + k + "}"; }
    return v;
  });
  return missing ? null : out;
}

export const TEMPLATES = {
  simple: { parts: [{ name: "image" }] },
  character: {
    axes: { pose: ["standing"], emotion: ["neutral", "happy", "sad"] },
    defaults: { pose: "standing", emotion: "neutral" },
    parts: [
      { name: "body", axis: "pose" },
      { name: "face", axis: "emotion" },
    ],
  },
};

export const PRESETS = {
  pose: ["standing", "sitting"],
  emotion: ["neutral", "happy", "sad", "angry"],
};
