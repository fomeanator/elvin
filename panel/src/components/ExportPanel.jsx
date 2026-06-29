import { useState } from "react";
import ResizeHandle from "./ResizeHandle.jsx";

// Export the current novel as a ready-to-open Unity project (.zip). The game
// runs in "online" mode: it loads its content from the server URL below — the
// same backend the panel writes to — so keep that server reachable for players.
export default function ExportPanel({ defaultName, notify, onClose }) {
  const [cfg, setCfg] = useState({
    name: defaultName || "My Novel",
    company: "",
    bundleId: "",
    serverUrl: "",
    askName: false,
    offline: true,
  });
  const [busy, setBusy] = useState(false);
  const set = (k, v) => setCfg((c) => ({ ...c, [k]: v }));

  async function download() {
    setBusy(true);
    try {
      const res = await fetch("/v1/export", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(cfg),
      });
      if (!res.ok) throw new Error(await res.text() || res.statusText);
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = (cfg.name || "game").replace(/[^A-Za-z0-9_.-]+/g, "") + ".zip";
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      notify && notify("Unity project downloaded ▸", "ok");
    } catch (e) {
      notify && notify("✗ Export failed: " + e.message, "err");
    } finally {
      setBusy(false);
    }
  }

  return (
    <aside className="docs enter">
      <ResizeHandle storageKey="ide-w-export" />
      <div className="docs-head">
        <h2>Export game</h2>
        <button className="btn-ghost sm" onClick={onClose}>✕</button>
      </div>

      <p className="docs-lede">
        Download a complete <strong>Unity project</strong>. Open it in Unity and
        hit <em>Build</em> to make a desktop/mobile game. The engine is pulled
        automatically.
      </p>

      <div className="export-mode">
        <button className={"export-tab" + (cfg.offline ? " on" : "")} onClick={() => set("offline", true)}>
          ▣ Offline <small>self-contained</small>
        </button>
        <button className={"export-tab" + (!cfg.offline ? " on" : "")} onClick={() => set("offline", false)}>
          ☁ Online <small>talks to server</small>
        </button>
      </div>

      <div className="export-form">
        <label className="export-row">
          <span>Game name</span>
          <input className="field" value={cfg.name} onChange={(e) => set("name", e.target.value)} placeholder="My Novel" />
        </label>
        <label className="export-row">
          <span>Company</span>
          <input className="field" value={cfg.company} onChange={(e) => set("company", e.target.value)} placeholder="LvnStudio" />
        </label>
        <label className="export-row">
          <span>Bundle id</span>
          <input className="field mono" value={cfg.bundleId} onChange={(e) => set("bundleId", e.target.value)} placeholder="com.studio.novel" />
        </label>
        {!cfg.offline && (
          <label className="export-row">
            <span>Server URL</span>
            <input className="field mono" value={cfg.serverUrl} onChange={(e) => set("serverUrl", e.target.value)} placeholder="https://your-server.com" />
          </label>
        )}
        <label className="export-check">
          <input type="checkbox" checked={cfg.askName} onChange={(e) => set("askName", e.target.checked)} />
          <span>Ask the player for a name on first launch</span>
        </label>
      </div>

      <p className="export-note">
        {cfg.offline ? (
          <>The novel is <strong>baked into the game</strong> (StreamingAssets) — it
          runs with no server, ready to share. Re-export to update content.</>
        ) : (
          <>The game loads its novel from <strong>Server URL</strong> at runtime.
          Leave it blank to point at this server. Keep the server reachable;
          edits here update live.</>
        )}
      </p>

      <button className="btn btn-primary wide-btn" onClick={download} disabled={busy}>
        {busy ? "Packing…" : "⤓ Download Unity project (.zip)"}
      </button>
    </aside>
  );
}
