import { adminUsers, adminOrders, adminAnalytics, adminFiles } from "../../lib/api.js";
import { useAsync, fmt, dt } from "../adminShared.jsx";
import { Page, LoadState, Empty } from "./ui.jsx";

const today = () => new Date().toISOString().slice(0, 10);

// Обзор — the landing page: the whole product at a glance. KPI row (audience +
// live economy + today's activity), the freshest orders, and today's top
// events. Each block loads independently so one slow endpoint doesn't blank
// the page.
export default function Overview({ token, onNav }) {
  const users = useAsync(() => adminUsers(token), [token]);
  const orders = useAsync(() => adminOrders(token), [token]);
  const day = useAsync(() => adminAnalytics(today(), token), [token]);
  // Черновик манифеста определяется по наличию файла в корне контента —
  // GET ?draft=1 неотличимо фолбэчит на живой манифест, а листинг честен.
  const root = useAsync(() => adminFiles("", token), [token]);
  const hasDraft = ((root.data && root.data.files) || []).some((f) => !f.dir && f.name === "manifest.draft.json");

  const totals = {};
  let linked = 0;
  for (const u of users.data || []) {
    if (Object.keys(u.providers || {}).length) linked++;
    for (const [c, v] of Object.entries(u.balances || {})) totals[c] = (totals[c] || 0) + Number(v || 0);
  }
  const events = Object.entries((day.data && day.data.by_name) || {}).sort((a, b) => b[1] - a[1]).slice(0, 8);
  const maxEv = events.length ? events[0][1] : 1;
  const recent = (orders.data || []).slice(0, 8);

  return (
    <Page title="Обзор" description="Аудитория, экономика и активность — одним экраном.">
      {hasDraft && (
        <div className="adm-alert">
          <span>Есть неопубликованный черновик манифеста — игроки его не видят.</span>
          <button className="adm-link" onClick={() => onNav("manifest")}>Открыть манифест →</button>
        </div>
      )}
      <div className="adm-kpis">
        <Kpi label="пользователей" value={users.data ? fmt(users.data.length) : "…"} onClick={() => onNav("users")} />
        <Kpi label="с привязкой" value={users.data ? fmt(linked) : "…"} onClick={() => onNav("users")} />
        {Object.entries(totals).map(([c, v]) => (
          <Kpi key={c} label={c + " в обороте"} value={fmt(v)} onClick={() => onNav("users")} />
        ))}
        <Kpi label="событий сегодня" value={day.data ? fmt(day.data.total) : "…"} onClick={() => onNav("analytics")} />
        <Kpi label="уникальных сегодня" value={day.data ? fmt(day.data.unique_users) : "…"} onClick={() => onNav("analytics")} />
      </div>

      <div className="adm-cols2">
        <section className="adm-panel">
          <header className="adm-panel-head">
            <h2>Последние заказы</h2>
            <button className="adm-link" onClick={() => onNav("orders")}>все →</button>
          </header>
          <LoadState loading={orders.loading} error={orders.error} empty={!recent.length} emptyText="Заказов пока нет.">
            <ul className="adm-feed">
              {recent.map((o, i) => (
                <li key={i} className="adm-feed-row">
                  <span className={"adm-feed-amt " + (o.type === "iap" ? "plus" : "minus")}>
                    {fmt(o.amount)} {o.currency || ""}
                  </span>
                  <span className="adm-feed-what">{o.sku || o.reason || o.type}</span>
                  <span className="adm-feed-when">{dt(o.ts, 16).slice(5)}</span>
                </li>
              ))}
            </ul>
          </LoadState>
        </section>

        <section className="adm-panel">
          <header className="adm-panel-head">
            <h2>События сегодня</h2>
            <button className="adm-link" onClick={() => onNav("analytics")}>подробнее →</button>
          </header>
          <LoadState loading={day.loading} error={day.error} empty={!events.length} emptyText="Сегодня событий ещё не было.">
            <ul className="adm-bars">
              {events.map(([n, v]) => (
                <li key={n} className="adm-bars-row">
                  <span className="adm-bars-name">{n}</span>
                  <span className="adm-bars-track"><span className="adm-bars-fill" style={{ width: Math.max(3, (v / maxEv) * 100) + "%" }} /></span>
                  <span className="adm-bars-val">{fmt(v)}</span>
                </li>
              ))}
            </ul>
          </LoadState>
        </section>
      </div>
      {!users.loading && !users.error && !(users.data || []).length && (
        <Empty text="Пользователей ещё нет — база пуста." hint="Первый запуск игры создаст device-пользователя." />
      )}
    </Page>
  );
}

function Kpi({ label, value, onClick }) {
  return (
    <button className="adm-kpi" onClick={onClick}>
      <span className="adm-kpi-value">{value}</span>
      <span className="adm-kpi-label">{label}</span>
    </button>
  );
}
