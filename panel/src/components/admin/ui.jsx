import { createContext, useContext, useEffect, useRef, useState } from "react";

// AdmShell carries shell-level controls (the sidebar toggle) down to every
// Page header without threading props through each section component.
export const AdmShell = createContext({ toggleSidebar: null });

// Shared redesign primitives for the admin: page scaffold, right-side drawer,
// loading skeletons and empty states. Pure presentational — data hooks stay in
// adminShared.jsx.

// Page: the content column of one section — a slim 48px header bar (title +
// count left, actions right; the dense-screen Mode A from the UX spec) over
// the scrollable body. A description, when given, is the first content line.
export function Page({ title, count, description, actions, children }) {
  const { toggleSidebar: onToggleSidebar } = useContext(AdmShell);
  return (
    <div className="adm-page">
      <header className="adm-pagehead">
        {onToggleSidebar && (
          <>
            <button className="adm-iconbtn ghost" onClick={onToggleSidebar} title="свернуть/развернуть меню (Ctrl+B)">
              <svg viewBox="0 0 20 20" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round">
                <rect x="2.5" y="3.5" width="15" height="13" rx="2" /><path d="M7.5 3.5v13" />
              </svg>
            </button>
            <span className="adm-vsep" />
          </>
        )}
        <h1>
          {title}
          {count != null && <span className="adm-count">{count}</span>}
        </h1>
        {actions && <div className="adm-pagehead-actions">{actions}</div>}
      </header>
      <div className="adm-pagebody">
        {description && <p className="adm-pagedesc">{description}</p>}
        {children}
      </div>
    </div>
  );
}

// Drawer: the right-side detail panel (records open here instead of a card
// appended under the table). Scrim click and Esc dismiss. width: 640 for a
// record with sub-tables (user), 480 for a plain viewer (save/order).
export function Drawer({ title, onClose, children, width = 480 }) {
  useEffect(() => {
    const onKey = (e) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  return (
    <div className="adm-drawer-scrim" onClick={onClose}>
      <aside className="adm-drawer" style={{ width }} onClick={(e) => e.stopPropagation()}>
        <header className="adm-drawer-head">
          <h2>{title}</h2>
          <button className="adm-iconbtn" onClick={onClose} title="закрыть (Esc)">✕</button>
        </header>
        <div className="adm-drawer-body">{children}</div>
      </aside>
    </div>
  );
}

// Skeleton: shimmer placeholder rows while a table/card loads — the layout
// doesn't jump when data lands.
export function Skeleton({ rows = 6 }) {
  return (
    <div className="adm-skeleton" aria-hidden>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="adm-skeleton-row" style={{ opacity: 1 - i * 0.11 }} />
      ))}
    </div>
  );
}

// Empty: a friendly centered empty state (icon + line + optional hint).
export function Empty({ icon = "○", text, hint }) {
  return (
    <div className="adm-emptystate">
      <div className="adm-emptystate-icon">{icon}</div>
      <p>{text}</p>
      {hint && <p className="adm-emptystate-hint">{hint}</p>}
    </div>
  );
}

// LoadState: the standard loading/error/empty switch above table content.
export function LoadState({ loading, error, empty, emptyText, children }) {
  if (loading) return <Skeleton />;
  if (error) return <Empty icon="⚠" text={error} />;
  if (empty) return <Empty text={emptyText || "Пусто."} />;
  return children;
}

// Facet: the spec's dashed "+ filter" button with a checkbox popover. Options
// carry live counts; picked values render as badges inside the button after a
// separator. Click-outside and Esc close the popover; «Сбросить» clears.
export function Facet({ label, options, selected, onChange }) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef(null);
  useEffect(() => {
    if (!open) return;
    const onDown = (e) => { if (rootRef.current && !rootRef.current.contains(e.target)) setOpen(false); };
    const onKey = (e) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", onDown);
    window.addEventListener("keydown", onKey);
    return () => { document.removeEventListener("mousedown", onDown); window.removeEventListener("keydown", onKey); };
  }, [open]);

  const toggle = (v) =>
    onChange(selected.includes(v) ? selected.filter((x) => x !== v) : [...selected, v]);

  return (
    <div className="adm-facet" ref={rootRef}>
      <button className={"adm-facet-btn" + (selected.length ? " has" : "")} onClick={() => setOpen((o) => !o)}>
        <span className="adm-facet-plus">＋</span> {label}
        {selected.length > 0 && (
          <>
            <span className="adm-facet-sep" />
            {selected.map((v) => <span key={v} className="adm-facet-badge">{v}</span>)}
          </>
        )}
      </button>
      {open && (
        <div className="adm-facet-pop">
          {options.map((o) => (
            <label key={o.value} className="adm-facet-opt">
              <input type="checkbox" checked={selected.includes(o.value)} onChange={() => toggle(o.value)} />
              <span className="adm-facet-name">{o.value}</span>
              <span className="adm-facet-count">{o.count}</span>
            </label>
          ))}
          {!options.length && <div className="adm-facet-none">нет значений</div>}
          {selected.length > 0 && (
            <button className="adm-facet-clear" onClick={() => { onChange([]); setOpen(false); }}>Сбросить</button>
          )}
        </div>
      )}
    </div>
  );
}

// countBy: options-with-counts for a Facet out of a row set.
export function countBy(rows, pick) {
  const m = new Map();
  for (const r of rows) {
    const v = pick(r);
    if (!v) continue;
    m.set(v, (m.get(v) || 0) + 1);
  }
  return [...m.entries()].sort((a, b) => b[1] - a[1]).map(([value, count]) => ({ value, count }));
}

// usePagination: client-side pages over a filtered row set. Resets to page 1
// when the row count changes (a new filter shrank the set).
export function usePagination(rows, initialSize = 25) {
  const [size, setSize] = useState(initialSize);
  const [page, setPage] = useState(1);
  const total = rows.length;
  const pages = Math.max(1, Math.ceil(total / size));
  const cur = Math.min(page, pages);
  const slice = rows.slice((cur - 1) * size, cur * size);
  return { slice, page: cur, pages, size, total,
    setPage: (p) => setPage(Math.min(Math.max(1, p), pages)),
    setSize: (s) => { setSize(s); setPage(1); } };
}

// Pagination: the bar under a table — rows-per-page select + page stepper.
// Hidden entirely while everything fits on one page of the smallest size.
export function Pagination({ p }) {
  if (p.total <= 25) return null;
  return (
    <div className="adm-pagination">
      <span className="adm-dim">{p.total} строк</span>
      <span className="adm-pagination-right">
        <label className="adm-dim">
          Строк:{" "}
          <select className="field adm-pagesize" value={p.size} onChange={(e) => p.setSize(Number(e.target.value))}>
            {[25, 50, 100].map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        </label>
        <span className="adm-pagenum">Стр. {p.page} из {p.pages}</span>
        <button className="adm-iconbtn" disabled={p.page <= 1} onClick={() => p.setPage(p.page - 1)}>‹</button>
        <button className="adm-iconbtn" disabled={p.page >= p.pages} onClick={() => p.setPage(p.page + 1)}>›</button>
      </span>
    </div>
  );
}

// Confirm: the destructive-action modal (spec tier 1/2 — never a browser
// confirm). Tier 1: message + Отмена/danger. Tier 2 (typeToConfirm set): the
// danger button unlocks only after the operator types the exact phrase.
export function Confirm({ title, body, dangerLabel = "Удалить", typeToConfirm, onConfirm, onClose }) {
  const [typed, setTyped] = useState("");
  const armed = !typeToConfirm || typed.trim() === typeToConfirm;
  useEffect(() => {
    const onKey = (e) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  return (
    <div className="adm-modal-scrim" onClick={onClose}>
      <div className="adm-modal" onClick={(e) => e.stopPropagation()}>
        <h2>{title}</h2>
        <div className="adm-modal-body">{body}</div>
        {typeToConfirm && (
          <input className="field adm-modal-type" autoFocus
                 placeholder={`введите: ${typeToConfirm}`}
                 value={typed} onChange={(e) => setTyped(e.target.value)} />
        )}
        <div className="adm-modal-actions">
          <button className="btn-ghost sm" autoFocus={!typeToConfirm} onClick={onClose}>Отмена</button>
          <button className="btn adm-danger" disabled={!armed}
                  onClick={() => { onConfirm(); onClose(); }}>{dangerLabel}</button>
        </div>
      </div>
    </div>
  );
}
