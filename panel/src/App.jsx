import { useCallback, useEffect, useRef, useState } from "react";
import TopBar from "./components/TopBar.jsx";
import LibraryHome from "./components/LibraryHome.jsx";
import SpritesView from "./components/SpritesView.jsx";
import ScriptSection from "./components/ScriptSection.jsx";
import AdminView from "./components/AdminView.jsx";
import { getManifest } from "./lib/api.js";

// Navigation is hierarchical: a Home that lists/adds novels (manifest titles),
// and — once you open a novel — a workspace with its Characters and its Script.
//
// The screen LIVES IN THE URL hash (#/, #/admin, #/novel/<id>/<section>): a
// reload lands you exactly where you were, deep links are shareable, and the
// browser back/forward buttons walk the app history.
const parseHash = () => {
  const h = window.location.hash.replace(/^#\/?/, "");
  const seg = h.split("/").map(decodeURIComponent);
  if (seg[0] === "admin") return { mode: "admin", titleId: null, section: "characters" };
  if (seg[0] === "novel" && seg[1])
    return { mode: "studio", titleId: seg[1], section: seg[2] === "script" ? "script" : "characters" };
  return { mode: "studio", titleId: null, section: "characters" };
};
const toHash = (mode, titleId, section) =>
  mode === "admin" ? "#/admin"
    : titleId != null ? `#/novel/${encodeURIComponent(titleId)}/${section}`
    : "#/";

export default function App() {
  // ?admin=1 forces the dashboard (the server redirects the retired /admin/
  // page here with it); otherwise the hash rules, falling back to Home.
  const initial = useRef(
    new URLSearchParams(window.location.search).has("admin")
      ? { mode: "admin", titleId: null, section: "characters" }
      : parseHash());
  const [mode, setMode] = useState(initial.current.mode);
  const [titleId, setTitleId] = useState(initial.current.titleId);
  const [titleName, setTitleName] = useState("");
  const [section, setSection] = useState(initial.current.section);

  const [path, setPath] = useState(() => localStorage.getItem("lvn_save_path") || "scripts/ch1.lvn");
  const [token, setToken] = useState(() => localStorage.getItem("lvn_admin_token") || "");
  const [status, setStatus] = useState({ kind: "success", text: "Ready" });
  // Toasts stack bottom-right (max 3, newest last), each self-dismissing —
  // rapid-fire notifications (bulk uploads, grants) no longer overwrite each other.
  const [toasts, setToasts] = useState([]);
  const toastSeq = useRef(0);

  useEffect(() => localStorage.setItem("lvn_save_path", path), [path]);
  useEffect(() => localStorage.setItem("lvn_admin_token", token), [token]);

  // State → URL. replaceState when the hash already matches (initial mount,
  // popstate echo), pushState on real navigation — so back/forward just work.
  useEffect(() => {
    const want = toHash(mode, titleId, section);
    if (window.location.hash === want) return;
    window.history.pushState(null, "", want);
  }, [mode, titleId, section]);

  // URL → state (back/forward, hand-edited hash).
  useEffect(() => {
    const onPop = () => {
      const s = parseHash();
      setMode(s.mode); setTitleId(s.titleId); setSection(s.section);
    };
    window.addEventListener("popstate", onPop);
    window.addEventListener("hashchange", onPop);
    return () => { window.removeEventListener("popstate", onPop); window.removeEventListener("hashchange", onPop); };
  }, []);

  // A deep-linked novel knows its id but not its display name — resolve it
  // from the manifest so the top bar doesn't greet a reload with a raw slug.
  useEffect(() => {
    if (titleId == null || titleName) return;
    let dead = false;
    getManifest()
      .then((m) => {
        if (dead) return;
        const t = (m.titles || []).find((x) => x.id === titleId);
        if (t && t.name) setTitleName(t.name);
      })
      .catch(() => {});
    return () => { dead = true; };
  }, [titleId, titleName]);

  const notify = useCallback((text, kind = "") => {
    const id = ++toastSeq.current;
    setToasts((ts) => [...ts.slice(-2), { id, text, kind }]);
    setTimeout(() => setToasts((ts) => ts.filter((t) => t.id !== id)), 4200);
  }, []);

  const creds = { path, setPath, token, setToken };

  const openNovel = useCallback((id, name) => { setTitleId(id); setTitleName(name || id); setSection("characters"); }, []);
  const goHome = useCallback(() => { setTitleId(null); setTitleName(""); }, []);

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
