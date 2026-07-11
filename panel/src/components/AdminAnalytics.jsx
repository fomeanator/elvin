import { useState } from "react";
import { adminAnalytics } from "../lib/api.js";
import { useAsync, Status, fmt } from "./adminShared.jsx";

const today = () => new Date().toISOString().slice(0, 10);

// One day's event summary: totals, uniques, and a per-event bar list — the
// same view the raw admin had, driven by /v1/analytics/summary.
export default function AdminAnalytics({ token }) {
  const [day, setDay] = useState(today);
  const { loading, error, data } = useAsync(() => adminAnalytics(day, token), [day, token]);

  const names = Object.entries((data && data.by_name) || {}).sort((a, b) => b[1] - a[1]);
  const max = names.length ? names[0][1] : 1;

  return (
    <div className="admin-card">
      <div className="admin-cardhead">
        <h2>
          За{" "}
          <input type="date" className="field admin-date" value={day}
                 onChange={(e) => e.target.value && setDay(e.target.value)} />
        </h2>
        {data && (
          <div className="admin-rowbtns">
            <span className="pill">событий: {fmt(data.total)}</span>
            <span className="pill">уникальных: {fmt(data.unique_users)}</span>
          </div>
        )}
      </div>
      <Status loading={loading} error={error} />
      {!loading && !error && !names.length && <p className="admin-empty">За этот день событий нет.</p>}
      {names.length > 0 && (
        <table className="admin-table">
          <thead><tr><th>событие</th><th className="admin-barcol"></th><th>число</th></tr></thead>
          <tbody>
            {names.map(([n, v]) => (
              <tr key={n}>
                <td>{n}</td>
                <td className="admin-barcol"><div className="admin-bar" style={{ width: Math.max(2, (v / max) * 100) + "%" }} /></td>
                <td>{fmt(v)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
