import { useState } from "react";
import { adminConfig, adminPutConfig } from "../lib/api.js";
import { useAsync, Status, authMsg, HistoryPanel } from "./adminShared.jsx";
import { useJsonDoc, JsonCard } from "./admin/jsonTools.jsx";

// The three live-reloaded economy configs, each an editable JSON document with
// the spec'd toolbar: save is disabled until the doc is dirty AND valid, a
// parse error shows inline, and every save is versioned (history + rollback).
const CONFIGS = [
  { file: "iap-catalog.json", title: "Паки валюты (IAP)",
    hint: '{ "sku": {"currency","amount","title","price","icon","bonus","order"} }' },
  { file: "ads.json", title: "Награды за рекламу",
    hint: '{ "placement": {"currency","amount","daily_cap"} }' },
  { file: "daily-rewards.json", title: "Ежедневные награды (стрик)",
    hint: '[ {"currency","amount"}, … день за днём; последний повторяется ]' },
];

export default function AdminEconomy({ token, notify }) {
  return (
    <>
      {CONFIGS.map((c) => (
        <ConfigCard key={c.file} cfg={c} token={token} notify={notify} />
      ))}
    </>
  );
}

function ConfigCard({ cfg, token, notify }) {
  const { loading, error, data, reload } = useAsync(() => adminConfig(cfg.file, token), [cfg.file, token]);
  const doc = useJsonDoc(data);
  const [showHist, setShowHist] = useState(false);
  const [busy, setBusy] = useState(false);

  async function save(parsed) {
    setBusy(true);
    try {
      await adminPutConfig(cfg.file, parsed, token);
      notify(cfg.file + " сохранён — уже действует", "ok");
      doc.reset();
      reload();
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
    finally { setBusy(false); }
  }

  return (
    <div className="admin-card">
      <div className="admin-cardhead">
        <h2>{cfg.title} <span className="pill">{cfg.file}</span></h2>
        <div className="admin-rowbtns">
          <button className="btn-ghost sm" onClick={() => setShowHist((s) => !s)}>История</button>
        </div>
      </div>
      <p className="admin-hint">{cfg.hint}. Сервер подхватывает сохранение сразу, без рестарта.</p>
      <Status loading={loading} error={error} />
      {data != null && <JsonCard doc={doc} onSave={save} busy={busy} height={220} />}
      {showHist && (
        <HistoryPanel file={cfg.file} token={token} notify={notify}
          onRolledBack={() => { doc.reset(); setShowHist(false); reload(); }} />
      )}
    </div>
  );
}
