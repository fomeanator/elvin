import { useCallback, useEffect, useRef, useState } from "react";
import { adminHistory, adminRollback } from "../lib/api.js";

// Shared plumbing for every admin-dashboard tab: the async-load hook, the
// debounced token, load-state rendering, formatting, and the file version
// history / rollback card (used by configs and the manifest alike).

export const fmt = (n) => Number(n || 0).toLocaleString("ru-RU");
export const dt = (s, n = 19) => String(s || "").slice(0, n).replace("T", " ");

export const authMsg = (e) =>
  (e && e.message) === "401"
    ? "Токен не подошёл (401) — проверьте admin-токен."
    : (e && e.message) || "ошибка";

// useDebounced trails a fast-changing value (the token being typed) by `ms`,
// so data loads fire once per pause instead of once per keystroke.
export function useDebounced(value, ms) {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

// useAsync runs a loader on mount and whenever a dep changes, tracking
// data/error/loading and exposing a manual reload — the shared fetch shape for
// every panel. Every run (effect OR manual reload) bumps a request id; a
// response only lands if its id is still current, so a slow stale response can
// never overwrite fresher data (e.g. user A's wallet over user B's card).
export function useAsync(loader, deps) {
  const [state, setState] = useState({ loading: true, error: "", data: null });
  const reqRef = useRef(0);
  const reload = useCallback(() => {
    const req = ++reqRef.current;
    const current = () => reqRef.current === req;
    setState((s) => ({ ...s, loading: true, error: "" }));
    loader()
      .then((data) => current() && setState({ loading: false, error: "", data }))
      .catch((e) => current() && setState({ loading: false, error: authMsg(e), data: null }));
    return () => { if (current()) reqRef.current++; }; // effect cleanup: cancel this run
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  useEffect(reload, [reload]);
  return { ...state, reload };
}

export function Status({ loading, error }) {
  if (loading) return <p className="admin-empty">Загрузка…</p>;
  if (error) return <p className="admin-empty err">{error}</p>;
  return null;
}

// HistoryPanel lists a file's saved versions with one-click rollback. Every
// save snapshots the previous version server-side, and a rollback is itself
// versioned — so nothing here is destructive.
export function HistoryPanel({ file, token, notify, onRolledBack }) {
  const { loading, error, data } = useAsync(() => adminHistory(file, token), [file, token]);
  const versions = (data && data.versions) || [];

  async function rollback(ts) {
    const when = new Date(Number(ts)).toLocaleString("ru-RU");
    if (!window.confirm(`Откатить ${file} на версию от ${when}?`)) return;
    try {
      await adminRollback(file, ts, token);
      notify("Откатено — клиенты подхватят сами", "ok");
      if (onRolledBack) onRolledBack();
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
  }

  return (
    <div className="admin-history">
      <h3>История: {file}</h3>
      {file === "manifest.json" && (
        <p className="admin-empty">Откат применяется к ОПУБЛИКОВАННОМУ манифесту (черновик не трогает).</p>
      )}
      <Status loading={loading} error={error} />
      {!loading && !error && !versions.length && (
        <p className="admin-empty">Пока пусто — версии появляются при каждом сохранении.</p>
      )}
      {versions.length > 0 && (
        <table className="admin-table">
          <thead><tr><th>сохранено</th><th>размер</th><th></th></tr></thead>
          <tbody>
            {versions.map((v) => (
              <tr key={v.ts}>
                <td>{new Date(Number(v.ts)).toLocaleString("ru-RU")}</td>
                <td className="muted">{fmt(v.size)} b</td>
                <td><button className="btn-ghost sm" onClick={() => rollback(v.ts)}>Откатить на эту</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
