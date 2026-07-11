import { useState } from "react";
import { adminManifest, adminPutManifest, adminPublishManifest, adminDiscardDraft } from "../lib/api.js";
import { useAsync, Status, authMsg } from "./adminShared.jsx";
import { useJsonDoc, JsonCard } from "./admin/jsonTools.jsx";
import { Confirm } from "./admin/ui.jsx";
import { adminHistory, adminRollback } from "../lib/api.js";
import { fmt } from "./adminShared.jsx";

// The manifest release controls: the draft → publish cycle, the raw-JSON
// editor with a save gate (dirty AND valid), and the version history with a
// tier-2 rollback confirm. Deliberately NOT a title/chapter editor — that's
// the Студия; this page is about releasing safely.
export default function AdminManifest({ token, notify }) {
  const [draft, setDraft] = useState(false);
  const { loading, error, data, reload } = useAsync(() => adminManifest(token, draft), [token, draft]);
  const doc = useJsonDoc(data);
  const [showHist, setShowHist] = useState(false);
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState(null); // { kind: "discard" } | { kind: "publish" }

  function switchDraft(on) {
    if (doc.dirty && !window.confirm("Есть несохранённые правки — уйти без сохранения?")) return;
    doc.reset();
    setDraft(on);
  }

  async function save(parsed) {
    setBusy(true);
    try {
      await adminPutManifest(parsed, token, draft);
      notify(draft ? "Черновик сохранён (игроки не видят)" : "Манифест сохранён — клиенты подхватят сами", "ok");
      doc.reset();
      reload();
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
    finally { setBusy(false); }
  }

  async function publish() {
    setBusy(true);
    try {
      await adminPublishManifest(token);
      notify("Опубликовано — клиенты подхватят сами", "ok");
      doc.reset();
      setDraft(false);
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
    finally { setBusy(false); }
  }

  async function discard() {
    setBusy(true);
    try {
      await adminDiscardDraft(token);
      notify("Черновик сброшен", "ok");
      doc.reset();
      setDraft(false);
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
    finally { setBusy(false); }
  }

  return (
    <div className="admin-card">
      <div className="admin-cardhead">
        <h2>
          Манифест (raw JSON)
          {draft && <span className="pill draft">ЧЕРНОВИК</span>}
        </h2>
        <div className="admin-rowbtns">
          <label className="admin-check">
            <input type="checkbox" checked={draft} onChange={(e) => switchDraft(e.target.checked)} /> черновик
          </label>
          {draft && (
            <button className="btn btn-primary" disabled={busy}
                    onClick={() => (doc.dirty ? setConfirm({ kind: "publish" }) : publish())}>
              Опубликовать
            </button>
          )}
          {draft && (
            <button className="btn-ghost sm danger" disabled={busy}
                    onClick={() => setConfirm({ kind: "discard" })}>Сбросить</button>
          )}
          <button className="btn-ghost sm" onClick={() => setShowHist((s) => !s)}>История</button>
        </div>
      </div>
      <p className="admin-hint">
        {draft
          ? "Черновик игроки НЕ видят — правь спокойно, потом «Опубликовать» одним действием."
          : "Правки уезжают живым клиентам в течение пары секунд (live-sync). Каждое сохранение попадает в историю."}
        {" "}Тайтлы и главы удобнее править в Студии; здесь — весь манифест как есть (спрайты, гардеробы, ui-темы).
      </p>
      <Status loading={loading} error={error} />
      {data != null && (
        <JsonCard doc={doc} onSave={save} busy={busy} height={520}
                  saveLabel={draft ? "Сохранить черновик" : "Сохранить манифест"} />
      )}
      {showHist && (
        <ManifestHistory token={token} notify={notify}
          onRolledBack={() => { doc.reset(); setShowHist(false); reload(); }} />
      )}

      {confirm?.kind === "discard" && (
        <Confirm
          title="Сбросить черновик"
          body={<p>Серверная копия черновика будет удалена. Несохранённые правки пропадут. Действие нельзя отменить.</p>}
          dangerLabel="Сбросить"
          onConfirm={discard}
          onClose={() => setConfirm(null)}
        />
      )}
      {confirm?.kind === "publish" && (
        <Confirm
          title="Опубликовать без правок из редактора?"
          body={<p>Публикуется <b>серверная</b> копия черновика — несохранённые правки в редакторе в неё <b>не входят</b>. Сначала «Сохранить черновик», если они нужны.</p>}
          dangerLabel="Опубликовать как есть"
          onConfirm={publish}
          onClose={() => setConfirm(null)}
        />
      )}
    </div>
  );
}

// ManifestHistory: version list with a tier-2 rollback confirm — the operator
// types the version's timestamp to arm the button (a live-manifest rollback is
// the most dangerous click in the panel).
function ManifestHistory({ token, notify, onRolledBack }) {
  const { loading, error, data } = useAsync(() => adminHistory("manifest.json", token), [token]);
  const versions = (data && data.versions) || [];
  const [confirmTs, setConfirmTs] = useState(null);

  async function rollback(ts) {
    try {
      await adminRollback("manifest.json", ts, token);
      notify("Откатено — клиенты подхватят сами", "ok");
      onRolledBack();
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
  }

  const stamp = (ts) => new Date(Number(ts)).toLocaleString("ru-RU");
  return (
    <div className="admin-history">
      <h3>История: manifest.json</h3>
      <p className="admin-hint">Откат применяется к ОПУБЛИКОВАННОМУ манифесту (черновик не трогает).</p>
      <Status loading={loading} error={error} />
      {!loading && !error && !versions.length && <p className="admin-hint">Пока пусто — версии появляются при каждом сохранении.</p>}
      {versions.length > 0 && (
        <table className="adm-table dense">
          <tbody>
            {versions.map((v) => (
              <tr key={v.ts}>
                <td>{stamp(v.ts)}</td>
                <td className="num muted">{fmt(v.size)} b</td>
                <td className="num"><button className="btn-ghost sm" onClick={() => setConfirmTs(v.ts)}>Откатить на эту</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {confirmTs && (
        <Confirm
          title="Откатить живой манифест"
          body={<p>Манифест вернётся к версии от <b>{stamp(confirmTs)}</b> и сразу уедет игрокам. Для подтверждения введите время версии.</p>}
          dangerLabel="Откатить"
          typeToConfirm={stamp(confirmTs)}
          onConfirm={() => rollback(confirmTs)}
          onClose={() => setConfirmTs(null)}
        />
      )}
    </div>
  );
}
