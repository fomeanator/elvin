import { useEffect, useMemo, useRef, useState } from "react";
import { getManifest, putAsset } from "../lib/api.js";
import ResizeHandle from "./ResizeHandle.jsx";

// Editor for the manifest's `ui` theme block — the data the engine maps onto
// every built-in screen: the in-game dialogue/choices (via VnThemeBuilder) and
// the shell (boot, loading, title, carousel, hud, name input). Two ways to edit:
// a schema-driven Form, or raw Code (the JSON of the ui block). A target picker
// switches between the global theme and any title's per-title override. Save (or
// Live auto-save) writes through the admin route and the running game restyles
// within one sync interval (~2s) — the same live loop as the script hot-reload.

// field kinds: c=color (#rrggbb[aa]), n=number, t=text, b=bool, s=select(options)
const SECTIONS = [
  { key: "dialogue", title: "Dialogue", preview: true, fields: [
    ["panel_color", "Panel", "c", "#0d0d14cc"], ["text_color", "Text", "c", "#f5f5f5"], ["speaker_color", "Speaker", "c", "#ffd166"],
    ["body_size", "Body size", "n", 34], ["speaker_size", "Speaker size", "n", 24], ["corner_radius", "Corner", "n", 12],
    ["chars_per_second", "Reveal cps", "n", 45], ["font", "Font (Resources)", "t", ""],
    ["nvl", "NVL mode", "b", false], ["nvl_top", "NVL top", "n", 0.12],
    // — placement: the universal popup —
    ["align", "Align", "s", "stretch", ["stretch", "center", "left", "right"]],
    ["x_percent", "Popup X %", "n", ""], ["y_percent", "Popup Y %", "n", ""],
    ["anchor", "Anchor", "s", "center", ["center", "bottom-center", "top-center", "top-left", "top-right", "bottom-left", "bottom-right", "left", "right"]],
    ["width_percent", "Width %", "n", ""], ["max_width_percent", "Max width %", "n", 80], ["max_height_percent", "Max height %", "n", ""],
    ["edge_padding", "Edge pad", "n", 24], ["bottom_padding", "Bottom pad", "n", 28], ["panel_min_height", "Min height", "n", 128],
    ["panel_padding_x", "Pad X", "n", 22], ["panel_padding_y", "Pad Y", "n", 18],
    ["panel_image", "Panel image url", "t", ""], ["name_image", "Name image url", "t", ""], ["panel_slice", "9-slice px", "n", 0],
  ]},
  { key: "choices", title: "Choices", preview: true, fields: [
    ["color", "Button", "c", "#1f1f29eb"], ["text_color", "Text", "c", "#f5f5f5"], ["cost_color", "Cost", "c", "#e6a33b"],
    ["font_size", "Font size", "n", 28], ["spacing", "Spacing", "n", 10], ["corner_radius", "Corner", "n", 10],
    // — placement —
    ["align", "Align", "s", "center", ["center", "left", "right"]],
    ["valign", "V-align", "s", "center", ["center", "top", "bottom"]],
    ["y_percent", "Stack Y %", "n", ""],
    ["min_width_percent", "Min width %", "n", 58], ["max_width_percent", "Max width %", "n", 86],
    ["padding_x", "Pad X", "n", 20], ["padding_y", "Pad Y", "n", 12],
    ["button_image", "Button image url", "t", ""], ["button_hover_image", "Hover image url", "t", ""], ["button_slice", "9-slice px", "n", 0],
  ]},
  { key: "boot", title: "Boot splash", fields: [
    ["bg_color", "Background", "c", "#0a0a0e"], ["bar_fill_color", "Bar fill", "c", "#c8a050"], ["bar_track_color", "Bar track", "c", "#ffffff22"],
    ["percent_color", "Percent", "c", "#cfc8bd"], ["show_percent", "Show percent", "b", true], ["min_seconds", "Min seconds", "n", 1.0],
  ]},
  { key: "loading", title: "Loading screen", fields: [
    ["bg_color", "Background", "c", "#000000"], ["scrim_color", "Scrim", "c", "#000000"], ["scrim_opacity", "Scrim opacity", "n", 0.65],
    ["bar_fill_color", "Bar fill", "c", "#c8a050"], ["bar_track_color", "Bar track", "c", "#ffffff22"],
    ["percent_color", "Percent", "c", "#ffffff"], ["hint_color", "Hint", "c", "#cfc8bd"],
    ["show_percent", "Show percent", "b", true], ["show_hint", "Show hint", "b", true], ["min_seconds", "Min seconds", "n", 0],
  ]},
  { key: "title", title: "Title card", fields: [
    ["chapter_color", "Chapter", "c", "#f4ecd8"], ["subtitle_color", "Subtitle", "c", "#cbb98f"],
    ["chapter_size", "Chapter size", "n", 64], ["subtitle_size", "Subtitle size", "n", 34],
    ["hold_seconds", "Hold sec", "n", 2.5], ["fade_seconds", "Fade sec", "n", 0.6],
  ]},
  { key: "carousel", title: "Title carousel", fields: [
    ["bg_color", "Background", "c", "#101015"], ["title_color", "Title", "c", "#f4ecd8"], ["subtitle_color", "Subtitle", "c", "#cbb98f"],
    ["title_size", "Title size", "n", 40], ["subtitle_size", "Subtitle size", "n", 22],
    ["play_text", "Play label", "t", "Play"], ["play_color", "Play text", "c", "#f4ecd8"], ["play_bg_color", "Play bg", "c", "#3a3a44"],
    ["dot_active_color", "Active dot", "c", "#f4ecd8"],
  ]},
  { key: "hud", title: "In-game HUD", fields: [
    ["bg_color", "Strip bg", "c", "#00000088"], ["height", "Height", "n", 0.07], ["progress_color", "Progress", "c", "#f4ecd8"],
    ["pill_bg_color", "Pill bg", "c", "#00000066"], ["pill_text_color", "Pill text", "c", "#f4ecd8"], ["show_progress", "Show progress", "b", true],
  ]},
  { key: "name_input", title: "Name input", fields: [
    ["bg_color", "Background", "c", "#101015"], ["prompt", "Prompt", "t", "Enter your name"], ["confirm_text", "Confirm", "t", "Confirm"],
    ["prompt_color", "Prompt", "c", "#cbb98f"], ["text_color", "Text", "c", "#f4ecd8"], ["max_length", "Max length", "n", 24],
  ]},
];

// native <input type=color> only speaks #rrggbb — strip to 6 digits for it,
// and on pick re-attach any alpha the field already had (#rrggbbaa).
function hex6(v) {
  const s = (v || "").replace("#", "");
  return "#" + (s.slice(0, 6).padEnd(6, "0")).toLowerCase();
}
function withPick(cur, picked) {
  const s = (cur || "").replace("#", "");
  const alpha = s.length === 8 ? s.slice(6, 8) : "";
  return picked.toLowerCase() + alpha;
}

function cssColor(hex) {
  if (!hex) return "transparent";
  const s = hex[0] === "#" ? hex.slice(1) : hex;
  if (s.length === 8) {
    const n = parseInt(s, 16);
    const r = (n >>> 24) & 255, g = (n >>> 16) & 255, b = (n >>> 8) & 255, a = (n & 255) / 255;
    return `rgba(${r},${g},${b},${a.toFixed(3)})`;
  }
  return "#" + s;
}

// keep only sections that carry real overrides (drop empty objects)
function prune(cfg) {
  const ui = {};
  for (const [k, v] of Object.entries(cfg || {})) {
    if (v && typeof v === "object" && !Array.isArray(v)) { if (Object.keys(v).length) ui[k] = v; }
    else if (v !== undefined && v !== null && v !== "") ui[k] = v;
  }
  return ui;
}

// read the ui block for a target ("global" | titleId) out of a manifest
function uiOf(manifest, target) {
  if (!manifest) return {};
  if (target === "global") return manifest.ui || {};
  const t = (manifest.titles || []).find((x) => x.id === target);
  return (t && t.ui) || {};
}

export default function ThemePanel({ token, notify, onClose, titleId }) {
  const [manifest, setManifest] = useState(null);
  const [titleName, setTitleName] = useState("");
  const [scope, setScope] = useState(titleId ? "title" : "global"); // "title" (open novel) | "global"
  const [cfg, setCfg] = useState({});               // { section: { field: value } } — overrides only
  const [open, setOpen] = useState("dialogue");
  const [mode, setMode] = useState("form");          // "form" | "code"
  const [code, setCode] = useState("{}");
  const [codeErr, setCodeErr] = useState("");
  const [live, setLive] = useState(false);
  const [busy, setBusy] = useState(false);

  const edited = useRef(false);   // true only after a user edit → gates live auto-save
  const liveTimer = useRef(0);

  // edit target: the open novel's per-title ui, or the global manifest ui
  const target = (scope === "global" || !titleId) ? "global" : titleId;

  // initial load
  useEffect(() => {
    getManifest().then((m) => {
      setManifest(m);
      const t = (m && m.titles) ? m.titles.find((x) => x.id === titleId) : null;
      setTitleName((t && (t.name || t.id)) || titleId || "");
    }).catch(() => {});
  }, [titleId]);

  // (re)load cfg whenever the manifest arrives or the target changes
  useEffect(() => {
    if (!manifest) return;
    const ui = uiOf(manifest, target);
    setCfg(structuredClone(ui));
    edited.current = false;
  }, [manifest, target]);

  // seed the code editor from cfg when entering code mode or switching target
  useEffect(() => {
    if (mode === "code") { setCode(JSON.stringify(cfg, null, 2)); setCodeErr(""); }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode, target]);

  // live auto-save: debounce a save after every user edit
  useEffect(() => {
    if (!live || !edited.current) return;
    clearTimeout(liveTimer.current);
    liveTimer.current = setTimeout(() => { doSave(cfg, true); }, 500);
    return () => clearTimeout(liveTimer.current);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cfg, live]);

  const set = (sec, key, v) => {
    edited.current = true;
    setCfg((c) => {
      const next = { ...c, [sec]: { ...(c[sec] || {}) } };
      if (v === "" || v === null || v === undefined) delete next[sec][key];
      else next[sec][key] = v;
      if (!Object.keys(next[sec]).length) delete next[sec];
      return next;
    });
  };

  function onCode(v) {
    setCode(v);
    try { const o = JSON.parse(v); setCodeErr(""); edited.current = true; setCfg(o); }
    catch (e) { setCodeErr(e.message); }
  }

  const defs = useMemo(() => {
    const d = {};
    for (const s of SECTIONS) { d[s.key] = {}; for (const [k, , , def] of s.fields) d[s.key][k] = def; }
    return d;
  }, []);
  const raw = (sec, key) => (cfg[sec] && cfg[sec][key] !== undefined ? cfg[sec][key] : "");
  const eff = (sec, key) => { const r = cfg[sec] && cfg[sec][key]; return r !== undefined ? r : defs[sec][key]; };

  async function doSave(current, isLive) {
    if (!token) { notify && notify("Set the admin token first", "err"); return; }
    setBusy(true);
    try {
      const m = await getManifest();              // fresh — never clobber script/chapter edits
      const ui = prune(current);
      if (target === "global") {
        if (Object.keys(ui).length) m.ui = ui; else delete m.ui;
      } else {
        const t = (m.titles || []).find((x) => x.id === target);
        if (!t) { notify && notify("✗ Title not found: " + target, "err"); return; }
        if (Object.keys(ui).length) t.ui = ui; else delete t.ui;
      }
      await putAsset("manifest.json", JSON.stringify(m, null, 2), token, "application/json");
      edited.current = false;
      if (!isLive) notify && notify("Theme saved — live in ~2s ▸", "ok");
    } catch (e) {
      notify && notify("✗ Save failed: " + e.message, "err");
    } finally {
      setBusy(false);
    }
  }

  const d = (k) => eff("dialogue", k), ch = (k) => eff("choices", k);
  const dirtyCount = (sec) => (cfg[sec] ? Object.keys(cfg[sec]).length : 0);

  return (
    <aside className="docs enter">
      <ResizeHandle storageKey="ide-w-theme" />
      <div className="docs-head">
        <h2>Theme</h2>
        <button className="btn-ghost sm" onClick={onClose}>✕</button>
      </div>

      {/* scope + edit-mode + live row */}
      <div className="thm-bar">
        <div className="thm-modes thm-scope">
          {titleId && (
            <button className={"vbtn sm" + (scope === "title" ? " active" : "")} onClick={() => setScope("title")} title={titleName}>This title</button>
          )}
          <button className={"vbtn sm" + (scope === "global" ? " active" : "")} onClick={() => setScope("global")}>★ Global</button>
        </div>
        <div className="thm-modes">
          <button className={"vbtn sm" + (mode === "form" ? " active" : "")} onClick={() => setMode("form")}>Form</button>
          <button className={"vbtn sm" + (mode === "code" ? " active" : "")} onClick={() => setMode("code")}>Code</button>
        </div>
        <label className="thm-live" title="Auto-save every edit — the game restyles within ~2s">
          <input type="checkbox" checked={live} onChange={(e) => setLive(e.target.checked)} /> Live
        </label>
      </div>

      <p className="docs-lede">
        {target === "global"
          ? "Global look for every title — dialogue, choices and the shell."
          : `Per-title override for “${titleName}”, layered over the global theme.`}
        {" "}Saved to the manifest's <code>ui</code> block; the engine applies it live.
      </p>

      {mode === "code" ? (
        <div className="thm-code">
          <textarea
            className="field mono thm-code-area"
            spellCheck={false}
            value={code}
            onChange={(e) => onCode(e.target.value)}
            placeholder='{ "dialogue": { "align": "center", "x_percent": 50 } }'
          />
          {codeErr
            ? <div className="thm-code-err">⚠ {codeErr}</div>
            : <div className="thm-code-ok">valid JSON · maps to <code>{target === "global" ? "ui" : "titles[].ui"}</code></div>}
        </div>
      ) : (
        <>
          {/* live preview of the in-game surface */}
          <div className="thm-preview">
            <div className="thm-choice" style={{ background: cssColor(ch("color")), color: cssColor(ch("text_color")), borderRadius: ch("corner_radius") + "px", fontSize: (ch("font_size") || 28) * 0.5 + "px" }}>
              A themed choice <span style={{ color: cssColor(ch("cost_color")) }}>· 50 gold</span>
            </div>
            <div className="thm-name" style={{ background: cssColor(d("panel_color")), color: cssColor(d("speaker_color")), fontSize: (d("speaker_size") || 24) * 0.5 + "px", borderRadius: (d("corner_radius") || 12) * 0.6 + "px" }}>Mara</div>
            <div className="thm-box" style={{ background: cssColor(d("panel_color")), color: cssColor(d("text_color")), fontSize: (d("body_size") || 34) * 0.5 + "px", borderRadius: (d("corner_radius") || 12) + "px", minHeight: eff("dialogue", "nvl") ? "90px" : "44px" }}>
              You came back. I wasn't sure you would…{eff("dialogue", "nvl") ? " (NVL)" : ""}
            </div>
          </div>

          {SECTIONS.map((s) => (
            <div key={s.key} className="thm-sec">
              <button className={"thm-sec-head" + (open === s.key ? " open" : "")} onClick={() => setOpen(open === s.key ? "" : s.key)}>
                <span>{open === s.key ? "▾" : "▸"}</span> {s.title}
                {dirtyCount(s.key) > 0 && <em className="thm-dirty">{dirtyCount(s.key)}</em>}
              </button>
              {open === s.key && (
                <div className="thm-form">
                  {s.fields.map(([k, label, type, , options]) => (
                    <label key={k} className={"thm-row" + (type === "b" ? " thm-row-bool" : "")}>
                      <span>{label}</span>
                      {type === "c" ? (
                        <span className="thm-color">
                          <input type="color" className="thm-swatch" value={hex6(eff(s.key, k))}
                            onChange={(e) => set(s.key, k, withPick(eff(s.key, k), e.target.value))}
                            title="Pick a colour" />
                          <input className="field mono" value={raw(s.key, k)} placeholder={defs[s.key][k]} onChange={(e) => set(s.key, k, e.target.value)} />
                        </span>
                      ) : type === "n" ? (
                        <input className="field mono thm-num" type="number" value={raw(s.key, k)} placeholder={String(defs[s.key][k])}
                          onChange={(e) => set(s.key, k, e.target.value === "" ? "" : Number(e.target.value))} />
                      ) : type === "b" ? (
                        <input type="checkbox" checked={!!eff(s.key, k)} onChange={(e) => set(s.key, k, e.target.checked)} />
                      ) : type === "s" ? (
                        <select className="field thm-sel" value={raw(s.key, k)} onChange={(e) => set(s.key, k, e.target.value)}>
                          <option value="">(default {String(defs[s.key][k])})</option>
                          {(options || []).map((o) => <option key={o} value={o}>{o}</option>)}
                        </select>
                      ) : (
                        <input className="field" value={raw(s.key, k)} placeholder={defs[s.key][k]} onChange={(e) => set(s.key, k, e.target.value)} />
                      )}
                    </label>
                  ))}
                </div>
              )}
            </div>
          ))}
        </>
      )}

      <button className="btn btn-primary wide-btn thm-save" onClick={() => doSave(cfg, false)} disabled={busy || (mode === "code" && !!codeErr)}>
        {busy ? "Saving…" : live ? "Live ● — Save now ▸" : "Save theme to manifest ▸"}
      </button>
    </aside>
  );
}
