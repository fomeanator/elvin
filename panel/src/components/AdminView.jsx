import { useEffect, useRef, useState } from "react";
import {
  adminUsers, adminUserDetail, adminGrant,
  adminOrders, adminSaves, adminSaveDetail, adminDeleteSave,
} from "../lib/api.js";
import { useAsync, useDebounced, authMsg, fmt, dt } from "./adminShared.jsx";
import Sidebar, { NAV } from "./admin/Sidebar.jsx";
import { AdmShell, Page, Drawer, LoadState, Empty, Confirm, usePagination, Pagination, Facet, countBy } from "./admin/ui.jsx";
import Overview from "./admin/Overview.jsx";
import AdminEconomy from "./AdminEconomy.jsx";
import AdminAssets from "./AdminAssets.jsx";
import AdminAnalytics from "./AdminAnalytics.jsx";
import AdminManifest from "./AdminManifest.jsx";

// The admin dashboard: a left nav rail over section pages, records opening in
// a right-side drawer. Sections: Обзор (landing), Аудитория (users/orders/
// saves), Игра (economy/assets/manifest), Метрики (analytics). Визуал stays in
// the Студия's ThemePanel and titles/chapters in LibraryHome — this dashboard
// is operations, not authoring.
const KEYS = NAV.flatMap((g) => g.items.map((i) => i.key));

export default function AdminView({ creds, notify }) {
  const [section, setSectionState] = useState(() => {
    const s = localStorage.getItem("lvn_admin_tab");
    return KEYS.includes(s) ? s : "overview";
  });
  const token = useDebounced(creds.token, 400); // не спамить 401 на каждый символ ввода
  // Повторный клик по активному пункту перемонтирует страницу — жест «обновить всё».
  const [bump, setBump] = useState(0);
  const onNav = (k) => {
    if (k === section) { setBump((b) => b + 1); return; }
    setSectionState(k);
    localStorage.setItem("lvn_admin_tab", k);
  };
  // Сайдбар сворачивается в иконочный рейл (Ctrl/Cmd+B), выбор переживает перезагрузку.
  // На узком экране (<900px) тот же тоггл открывает сайдбар ОВЕРЛЕЕМ поверх контента.
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem("lvn_admin_sidebar") === "collapsed");
  const [mobileOpen, setMobileOpen] = useState(false);
  const toggleSidebar = () => {
    if (window.innerWidth < 900) { setMobileOpen((o) => !o); return; }
    setCollapsed((c) => {
      localStorage.setItem("lvn_admin_sidebar", c ? "expanded" : "collapsed");
      return !c;
    });
  };
  useEffect(() => {
    const onKey = (e) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "b") { e.preventDefault(); toggleSidebar(); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  // Без токена — полноэкранный вход вместо всего шелла (гейт, не пустой дашборд).
  if (!token) {
    return (
      <div className="adm-gate">
        <TokenGate creds={creds} />
      </div>
    );
  }

  // На мобилке выбор пункта закрывает оверлей-меню.
  const onNavMobile = (k) => { setMobileOpen(false); onNav(k); };

  return (
    <AdmShell.Provider value={{ toggleSidebar }}>
    <div className={"adm" + (collapsed ? " collapsed" : "") + (mobileOpen ? " mobile-open" : "")}>
      <Sidebar active={section} onNav={onNavMobile} tokenOk={!!token} collapsed={collapsed} />
      {mobileOpen && <div className="adm-mobile-scrim" onClick={() => setMobileOpen(false)} />}
      <main className="adm-main" key={section + ":" + bump}>
        {(
          <>
            {section === "overview" && <Overview token={token} onNav={onNav} />}
            {section === "users" && <UsersPage token={token} notify={notify} />}
            {section === "orders" && <OrdersPage token={token} />}
            {section === "saves" && <SavesPage token={token} notify={notify} />}
            {section === "economy" && (
              <Page title="Экономика" description="Живые конфиги паков, рекламы и ежедневных наград — применяются без рестарта.">
                <AdminEconomy token={token} notify={notify} />
              </Page>
            )}
            {section === "assets" && (
              <Page title="Ассеты" description="Контент-директория сервера: арт, фоны, скрипты. Клик по имени копирует URL.">
                <AdminAssets token={token} notify={notify} />
              </Page>
            )}
            {section === "analytics" && (
              <Page title="Аналитика" description="События игры по дням: количество, уникальные игроки, разбивка по событиям.">
                <AdminAnalytics token={token} />
              </Page>
            )}
            {section === "manifest" && (
              <Page title="Манифест" description="Release-цикл: черновик → публикация, raw JSON и история версий с откатом.">
                <AdminManifest token={token} notify={notify} />
              </Page>
            )}
          </>
        )}
      </main>
    </div>
    </AdmShell.Provider>
  );
}

// TokenGate: the full-screen sign-in when no token is set — the field here
// writes the same shared creds the top bar uses.
function TokenGate({ creds }) {
  const [v, setV] = useState("");
  return (
    <div className="adm-gate-card">
      <div className="adm-gate-brand"><span className="adm-brand-tile">◆</span> LVN Панель</div>
      <h1>Вход в панель</h1>
      <p>Админ-токен сервера (флаг <code>-admin-token</code>).</p>
      <div className="adm-gate-row">
        <input
          className="field" type="password" placeholder="admin token" autoFocus
          value={v}
          onChange={(e) => setV(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") creds.setToken(v.trim()); }}
        />
        <button className="btn btn-primary" onClick={() => creds.setToken(v.trim())}>Войти</button>
      </div>
    </div>
  );
}

// ── Users ────────────────────────────────────────────────────────────────────
function UsersPage({ token, notify }) {
  const { loading, error, data: allUsers, reload } = useAsync(() => adminUsers(token), [token]);
  const [selected, setSelected] = useState(null);
  const [q, setQ] = useState("");

  const needle = q.trim().toLowerCase();
  const users = (allUsers || []).filter((u) =>
    !needle ||
    (u.user_id || "").toLowerCase().includes(needle) ||
    (u.name || "").toLowerCase().includes(needle));
  const pg = usePagination(users);

  return (
    <Page
      title="Пользователи"
      count={allUsers ? (needle ? `${users.length} из ${allUsers.length}` : allUsers.length) : null}
      description="Аккаунты игроков, их кошельки и привязки. Клик по строке — карточка с грантом."
      actions={
        <>
          <input className="field adm-search" placeholder="поиск: id или имя"
                 value={q} onChange={(e) => setQ(e.target.value)} />
          <button className="adm-iconbtn" onClick={reload} title="обновить">⟳</button>
        </>
      }
    >
      <LoadState loading={loading} error={error} empty={!users.length}
                 emptyText={needle ? "Никто не совпал с поиском." : "Пользователей ещё нет."}>
        <div className="adm-tablewrap">
          <table className="adm-table">
            <thead>
              <tr><th>игрок</th><th>создан</th><th>провайдеры</th><th className="num">балансы</th></tr>
            </thead>
            <tbody>
              {pg.slice.map((u) => (
                <tr key={u.user_id} className="adm-row" onClick={() => setSelected(u.user_id)}>
                  <td>
                    <span className="admin-avatar">{(u.name || u.user_id.replace(/^u_/, "")).slice(0, 1).toUpperCase()}</span>
                    <span className="adm-cell-main">{u.name || <code>{u.user_id}</code>}</span>
                    {u.name && <code className="adm-cell-sub">{u.user_id}</code>}
                  </td>
                  <td className="muted">{dt(u.created, 16)}</td>
                  <td>
                    {Object.keys(u.providers || {}).length
                      ? Object.keys(u.providers).map((p) => <span key={p} className="pill">{p}</span>)
                      : <span className="muted">device</span>}
                  </td>
                  <td className="num">
                    {Object.entries(u.balances || {}).map(([c, v]) => (
                      <span key={c} className="pill">{fmt(v)} {c}</span>
                    ))}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <Pagination p={pg} />
      </LoadState>
      {selected && (
        <Drawer title="Игрок" width={640} onClose={() => setSelected(null)}>
          <UserDetail key={selected} id={selected} token={token} notify={notify} />
        </Drawer>
      )}
    </Page>
  );
}

function UserDetail({ id, token, notify }) {
  const { loading, error, data, reload } = useAsync(() => adminUserDetail(id, token), [id, token]);
  const [cur, setCur] = useState("");
  const [amt, setAmt] = useState("");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);

  async function grant() {
    const currency = cur.trim();
    const amount = Number(amt);
    if (!currency || !amount) { notify("Нужны валюта и количество", "err"); return; }
    // Сервер декодирует amount в int64 — дробное значение упало бы туманным 400.
    if (!Number.isSafeInteger(amount)) { notify("Количество должно быть целым числом", "err"); return; }
    setBusy(true); // деньги: двойной клик не должен начислить дважды
    try {
      await adminGrant({ user_id: id, currency, amount, reason: reason.trim() || "admin:panel" }, token);
      notify("Готово", "ok");
      setAmt("");
      reload();
    } catch (e) {
      notify("✗ " + authMsg(e), "err");
    } finally { setBusy(false); }
  }

  const wallet = data && data.wallet;
  const currencies = [...new Set([...Object.keys((wallet && wallet.balances) || {}), "gold", "crystals", "energy"])];

  return (
    <LoadState loading={loading} error={error}>
      {wallet && (
        <>
          <div className="adm-id-block">
            <code>{id}</code>
            {data.name && <b>{data.name}</b>}
          </div>

          <div className="admin-balances">
            {Object.entries(wallet.balances || {}).map(([c, v]) => (
              <div key={c} className="admin-balance">
                <span className="admin-stat-num">{fmt(v)}</span>
                <span className="admin-stat-label">{c}</span>
              </div>
            ))}
            {!Object.keys(wallet.balances || {}).length && <Empty text="Кошелёк пуст." />}
          </div>

          <section className="adm-drawer-section">
            <h3>Начислить / списать</h3>
            <div className="adm-form-grid">
              <input className="field" placeholder="валюта" value={cur} onChange={(e) => setCur(e.target.value)} />
              <input className="field" placeholder="±количество" type="number" value={amt} onChange={(e) => setAmt(e.target.value)} />
              <input className="field span2" placeholder="причина (audit)" value={reason} onChange={(e) => setReason(e.target.value)} />
              <button className="btn btn-primary span2" onClick={grant} disabled={busy}>Применить</button>
            </div>
            <div className="admin-quickcur">
              {currencies.map((c) => (
                <button key={c} className={"pill as-pill" + (cur === c ? " active" : "")} onClick={() => setCur(c)}>{c}</button>
              ))}
            </div>
          </section>

          <section className="adm-drawer-section">
            <h3>Инвентарь ({Object.keys(wallet.inventory || {}).length})</h3>
            {Object.keys(wallet.inventory || {}).length
              ? <div className="admin-inv">{Object.keys(wallet.inventory).map((sku) => <span key={sku} className="pill">{sku}</span>)}</div>
              : <p className="adm-dim">Пусто.</p>}
          </section>

          <section className="adm-drawer-section">
            <h3>История</h3>
            {(wallet.history || []).length ? (
              <table className="adm-table dense">
                <tbody>
                  {(wallet.history || []).slice(-25).reverse().map((h, i) => (
                    <tr key={i}>
                      <td className="muted">{dt(h.ts, 16).slice(5)}</td>
                      <td className={"num " + (h.type === "earn" ? "amt-plus" : "amt-minus")}>
                        {h.type === "earn" ? "+" : "−"}{fmt(h.amount)} {h.currency}
                      </td>
                      <td className="muted adm-ellipsis">{h.sku || h.reason || ""}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            ) : <p className="adm-dim">Операций не было.</p>}
          </section>
        </>
      )}
    </LoadState>
  );
}

// ── Orders ───────────────────────────────────────────────────────────────────
function OrdersPage({ token }) {
  const { loading, error, data: allOrders, reload } = useAsync(() => adminOrders(token), [token]);
  const [q, setQ] = useState("");
  const [fType, setFType] = useState([]);      // facet: iap / spend
  const [fCur, setFCur] = useState([]);        // facet: валюта
  const needle = q.trim().toLowerCase();
  const all = allOrders || [];
  const orders = all.filter((o) =>
    (!needle ||
      [o.user_id, o.sku, o.reason, o.type, o.currency].some((v) => (v || "").toLowerCase().includes(needle))) &&
    (!fType.length || fType.includes(o.type || "spend")) &&
    (!fCur.length || fCur.includes(o.currency || "")));
  const pg = usePagination(orders);

  return (
    <Page
      title="Заказы"
      count={allOrders ? (needle ? `${orders.length} из ${allOrders.length}` : allOrders.length) : null}
      description="Покупки паков (IAP) и внутриигровые траты со sku."
      actions={
        <>
          <Facet label="Тип" options={countBy(all, (o) => o.type || "spend")} selected={fType} onChange={setFType} />
          <Facet label="Валюта" options={countBy(all, (o) => o.currency)} selected={fCur} onChange={setFCur} />
          <input className="field adm-search" placeholder="фильтр: юзер / sku / причина"
                 value={q} onChange={(e) => setQ(e.target.value)} />
          <button className="adm-iconbtn" onClick={reload} title="обновить">⟳</button>
        </>
      }
    >
      <LoadState loading={loading} error={error} empty={!orders.length}
                 emptyText={needle ? "Ничего не совпало." : "Заказов пока нет."}>
        <div className="adm-tablewrap">
          <table className="adm-table">
            <thead>
              <tr><th>время</th><th>юзер</th><th>тип</th><th>sku</th><th className="num">сумма</th><th>причина</th></tr>
            </thead>
            <tbody>
              {pg.slice.map((o, i) => (
                <tr key={i}>
                  <td className="muted">{dt(o.ts)}</td>
                  <td><code>{o.user_id}</code></td>
                  <td>{o.type === "iap" ? <span className="tag-ok">IAP</span> : <span className="tag-spend">spend</span>}</td>
                  <td className="adm-ellipsis">{o.sku || ""}</td>
                  <td className={"num " + (o.type === "iap" ? "amt-plus" : "amt-minus")}>{fmt(o.amount)} {o.currency || ""}</td>
                  <td className="muted adm-ellipsis">{o.reason || ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <Pagination p={pg} />
      </LoadState>
    </Page>
  );
}

// SaveKey renders a "<uid>__<title>" composite readably: dim uid, bright title
// ("__global" — the cross-novel stats blob — shows as a badge).
function SaveKey({ k }) {
  const i = k.indexOf("__");
  if (i < 0) return <code>{k}</code>;
  const uid = k.slice(0, i), title = k.slice(i + 2);
  return (
    <code>
      <span className="savekey-uid">{uid}</span>
      <span className="savekey-sep">·</span>
      {title === "__global" || title === "" ? <span className="pill">global</span> : <span className="savekey-title">{title}</span>}
    </code>
  );
}

// ── Saves ────────────────────────────────────────────────────────────────────
function SavesPage({ token, notify }) {
  const { loading, error, data: allSaves, reload } = useAsync(() => adminSaves(token), [token]);
  const [view, setView] = useState(null); // { key, body }
  const [confirmKey, setConfirmKey] = useState(null); // ключ, ждущий подтверждения удаления
  const [q, setQ] = useState("");
  // Последний осмысленный клик побеждает: ответ open() применяется только если
  // с тех пор не открыли другой сейв и не удалили этот.
  const openReq = useRef("");
  const [fTitle, setFTitle] = useState([]); // facet: тайтл из ключа
  const needle = q.trim().toLowerCase();
  const all = allSaves || [];
  const titleOf = (k) => { const i = (k || "").indexOf("__"); const t = i < 0 ? "" : k.slice(i + 2); return t === "__global" || t === "" ? "global" : t; };
  const saves = all.filter((s) =>
    (!needle || (s.key || "").toLowerCase().includes(needle)) &&
    (!fTitle.length || fTitle.includes(titleOf(s.key))));
  const pg = usePagination(saves);

  async function open(key) {
    openReq.current = key;
    try {
      const body = await adminSaveDetail(key, token);
      if (openReq.current === key) setView({ key, body });
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
  }
  async function remove(key) {
    try {
      await adminDeleteSave(key, token);
      notify("Удалено", "ok");
      if (openReq.current === key) openReq.current = ""; // сирый open() не воскресит удалённый
      if (view && view.key === key) setView(null);
      reload();
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
  }

  return (
    <Page
      title="Сохранения"
      count={allSaves ? (needle ? `${saves.length} из ${allSaves.length}` : allSaves.length) : null}
      description="Облачные сейвы игроков (user · title). Клик по строке — содержимое."
      actions={
        <>
          <Facet label="Тайтл" options={countBy(all, (s) => titleOf(s.key))} selected={fTitle} onChange={setFTitle} />
          <input className="field adm-search" placeholder="фильтр по ключу"
                 value={q} onChange={(e) => setQ(e.target.value)} />
          <button className="adm-iconbtn" onClick={reload} title="обновить">⟳</button>
        </>
      }
    >
      <LoadState loading={loading} error={error} empty={!saves.length}
                 emptyText={needle ? "Ничего не совпало." : "Сохранений пока нет."}>
        <div className="adm-tablewrap">
          <table className="adm-table">
            <thead>
              <tr><th>ключ</th><th className="num">размер</th><th>обновлён</th><th></th></tr>
            </thead>
            <tbody>
              {pg.slice.map((s) => (
                <tr key={s.key} className="adm-row" onClick={() => open(s.key)}>
                  <td><SaveKey k={s.key} /></td>
                  <td className="num muted">{fmt(s.size)} b</td>
                  <td className="muted">{dt(s.modified)}</td>
                  <td className="num">
                    <button className="adm-iconbtn danger" title="удалить"
                            onClick={(e) => { e.stopPropagation(); setConfirmKey(s.key); }}>✕</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <Pagination p={pg} />
      </LoadState>
      {view && (
        <Drawer title={<SaveKey k={view.key} />} width={560} onClose={() => setView(null)}>
          <pre className="admin-pre tall">{JSON.stringify(view.body, null, 2)}</pre>
        </Drawer>
      )}
      {confirmKey && (
        <Confirm
          title="Удалить сохранение"
          body={<p>Сейв <b><code>{confirmKey}</code></b> будет удалён у игрока. Действие нельзя отменить.</p>}
          onConfirm={() => remove(confirmKey)}
          onClose={() => setConfirmKey(null)}
        />
      )}
    </Page>
  );
}
