import { useCallback, useEffect, useRef, useState } from "react";
import TopBar from "./components/TopBar.jsx";
import LibraryHome from "./components/LibraryHome.jsx";
import SpritesView from "./components/SpritesView.jsx";
import ScriptSection from "./components/ScriptSection.jsx";
import AdminView from "./components/AdminView.jsx";

// Navigation is hierarchical: a Home that lists/adds novels (manifest titles),
// and — once you open a novel — a workspace with its Characters and its Script.
export default function App() {
  // "studio" (authoring IDE) | "admin" (dashboard) — persisted so a reload
  // drops you back where you worked.
  const [mode, setMode] = useState(() => localStorage.getItem("lvn_mode") || "studio");
  useEffect(() => localStorage.setItem("lvn_mode", mode), [mode]);
  const [titleId, setTitleId] = useState(null);   // null = Home
  const [titleName, setTitleName] = useState("");
  const [section, setSection] = useState("characters"); // "characters" | "script"

  const [path, setPath] = useState(() => localStorage.getItem("lvn_save_path") || "scripts/ch1.lvn");
  const [token, setToken] = useState(() => localStorage.getItem("lvn_admin_token") || "");
  const [status, setStatus] = useState({ kind: "success", text: "Ready" });
  // Toasts stack bottom-right (max 3, newest last), each self-dismissing —
  // rapid-fire notifications (bulk uploads, grants) no longer overwrite each other.
  const [toasts, setToasts] = useState([]);
  const toastSeq = useRef(0);

  useEffect(() => localStorage.setItem("lvn_save_path", path), [path]);
  useEffect(() => localStorage.setItem("lvn_admin_token", token), [token]);

  const notify = useCallback((text, kind = "") => {
    const id = ++toastSeq.current;
    setToasts((ts) => [...ts.slice(-2), { id, text, kind }]);
    setTimeout(() => setToasts((ts) => ts.filter((t) => t.id !== id)), 4200);
  }, []);

  const creds = { path, setPath, token, setToken };

  const openNovel = useCallback((id, name) => { setTitleId(id); setTitleName(name || id); setSection("characters"); }, []);
  const goHome = useCallback(() => setTitleId(null), []);

  const nav = { mode, setMode, titleId, titleName, section, setSection, goHome };

  return (
    <div className="app">
      <TopBar nav={nav} status={status} creds={creds} />
      <div className="workspace">
        {mode === "admin" ? (
          <AdminView creds={creds} notify={notify} />
        ) : (
          <>
            {titleId == null && (
              <LibraryHome creds={creds} notify={notify} onOpen={openNovel} />
            )}
            {titleId != null && section === "characters" && (
              <SpritesView creds={creds} notify={notify} titleId={titleId} />
            )}
            {titleId != null && section === "script" && (
              <ScriptSection creds={creds} notify={notify} titleId={titleId} setStatus={setStatus} />
            )}
          </>
        )}
      </div>
      <div className="toast-stack">
        {toasts.map((t) => (
          <div key={t.id} className={"save-status show stacked " + (t.kind || "")}>{t.text}</div>
        ))}
      </div>
    </div>
  );
}
