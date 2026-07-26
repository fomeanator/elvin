import { useEffect, useRef, useState } from "react";
import { getManifest, putAsset, uploadStagedWithRetry, importBundleFromPaths, listImportTemplates, stageExtractArticy } from "../lib/api.js";
import { slug } from "../lib/sprites.js";
import { summarizeWrite, STATUS_LABEL, CHANGED_STATUSES } from "../lib/conflicts.js";
import ImportMapper from "./ImportMapper.jsx";

const chapterCount = (t) => (t.seasons || []).reduce((n, s) => n + (s.chapters || []).length, 0);

export default function LibraryHome({ creds, notify, onOpen, onOpenAdmin }) {
  const [titles, setTitles] = useState([]);
  const [bust, setBust] = useState(() => Date.now());
  const [modal, setModal] = useState(null); // {mode, draft, originalId}
  // Import modal: {name, template, files:{key:File}, uploads:{key:{pct,path,err}}, busy}
  const [bundle, setBundle] = useState(null);
  const [importing, setImporting] = useState(null); // {pct, phase} — final "run the import" step only
  const uploadPromises = useRef({}); // key -> in-flight/settled upload promise, awaited by "Импортировать"
  const [templateList, setTemplateList] = useState([]); // known import-template names, for the picker
  const [mapperDir, setMapperDir] = useState(null); // extracted articy project dir once staged — opens ImportMapper
  const [mapperBusy, setMapperBusy] = useState(false); // extracting the archive before the mapper can open
  // Что импорт СДЕЛАЛ с контентом — показывается сразу после прогона, до того
  // как автор уйдёт в новеллу. Конфликты здесь не примечание, а развилка.
  const [report, setReport] = useState(null); // { id, name, res }

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
    uploadPromises.current = {};
    setBundle({ name: "", template: "", files: {}, uploads: {} });
    listImportTemplates(creds.token).then(setTemplateList).catch(() => setTemplateList([]));
  }

  // "Настроить роли ▸": extract the staged articy archive ONCE (a no-op if
  // detectRoles already has a dir from an earlier click this session) so the
  // mapper can preview speakers before the real import runs.
  async function openMapper() {
    if (!bundle || !bundle.uploads.articy || !bundle.uploads.articy.path) {
      notify("Дождись загрузки articy-проекта.", "err");
      return;
    }
    setMapperBusy(true);
    try {
      const { dir } = await stageExtractArticy(bundle.uploads.articy.path, creds.token);
      setMapperDir(dir);
    } catch (e) {
      notify("✗ " + e.message, "err");
    } finally {
      setMapperBusy(false);
    }
  }

  // A picked file starts uploading IMMEDIATELY (resumable, chunked) — the
  // author shouldn't have to fill in every slot and hit "Импортировать"
  // before bytes start moving. The staging id keys off the file's own name +
  // size (not the title id, which may not exist yet), so re-picking the same
  // file later — even for a different title name — resumes rather than
  // reuploading. The in-flight promise is kept in uploadPromises so
  // "Импортировать" can await it instead of racing the upload.
  function pickBundleFile(key, file) {
    setBundle((s) => ({
      ...s,
      files: { ...s.files, [key]: file || null },
      name: (s.name && s.name.trim()) || (key === "articy" && file ? file.name.replace(/\.(zip|rar)$/i, "") : s.name),
      uploads: { ...s.uploads, [key]: file ? { pct: 0, path: null, err: null } : undefined },
    }));
    if (!file) { delete uploadPromises.current[key]; return; }

    const ext = (file.name.match(/\.[^.]+$/) || [""])[0];
    const stageId = `${key}-${slug(file.name) || "file"}-${file.size}${ext}`;
    const p = uploadStagedWithRetry(file, stageId, creds.token, (frac) => {
      setBundle((s) => (s && s.uploads[key] ? { ...s, uploads: { ...s.uploads, [key]: { ...s.uploads[key], pct: frac } } } : s));
    }).then((path) => {
      setBundle((s) => (s && s.uploads[key] ? { ...s, uploads: { ...s.uploads, [key]: { pct: 1, path, err: null } } } : s));
      return path;
    }).catch((e) => {
      setBundle((s) => (s && s.uploads[key] ? { ...s, uploads: { ...s.uploads, [key]: { pct: 0, path: null, err: e.message } } } : s));
      throw e;
    });
    uploadPromises.current[key] = p;
    p.catch(() => {}); // swallow here — startImport (or a re-pick) surfaces the real error
  }

  // "Импортировать": wait for every picked file's staged upload to actually
  // finish (they've been uploading in the background since they were picked),
  // then run the import as a fast, separate JSON {dir} step.
  async function startImport() {
    const name = (bundle.name || "").trim();
    if (!bundle.files.articy) { notify("Выбери articy-проект (.rar / .zip).", "err"); return; }
    if (!name) { notify("Назови новеллу.", "err"); return; }
    let id = slug(name) || "imported";
    let base = id, i = 1;
    while (titles.some((t) => t.id === id)) id = base + "-" + ++i;
    const template = (bundle.template || "").trim();

    setBundle((s) => ({ ...(s || {}), busy: true }));
    setImporting({ pct: 0.99, phase: "Ждём загрузку файлов…" });
    try {
      const paths = {};
      for (const key of Object.keys(bundle.files)) {
        if (!bundle.files[key]) continue;
        paths[key] = await uploadPromises.current[key];
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
      // Always show what changed. A re-import that quietly opens the novel
      // hides the one thing the author must act on — the files it REFUSED to
      // overwrite — and those stay parked until somebody looks.
      setReport({ id: r.id || id, name: r.name || name, res: r });
    } catch (e) {
      setImporting(null);
      setBundle((s) => ({ ...(s || {}), busy: false }));
      notify("✗ " + e.message + " — загрузка не потеряна, можно повторить.", "err");
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

      {bundle && !importing && !mapperDir && (
        <BundleModal
          bundle={bundle}
          setBundle={setBundle}
          templateList={templateList}
          onPickFile={pickBundleFile}
          onImport={startImport}
          onOpenMapper={openMapper}
          mapperBusy={mapperBusy}
          onCancel={() => setBundle(null)}
          notify={notify}
        />
      )}

      {mapperDir && (
        <ImportMapper
          dir={mapperDir}
          // The spreadsheet the SAME bundle will be imported with — without it
          // the preview reports an art-less protagonist and unmapped emotion
          // colours the import actually resolves from the sheet.
          varsPath={(bundle && bundle.uploads.vars && bundle.uploads.vars.path) || ""}
          initialTemplateName={bundle && bundle.template}
          creds={creds}
          notify={notify}
          onSaved={(name) => {
            setBundle((s) => (s ? { ...s, template: name } : s));
            setTemplateList((l) => (l.includes(name) ? l : [...l, name].sort()));
            setMapperDir(null);
          }}
          onCancel={() => setMapperDir(null)}
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

      {report && (
        <ImportReport
          report={report}
          onOpenNovel={() => { const r = report; setReport(null); onOpen(r.id, r.name); }}
          onOpenConflicts={() => { setReport(null); if (onOpenAdmin) onOpenAdmin("conflicts"); }}
          onClose={() => setReport(null)}
        />
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

// ImportReport: the "что изменилось" beat of the author's path. The import is
// a three-way merge (importer/baseline.go), so its result is not one number:
// some files were regenerated, some were left exactly as the author edited
// them, and some COULD NOT be decided — those are parked as <file>.incoming
// and the novel has two versions until a human picks one. That last group is
// the primary action here, not a footnote.
function ImportReport({ report, onOpenNovel, onOpenConflicts, onClose }) {
  const r = report.res || {};
  const { counts, conflicts } = summarizeWrite(r.files);
  const warnings = r.warnings || [];
  const lvnCheck = r.lvn_check || [];
  const hasConflicts = conflicts.length > 0;

  return (
    <div className="sp-chooser" onClick={onClose}>
      <div className="sp-chooser-box novel-modal import-report" onClick={(e) => e.stopPropagation()}>
        <h3>{hasConflicts ? "Импорт прошёл — но есть спорные файлы" : "Импорт прошёл"}</h3>
        <p className="import-hint">
          «{report.name}» · глав: {r.chapters || 0} · артов: {r.art_files || 0}
          {r.bg_missing ? ` · фонов не найдено: ${r.bg_missing}` : ""}
        </p>

        <div className="ir-counts">
          {CHANGED_STATUSES.filter((s) => counts[s]).map((s) => (
            <span key={s} className={"ir-count " + s}>
              <b>{counts[s]}</b> {STATUS_LABEL[s] || s}
            </span>
          ))}
          {!Object.values(counts).some(Boolean) && <span className="ir-count">сервер не прислал отчёт по файлам</span>}
        </div>

        {hasConflicts && (
          <div className="ir-conflicts">
            <p>
              Эти файлы вы правили руками, и новый экспорт изменил их иначе — <b>ничего не перезаписано</b>.
              Новая версия лежит рядом как <code>.incoming</code> и ждёт вашего решения:
            </p>
            <ul>
              {conflicts.slice(0, 8).map((c) => <li key={c}><code>{c}</code></li>)}
              {conflicts.length > 8 && <li className="muted">…и ещё {conflicts.length - 8}</li>}
            </ul>
          </div>
        )}

        {lvnCheck.length > 0 && (
          <details className="ir-details">
            <summary>Замечания к скриптам ({lvnCheck.length})</summary>
            <ul>{lvnCheck.slice(0, 20).map((w, i) => <li key={i}>{w}</li>)}</ul>
          </details>
        )}
        {warnings.length > 0 && (
          <details className="ir-details">
            <summary>Предупреждения импорта ({warnings.length})</summary>
            <ul>{warnings.slice(0, 20).map((w, i) => <li key={i}>{w}</li>)}</ul>
          </details>
        )}

        <div className="novel-modal-actions">
          <div className="grow" />
          <button className="btn-ghost" onClick={onOpenNovel}>Открыть новеллу</button>
          {hasConflicts
            ? <button className="btn btn-primary" onClick={onOpenConflicts}>Разобрать конфликты ({conflicts.length}) ▸</button>
            : <button className="btn btn-primary" onClick={onClose}>Готово</button>}
        </div>
      </div>
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

function BundleModal({ bundle, setBundle, templateList, onPickFile, onImport, onOpenMapper, mapperBusy, onCancel, notify }) {
  const name = (bundle.name || "").trim();
  // "Ready" means articy is fully STAGED (not just picked) — the upload
  // started the instant it was picked, so by the time the author has named
  // the novel it's often already done.
  const articyUpload = bundle.uploads.articy;
  const ready = !!(bundle.files.articy && articyUpload && articyUpload.path && name);
  const staged = !!(articyUpload && articyUpload.path);

  function go() {
    if (!bundle.files.articy) { notify("Выбери articy-проект (.rar / .zip).", "err"); return; }
    if (!name) { notify("Назови новеллу.", "err"); return; }
    onImport();
  }

  return (
    <div className="sp-chooser" onClick={onCancel}>
      <div className="sp-chooser-box novel-modal" onClick={(e) => e.stopPropagation()}>
        <h3>Импорт новеллы</h3>
        {BUNDLE_FIELDS.map((f) => {
          const picked = bundle.files[f.key];
          const up = bundle.uploads[f.key];
          const state = !picked ? "empty" : up && up.err ? "err" : up && up.path ? "done" : "uploading";
          return (
            <label key={f.key} className={"import-drop" + (state !== "empty" ? " over" : "")}>
              <input type="file" accept={f.accept} style={{ display: "none" }}
                     onChange={(e) => onPickFile(f.key, e.target.files && e.target.files[0])} />
              {state === "empty" && <><b>«{f.label}»{f.required ? " *" : ""}</b><span>{f.hint}</span></>}
              {state === "uploading" && (
                <>
                  <b>{picked.name}</b>
                  <div className="import-bar"><div className="import-bar-fill" style={{ width: Math.round((up.pct || 0) * 100) + "%" }} /></div>
                  <span>Загрузка… {Math.round((up.pct || 0) * 100)}%</span>
                </>
              )}
              {state === "done" && <b>✓ {picked.name}</b>}
              {state === "err" && <><b>✗ {picked.name}</b><span>{up.err} — выбери файл заново, чтобы повторить</span></>}
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
          <div className="bundle-template-row">
            <select className="field wide" value={bundle.template || ""}
                    onChange={(e) => setBundle((s) => ({ ...s, template: e.target.value }))}>
              <option value="">— по умолчанию —</option>
              {templateList.filter((n) => n !== "default").map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
            <button className="btn-ghost" onClick={onOpenMapper} disabled={!staged || mapperBusy}>
              {mapperBusy ? "Распаковка…" : "Настроить роли ▸"}
            </button>
          </div>
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
