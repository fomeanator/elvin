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
                  <th>sku</th><th>Имя</th><th>Описание</th><th>Арт</th>{kind === "backdrop" && <th>Мини</th>}{kind === "backdrop" && <th>Спайн</th>}
                  <th>Ступень</th><th>Цена</th><th>Валюта</th><th title="продаётся в гардеробе">Прод.</th>
                  <th title="выпадает в крутках">Крутка</th><th title="свой вес в барабане; 0 — по ступени">Вес</th>
                  <th title="за сколько продаётся копия из крутки; 0 — за цену">Копия</th>
                  <th title="порядок показа; 0 — как в манифесте">Поряд.</th>
                  <th title="метки через запятую">Метки</th>
                  <th title="не показывать нигде; у игроков остаётся">Скрыт</th>
                  <th title="в каких наборах приз (id через запятую); пусто при «Крутка» — первый набор">Наборы</th>
                </tr>
              </thead>
              <tbody>
                {rows.map(({ s, i }) => (
                  <tr key={s.sku}>
                    <td className="muted" title={s.sku}>{shortSku(s)}</td>
                    <td><input className="field" value={s.name || ""} onChange={(e) => edit(i, { name: e.target.value })} /></td>
                    <td><input className="field" style={{ minWidth: 200 }} value={s.description || ""} placeholder="текст в подробностях" onChange={(e) => edit(i, { description: e.target.value })} title={s.description || ""} /></td>
                    <td><input className="field" value={s.art || ""} onChange={(e) => edit(i, { art: e.target.value })} title={s.art || ""} /></td>
                    {kind === "backdrop" && <td><input className="field" value={s.preview || ""} onChange={(e) => edit(i, { preview: e.target.value })} /></td>}
                    {kind === "backdrop" && <td><input className="field" value={s.spine || ""} placeholder="/content/spine/имя/" title="живой фон: папка спайна; арт остаётся обложкой и подложкой" onChange={(e) => edit(i, { spine: e.target.value })} /></td>}
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
                    <td className="num"><input className="field" type="number" min="0" style={{ width: 80 }} value={s.sell_price || 0} onChange={(e) => edit(i, { sell_price: Number(e.target.value) || 0 })} /></td>
                    <td className="num"><input className="field" type="number" min="0" style={{ width: 64 }} value={s.order || 0} onChange={(e) => edit(i, { order: Number(e.target.value) || 0 })} /></td>
                    <td><input className="field" style={{ minWidth: 110 }} value={s.tags || ""} onChange={(e) => edit(i, { tags: e.target.value })} /></td>
                    <td className="num"><input type="checkbox" checked={!!s.hidden} onChange={(e) => edit(i, { hidden: e.target.checked })} /></td>
                    <td><input className="field" style={{ width: 110 }} value={(s.cases || []).join(",")} placeholder={(live.cases?.[0]?.id) || "base"} onChange={(e) => edit(i, { cases: e.target.value.split(",").map((x) => x.trim()).filter(Boolean) })} /></td>
                  </tr>
                ))}
                {rows.length === 0 && <tr><td colSpan="16" className="muted">Пусто — нажмите «Собрать из манифеста»</td></tr>}
              </tbody>
            </table>
          </div>

          <NewSkinForm kind={kind} onAdd={(sk) => { const next = structuredClone(live); next.skins.push(sk); setDoc(next); }} existing={live.skins || []} />

          <h3 style={{ marginTop: 18 }}>Валюты</h3>
          <p className="admin-hint">Как валюта выглядит везде — шапка, ценники, плитки, крутки: имя, форма при сумме («1 200 кристаллов»), цвет, картинка (адрес в контенте, например /content/ui/stage/icon-crystal.png). Без картинки — векторный значок движка (Gem, Energy, Coin, Key, Heart). Сохранил — картинка сменилась по всему приложению.</p>
          <CurrenciesTable currencies={live.currencies || {}} onChange={(currencies) => { const next = structuredClone(live); next.currencies = currencies; setDoc(next); }} />

          <h3 style={{ marginTop: 18 }}>Наборы круток</h3>
          <p className="admin-hint">Кейсы на выбор в крутке: имя, описание, обложка, цена крутки; приз попадает в набор, если тот назван у скина в колонке «Наборы» (пусто при «Крутка» — первый набор). Один набор — выбора в игре нет.</p>
          <CasesTable cases={live.cases || []} onChange={(cases) => { const next = structuredClone(live); next.cases = cases; setDoc(next); }} />

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

// НОВЫЙ СКИН ИЗ АДМИНКИ (Илья: «по максимуму»): аватарка и фон заводятся в
// манифесте при применении, наряд — если у героя есть такая ось.
function NewSkinForm({ kind, onAdd, existing }) {
  const [f, setF] = useState({ entity: "", axis: "", id: "", name: "", art: "" });
  const set = (k) => (e) => setF({ ...f, [k]: e.target.value });
  const sku = kind === "wardrobe" ? (f.entity && f.axis && f.id ? `wardrobe:${f.entity}:${f.axis}:${f.id}` : "")
    : kind === "backdrop" ? (f.id ? `wardrobe:menu:backdrop:${f.id}` : "")
    : (f.id ? `avatar.${f.id}` : "");
  const taken = sku && existing.some((s) => s.sku === sku);
  const ok = sku && !taken && f.art;
  return (
    <div className="admin-rowbtns" style={{ marginTop: 10, gap: 6, flexWrap: "wrap", alignItems: "center" }}>
      <span className="muted">Новый:</span>
      {kind === "wardrobe" && <input className="field" style={{ width: 120 }} placeholder="герой (id)" value={f.entity} onChange={set("entity")} />}
      {kind === "wardrobe" && <input className="field" style={{ width: 110 }} placeholder="ось (outfit…)" value={f.axis} onChange={set("axis")} />}
      <input className="field" style={{ width: 130 }} placeholder={kind === "wardrobe" ? "значение" : "id"} value={f.id} onChange={set("id")} />
      <input className="field" style={{ width: 160 }} placeholder="имя" value={f.name} onChange={set("name")} />
      <input className="field" style={{ width: 260 }} placeholder="арт: /content/…" value={f.art} onChange={set("art")} />
      <button className="btn-ghost sm" disabled={!ok} title={taken ? "такой sku уже есть" : sku} onClick={() => {
        onAdd({ sku, kind, name: f.name, art: f.art, currency: "crystals" });
        setF({ entity: "", axis: "", id: "", name: "", art: "" });
      }}>Добавить</button>
      {sku && <span className="muted">{sku}</span>}
    </div>
  );
}

function CasesTable({ cases, onChange }) {
  const edit = (i, patch) => { const next = cases.map((c, j) => (j === i ? { ...c, ...patch } : c)); onChange(next); };
  const add = () => onChange([...cases, { id: "case" + (cases.length + 1), name: "Новый набор", spin_currency: "crystals", spin_price: 50 }]);
  const remove = (i) => onChange(cases.filter((_, j) => j !== i));
  return (
    <div className="admin-tablewrap">
      <table className="adm-table dense">
        <thead><tr><th>id</th><th>Имя</th><th>Описание</th><th>Обложка</th><th>Цена</th><th>Валюта</th><th>Поряд.</th><th>Скрыт</th><th></th></tr></thead>
        <tbody>
          {cases.map((c, i) => (
            <tr key={i}>
              <td><input className="field" style={{ width: 90 }} value={c.id || ""} onChange={(e) => edit(i, { id: e.target.value.trim() })} /></td>
              <td><input className="field" value={c.name || ""} onChange={(e) => edit(i, { name: e.target.value })} /></td>
              <td><input className="field" style={{ minWidth: 200 }} value={c.description || ""} onChange={(e) => edit(i, { description: e.target.value })} /></td>
              <td><input className="field" value={c.cover || ""} placeholder="/content/…" onChange={(e) => edit(i, { cover: e.target.value })} /></td>
              <td className="num"><input className="field" type="number" min="0" style={{ width: 80 }} value={c.spin_price || 0} onChange={(e) => edit(i, { spin_price: Number(e.target.value) || 0 })} /></td>
              <td>
                <select className="field" value={c.spin_currency || "crystals"} onChange={(e) => edit(i, { spin_currency: e.target.value })}>
                  {CURRENCIES.map((x) => <option key={x} value={x}>{x}</option>)}
                </select>
              </td>
              <td className="num"><input className="field" type="number" min="0" style={{ width: 64 }} value={c.order || 0} onChange={(e) => edit(i, { order: Number(e.target.value) || 0 })} /></td>
              <td className="num"><input type="checkbox" checked={!!c.hidden} onChange={(e) => edit(i, { hidden: e.target.checked })} /></td>
              <td className="num"><button className="btn-ghost sm" onClick={() => remove(i)} title="убрать набор из каталога (призы у скинов остаются)">×</button></td>
            </tr>
          ))}
          {cases.length === 0 && <tr><td colSpan="9" className="muted">Наборов нет — один набор по умолчанию из верхнего уровня барабана</td></tr>}
        </tbody>
      </table>
      <button className="btn-ghost sm" style={{ marginTop: 6 }} onClick={add}>+ набор</button>
    </div>
  );
}

function normalize(data) {
  const d = data && typeof data === "object" ? data : {};
  return { rarity_colors: d.rarity_colors || {}, rarity_weights: d.rarity_weights || {}, currencies: d.currencies || {}, cases: Array.isArray(d.cases) ? d.cases : [], skins: Array.isArray(d.skins) ? d.skins : [] };
}

// ВАЛЮТЫ — ОДНО МЕСТО (TR-117): раньше картинка кристалла жила в четырёх
// картах манифеста, и половина экранов рисовала вектор-заглушку.
function CurrenciesTable({ currencies, onChange }) {
  const ids = Object.keys(currencies);
  const edit = (id, patch) => onChange({ ...currencies, [id]: { ...(currencies[id] || {}), ...patch } });
  const rename = (from, to) => {
    if (!to || to === from || currencies[to]) return;
    const next = {}; for (const k of ids) next[k === from ? to : k] = currencies[k]; onChange(next);
  };
  const remove = (id) => { const next = { ...currencies }; delete next[id]; onChange(next); };
  const add = () => { const id = ids.includes("crystals") ? "currency" + (ids.length + 1) : "crystals"; onChange({ ...currencies, [id]: { name: "", unit: "", icon: "Gem", color: "#f0c860", image: "" } }); };
  return (
    <div>
      <table className="adm-table dense">
        <thead><tr><th>id</th><th>Имя</th><th>Единица</th><th>Цвет</th><th>Картинка</th><th>Вектор</th><th></th></tr></thead>
        <tbody>
          {ids.map((id) => { const c = currencies[id] || {}; return (
            <tr key={id}>
              <td><input className="field" style={{ width: 110 }} defaultValue={id} onBlur={(e) => rename(id, e.target.value.trim())} /></td>
              <td><input className="field" style={{ width: 130 }} value={c.name || ""} onChange={(e) => edit(id, { name: e.target.value })} /></td>
              <td><input className="field" style={{ width: 130 }} value={c.unit || ""} onChange={(e) => edit(id, { unit: e.target.value })} /></td>
              <td><input type="color" value={c.color || "#f0c860"} onChange={(e) => edit(id, { color: e.target.value })} /> <span className="muted">{c.color || "—"}</span></td>
              <td><input className="field" style={{ width: 280 }} placeholder="/content/ui/….png" value={c.image || ""} onChange={(e) => edit(id, { image: e.target.value })} /></td>
              <td><input className="field" style={{ width: 90 }} placeholder="Gem" value={c.icon || ""} onChange={(e) => edit(id, { icon: e.target.value })} /></td>
              <td><button className="btn-ghost sm" onClick={() => remove(id)}>✕</button></td>
            </tr>); })}
        </tbody>
      </table>
      <button className="btn-ghost sm" style={{ marginTop: 6 }} onClick={add}>+ валюта</button>
    </div>
  );
}

// Пустые поля не сохраняем — каталог остаётся читаемым глазами.
function clean(doc) {
  return {
    rarity_colors: doc.rarity_colors,
    rarity_weights: doc.rarity_weights,
    currencies: Object.fromEntries(Object.entries(doc.currencies || {}).filter(([id]) => id).map(([id, c]) => {
      const out = {}; for (const k of ["name", "unit", "icon", "color", "image"]) if (c && c[k]) out[k] = c[k]; return [id, out];
    })),
    cases: (doc.cases || []).filter((c) => c.id).map((c) => {
      const out = { id: c.id };
      for (const k of ["name", "description", "cover", "spin_currency"]) if (c[k]) out[k] = c[k];
      for (const k of ["spin_price", "order"]) if (c[k]) out[k] = c[k];
      if (c.hidden) out.hidden = true;
      if (c.sectors) out.sectors = c.sectors;
      return out;
    }),
    skins: doc.skins.map((s) => {
      const out = { sku: s.sku, kind: s.kind };
      for (const k of ["name", "description", "art", "preview", "rarity", "currency", "tags"]) if (s[k]) out[k] = s[k];
      for (const k of ["price", "gacha_weight", "sell_price", "order"]) if (s[k]) out[k] = s[k];
      for (const k of ["buy", "gacha", "hidden"]) if (s[k]) out[k] = true;
      if (Array.isArray(s.cases) && s.cases.length) out.cases = s.cases;
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
