import { useEffect, useMemo, useState } from "react";
import { adminConfig, adminPutConfig, adminFetch } from "../lib/api.js";
import { useAsync, Status, authMsg, HistoryPanel } from "./adminShared.jsx";

// СКИНЫ — ОДНО МЕСТО НАСТРОЙКИ (TR-114). Таблица поверх skins.json: наряды,
// фоны меню, аватарки и призы круток одной формой — имя, арт, ступень, цена,
// «продаётся», «в крутке», вес. Цвет — у СТУПЕНИ (палитра внизу), не у скина
// (решение Ильи 15.09). Сохранение уходит в PUT /v1/admin/config/skins.json,
// и сервер сам раскладывает каталог по манифесту и барабану — сборка не нужна.

const KINDS = [
  { key: "wardrobe", label: "Наряды" },
  { key: "backdrop", label: "Фоны меню" },
  { key: "avatar", label: "Аватарки" },
];
const RARITIES = ["", "common", "uncommon", "rare", "mythical", "legendary", "immortal"];
const RARITY_RU = { "": "—", common: "обычный", uncommon: "необычный", rare: "редкий", mythical: "мифический", legendary: "легендарный", immortal: "бессмертный" };
const CURRENCIES = ["crystals", "energy"];

export default function AdminSkins({ token, notify }) {
  const { loading, error, data, reload } = useAsync(() => adminConfig("skins.json", token), [token]);
  const [doc, setDoc] = useState(null);      // рабочая копия; null — зеркало сервера
  const [kind, setKind] = useState("wardrobe");
  const [q, setQ] = useState("");
  const [busy, setBusy] = useState(false);
  const [showHist, setShowHist] = useState(false);

  useEffect(() => { setDoc(null); }, [data]);
  const live = doc || normalize(data);
  const dirty = doc != null && JSON.stringify(doc) !== JSON.stringify(normalize(data));

  const rows = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return (live.skins || []).map((s, i) => ({ s, i })).filter(({ s }) =>
      s.kind === kind && (!needle || (s.sku + " " + (s.name || "")).toLowerCase().includes(needle)));
  }, [live, kind, q]);

  function edit(i, patch) {
    const next = structuredClone(live);
    next.skins[i] = { ...next.skins[i], ...patch };
    setDoc(next);
  }
  function editPalette(key, value) {
    const next = structuredClone(live);
    next.rarity_colors = { ...(next.rarity_colors || {}), [key]: value };
    setDoc(next);
  }
  function editWeight(key, value) {
    const next = structuredClone(live);
    next.rarity_weights = { ...(next.rarity_weights || {}), [key]: Number(value) || 0 };
    setDoc(next);
  }

  async function save() {
    setBusy(true);
    try {
      const r = await adminPutConfig("skins.json", clean(live), token);
      notify("Скины сохранены и применены: " + (r.applied ?? "?") + " записей в манифесте", "ok");
      setDoc(null);
      reload();
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
    finally { setBusy(false); }
  }
  async function collect() {
    setBusy(true);
    try {
      const r = await adminFetch("/v1/admin/skins/collect", token, { method: "POST" });
      notify("Собрано из манифеста: " + (r.collected ?? "?") + " скинов (правки целы)", "ok");
      setDoc(null);
      reload();
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
    finally { setBusy(false); }
  }

  const counts = countKinds(live.skins || []);
  return (
    <div className="admin-card">
      <div className="admin-cardhead">
        <h2>Скины <span className="pill">skins.json</span></h2>
        <div className="admin-rowbtns">
          <button className="btn-ghost sm" onClick={collect} disabled={busy} title="добавить в каталог новое из манифеста; правленое не трогается">Собрать из манифеста</button>
          <button className="btn-ghost sm" onClick={() => setShowHist((s) => !s)}>История</button>
          <button className="btn sm" onClick={save} disabled={!dirty || busy}>{busy ? "…" : "Сохранить и применить"}</button>
        </div>
      </div>
      <p className="admin-hint">
        Одно место: цена, ступень, арт, «продаётся» и «в крутке» для нарядов, фонов и аватарок.
        Сохранение сразу раскладывается по манифесту и барабану — без сборки. Цвет несёт ступень (палитра ниже).
        Новый скин заводится в манифесте (наряд, фон, аватарка), потом «Собрать из манифеста».
      </p>
      <Status loading={loading} error={error} />
      {data != null && (
        <>
          <div className="admin-rowbtns" style={{ margin: "8px 0", gap: 8, flexWrap: "wrap" }}>
            {KINDS.map((k) => (
              <button key={k.key} className={"btn-ghost sm" + (kind === k.key ? " active" : "")} onClick={() => setKind(k.key)}>
                {k.label} <span className="pill">{counts[k.key] || 0}</span>
              </button>
            ))}
            <input className="field" style={{ maxWidth: 260 }} placeholder="поиск по sku или имени" value={q} onChange={(e) => setQ(e.target.value)} />
          </div>
          <div className="admin-tablewrap">
            <table className="adm-table dense">
              <thead>
                <tr>
                  <th>sku</th><th>Имя</th><th>Арт</th>{kind === "backdrop" && <th>Мини</th>}
                  <th>Ступень</th><th>Цена</th><th>Валюта</th><th title="продаётся в гардеробе">Прод.</th>
                  <th title="выпадает в крутках">Крутка</th><th title="свой вес в барабане; 0 — по ступени">Вес</th>
                </tr>
              </thead>
              <tbody>
                {rows.map(({ s, i }) => (
                  <tr key={s.sku}>
                    <td className="muted" title={s.sku}>{shortSku(s)}</td>
                    <td><input className="field" value={s.name || ""} onChange={(e) => edit(i, { name: e.target.value })} /></td>
                    <td><input className="field" value={s.art || ""} onChange={(e) => edit(i, { art: e.target.value })} title={s.art || ""} /></td>
                    {kind === "backdrop" && <td><input className="field" value={s.preview || ""} onChange={(e) => edit(i, { preview: e.target.value })} /></td>}
                    <td>
                      <select className="field" value={s.rarity || ""} onChange={(e) => edit(i, { rarity: e.target.value })}
                              style={{ borderLeft: "4px solid " + ((live.rarity_colors || {})[s.rarity] || "transparent") }}>
                        {RARITIES.map((r) => <option key={r} value={r}>{RARITY_RU[r]}</option>)}
                      </select>
                    </td>
                    <td className="num"><input className="field" type="number" min="0" style={{ width: 90 }} value={s.price || 0} onChange={(e) => edit(i, { price: Number(e.target.value) || 0 })} /></td>
                    <td>
                      <select className="field" value={s.currency || "crystals"} onChange={(e) => edit(i, { currency: e.target.value })}>
                        {CURRENCIES.map((c) => <option key={c} value={c}>{c}</option>)}
                      </select>
                    </td>
                    <td className="num"><input type="checkbox" checked={!!s.buy} onChange={(e) => edit(i, { buy: e.target.checked })} /></td>
                    <td className="num"><input type="checkbox" checked={!!s.gacha} onChange={(e) => edit(i, { gacha: e.target.checked })} /></td>
                    <td className="num"><input className="field" type="number" min="0" step="0.1" style={{ width: 70 }} value={s.gacha_weight || 0} onChange={(e) => edit(i, { gacha_weight: Number(e.target.value) || 0 })} /></td>
                  </tr>
                ))}
                {rows.length === 0 && <tr><td colSpan="10" className="muted">Пусто — нажмите «Собрать из манифеста»</td></tr>}
              </tbody>
            </table>
          </div>

          <h3 style={{ marginTop: 18 }}>Ступени: цвет и вес в крутке</h3>
          <p className="admin-hint">Цвет плиток, рамок и церемонии — у ступени. Вес — доля ступени внутри «Редкого» (сумма любая): чем меньше, тем реже выпадает приз этой ступени.</p>
          <table className="adm-table dense" style={{ maxWidth: 560 }}>
            <thead><tr><th>Ступень</th><th>Цвет</th><th>Вес</th></tr></thead>
            <tbody>
              {RARITIES.filter(Boolean).map((r) => (
                <tr key={r}>
                  <td>{RARITY_RU[r]} <span className="muted">{r}</span></td>
                  <td>
                    <input type="color" value={(live.rarity_colors || {})[r] || "#9d9d9d"} onChange={(e) => editPalette(r, e.target.value)} />
                    <span className="muted" style={{ marginLeft: 8 }}>{(live.rarity_colors || {})[r] || "—"}</span>
                  </td>
                  <td className="num"><input className="field" type="number" min="0" style={{ width: 90 }} value={(live.rarity_weights || {})[r] ?? 0} onChange={(e) => editWeight(r, e.target.value)} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
      {showHist && (
        <HistoryPanel file="skins.json" token={token} notify={notify}
          onRolledBack={() => { setDoc(null); setShowHist(false); reload(); }} />
      )}
    </div>
  );
}

function normalize(data) {
  const d = data && typeof data === "object" ? data : {};
  return { rarity_colors: d.rarity_colors || {}, rarity_weights: d.rarity_weights || {}, skins: Array.isArray(d.skins) ? d.skins : [] };
}

// Пустые поля не сохраняем — каталог остаётся читаемым глазами.
function clean(doc) {
  return {
    rarity_colors: doc.rarity_colors,
    rarity_weights: doc.rarity_weights,
    skins: doc.skins.map((s) => {
      const out = { sku: s.sku, kind: s.kind };
      for (const k of ["name", "art", "preview", "rarity", "currency"]) if (s[k]) out[k] = s[k];
      if (s.price) out.price = s.price;
      if (s.buy) out.buy = true;
      if (s.gacha) out.gacha = true;
      if (s.gacha_weight) out.gacha_weight = s.gacha_weight;
      return out;
    }),
  };
}

function countKinds(skins) {
  const c = {};
  for (const s of skins) c[s.kind] = (c[s.kind] || 0) + 1;
  return c;
}

// «wardrobe:hero:outfit:orchid» читается как «hero · outfit · orchid».
function shortSku(s) {
  const parts = String(s.sku).split(":");
  if (s.kind === "wardrobe" && parts.length === 4) return parts[1] + " · " + parts[2] + " · " + parts[3];
  if (s.kind === "backdrop" && parts.length === 4) return parts[3];
  return s.sku.replace(/^avatar\./, "");
}
