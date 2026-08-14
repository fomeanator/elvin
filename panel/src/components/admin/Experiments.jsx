import { useEffect, useState } from "react";
import { adminExperiments, adminSaveExperiments, analyticsExperiment } from "../../lib/api.js";
import { useAsync, fmt } from "../adminShared.jsx";
import { Page, LoadState, Empty } from "./ui.jsx";

// ЭКСПЕРИМЕНТЫ — то, чем крутят продукт, не пересобирая игру.
//
// Развилку в истории ставит автор в .lvns. Здесь решают, КОМУ она достанется:
// доля трафика, аудитория, слой, выключатель. Разделение не косметическое —
// проценты, записанные в сценарий, означали бы, что плохой вариант нельзя
// погасить до следующей сборки.
//
// Два предупреждения перенесены сюда из сервера намеренно: их проще не заметить
// в тексте ошибки, чем в интерфейсе, а цена обоих — испорченные данные.

const emptyExperiment = () => ({
  name: "",
  layer: "",
  version: 1,
  enabled: false,
  variants: [{ id: "a", weight: 50 }, { id: "b", weight: 50 }],
  audience: {},
  note: "",
});

export default function Experiments({ token, notify }) {
  const list = useAsync(() => adminExperiments(token), [token]);
  const [draft, setDraft] = useState(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (list.data && draft === null) setDraft(list.data.experiments || []);
  }, [list.data, draft]);

  const items = draft || [];
  const set = (i, patch) =>
    setDraft(items.map((e, k) => (k === i ? { ...e, ...patch } : e)));

  const save = async () => {
    setBusy(true);
    try {
      await adminSaveExperiments(items, token);
      notify?.("Сохранено", "ok");
      list.reload();
    } catch (e) {
      // Сервер отвечает объяснением последствия, а не кодом — показываем его
      // целиком: там сказано, что именно сломается.
      notify?.(String(e.message || e), "err");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Page
      title="Эксперименты"
      description={"Развилку ставит автор в сценарии: если abtest(\"имя\") == \"b\". " +
        "Здесь решают, кому она достанется — доля, аудитория, слой. Всё меняется без новой сборки."}
      actions={
        <>
          <button className="btn" onClick={() => setDraft([...items, emptyExperiment()])}>
            + эксперимент
          </button>
          <button className="btn primary" disabled={busy} onClick={save}>
            {busy ? "сохраняю…" : "Сохранить"}
          </button>
          <button className="adm-iconbtn" onClick={() => { setDraft(null); list.reload(); }} title="сбросить правки">⟳</button>
        </>
      }
    >
      <LoadState loading={list.loading} error={list.error}>
        {!items.length ? (
          <Empty text="Экспериментов нет. Первый имеет смысл ставить туда, где вы уже подозреваете потерю." />
        ) : (
          items.map((e, i) => (
            <Experiment key={i} e={e} token={token}
                        onChange={(p) => set(i, p)}
                        onRemove={() => setDraft(items.filter((_, k) => k !== i))} />
          ))
        )}
      </LoadState>
    </Page>
  );
}

function Experiment({ e, token, onChange, onRemove }) {
  const [open, setOpen] = useState(false);
  const total = (e.variants || []).reduce((n, v) => n + (Number(v.weight) || 0), 0);
  const share = (v) => (total > 0 ? Math.round((Number(v.weight) || 0) * 100 / total) : Math.round(100 / (e.variants || []).length));

  return (
    <section className="adm-panel">
      <header className="adm-panel-head">
        <h2>{e.name || "без имени"}</h2>
        <span className="adm-dim">
          {e.enabled ? "идёт" : "выключен"}
          {e.layer ? " · слой " + e.layer : ""} · версия {e.version || 1}
        </span>
      </header>

      <div className="exp-grid">
        <label className="adm-dim">Имя <span className="adm-dim">— то же, что в abtest("…")</span>
          <input className="field mono" value={e.name}
                 onChange={(ev) => onChange({ name: ev.target.value })} />
        </label>
        <label className="adm-dim" title="Внутри слоя игрок участвует ровно в одном эксперименте: иначе два теста накладываются и эффекты смешиваются">
          Слой
          <input className="field mono" value={e.layer || ""} placeholder="сюжет / экономика"
                 onChange={(ev) => onChange({ layer: ev.target.value })} />
        </label>
        <label className="adm-dim">Версия
          <input type="number" min="1" className="field" value={e.version || 1}
                 onChange={(ev) => onChange({ version: Number(ev.target.value) || 1 })} />
        </label>
        <label className="export-check">
          <input type="checkbox" checked={!!e.enabled}
                 onChange={(ev) => onChange({ enabled: ev.target.checked })} />
          <span>Идёт (выключенный отдаёт всем первый вариант — история продолжает играться)</span>
        </label>
      </div>

      <h3 className="adm-dim">Варианты и доли</h3>
      {(e.variants || []).map((v, k) => (
        <div className="exp-variant" key={k}>
          <input className="field mono" value={v.id} placeholder="имя варианта"
                 onChange={(ev) => onChange({
                   variants: e.variants.map((x, j) => (j === k ? { ...x, id: ev.target.value } : x)),
                 })} />
          <input type="number" min="0" className="field" value={v.weight ?? 0}
                 onChange={(ev) => onChange({
                   variants: e.variants.map((x, j) => (j === k ? { ...x, weight: Number(ev.target.value) || 0 } : x)),
                 })} />
          <span className="adm-dim">{share(v)}% трафика</span>
          {k === 0 && <span className="adm-dim">— «как было», с ним сравнивают</span>}
        </div>
      ))}
      <button className="btn" onClick={() => onChange({
        variants: [...(e.variants || []), { id: "", weight: 0 }],
      })}>+ вариант</button>

      <p className="adm-dim warn-note">
        Поменяли доли — <b>поднимите версию</b>. Иначе часть игроков молча переедет
        в другие группы, а старые и новые данные смешаются: цифры останутся
        правдоподобными и будут означать не то.
      </p>

      <h3 className="adm-dim">Кому показывать</h3>
      <div className="exp-grid">
        <label className="adm-dim">Канал привлечения
          <input className="field mono" value={e.audience?.channel || ""} placeholder="telegram/aug"
                 onChange={(ev) => onChange({ audience: { ...e.audience, channel: ev.target.value } })} />
        </label>
        <label className="adm-dim">Когорта (день прихода)
          <input className="field mono" value={e.audience?.cohort || ""} placeholder="2026-08-14"
                 onChange={(ev) => onChange({ audience: { ...e.audience, cohort: ev.target.value } })} />
        </label>
        <label className="adm-dim">Платящие
          <select className="field" value={e.audience?.payer || ""}
                  onChange={(ev) => onChange({ audience: { ...e.audience, payer: ev.target.value } })}>
            <option value="">все</option>
            <option value="yes">только платящие</option>
            <option value="no">только неплатящие</option>
          </select>
        </label>
      </div>
      <p className="adm-dim">Пустое поле значит «всем», а не «никому».</p>

      <div className="exp-actions">
        <button className="btn" onClick={() => setOpen(!open)}>
          {open ? "скрыть результат" : "Результат"}
        </button>
        <button className="btn danger" onClick={onRemove}>удалить</button>
      </div>
      {open && e.name && <Result name={e.name} token={token} />}
    </section>
  );
}

// Результат: цель и предохранители рядом. Одного числа здесь быть не может —
// вариант, поднявший покупки и уронивший дочитывание, это не победа.
function Result({ name, token }) {
  const rep = useAsync(() => analyticsExperiment("days=30", name, token), [name, token]);
  const d = rep.data || {};
  const pct = (v) => (v == null ? "—" : Math.round(v * 1000) / 10 + "%");

  return (
    <LoadState loading={rep.loading} error={rep.error}>
      {d.note && <p className="adm-dim">⚠ {d.note}</p>}
      {!(d.variants || []).length ? <Empty text="Данных по этому эксперименту пока нет." /> : (
        <>
          <div className="adm-tablewrap">
            <table className="adm-table">
              <thead><tr>
                <th>вариант</th><th className="num">игроков</th><th>дочитали</th>
                <th>вернулись</th><th>платят</th><th className="num">ARPU</th>
              </tr></thead>
              <tbody>
                {d.variants.map((v) => (
                  <tr key={v.variant}>
                    <td><span className="adm-cell-main">{v.variant}</span></td>
                    <td className="num">{fmt(v.players)}</td>
                    <td>{pct(v.completion)}</td>
                    <td>{pct(v.retention_d1)}</td>
                    <td>{pct(v.conversion)}</td>
                    <td className="num">{v.arpu}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <ul className="exp-verdict">
            {(d.verdict || []).map((v, i) => (
              <li key={i} className={v.significant ? "sig" : ""}>
                <b>{v.metric}</b>: {v.text}
                {v.need_players ? " (сейчас в группе меньше)" : ""}
              </li>
            ))}
          </ul>
          {(d.notes || []).map((n, i) => <p key={i} className="adm-dim">{n}</p>)}
        </>
      )}
    </LoadState>
  );
}
