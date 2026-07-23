import { useEffect, useState } from "react";
import { getManifest, putAsset, uploadStagedWithRetry, importBundleFromPaths } from "../lib/api.js";
import { slug } from "../lib/sprites.js";

const chapterCount = (t) => (t.seasons || []).reduce((n, s) => n + (s.chapters || []).length, 0);

export default function LibraryHome({ creds, notify, onOpen }) {
  const [titles, setTitles] = useState([]);
  const [bust, setBust] = useState(() => Date.now());
  const [modal, setModal] = useState(null); // {mode, draft, originalId}
  const [bundle, setBundle] = useState(null); // import modal: {name, files:{...}, busy}
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

  // Import: the articy project is required, art/vars packs are optional — one
  // modal covers both "just play the story" and "the full novel with real art".
  function openBundle() {
    if (!creds.token) { notify("Set the admin token first (top bar).", "err"); return; }
    setBundle({ name: "", files: { articy: null, backgrounds: null, heroine: null, characters: null, vars: null } });
  }

  // Uploads each of the (up to 5) files separately via resumable staged
  // upload — a dropped/slow connection resumes from the last acked byte
  // instead of restarting the whole multi-hundred-MB request — then runs the
  // import as a second, near-instant step once everything is on disk. The
  // staging id is derived from the title id + file size, so re-opening the
  // dialog and re-picking the SAME files (e.g. after a reload) resumes rather
  // than reuploading.
  async function runBundleImport(files, name, template) {
    let id = slug(name) || "imported";
    let base = id, i = 1;
    while (titles.some((t) => t.id === id)) id = base + "-" + ++i;
    setBundle((s) => ({ ...(s || {}), busy: true }));

    const entries = Object.entries(files).filter(([, f]) => f);
    const totalBytes = entries.reduce((n, [, f]) => n + f.size, 0) || 1;
    const uploadedBytes = {};
    const reportProgress = () => {
      const done = entries.reduce((n, [k]) => n + (uploadedBytes[k] || 0), 0);
      setImporting({ pct: Math.min(done / totalBytes, 0.99), phase: "Загрузка файлов…" });
    };
    setImporting({ pct: 0, phase: "Загрузка файлов…" });

    try {
      const paths = {};
      for (const [key, f] of entries) {
        // Keep the original extension: the server picks its archive extractor
        // (.zip vs .rar) off the staged filename's suffix.
        const ext = (f.name.match(/\.[^.]+$/) || [""])[0];
        const stageId = `${id}-${key}-${f.size}${ext}`;
        paths[key] = await uploadStagedWithRetry(f, stageId, creds.token, (frac) => {
          uploadedBytes[key] = frac * f.size;
          reportProgress();
        });
      }
      setImporting({ pct: 0.99, phase: "Импорт на сервере…" });
      const r = await importBundleFromPaths(paths, { id, name, subtitle: "", template }, creds.token);
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
      notify("✗ " + e.message + " — прогресс загрузки сохранён, можно повторить.", "err");
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

        <button className="novel novel-add novel-import" onClick={openBundle} title="Импорт articy-проекта — просто script, или + фоны/героиня/персонажи/переменные для полной новеллы">
          <span className="novel-add-mark">⇪</span>
          Импорт новеллы
        </button>
      </div>

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
            <h3>Импорт новеллы…</h3>
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

// BundleModal: labelled file pickers for a novel import — the articy project
// (required) plus optional backgrounds / heroine / characters / vars packs —
// and a name. Each picker is a <label>-wrapped input so a click opens the OS
// dialog natively (programmatic input.click() on a display:none input
// silently no-ops in some browsers). Enabled once the required articy file
// and a name are set; the optional packs can be left empty for a bare
// story-only import.
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
        <h3>Импорт новеллы</h3>
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
