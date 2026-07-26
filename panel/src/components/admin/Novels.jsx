import { useCallback, useEffect, useRef, useState } from "react";
import { adminManifest, adminConflicts, adminFiles, adminHistory, adminRollback } from "../../lib/api.js";
import { useAsync, authMsg, fmt, dt } from "../adminShared.jsx";
import { Page, Drawer, LoadState, Empty, Confirm, Facet, countBy } from "./ui.jsx";
import { countByTitle } from "../../lib/conflicts.js";
import {
  novelRows, stateOf, STATE_LABEL, chapterFilesOf, buildFeed, runPool,
  sourceOfLastChange, SOURCE_LABEL, AUTHOR_UNKNOWN_NOTE,
} from "../../lib/authorhub.js";

// Мои новеллы — the author's cabinet, step two of the path (залил → УВИДЕЛ ЧТО
// ИЗМЕНИЛОСЬ → разобрал конфликты → статистика).
//
// The owner's requirement, literally: "если автор что-то сделает, а админ
// поменяет, надо чтобы автор видел кто когда менял, и чтобы изменения одной
// кнопкой выключались". Two of those three the server can answer honestly —
// WHEN (the .history snapshots) and WHAT (which file, which direction, and
// whether the change came from the engine or from an import). WHO it cannot:
// the whole admin API is one shared bearer token. The cabinet says so out loud
// rather than printing a plausible name.

const localTS = (ms) => new Date(Number(ms)).toLocaleString("ru-RU");

export default function Novels({ token, notify, onNav, onOpenConflicts }) {
  const manifest = useAsync(() => adminManifest(token), [token]);
  const conflicts = useAsync(() => adminConflicts(token, { diff: false }), [token]);
  // content/.lvn-import holds one baseline per title; the directory simply
  // does not exist until the first import, and a 404 there is "нет импортов",
  // not a failure of the page.
  const baselines = useAsync(() => adminFiles(".lvn-import", token).catch(() => ({ files: [] })), [token]);

  const [selected, setSelected] = useState(null);
  const [fAuthor, setFAuthor] = useState([]);
  const [q, setQ] = useState("");

  const titles = (manifest.data && manifest.data.titles) || [];
  const counts = countByTitle((conflicts.data && conflicts.data.conflicts) || []);
  const all = novelRows(titles, counts, (baselines.data && baselines.data.files) || []);

  const needle = q.trim().toLowerCase();
  const rows = all.filter((r) =>
    (!needle || (r.name + " " + r.id).toLowerCase().includes(needle)) &&
    (!fAuthor.length || fAuthor.includes(r.author || "без автора")));

  const withConflicts = all.filter((r) => r.conflicts > 0);
  const parked = withConflicts.reduce((n, r) => n + r.conflicts, 0);
  const current = selected ? all.find((r) => r.id === selected) : null;

  return (
    <Page
      title="Мои новеллы"
      count={manifest.data ? all.length : null}
      description="Состояние каждой новеллы: разобраны ли конфликты импорта, когда её обновляли и что в ней меняли руками."
      actions={
        <>
          <Facet label="Автор" options={countBy(all, (r) => r.author || "без автора")} selected={fAuthor} onChange={setFAuthor} />
          <input className="field adm-search" placeholder="поиск по названию" value={q} onChange={(e) => setQ(e.target.value)} />
          <button className="adm-iconbtn" title="обновить"
                  onClick={() => { manifest.reload(); conflicts.reload(); baselines.reload(); }}>⟳</button>
        </>
      }
    >
      {parked > 0 && (
        <div className="adm-alert">
          <span>
            Не разобрано файлов: <b>{parked}</b> в {withConflicts.length}{" "}
            {withConflicts.length === 1 ? "новелле" : "новеллах"} — пока они висят, у этих новелл
            две версии: игрокам отдаётся ваша, импорт лежит рядом.
          </span>
          <button className="adm-link" onClick={() => onOpenConflicts()}>Разобрать →</button>
        </div>
      )}

      <LoadState
        loading={manifest.loading} error={manifest.error} empty={!rows.length}
        emptyText={needle ? "Ничего не совпало." : "В манифесте пока нет новелл."}
      >
        <div className="adm-tablewrap">
          <table className="adm-table">
            <thead>
              <tr>
                <th>новелла</th><th>автор</th><th className="num">глав</th>
                <th>состояние</th><th>база импорта</th><th></th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => {
                const st = stateOf(r);
                return (
                  <tr key={r.id} className="adm-row" onClick={() => setSelected(r.id)}>
                    <td>
                      <span className="adm-cell-main">{r.name}</span>
                      <code className="adm-cell-sub adm-nomargin">{r.id}</code>
                    </td>
                    <td>{r.author ? <span className="pill">{r.author}</span> : <span className="muted">—</span>}</td>
                    <td className="num">{r.chapters}</td>
                    <td>
                      <span className={"adm-state " + st}>{STATE_LABEL[st]}</span>
                      {r.conflicts > 0 && <span className="pill warn-pill">{r.conflicts}</span>}
                    </td>
                    <td className="muted">{r.importedAt ? dt(r.importedAt, 16) : "не импортировалась"}</td>
                    <td className="num">
                      <button className="btn-ghost sm" onClick={(e) => { e.stopPropagation(); setSelected(r.id); }}>Изменения ▸</button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </LoadState>

      {!manifest.loading && all.some((r) => !r.author) && (
        <p className="adm-dim adm-foot-note">
          Новелл без поля <code>author</code> в манифесте: {all.filter((r) => !r.author).length} — их события
          не попадут в разрез «по авторам» в аналитике.{" "}
          <button className="adm-link" onClick={() => onNav("manifest")}>Открыть манифест →</button>
        </p>
      )}

      {current && (
        <Drawer title={current.name} width={720} onClose={() => setSelected(null)}>
          <NovelDetail
            key={current.id}
            row={current}
            token={token}
            notify={notify}
            onOpenConflicts={() => onOpenConflicts(current.id)}
          />
        </Drawer>
      )}
    </Page>
  );
}

// NovelDetail: one novel's change feed. There is no batch history endpoint —
// /v1/admin/history answers for ONE file — so the cabinet asks per file
// through a small pool instead of firing fifty parallel requests from a tab.
function NovelDetail({ row, token, notify, onOpenConflicts }) {
  const files = chapterFilesOf(row.title);
  const [state, setState] = useState({ loading: true, error: "", history: {}, mtimes: {} });
  const [confirmRow, setConfirmRow] = useState(null);
  // Request token, not a "still mounted" flag: React StrictMode mounts an
  // effect, tears it down and mounts it again, so a boolean ref set to false
  // by the first cleanup would leave the second run unable to publish its
  // result — the drawer then sits on «опрашиваем…» forever (seen live).
  const reqRef = useRef(0);

  const load = useCallback(async () => {
    const req = ++reqRef.current;
    const current = () => reqRef.current === req;
    setState((s) => ({ ...s, loading: true, error: "" }));
    try {
      // mtimes come from the directory listings the files live in (one listing
      // per distinct directory, not one per file) — they are what tells an
      // import-written file from an edited one.
      const dirs = [...new Set(files.map((f) => f.rel.split("/").slice(0, -1).join("/")))];
      const listings = await runPool(dirs, 4, (d) => adminFiles(d, token).catch(() => ({ dir: d, files: [] })));
      const mtimes = {};
      listings.forEach((l, i) => {
        for (const f of (l && l.files) || []) {
          if (!f.dir) mtimes[dirs[i] + "/" + f.name] = f.modified;
        }
      });
      const results = await runPool(files.slice(0, 120), 5, (f) =>
        adminHistory(f.rel, token).then((d) => d.versions || []).catch(() => []));
      const history = {};
      files.slice(0, 120).forEach((f, i) => { history[f.rel] = results[i] || []; });
      if (current()) setState({ loading: false, error: "", history, mtimes });
    } catch (e) {
      if (current()) setState({ loading: false, error: authMsg(e), history: {}, mtimes: {} });
    }
    // files is derived from row.title, which is stable for the drawer's life
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [row.id, token]);

  useEffect(() => { load(); }, [load]);

  const feed = buildFeed(state.history, files);
  const newestOf = (rel) => {
    const v = state.history[rel] || [];
    return v.length ? String(v.reduce((a, b) => (Number(a.ts) > Number(b.ts) ? a : b)).ts) : "";
  };

  async function rollback(rel, ts) {
    try {
      await adminRollback(rel, ts, token);
      notify("✓ Версия возвращена — клиенты подхватят сами", "ok");
      load();
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
  }

  const edited = files.filter((f) => (state.history[f.rel] || []).length);
  // A chapter's .lvns source is only written when the novel was imported (or
  // authored in Studio); listing a path that does not exist on disk would fill
  // the table with "— / неизвестно" rows. Filter only once the directory
  // listing actually answered, so a failed listing shows everything instead of
  // nothing.
  const listed = Object.keys(state.mtimes).length > 0;
  const shown = files.slice(0, 120).filter((f) =>
    !listed || state.mtimes[f.rel] || (state.history[f.rel] || []).length);

  return (
    <>
      <div className="adm-id-block">
        <code>{row.id}</code>
        <b>{row.name}</b>
        <span className="adm-dim">
          {row.author ? <>автор: <b>{row.author}</b></> : "автор не указан в манифесте"} · глав: {row.chapters}
        </span>
      </div>

      {row.conflicts > 0 && (
        <div className="adm-alert">
          <span>{row.conflicts} файл(ов) ждут разбора после импорта.</span>
          <button className="adm-link" onClick={onOpenConflicts}>Разобрать →</button>
        </div>
      )}

      <div className="nv-facts">
        <div><span className="adm-kpi-label">база импорта</span><b>{row.importedAt ? dt(row.importedAt, 16) : "—"}</b></div>
        <div><span className="adm-kpi-label">файлов новеллы</span><b>{shown.length}</b></div>
        <div><span className="adm-kpi-label">правок в движке</span><b>{feed.length}</b></div>
      </div>

      <section className="adm-drawer-section">
        <h3>Файлы новеллы</h3>
        {state.loading && <p className="adm-dim">Опрашиваем историю по файлам…</p>}
        {state.error && <Empty icon="⚠" text={state.error} />}
        {!state.loading && !files.length && <p className="adm-dim">В манифесте у новеллы нет глав со скриптами.</p>}
        {!state.loading && files.length > 0 && (
          <table className="adm-table dense">
            <thead><tr><th>файл</th><th>последнее изменение</th><th>источник</th><th className="num">правок</th></tr></thead>
            <tbody>
              {shown.map((f) => {
                const versions = state.history[f.rel] || [];
                const newest = newestOf(f.rel);
                const src = sourceOfLastChange(state.mtimes[f.rel], newest);
                return (
                  <tr key={f.rel}>
                    <td><code>{f.rel.split("/").pop()}</code>{f.chapter && <span className="adm-dim"> · {f.chapter}</span>}</td>
                    <td className="muted">{state.mtimes[f.rel] ? dt(state.mtimes[f.rel], 16) : "—"}</td>
                    <td><span className={"adm-src " + src}>{SOURCE_LABEL[src]}</span></td>
                    <td className="num">{versions.length || <span className="muted">0</span>}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </section>

      <section className="adm-drawer-section">
        <h3>Лента правок</h3>
        <p className="adm-dim">{AUTHOR_UNKNOWN_NOTE}</p>
        {!state.loading && !feed.length && (
          <p className="adm-dim">Внутри движка эту новеллу ещё не правили — всё, что на диске, положил импорт.</p>
        )}
        {feed.length > 0 && (
          <>
            {edited.length > 0 && (
              <div className="nv-undo">
                <span>Одной кнопкой отменить последнюю правку:</span>
                {edited.map((f) => (
                  <button key={f.rel} className="btn-ghost sm"
                          onClick={() => setConfirmRow({ rel: f.rel, ts: newestOf(f.rel), last: true })}>
                    ↩ {f.rel.split("/").pop()}
                  </button>
                ))}
              </div>
            )}
            <table className="adm-table dense">
              <thead><tr><th>когда</th><th>файл</th><th className="num">размер до</th><th></th></tr></thead>
              <tbody>
                {feed.map((r) => (
                  <tr key={r.rel + ":" + r.ts}>
                    <td>{localTS(r.ts)}</td>
                    <td>
                      <code>{r.rel.split("/").pop()}</code>
                      {r.chapter && <span className="adm-dim"> · {r.chapter}</span>}
                      {r.undoLast && <span className="pill">последняя</span>}
                    </td>
                    <td className="num muted">
                      {fmt(r.size)} b
                      {r.prevSize != null && (
                        <span className={r.size > r.prevSize ? " amt-plus" : r.size < r.prevSize ? " amt-minus" : " muted"}>
                          {" "}({r.size > r.prevSize ? "+" : "−"}{fmt(Math.abs(r.size - r.prevSize))})
                        </span>
                      )}
                    </td>
                    <td className="num">
                      <button className="btn-ghost sm" onClick={() => setConfirmRow({ rel: r.rel, ts: r.ts, last: r.undoLast })}>
                        {r.undoLast ? "Отменить" : "Вернуть эту"}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <p className="adm-dim">
              Каждая строка — состояние файла ПЕРЕД той правкой. «Отменить» на верхней строке файла возвращает
              его к виду до последнего изменения; сам откат тоже версионируется, так что он обратим.
            </p>
          </>
        )}
      </section>

      {confirmRow && (
        <Confirm
          title={confirmRow.last ? "Отменить последнюю правку" : "Вернуть эту версию"}
          dangerLabel={confirmRow.last ? "Отменить правку" : "Вернуть"}
          body={
            <p>
              <code>{confirmRow.rel}</code> вернётся к состоянию от <b>{localTS(confirmRow.ts)}</b>.
              Текущая версия сначала уйдёт в историю, так что откат обратим.
            </p>
          }
          onConfirm={() => rollback(confirmRow.rel, confirmRow.ts)}
          onClose={() => setConfirmRow(null)}
        />
      )}
    </>
  );
}
