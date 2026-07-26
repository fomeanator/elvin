import { useCallback, useEffect, useState } from "react";
import { adminConflicts, adminResolveConflict } from "../../lib/api.js";
import { useAsync, authMsg, fmt, dt } from "../adminShared.jsx";
import { Page, Drawer, LoadState, Empty, Confirm } from "./ui.jsx";
import {
  parseUnifiedDiff, isImagePath, kindOf, KIND_LABEL, titleOf,
  groupByTitle, sizeDelta, canKeepMine, previewUrl, shortSha,
} from "../../lib/conflicts.js";

// Разбор конфликтов — step three of the author's path (залил → увидел →
// РАЗОБРАЛ → посмотрел статистику).
//
// A re-import that disagrees with a hand edit writes nothing: the new version
// is parked as <file>.incoming and the author's file stays live. That is safe
// but UNFINISHED — until somebody chooses a side, the novel's players are on
// one version and the export says another. This screen is the only place that
// state is visible, so it leads with it instead of hiding it in a tab badge.

export default function Conflicts({ token, notify, focusTitle, onFocusUsed, onNav, onChanged }) {
  const { loading, error, data, reload } = useAsync(() => adminConflicts(token), [token]);
  const [open, setOpen] = useState(null);      // rel of the conflict in the drawer
  const [busy, setBusy] = useState("");        // rel currently being resolved
  const [bulk, setBulk] = useState(null);      // {choice} awaiting confirmation
  const [bulkRun, setBulkRun] = useState(null); // {done, total, failed:[]}
  // Arriving from a novel's cabinet pre-filters to that novel. The focus is
  // consumed on mount: opening «Конфликты» from the menu later must show
  // everything, not a filter set three clicks ago on another screen.
  const [only, setOnly] = useState(focusTitle || "");
  useEffect(() => { if (focusTitle && onFocusUsed) onFocusUsed(); }, [focusTitle, onFocusUsed]);

  const all = (data && data.conflicts) || [];
  const groups = groupByTitle(all);
  const rows = only ? all.filter((c) => titleOf(c) === only) : all;

  // Resolving is the one destructive-ish action here, and it is done one file
  // at a time on purpose: each call re-validates the chosen bytes server-side
  // and can fail on its own (422 on a broken .lvn), so a shared "success"
  // would be a lie for the file that was rejected.
  const resolve = useCallback(async (rel, choice) => {
    setBusy(rel);
    try {
      const res = await adminResolveConflict(rel, choice, token);
      notify(`✓ ${rel}: ${choice === "mine" ? "оставлена ваша версия" : "принята версия из импорта"}` +
        (res && res.warnings && res.warnings.length ? ` (${res.warnings.length} предупр.)` : ""), "ok");
      setOpen((cur) => (cur === rel ? null : cur));
      reload();
      // The nav badge is the shell's "state undefined" flag — it must drop the
      // moment a file is decided, not 30 seconds later on the next poll.
      if (onChanged) onChanged();
      return true;
    } catch (e) {
      notify("✗ " + rel + ": " + authMsg(e), "err");
      return false;
    } finally { setBusy(""); }
  }, [token, notify, reload, onChanged]);

  async function runBulk(choice) {
    const list = rows.map((c) => c.rel);
    setBulkRun({ done: 0, total: list.length, failed: [] });
    const failed = [];
    for (let i = 0; i < list.length; i++) {
      try {
        await adminResolveConflict(list[i], choice, token);
      } catch (e) {
        failed.push(list[i] + " — " + authMsg(e));
      }
      setBulkRun({ done: i + 1, total: list.length, failed: [...failed] });
    }
    reload();
    if (onChanged) onChanged();
    if (failed.length) notify(`Разобрано ${list.length - failed.length} из ${list.length}; ${failed.length} не удалось`, "err");
    else notify(`✓ Разобрано файлов: ${list.length}`, "ok");
    setTimeout(() => setBulkRun(null), failed.length ? 8000 : 1200);
  }

  const current = all.find((c) => c.rel === open) || null;

  return (
    <Page
      title="Конфликты импорта"
      count={data ? (only ? `${rows.length} из ${all.length}` : all.length) : null}
      description={
        all.length
          ? "Импорт НЕ перезаписал эти файлы: в них есть и ваша правка, и новая версия из articy. Пока файл не разобран, у новеллы две правды — игрокам отдаётся ваша, экспорт лежит рядом."
          : "Импорт не перезаписывает ручные правки: спорные файлы паркуются здесь как <файл>.incoming и ждут решения."
      }
      actions={
        <>
          {groups.length > 1 && (
            <select className="field adm-pagesize adm-titlepick" value={only} onChange={(e) => setOnly(e.target.value)}>
              <option value="">все новеллы</option>
              {groups.map((g) => (
                <option key={g.title || "—"} value={g.title}>
                  {(g.title || "вне новелл") + " (" + g.rows.length + ")"}
                </option>
              ))}
            </select>
          )}
          {rows.length > 1 && (
            <>
              <button className="btn-ghost sm" disabled={!!bulkRun} onClick={() => setBulk({ choice: "mine" })}>
                Оставить все свои
              </button>
              <button className="btn-ghost sm" disabled={!!bulkRun} onClick={() => setBulk({ choice: "incoming" })}>
                Принять весь импорт
              </button>
            </>
          )}
          <button className="adm-iconbtn" onClick={reload} title="обновить">⟳</button>
        </>
      }
    >
      {bulkRun && (
        <div className="adm-alert">
          <span>Разбор: {bulkRun.done} из {bulkRun.total}
            {bulkRun.failed.length ? ` · не удалось: ${bulkRun.failed.length}` : ""}</span>
        </div>
      )}
      {bulkRun && bulkRun.failed.length > 0 && (
        <ul className="adm-errlist">{bulkRun.failed.map((f, i) => <li key={i}>{f}</li>)}</ul>
      )}

      <LoadState
        loading={loading} error={error} empty={!rows.length}
        emptyText={only ? "В этой новелле конфликтов нет." : "Конфликтов нет — последний импорт лёг чисто."}
      >
        <div className="adm-tablewrap">
          <table className="adm-table">
            <thead>
              <tr>
                <th>файл</th><th>новелла</th><th>тип</th>
                <th className="num">ваша</th><th className="num">входящая</th>
                <th>изменение</th><th></th>
              </tr>
            </thead>
            <tbody>
              {rows.map((c) => {
                const delta = sizeDelta(c);
                const kind = kindOf(c.rel);
                return (
                  <tr key={c.rel} className="adm-row" onClick={() => setOpen(c.rel)}>
                    <td>
                      <span className="adm-cell-main">{c.rel.split("/").pop()}</span>
                      <code className="adm-cell-sub adm-nomargin">{c.rel}</code>
                    </td>
                    <td>{titleOf(c) ? <span className="pill">{titleOf(c)}</span> : <span className="muted">—</span>}</td>
                    <td className="muted">{KIND_LABEL[kind]}</td>
                    <td className="num muted">{fmt(c.mine.size)} b</td>
                    <td className="num muted">{fmt(c.incoming.size)} b</td>
                    <td className={delta > 0 ? "amt-plus" : delta < 0 ? "amt-minus" : "muted"}>
                      {delta > 0 ? "+" : delta < 0 ? "−" : "±"}{fmt(Math.abs(delta))} b
                      {!c.undoable && <span className="pill warn-pill" title="бинарные файлы не попадают в историю правок">без отката</span>}
                    </td>
                    <td className="num"><button className="btn-ghost sm" onClick={(e) => { e.stopPropagation(); setOpen(c.rel); }}>Сравнить ▸</button></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </LoadState>

      {current && (
        <Drawer title={current.rel.split("/").pop()} width={860} onClose={() => setOpen(null)}>
          <ConflictDetail
            key={current.rel}
            conflict={current}
            token={token}
            busy={busy === current.rel}
            onResolve={resolve}
            onNav={onNav}
          />
        </Drawer>
      )}

      {bulk && (
        <Confirm
          title={bulk.choice === "mine" ? "Оставить все свои версии" : "Принять весь импорт"}
          dangerLabel={bulk.choice === "mine" ? "Оставить свои" : "Принять импорт"}
          typeToConfirm={bulk.choice === "incoming" ? "заменить" : undefined}
          body={
            <>
              <p>Файлов: <b>{rows.length}</b>{only ? <> в новелле <code>{only}</code></> : null}.</p>
              {bulk.choice === "incoming"
                ? <p>Ваши версии будут заменены версиями из последнего импорта. Скрипты попадут в историю (откат возможен), <b>бинарный арт — нет</b>.</p>
                : <p>Версии из импорта будут отброшены. Следующий импорт больше не поднимет эти файлы: решение записывается в базу импорта.</p>}
            </>
          }
          onConfirm={() => runBulk(bulk.choice)}
          onClose={() => setBulk(null)}
        />
      )}
    </Page>
  );
}

// ConflictDetail: both sides of ONE file. The listing carries a 400-line diff
// excerpt; opening a file refetches it with ?rel=, which raises the server's
// budget to 4000 lines — so the decision is made on the whole change, not on
// the part that fit in the list response.
function ConflictDetail({ conflict, token, busy, onResolve, onNav }) {
  const [full, setFull] = useState(null);
  const [err, setErr] = useState("");
  const [confirm, setConfirm] = useState(null); // choice awaiting confirmation
  const rel = conflict.rel;

  useEffect(() => {
    let dead = false;
    adminConflicts(token, { rel })
      .then((d) => { if (!dead) setFull((d.conflicts || [])[0] || null); })
      .catch((e) => { if (!dead) setErr(authMsg(e)); });
    return () => { dead = true; };
  }, [rel, token]);

  const c = full || conflict;
  const diff = parseUnifiedDiff(c.diff);
  const image = isImagePath(rel) && c.mine.exists && c.incoming.exists;
  const keepMine = canKeepMine(c);

  const act = (choice) => {
    if (!c.undoable && choice === "incoming") { setConfirm(choice); return; }
    onResolve(rel, choice);
  };

  return (
    <>
      <div className="adm-id-block">
        <code>{rel}</code>
        <span className="adm-dim">
          {KIND_LABEL[kindOf(rel)]}
          {(c.titles || []).length ? <> · новелла <b>{c.titles.join(", ")}</b></> : null}
        </span>
      </div>

      {!c.undoable && (
        <div className="adm-alert">
          <span>Откат невозможен: бинарные файлы не попадают в историю правок. Скачайте свою версию, если она нужна.</span>
          <a className="adm-link" href={previewUrl(c, "mine")} download>Скачать мою →</a>
        </div>
      )}
      {!keepMine && (
        <div className="adm-alert">
          <span>Вашего файла на диске нет — доступен только вариант «Принять импорт».</span>
        </div>
      )}

      <div className="cf-sides">
        <SideCard label="Ваша версия (сейчас у игроков)" side={c.mine} url={previewUrl(c, "mine")} image={image} />
        <SideCard label="Из импорта (припарковано)" side={c.incoming} url={previewUrl(c, "incoming")} image={image} accent />
      </div>

      <div className="cf-actions">
        <button className="btn-ghost" disabled={busy || !keepMine} onClick={() => act("mine")}>
          Оставить мою
        </button>
        <button className="btn btn-primary" disabled={busy} onClick={() => act("incoming")}>
          {busy ? "…" : "Принять импорт ▸"}
        </button>
      </div>
      <p className="adm-dim cf-note">
        Решение записывается в базу импорта: следующий импорт сравнивает уже с ним и не поднимет этот файл заново.
        {c.undoable ? " Замена попадёт в историю правок — можно откатить в «Мои новеллы»." : ""}
      </p>

      <section className="adm-drawer-section">
        <h3>Что отличается</h3>
        {err && <Empty icon="⚠" text={err} />}
        {c.diff_note && !image && <p className="adm-dim">{c.diff_note}</p>}
        {image && (
          <p className="adm-dim">Бинарная картинка — построчного сравнения нет; сверяйте превью выше.</p>
        )}
        {c.diff && (
          <>
            <p className="adm-dim">
              <span className="amt-plus">+{diff.added}</span>{" / "}
              <span className="amt-minus">−{diff.removed}</span>{" строк"}
              {!full && " · показан фрагмент, полный дифф грузится…"}
            </p>
            <div className="cf-diff">
              {diff.rows.map((r, i) => (
                <div key={i} className={"cf-diff-line " + r.kind}>
                  <span className="cf-diff-gutter">{r.kind === "add" ? "+" : r.kind === "del" ? "−" : ""}</span>
                  <span className="cf-diff-text">{r.text || " "}</span>
                </div>
              ))}
            </div>
          </>
        )}
        {!c.diff && !c.diff_note && !image && <p className="adm-dim">Сервер не отдал дифф для этого файла.</p>}
      </section>

      {onNav && (c.titles || []).length > 0 && (
        <section className="adm-drawer-section">
          <button className="adm-link" onClick={() => onNav("novels")}>Открыть кабинет новеллы →</button>
        </section>
      )}

      {confirm && (
        <Confirm
          title="Заменить файл без возможности отката"
          dangerLabel="Принять импорт"
          body={<p><code>{rel}</code> — бинарный файл, история правок его не хранит. После замены вернуть прежнюю версию можно будет только повторной загрузкой.</p>}
          onConfirm={() => onResolve(rel, "incoming")}
          onClose={() => setConfirm(null)}
        />
      )}
    </>
  );
}

function SideCard({ label, side, url, image, accent }) {
  return (
    <div className={"cf-side" + (accent ? " accent" : "")}>
      <h4>{label}</h4>
      {!side.exists && <p className="adm-dim">файла нет</p>}
      {side.exists && (
        <>
          {image && <div className="cf-thumb"><img src={url} alt="" /></div>}
          <dl className="cf-meta">
            <dt>размер</dt><dd>{fmt(side.size)} b</dd>
            <dt>изменён</dt><dd>{dt(side.modified, 16)}</dd>
            <dt>sha</dt><dd><code>{shortSha(side.sha)}</code></dd>
          </dl>
        </>
      )}
    </div>
  );
}
