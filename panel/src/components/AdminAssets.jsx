import { useRef, useState } from "react";
import { adminFiles, adminDeleteAsset, putAsset } from "../lib/api.js";
import { useAsync, Status, authMsg, fmt } from "./adminShared.jsx";

const isImg = (n) => /\.(png|jpe?g|webp|gif)$/i.test(n);

// The content-directory browser: breadcrumb navigation, image previews, upload
// into the current directory, click-to-copy content urls, and delete. Scripts
// are versioned server-side on delete; art is gone for good — the confirm says so.
export default function AdminAssets({ token, notify }) {
  const [dir, setDir] = useState("");
  const { loading, error, data, reload } = useAsync(() => adminFiles(dir, token), [dir, token]);
  const fileInput = useRef(null);
  const [uploading, setUploading] = useState(false);

  const files = (data && data.files) || [];
  const crumbs = ["content", ...dir.split("/").filter(Boolean)];
  const rel = (name) => (dir ? dir + "/" : "") + name;

  async function upload(list) {
    if (!list || !list.length) return;
    setUploading(true);
    try {
      for (const f of list) {
        await putAsset(rel(f.name), f, token, f.type || "application/octet-stream");
        notify("Загружен " + f.name, "ok");
      }
      reload();
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
    finally { setUploading(false); if (fileInput.current) fileInput.current.value = ""; }
  }

  async function remove(path) {
    if (!window.confirm("Удалить " + path + "? (скрипты уходят в историю, арт — безвозвратно)")) return;
    try {
      await adminDeleteAsset(path, token);
      notify("Удалено", "ok");
      reload();
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
  }

  function copyUrl(path) {
    const url = "/content/" + path;
    navigator.clipboard.writeText(url);
    notify("Скопировано: " + url, "ok");
  }

  return (
    <div className="admin-card">
      <div className="admin-cardhead">
        <h2 className="admin-crumbs">
          {crumbs.map((c, i) => (
            <span key={i}>
              {i > 0 && <span className="crumb-sep">/</span>}
              <button className="as-link crumb-link" onClick={() => setDir(crumbs.slice(1, i + 1).join("/"))}>{c}</button>
            </span>
          ))}
        </h2>
        <div className="admin-rowbtns">
          <input ref={fileInput} type="file" multiple style={{ display: "none" }}
                 onChange={(e) => upload(Array.from(e.target.files || []))} />
          <button className="btn btn-primary" disabled={uploading}
                  onClick={() => fileInput.current && fileInput.current.click()}>
            {uploading ? "Загрузка…" : "Загрузить файлы сюда"}
          </button>
        </div>
      </div>
      <p className="admin-hint">Клик по имени — копирует content-url для манифеста/скрипта. Картинки с превью.</p>
      <Status loading={loading} error={error} />
      {!loading && !error && (
        <div className="admin-filegrid">
          {files.map((f) => f.dir ? (
            <button key={f.name} className="admin-file admin-dir" onClick={() => setDir(rel(f.name))}>
              <div className="admin-fileicon">📁</div>
              <div className="admin-filename">{f.name}</div>
            </button>
          ) : (
            <div key={f.name} className="admin-file">
              {isImg(f.name)
                ? <img src={"/content/" + rel(f.name)} loading="lazy" alt={f.name} className="admin-filepreview" />
                : <div className="admin-fileicon">📄</div>}
              <button className="as-link admin-filename" title={"скопировать /content/" + rel(f.name)}
                      onClick={() => copyUrl(rel(f.name))}>{f.name}</button>
              <div className="muted admin-filesize">{fmt(f.size)} b</div>
              <button className="btn-ghost sm danger" onClick={() => remove(rel(f.name))}>удалить</button>
            </div>
          ))}
          {!files.length && <p className="admin-empty">Пусто.</p>}
        </div>
      )}
    </div>
  );
}
