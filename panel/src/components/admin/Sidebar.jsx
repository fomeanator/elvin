import { useEffect, useState } from "react";

// Left navigation rail of the admin: brand header, grouped sections with
// stroke icons, and a live server/token status footer. Collapsible to an
// icon rail (Cmd/Ctrl+B, persisted). Navigation state lives in AdminView.

const I = {
  overview: <path d="M3 3h7v5H3zM12 3h5v9h-5zM3 10h7v7H3zM12 14h5v3h-5z" />,
  users: <><circle cx="7" cy="6.5" r="3" /><path d="M1.5 17c0-3 2.5-5 5.5-5s5.5 2 5.5 5" /><circle cx="14.5" cy="7.5" r="2.2" /><path d="M13.5 12.2c2.6.2 4.5 2 4.5 4.3" /></>,
  orders: <><path d="M4 2.5h12v15l-2-1.4-2 1.4-2-1.4-2 1.4-2-1.4-2 1.4z" /><path d="M7 7h6M7 10.5h6" /></>,
  saves: <><path d="M5.5 14.5a3.5 3.5 0 0 1-.4-7A5 5 0 0 1 15 8.5a3 3 0 0 1-.5 6z" /><path d="M10 11v5M8 14l2 2 2-2" /></>,
  economy: <><circle cx="10" cy="10" r="7" /><path d="M10 5.8v8.4M12.6 7.4c-.6-.9-1.5-1.3-2.6-1.3-1.4 0-2.5.8-2.5 2s1 1.7 2.5 2c1.6.3 2.7.9 2.7 2.1 0 1.3-1.2 2.1-2.7 2.1-1.2 0-2.2-.5-2.8-1.4" /></>,
  assets: <><path d="M2.5 5.5a1.5 1.5 0 0 1 1.5-1.5h4l2 2.5h6a1.5 1.5 0 0 1 1.5 1.5v7A1.5 1.5 0 0 1 16 16.5H4A1.5 1.5 0 0 1 2.5 15z" /></>,
  analytics: <><path d="M3 17h14" /><path d="M5 13.5v-3M9 13.5V6.5M13 13.5v-5M17 13.5v-9" /></>,
  manifest: <><path d="M5 2.5h7l3.5 3.5v11.5h-10.5z" /><path d="M12 2.5V6h3.5M7.5 10h5M7.5 13h5" /></>,
};

export function NavIcon({ name }) {
  return (
    <svg className="adm-icon" viewBox="0 0 20 20" fill="none" stroke="currentColor"
         strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
      {I[name]}
    </svg>
  );
}

export const NAV = [
  { section: "", items: [
    { key: "overview", label: "Обзор", icon: "overview" },
  ]},
  { section: "Аудитория", items: [
    { key: "users", label: "Пользователи", icon: "users" },
    { key: "orders", label: "Заказы", icon: "orders" },
    { key: "saves", label: "Сохранения", icon: "saves" },
  ]},
  { section: "Игра", items: [
    { key: "economy", label: "Экономика", icon: "economy" },
    { key: "assets", label: "Ассеты", icon: "assets" },
    { key: "manifest", label: "Манифест", icon: "manifest" },
  ]},
  { section: "Метрики", items: [
    { key: "analytics", label: "Аналитика", icon: "analytics" },
  ]},
];

export default function Sidebar({ active, onNav, tokenOk, collapsed }) {
  // Server heartbeat for the footer — /healthz is public and cheap.
  const [alive, setAlive] = useState(null);
  useEffect(() => {
    let live = true;
    const ping = () => fetch("/healthz").then((r) => live && setAlive(r.ok)).catch(() => live && setAlive(false));
    ping();
    const t = setInterval(ping, 15000);
    return () => { live = false; clearInterval(t); };
  }, []);

  return (
    <nav className="adm-sidebar">
      <div className="adm-brand" title="LVN Панель">
        <span className="adm-brand-tile">◆</span>
        {!collapsed && <span className="adm-brand-name">LVN Панель</span>}
      </div>
      <div className="adm-sidebar-scroll">
        {NAV.map((g, gi) => (
          <div key={gi} className="adm-navgroup">
            {g.section && (collapsed
              ? <div className="adm-navgroup-rule" />
              : <div className="adm-navgroup-title">{g.section}</div>)}
            {g.items.map((it) => (
              <button
                key={it.key}
                className={"adm-navitem" + (active === it.key ? " active" : "")}
                onClick={() => onNav(it.key)}
                title={collapsed ? it.label : active === it.key ? "клик — обновить данные" : undefined}
              >
                <NavIcon name={it.icon} />
                {!collapsed && <span>{it.label}</span>}
              </button>
            ))}
          </div>
        ))}
      </div>
      <footer className="adm-sidebar-foot" title={(alive ? "сервер онлайн" : "сервер недоступен") + " · " + (tokenOk ? "токен ок" : "нет токена")}>
        <span className={"adm-foot-disc " + (alive == null ? "wait" : alive ? "ok" : "bad")}>
          <span className="adm-dot-core" />
        </span>
        {!collapsed && (
          <span className="adm-foot-lines">
            <b>{alive == null ? "Сервер…" : alive ? "Сервер онлайн" : "Сервер недоступен"}</b>
            <i>{tokenOk ? "токен ок" : "нет токена"}</i>
          </span>
        )}
      </footer>
    </nav>
  );
}
