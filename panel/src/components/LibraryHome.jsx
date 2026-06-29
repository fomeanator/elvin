import { useEffect, useState } from "react";
import { getManifest, putAsset, importArticy } from "../lib/api.js";
import { slug } from "../lib/sprites.js";

const chapterCount = (t) => (t.seasons || []).reduce((n, s) => n + (s.chapters || []).length, 0);

export default function LibraryHome({ creds, notify, onOpen }) {
  const [titles, setTitles] = useState([]);
  const [bust, setBust] = useState(() => Date.now());
  const [modal, setModal] = useState(null); // {mode, draft, originalId}
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

  // One-click articy import: pick the extracted .adpd folder; the server does the
  // whole pipeline (compile → auto-stage → matte art → manifest title). When it
  // returns, the new novel is already live in the game — we just refresh the shelf.
  function importArticyFolder() {
    if (!creds.token) { notify("Set the admin token first (top bar).", "err"); return; }
    const picker = document.createElement("input");
    picker.type = "file";
    picker.webkitdirectory = true;
    picker.onchange = async () => {
      const files = Array.from(picker.files || []);
      if (!files.length) return;
      const top = (files[0].webkitRelativePath || "").split("/")[0] || "imported";
      const name = window.prompt("Название новеллы:", top.replace(/_/g, " "));
      if (name == null) return;
      let id = slug(name) || slug(top);
      let base = id, i = 1;
      while (titles.some((t) => t.id === id)) id = base + "-" + ++i;
      setImporting({ pct: 0, phase: "Загрузка " + files.length + " файлов…" });
      try {
        const r = await importArticy(
          files, { id, name, subtitle: "Импорт из articy:draft (.adpd)" }, creds.token,
          (p) => setImporting((s) => ({ ...s, pct: p < 0.99 ? p : 0.99 })),
        );
        setImporting({ pct: 1, phase: "Готово" });
        const says = (r.ops && r.ops.say) || 0;
        notify(`✓ «${r.name}»: ${says} реплик, ${r.art_files} артов, ${r.bg_missing} фонов без матча`, "ok");
        setTitles((await getManifest()).titles || []);
        setBust(Date.now());
        setImporting(null);
        if (r.id) onOpen(r.id, r.name);
      } catch (e) {
        setImporting(null);
        notify("✗ " + e.message, "err");
      }
    };
    picker.click();
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

        <button className="novel novel-add novel-import" onClick={importArticyFolder} title="Import an articy:draft (.adpd) project — one click loads it into the IDE and the game">
          <span className="novel-add-mark">⇪</span>
          Import articy
        </button>
      </div>

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
