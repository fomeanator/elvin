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
    const part = {
      key: uid(),
      name: lay.name || (lay.url ? deriveName(lay.url) : ""),
      axis: lay.axis != null ? lay.axis : tokenOf(lay.url),
      when: lay.when || null,
      url: lay.url || "",
    };
    if (!part.url) part.url = partPath(part, id || slug(e.name));
    return part;
  });
  // ensure axes referenced by tokens exist
  parts.forEach((p) => { if (p.axis && !axes[p.axis]) axes[p.axis] = []; });
  return { id: id || "", name: e.name || "", color: e.color || "", axes, picked, parts };
}

// Editor state -> catalog entity (manifest shape).
export function toEntity(ed) {
  const layers = ed.parts
    .filter((p) => p.url)
    .map((p) => (p.when ? { url: p.url, when: p.when } : p.url));
  const out = { layers };
  if (ed.name) out.name = ed.name;
  if (ed.color) out.color = ed.color;
  const axes = {};
  Object.keys(ed.axes).forEach((k) => { if ((ed.axes[k] || []).length) axes[k] = ed.axes[k]; });
  if (Object.keys(axes).length) out.axes = axes;
  const defaults = {};
  Object.keys(ed.picked).forEach((k) => { if (ed.picked[k]) defaults[k] = ed.picked[k]; });
  if (Object.keys(defaults).length) out.defaults = defaults;
  return out;
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
