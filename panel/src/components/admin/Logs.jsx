import { useState } from "react";
import { adminLogSessions, adminClientLogs, adminLogLevel, adminSetLogLevel, adminLogFetch, adminLogFetchDrop } from "../../lib/api.js";
import { useAsync, fmt, dt } from "../adminShared.jsx";
import { LoadState, Empty } from "./ui.jsx";

// ── ЛОГИ С УСТРОЙСТВ — ПУЛЬТ (TR-86, этап 3) ────────────────────────────────
//
// Устройство хранит всё само; сюда приезжают отклонения с хвостом, окна
// кадров и то, что попросили: подробный лог на срок или кусок кольца за
// период. Здесь видно, ЧТО ПРИСЛАЛО устройство (сессии дня: строки по
// уровням, отклонения, fps), сами строки с фильтрами и две кнопки пульта.

const todayISO = () => new Date().toISOString().slice(0, 10);
const LEVELS = ["", "exception", "error", "warning", "info", "trace", "ring"];

export default function Logs({ token }) {
  const [day, setDay] = useState(todayISO());
  const [device, setDevice] = useState("");
  const [session, setSession] = useState("");
  const [level, setLevel] = useState("");
  const [tag, setTag] = useState("");
  const [open, setOpen] = useState(null);
  const [hours, setHours] = useState(24);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [msg, setMsg] = useState("");
  const [busy, setBusy] = useState(false);

  const sessions = useAsync(() => adminLogSessions(day, device, token), [day, device, token]);
  const lines = useAsync(() => adminClientLogs({ day, device, session, level, tag, n: 400 }, token), [day, device, session, level, tag, token]);
  const directives = useAsync(() => adminLogLevel(token), [token]);

  const act = async (fn, ok) => {
    setBusy(true); setMsg("");
    try { await fn(); setMsg(ok); directives.reload && directives.reload(); }
    catch (e) { setMsg("✗ " + (e.message || e)); }
    finally { setBusy(false); }
  };
  const list = (sessions.data || {}).sessions || [];
  const rows = (lines.data || {}).lines || [];
  const dirs = (directives.data || {}).directives || [];

  return (
    <>
      <section className="adm-panel">
        <header className="adm-panel-head">
          <h2>Что прислали устройства</h2>
          <div className="admin-rowbtns" style={{ gap: 8, flexWrap: "wrap" }}>
            <input className="field" type="date" value={day} onChange={(e) => { setDay(e.target.value); setSession(""); }} />
            <input className="field" style={{ width: 280 }} placeholder="устройство (начало id)" value={device}
                   onChange={(e) => { setDevice(e.target.value.trim()); setSession(""); }} />
          </div>
        </header>
        <LoadState loading={sessions.loading} error={sessions.error}>
          {!list.length ? <Empty text="За этот день от устройств ничего не приезжало." /> : (
            <div className="adm-tablewrap">
              <table className="adm-table">
                <thead><tr><th>сессия</th><th>устройство</th><th>сборка</th><th className="num">строк</th><th className="num">ошибок</th><th className="num">отклонений</th><th className="num">fps</th><th className="num">худший кадр</th><th className="num">окон с рывками</th></tr></thead>
                <tbody>
                  {list.map((s) => (
                    <tr key={s.session} className={session === s.session ? "row-active" : ""} style={{ cursor: "pointer" }}
                        onClick={() => { setSession(session === s.session ? "" : s.session); setDevice(s.device); }}>
                      <td><span className="adm-cell-main">{dt(s.first, 16)}</span><div className="muted">{s.session}</div></td>
                      <td className="muted" title={s.device}>{s.device.slice(0, 10)}…<div>{s.model}</div></td>
                      <td className="muted">{s.app}</td>
                      <td className="num">{fmt(s.lines)}</td>
                      <td className={"num" + ((s.levels?.error || 0) + (s.levels?.exception || 0) ? " amt-minus" : " muted")}>{(s.levels?.error || 0) + (s.levels?.exception || 0)}</td>
                      <td className={"num" + (s.deviations ? " amt-minus" : " muted")}>{s.deviations || "—"}</td>
                      <td className="num">{s.fps ? s.fps.toFixed(1) : "—"}</td>
                      <td className="num muted">{s.worst_ms ? Math.round(s.worst_ms) + " мс" : "—"}</td>
                      <td className="num muted">{s.windows ? s.janky_windows + " / " + s.windows : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </LoadState>
      </section>

      <section className="adm-panel">
        <header className="adm-panel-head">
          <h2>Пульт</h2>
          <span className="adm-dim">просим у устройства то, чего в обычном режиме не шлют</span>
        </header>
        <div className="admin-rowbtns" style={{ gap: 8, flexWrap: "wrap", alignItems: "center" }}>
          <input className="field" style={{ width: 280 }} placeholder="id устройства" value={device} onChange={(e) => setDevice(e.target.value.trim())} />
          <input className="field" type="number" min="1" max="168" style={{ width: 70 }} value={hours} onChange={(e) => setHours(Number(e.target.value) || 24)} />
          <button className="btn-ghost sm" disabled={busy || !device}
                  onClick={() => act(() => adminSetLogLevel(device, hours, token), "указание отдано — Trace пойдёт с первой же пачкой логов")}>
            Подробный лог на {hours} ч
          </button>
          <span className="adm-dim">·</span>
          <input className="field" type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} />
          <input className="field" type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} />
          <button className="btn-ghost sm" disabled={busy || !device || !from || !to}
                  onClick={() => act(() => adminLogFetch(device, new Date(from).toISOString(), new Date(to).toISOString(), token), "запрошено — кусок приедет, когда устройство выйдет в сеть")}>
            Запросить период
          </button>
        </div>
        {msg && <p className="adm-dim">{msg}</p>}
        <LoadState loading={directives.loading} error={directives.error}>
          {!dirs.length ? <p className="adm-dim">Живых указаний нет.</p> : (
            <ul className="adm-bars">
              {dirs.map((d) => (
                <li key={d.device} className="adm-bars-row" style={{ alignItems: "flex-start" }}>
                  <span className="adm-bars-name" title={d.device}>{d.device}</span>
                  <span className="muted" style={{ flex: 1 }}>
                    {d.until && <div>Trace до {dt(d.until, 16)} <button className="btn-ghost sm" disabled={busy} onClick={() => act(() => adminSetLogLevel(d.device, 0, token), "снято")}>снять</button></div>}
                    {(d.fetch || []).map((f) => (
                      <div key={f.from}>кусок {dt(f.from, 16)} … {dt(f.to, 16)} <button className="btn-ghost sm" disabled={busy} onClick={() => act(() => adminLogFetchDrop(d.device, f.from, f.to, token), "снято")}>снять</button></div>
                    ))}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </LoadState>
      </section>

      <section className="adm-panel">
        <header className="adm-panel-head">
          <h2>Строки</h2>
          <div className="admin-rowbtns" style={{ gap: 8, flexWrap: "wrap" }}>
            <select className="field" value={level} onChange={(e) => setLevel(e.target.value)}>
              {LEVELS.map((l) => <option key={l} value={l}>{l || "все уровни"}</option>)}
            </select>
            <input className="field" style={{ width: 180 }} placeholder="тег, напр. [lvn-perf]" value={tag} onChange={(e) => setTag(e.target.value.trim())} />
            {session && <span className="adm-dim">сессия {session} <button className="btn-ghost sm" onClick={() => setSession("")}>все</button></span>}
          </div>
        </header>
        <LoadState loading={lines.loading} error={lines.error}>
          {!rows.length ? <Empty text="Строк по этим условиям нет." /> : (
            <div className="adm-tablewrap">
              <table className="adm-table dense">
                <thead><tr><th>время</th><th>уровень</th><th>строка</th></tr></thead>
                <tbody>
                  {rows.slice().reverse().map((r, i) => (
                    <tr key={i} className={r.level === "error" || r.level === "exception" ? "row-warn" : ""}
                        style={{ cursor: r.tail || r.stack ? "pointer" : "default" }} onClick={() => setOpen(open === i ? null : i)}>
                      <td className="muted" style={{ whiteSpace: "nowrap" }}>{dt(r.ts, 19).slice(11)}</td>
                      <td className="muted">{r.level}{r.n > 1 ? " ×" + r.n : ""}</td>
                      <td style={{ fontFamily: "ui-monospace, monospace", fontSize: 12, wordBreak: "break-word" }}>
                        {r.msg}{r.tail ? " ⋯ хвост" : ""}
                        {open === i && r.stack && <pre className="adm-pre">{r.stack}</pre>}
                        {open === i && r.tail && <pre className="adm-pre">{r.tail}</pre>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </LoadState>
      </section>
    </>
  );
}
