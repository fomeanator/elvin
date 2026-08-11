import { useCallback, useEffect, useRef, useState } from "react";
import { Page, LoadState, Confirm } from "./ui.jsx";
import { adminBuilds, adminRegisterBuild, adminDeleteBuild, downloadBuild, uploadStagedWithRetry } from "../../lib/api.js";

// СБОРКИ — где команда берёт свежий билд.
//
// Раздел отвечает ровно на один вопрос, который до него задавали в переписке
// каждую неделю: «где взять последнюю версию?». Поэтому наверху не таблица, а
// одна карточка — та самая последняя версия и кнопка её скачать. История ниже
// нужна реже: она про «откатиться на предыдущую», а не про «поставить себе».

const MB = 1 << 20;
const size = (n) => (n >= MB ? (n / MB).toFixed(1) + " МБ" : Math.max(1, Math.round(n / 1024)) + " КБ");
const when = (s) => String(s || "").slice(0, 16).replace("T", " ");
const PLATFORM_RU = { android: "Android", ios: "iOS", other: "прочее" };

// Версию угадываем из имени файла: в CI билд почти всегда называется
// app-1.4.2-release.apk, и переписывать это руками каждый раз незачем.
const guessVersion = (name) => {
  const m = String(name || "").match(/\d+(?:\.\d+){1,3}/);
  return m ? m[0] : new Date().toISOString().slice(0, 10);
};

export default function Builds({ token, notify }) {
  const [data, setData] = useState(null);
  const [error, setError] = useState("");
  const [killing, setKilling] = useState(null);

  const load = useCallback(() => {
    setError("");
    adminBuilds(token)
      .then(setData)
      .catch((e) => { setData({ builds: [] }); setError(String(e.message || e)); });
  }, [token]);
  useEffect(load, [load]);

  const builds = (data && data.builds) || [];
  const latest = data && data.latest;

  const grab = async (b) => {
    try {
      notify && notify(`Качаем ${b.version}…`);
      await downloadBuild(b.id, b.file, token);
    } catch (e) {
      notify && notify(String(e.message || e), "error");
    }
  };

  const remove = async (b) => {
    try {
      await adminDeleteBuild(b.id, token);
      notify && notify(`Сборка ${b.version} удалена`);
      load();
    } catch (e) {
      notify && notify(String(e.message || e), "error");
    }
  };

  return (
    <Page
      title="Сборки"
      count={builds.length || undefined}
      description="Свежий билд лежит здесь, а не в переписке. Ссылка «последняя версия» всегда отдаёт верхнюю строку."
    >
      <Upload token={token} notify={notify} onDone={load} />

      <LoadState loading={data === null} error={error} empty={!builds.length}
                 emptyText="Сборок ещё нет — залейте первую">
        {latest && <Latest build={latest} onGrab={() => grab(latest)} />}

        {builds.length > 1 && (
          <div className="adm-panel">
            <div className="adm-panel-head"><h2>Предыдущие</h2></div>
            <div className="adm-tablewrap">
              <table className="adm-table">
                <thead>
                  <tr><th>версия</th><th>платформа</th><th>размер</th><th>залита</th><th>кто</th><th>что нового</th><th /></tr>
                </thead>
                <tbody>
                  {builds.slice(1).map((b) => (
                    <tr key={b.id}>
                      <td>{b.version}</td>
                      <td>{PLATFORM_RU[b.platform] || b.platform}</td>
                      <td className="muted">{size(b.size)}</td>
                      <td className="muted">{when(b.uploaded)}</td>
                      <td className="muted">{b.by}</td>
                      <td className="muted">{b.notes || "—"}</td>
                      <td>
                        <button className="btn-ghost sm" onClick={() => grab(b)}>Скачать</button>{" "}
                        <button className="btn-ghost sm" onClick={() => setKilling(b)}>Удалить</button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </LoadState>

      {killing && (
        <Confirm
          title={`Удалить сборку ${killing.version}?`}
          body="Файл сотрётся с сервера — если он больше нигде не сохранён, скачать его будет неоткуда."
          onConfirm={() => remove(killing)}
          onClose={() => setKilling(null)}
        />
      )}
    </Page>
  );
}

// Карточка последней версии: то, ради чего раздел и заводился.
function Latest({ build, onGrab }) {
  return (
    <div className="adm-panel adm-build-latest">
      <div className="adm-build-head">
        <div>
          <div className="adm-build-version">{build.version}</div>
          <div className="muted">
            {PLATFORM_RU[build.platform] || build.platform} · {size(build.size)} · {when(build.uploaded)} · {build.by}
          </div>
        </div>
        <button className="btn btn-primary" onClick={onGrab}>Скачать последнюю версию</button>
      </div>
      {build.notes && <p className="adm-build-notes">{build.notes}</p>}
      {/* Сумму показываем целиком по клику: ею сверяют, что на телефон уехал
          именно этот файл, а не позавчерашний из кэша. */}
      <code className="adm-build-sum" title={build.sha256}>SHA-256 {String(build.sha256 || "").slice(0, 16)}…</code>
    </div>
  );
}

// Залив: кусочный, с прогрессом и докачкой. APK — это сотня мегабайт, и
// обрыв на девяностом не должен отправлять всё сначала (uploadStagedWithRetry).
function Upload({ token, notify, onDone }) {
  const [file, setFile] = useState(null);
  const [version, setVersion] = useState("");
  const [notes, setNotes] = useState("");
  const [progress, setProgress] = useState(-1);
  const input = useRef(null);

  const pick = (f) => {
    setFile(f || null);
    if (f) setVersion(guessVersion(f.name));
  };

  const send = async () => {
    if (!file) return;
    setProgress(0);
    try {
      // id стабилен по имени+размеру: повторный выбор того же файла после
      // перезагрузки страницы продолжит залив, а не начнёт заново.
      const id = `${file.name}-${file.size}`.replace(/[^A-Za-z0-9_.-]/g, "_");
      const path = await uploadStagedWithRetry(file, id, token, setProgress);
      // filename обязателен: на диске файл лежит под id залива, и по нему
      // сервер не отличит .apk от чего угодно.
      await adminRegisterBuild({ path, filename: file.name, version, notes }, token);
      notify && notify(`Сборка ${version} залита`);
      setFile(null); setNotes(""); setProgress(-1);
      if (input.current) input.current.value = "";
      onDone();
    } catch (e) {
      setProgress(-1);
      notify && notify(String(e.message || e), "error");
    }
  };

  const busy = progress >= 0;
  return (
    <div className="adm-panel adm-build-upload">
      <div className="adm-build-row">
        <input ref={input} type="file" accept=".apk,.aab,.ipa,.zip" disabled={busy}
               onChange={(e) => pick(e.target.files && e.target.files[0])} />
        <input className="field" placeholder="версия" value={version} disabled={busy}
               onChange={(e) => setVersion(e.target.value)} style={{ maxWidth: 140 }} />
        <input className="field" placeholder="что нового — увидит вся команда" value={notes} disabled={busy}
               onChange={(e) => setNotes(e.target.value)} />
        <button className="btn btn-primary" disabled={!file || !version || busy} onClick={send}>
          {busy ? Math.round(progress * 100) + " %" : "Залить"}
        </button>
      </div>
      {busy && <div className="adm-build-bar"><span style={{ width: Math.round(progress * 100) + "%" }} /></div>}
    </div>
  );
}
