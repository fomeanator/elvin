import { useEffect, useState } from "react";
import { getManifest, putAsset, importArticy, importBundle } from "../lib/api.js";
import { slug } from "../lib/sprites.js";

const chapterCount = (t) => (t.seasons || []).reduce((n, s) => n + (s.chapters || []).length, 0);

export default function LibraryHome({ creds, notify, onOpen }) {
  const [titles, setTitles] = useState([]);
  const [bust, setBust] = useState(() => Date.now());
  const [modal, setModal] = useState(null); // {mode, draft, originalId}
  const [imp, setImp] = useState(null); // articy import modal: {name, files, label, drag, busy}
  const [bundle, setBundle] = useState(null); // bundle import modal: {name, files:{...}, busy}
  const [importing, setImporting] = useState(null); // {pct, phase}

  useEffect(() => {
    (async () => {
      try { setTitles((await getManifest()).titles || []); } catch { setTitles([]); }
    })();
  }, []);

  async function persist(nextTitles) {
    setTitles(nextTitles);
    try {
      const m = await getManifest();
      m.titles = nextTitles;
      await putAsset("manifest.json", JSON.stringify(m, null, 2), creds.token, "application/json");
      notify("✓ Library saved — live in ~2s", "ok");
      return true;
    } catch (e) { notify("✗ " + e.message, "err"); return false; }
  }

  function openNew() {
    setModal({ mode: "new", draft: { name: "", subtitle: "", cover_url: "" }, originalId: null });
  }

  // Articy import via an in-app modal: drop (or pick) a .zip of the extracted
  // .adpd project — no native folder dialog (which silently no-ops in some
  // browsers when the input isn't in the DOM) and no window.prompt.
  function openImport() {
    if (!creds.token) { notify("Set the admin token first (top bar).", "err"); return; }
    setImp({ name: "", files: null, label: "", drag: false });
  }

  // Run the import for the chosen source (a .zip File, or a folder's FileList).
  async function runImport(files, name) {
    let id = slug(name) || "imported";
    let base = id, i = 1;
    while (titles.some((t) => t.id === id)) id = base + "-" + ++i;
    setImp((s) => ({ ...(s || {}), busy: true }));
    setImporting({ pct: 0, phase: "Загрузка " + files.length + " файл(а/ов)…" });
    try {
      const r = await importArticy(
        files, { id, name, subtitle: "Импорт из articy:draft (.adpd)" }, creds.token,
        (p) => setImporting((s) => ({ ...s, pct: p < 0.99 ? p : 0.99, phase: "Загрузка… " })),
      );
      setImporting({ pct: 1, phase: "Готово" });
      const says = (r.ops && r.ops.say) || 0;
      notify(`✓ «${r.name}»: ${says} реплик, ${r.art_files} артов`, "ok");
      setTitles((await getManifest()).titles || []);
      setBust(Date.now());
      setImporting(null);
      setImp(null);
      if (r.id) onOpen(r.id, r.name);
    } catch (e) {
      setImporting(null);
      setImp((s) => ({ ...(s || {}), busy: false }));
      notify("✗ " + e.message, "err");
    }
  }
  // Full novel bundle: pick the articy project + optional art/vars packs.
  function openBundle() {
    if (!creds.token) { notify("Set the admin token first (top bar).", "err"); return; }
    setBundle({ name: "", files: { articy: null, backgrounds: null, heroine: null, characters: null, vars: null } });
  }

  async function runBundleImport(files, name, template) {
    let id = slug(name) || "imported";
    let base = id, i = 1;
    while (titles.some((t) => t.id === id)) id = base + "-" + ++i;
    setBundle((s) => ({ ...(s || {}), busy: true }));
    setImporting({ pct: 0, phase: "Загрузка файлов…" });
    try {
      const r = await importBundle(
        files, { id, name, subtitle: "", template }, creds.token,
        (p) => setImporting((s) => ({ ...s, pct: p < 0.99 ? p : 0.99, phase: "Загрузка… " })),
      );
      setImporting({ pct: 1, phase: "Готово" });
      const says = (r.ops && r.ops.say) || 0;
      notify(`✓ «${r.name || name}»: ${says} реплик, ${r.art_files || 0} артов`, "ok");
      setTitles((await getManifest()).titles || []);
      setBust(Date.now());
      setImporting(null);
      setBundle(null);
      if (r.id) onOpen(r.id, r.name);
    } catch (e) {
      setImporting(null);
      setBundle((s) => ({ ...(s || {}), busy: false }));
      notify("✗ " + e.message, "err");
    }
  }

  function openEdit(t) {
    setModal({ mode: "edit", draft: { id: t.id, name: t.name || "", subtitle: t.subtitle || "", cover_url: t.cover_url || "" }, originalId: t.id });
  }

  async function uploadCover(draft, setDraft) {
    const id = slug(draft.id || draft.name);
    if (!id) { notify("Name the novel first.", "err"); return; }
    const target = draft.cover_url || `/content/ui/cover_${id}.png`;
    const picker = document.createElement("input");
    picker.type = "file"; picker.accept = "image/*";
    picker.onchange = async () => {
      const f = picker.files && picker.files[0];
      if (!f) return;
      notify("Uploading cover…");
      try {
        const d = await putAsset(target, f, creds.token, f.type || "application/octet-stream");
        setBust(Date.now());
        setDraft({ ...draft, cover_url: target });
        notify(`✓ Cover uploaded (${(d.bytes / 1024).toFixed(1)} KB)`, "ok");
      } catch (e) { notify("✗ " + e.message, "err"); }
    };
    picker.click();
  }

  async function saveModal() {
    const d = modal.draft;
    const name = d.name.trim();
    if (!name) { notify("A novel needs a name.", "err"); return; }
    let id = slug(d.id || name);
    if (modal.mode === "new") {
      let base = id, i = 1;
      while (titles.some((t) => t.id === id)) id = base + "-" + ++i;
    }
    const next = titles.slice();
    if (modal.mode === "new") {
      next.push({ id, name, subtitle: d.subtitle.trim(), cover_url: d.cover_url, seasons: [{ chapters: [] }] });
    } else {
      const idx = next.findIndex((t) => t.id === modal.originalId);
      if (idx >= 0) next[idx] = { ...next[idx], id, name, subtitle: d.subtitle.trim(), cover_url: d.cover_url };
    }
    if (await persist(next)) {
      const created = next.find((t) => t.id === id);
      setModal(null);
      if (modal.mode === "new" && created) onOpen(created.id, created.name);
    }
  }

  async function deleteTitle() {
    if (!modal || modal.mode !== "edit") return;
    const next = titles.filter((t) => t.id !== modal.originalId);
    if (await persist(next)) setModal(null);
  }

  return (
    <div className="home">
      <div className="home-head enter">
        <h1>Your library</h1>
        <p>Pick a novel to work on its characters &amp; script — or start a new one.</p>
      </div>

      <div className="shelf enter d1">
        {titles.map((t) => (
          <div key={t.id} className="novel" onClick={() => onOpen(t.id, t.name)}>
            <div className="novel-cover">
              {t.cover_url
                ? <img src={t.cover_url + "?v=" + bust} alt="" onError={(e) => { e.currentTarget.style.display = "none"; }} />
                : <span className="novel-cover-ph">{(t.name || t.id)[0]?.toUpperCase()}</span>}
              <button className="novel-edit" onClick={(e) => { e.stopPropagation(); openEdit(t); }} title="Edit novel">⚙</button>
            </div>
            <div className="novel-meta">
              <span className="novel-name">{t.name || t.id}</span>
              {t.subtitle && <span className="novel-sub">{t.subtitle}</span>}
              <span className="novel-count">{chapterCount(t)} chapter{chapterCount(t) === 1 ? "" : "s"}</span>
            </div>
          </div>
        ))}

        <button className="novel novel-add" onClick={openNew}>
          <span className="novel-add-mark">＋</span>
          New novel
        </button>

        <button className="novel novel-add novel-import" onClick={openImport} title="Import an articy:draft (.adpd) project — drop a .zip and play it">
          <span className="novel-add-mark">⇪</span>
          Import articy
        </button>

        <button className="novel novel-add novel-import" onClick={openBundle} title="Импорт полной новеллы — articy-проект + фоны/героиня/персонажи/переменные">
          <span className="novel-add-mark">⇪</span>
          Импорт новеллы (5 файлов)
        </button>
      </div>

      {imp && !importing && (
        <ImportModal
          imp={imp}
          setImp={setImp}
          onImport={runImport}
          onCancel={() => setImp(null)}
          notify={notify}
        />
      )}

      {bundle && !importing && (
        <BundleModal
          bundle={bundle}
          setBundle={setBundle}
          onImport={runBundleImport}
          onCancel={() => setBundle(null)}
          notify={notify}
        />
      )}

      {importing && (
        <div className="sp-chooser">
          <div className="sp-chooser-box import-progress">
            <h3>Импорт articy…</h3>
            <div className="import-bar"><div className="import-bar-fill" style={{ width: Math.round(importing.pct * 100) + "%" }} /></div>
            <p>{importing.phase} {importing.pct > 0 && importing.pct < 1 ? Math.round(importing.pct * 100) + "%" : ""}</p>
            <p className="import-hint">Сервер компилирует .adpd, расставляет сцены и обтравливает арт — это займёт несколько секунд после загрузки.</p>
          </div>
        </div>
      )}

      {modal && (
        <NovelModal
          modal={modal}
          setDraft={(draft) => setModal({ ...modal, draft })}
          onUploadCover={uploadCover}
          onSave={saveModal}
          onDelete={deleteTitle}
          onCancel={() => setModal(null)}
          bust={bust}
        />
      )}
    </div>
  );
}

function NovelModal({ modal, setDraft, onUploadCover, onSave, onDelete, onCancel, bust }) {
  const d = modal.draft;
  return (
    <div className="sp-chooser" onClick={onCancel}>
      <div className="sp-chooser-box novel-modal" onClick={(e) => e.stopPropagation()}>
        <h3>{modal.mode === "new" ? "New novel" : "Edit novel"}</h3>
        <div className="novel-modal-row">
          <button className="novel-modal-cover" onClick={() => onUploadCover(d, setDraft)} title="Upload cover">
            {d.cover_url ? <img src={d.cover_url + "?v=" + bust} alt="" /> : <span>＋<em>cover</em></span>}
          </button>
          <div className="novel-modal-fields">
            <label className="adv-field">
              <span>Name</span>
              <input className="field wide" autoFocus placeholder="The Last Guest" value={d.name} onChange={(e) => setDraft({ ...d, name: e.target.value })} />
            </label>
            <label className="adv-field">
              <span>Subtitle <em>(tagline on the card)</em></span>
              <input className="field wide" placeholder="A dark-fantasy visual novel" value={d.subtitle} onChange={(e) => setDraft({ ...d, subtitle: e.target.value })} />
            </label>
          </div>
        </div>
        <div className="novel-modal-actions">
          {modal.mode === "edit" && <button className="btn-ghost" onClick={onDelete}>Delete novel</button>}
          <div className="grow" />
          <button className="btn-ghost" onClick={onCancel}>Cancel</button>
          <button className="btn btn-primary" onClick={onSave}>{modal.mode === "new" ? "Create ▸" : "Save"}</button>
        </div>
      </div>
    </div>
  );
}

// ImportModal: drop a .zip of an extracted .adpd project (or pick a .zip / folder),
// name it, and import. The pickers are <label>-wrapped file inputs — a label click
// natively opens the OS picker in every browser (programmatic input.click() on a
// display:none input silently no-ops in some browsers, which is what broke before).
function ImportModal({ imp, setImp, onImport, onCancel, notify }) {
  const pick = (files, suggested) =>
    setImp((s) => ({
      ...s,
      files,
      label: files.length === 1 ? files[0].name : files.length + " файлов",
      name: (s.name && s.name.trim()) ? s.name : suggested,
      drag: false,
    }));

  function onDrop(e) {
    e.preventDefault();
    const arr = Array.from(e.dataTransfer.files || []);
    const arc = arr.find((f) => /\.(zip|rar)$/i.test(f.name));
    if (arc) { pick([arc], arc.name.replace(/\.(zip|rar)$/i, "")); return; }
    setImp((s) => ({ ...s, drag: false }));
    notify("Перетащи .zip или .rar проекта articy (или нажми «выбрать»)", "err");
  }
  function onZip(e) {
    const f = e.target.files && e.target.files[0];
    if (f) pick([f], f.name.replace(/\.(zip|rar)$/i, ""));
  }
  function onDir(e) {
    const files = Array.from(e.target.files || []);
    if (!files.length) return;
    const top = (files[0].webkitRelativePath || "").split("/")[0] || "imported";
    pick(files, top.replace(/_/g, " "));
  }
  function go() {
    if (!imp.files || !imp.files.length) { notify("Выбери .zip / .rar или папку проекта.", "err"); return; }
    const name = (imp.name || "").trim();
    if (!name) { notify("Назови новеллу.", "err"); return; }
    onImport(imp.files, name);
  }

  return (
    <div className="sp-chooser" onClick={onCancel}>
      <div className="sp-chooser-box novel-modal" onClick={(e) => e.stopPropagation()}>
        <h3>Импорт articy</h3>
        <label
          className={"import-drop" + (imp.drag ? " over" : "")}
          onDragOver={(e) => { e.preventDefault(); if (!imp.drag) setImp((s) => ({ ...s, drag: true })); }}
          onDragLeave={() => setImp((s) => ({ ...s, drag: false }))}
          onDrop={onDrop}
        >
          <input type="file" accept=".zip,.rar,application/zip,application/vnd.rar,application/x-rar-compressed" style={{ display: "none" }} onChange={onZip} />
          {imp.files
            ? <b>✓ {imp.label}</b>
            : <><b>Перетащи или нажми — .zip / .rar</b><span>проекта articy:draft (.adpd)</span></>}
        </label>
        <div className="import-src-row">
          <label className="btn-ghost import-pick">
            Выбрать .zip / .rar
            <input type="file" accept=".zip,.rar,application/zip,application/vnd.rar,application/x-rar-compressed" style={{ display: "none" }} onChange={onZip} />
          </label>
          <label className="btn-ghost import-pick">
            …или папку
            <input type="file" webkitdirectory="" directory="" style={{ display: "none" }} onChange={onDir} />
          </label>
        </div>
        <label className="adv-field">
          <span>Название новеллы</span>
          <input className="field wide" autoFocus placeholder="Моя новелла" value={imp.name}
                 onChange={(e) => setImp((s) => ({ ...s, name: e.target.value }))} />
        </label>
        <div className="novel-modal-actions">
          <div className="grow" />
          <button className="btn-ghost" onClick={onCancel}>Отмена</button>
          <button className="btn btn-primary" onClick={go} disabled={imp.busy}>Импортировать ▸</button>
        </div>
      </div>
    </div>
  );
}

// BundleModal: five labelled file pickers for a full novel import — the articy
// project (required) plus optional backgrounds / heroine / characters / vars
// packs — and a name. Each picker is a <label>-wrapped input so a click opens
// the OS dialog natively (see the ImportModal note). Enabled once the required
// articy file and a name are set.
const BUNDLE_FIELDS = [
  { key: "articy", label: "Articy проект", hint: ".rar / .zip", accept: ".rar,.zip,application/zip,application/vnd.rar,application/x-rar-compressed", required: true },
  { key: "backgrounds", label: "Фоны", hint: ".zip", accept: ".zip,application/zip" },
  { key: "heroine", label: "Героиня", hint: ".zip", accept: ".zip,application/zip" },
  { key: "characters", label: "Персонажи", hint: ".zip", accept: ".zip,application/zip" },
  { key: "vars", label: "Переменные", hint: ".xlsx", accept: ".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
];

function BundleModal({ bundle, setBundle, onImport, onCancel, notify }) {
  const setFile = (key, file) =>
    setBundle((s) => ({
      ...s,
      files: { ...s.files, [key]: file || null },
      name: (s.name && s.name.trim()) || (key === "articy" && file ? file.name.replace(/\.(zip|rar)$/i, "") : s.name),
    }));

  const name = (bundle.name || "").trim();
  const ready = !!(bundle.files.articy && name);

  function go() {
    if (!bundle.files.articy) { notify("Выбери articy-проект (.rar / .zip).", "err"); return; }
    if (!name) { notify("Назови новеллу.", "err"); return; }
    onImport(bundle.files, name, (bundle.template || "").trim());
  }

  return (
    <div className="sp-chooser" onClick={onCancel}>
      <div className="sp-chooser-box novel-modal" onClick={(e) => e.stopPropagation()}>
        <h3>Импорт новеллы (5 файлов)</h3>
        {BUNDLE_FIELDS.map((f) => {
          const picked = bundle.files[f.key];
          return (
            <label key={f.key} className={"import-drop" + (picked ? " over" : "")}>
              <input type="file" accept={f.accept} style={{ display: "none" }}
                     onChange={(e) => setFile(f.key, e.target.files && e.target.files[0])} />
              {picked
                ? <b>✓ {picked.name}</b>
                : <><b>«{f.label}»{f.required ? " *" : ""}</b><span>{f.hint}</span></>}
            </label>
          );
        })}
        <label className="adv-field">
          <span>Название новеллы</span>
          <input className="field wide" autoFocus placeholder="Моя новелла" value={bundle.name}
                 onChange={(e) => setBundle((s) => ({ ...s, name: e.target.value }))} />
        </label>
        <label className="adv-field">
          <span>Шаблон импорта</span>
          <input className="field wide" placeholder="cold (по умолчанию)" value={bundle.template || ""}
                 onChange={(e) => setBundle((s) => ({ ...s, template: e.target.value }))} />
        </label>
        <div className="novel-modal-actions">
          <div className="grow" />
          <button className="btn-ghost" onClick={onCancel}>Отмена</button>
          <button className="btn btn-primary" onClick={go} disabled={!ready || bundle.busy}>Импортировать ▸</button>
        </div>
      </div>
    </div>
  );
}
