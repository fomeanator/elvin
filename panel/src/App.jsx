import { useCallback, useEffect, useRef, useState } from "react";
import TopBar from "./components/TopBar.jsx";
import LibraryHome from "./components/LibraryHome.jsx";
import SpritesView from "./components/SpritesView.jsx";
import ScriptSection from "./components/ScriptSection.jsx";

// Navigation is hierarchical: a Home that lists/adds novels (manifest titles),
// and — once you open a novel — a workspace with its Characters and its Script.
export default function App() {
  const [titleId, setTitleId] = useState(null);   // null = Home
  const [titleName, setTitleName] = useState("");
  const [section, setSection] = useState("characters"); // "characters" | "script"

  const [path, setPath] = useState(() => localStorage.getItem("lvn_save_path") || "scripts/ch1.lvn");
  const [token, setToken] = useState(() => localStorage.getItem("lvn_admin_token") || "");
  const [status, setStatus] = useState({ kind: "success", text: "Ready" });
  const [toast, setToast] = useState({ text: "", kind: "", show: false });
  const toastTimer = useRef(0);

  useEffect(() => localStorage.setItem("lvn_save_path", path), [path]);
  useEffect(() => localStorage.setItem("lvn_admin_token", token), [token]);

  const notify = useCallback((text, kind = "") => {
    setToast({ text, kind, show: true });
    clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToast((t) => ({ ...t, show: false })), 4200);
  }, []);

  const creds = { path, setPath, token, setToken };

  const openNovel = useCallback((id, name) => { setTitleId(id); setTitleName(name || id); setSection("characters"); }, []);
  const goHome = useCallback(() => setTitleId(null), []);

  const nav = { titleId, titleName, section, setSection, goHome };

  return (
    <div className="app">
      <TopBar nav={nav} status={status} creds={creds} />
      <div className="workspace">
        {titleId == null && (
          <LibraryHome creds={creds} notify={notify} onOpen={openNovel} />
        )}
        {titleId != null && section === "characters" && (
          <SpritesView creds={creds} notify={notify} titleId={titleId} />
        )}
        {titleId != null && section === "script" && (
          <ScriptSection creds={creds} notify={notify} titleId={titleId} setStatus={setStatus} />
        )}
      </div>
      <div className={"save-status " + (toast.kind ? toast.kind + " " : "") + (toast.show ? "show" : "")}>
        {toast.text}
      </div>
    </div>
  );
}
