import { useState } from "react";
// The fixed header: identity, a breadcrumb (Library › Novel) with the
// Characters / Script switch once a novel is open, and the shared admin token.
export default function TopBar({ nav, status, creds }) {
  const admin = nav.mode === "admin";
  const inside = !admin && nav.titleId != null;
  return (
    <header className="topbar">
      <div className="brand">
        <button className="brand-mark as-link" onClick={() => { nav.setMode("studio"); nav.goHome(); }} title="Library">
          ELVIN <em>Studio</em>
        </button>

        <div className="mode-switch" role="tablist">
          <button className={"vbtn" + (!admin ? " active" : "")} onClick={() => nav.setMode("studio")}>Студия</button>
          <button className={"vbtn" + (admin ? " active" : "")} onClick={() => nav.setMode("admin")}>Админка</button>
        </div>

        {admin ? null : inside ? (
          <>
            <span className="crumb-sep">›</span>
            <button className="crumb" onClick={nav.goHome}>Library</button>
            <span className="crumb-sep">›</span>
            <span className="crumb current">{nav.titleName}</span>
            <div className="view-switch" role="tablist">
              <button
                className={"vbtn" + (nav.section === "characters" ? " active" : "")}
                onClick={() => nav.setSection("characters")}
              >
                Characters
              </button>
              <button
                className={"vbtn" + (nav.section === "script" ? " active" : "")}
                onClick={() => nav.setSection("script")}
              >
                Script
              </button>
            </div>
            {nav.section === "script" && (
              <span className={"badge " + status.kind} title={status.title || ""}>{status.text}</span>
            )}
          </>
        ) : (
          <span className="brand-tag">library</span>
        )}
      </div>

      <div className="topbar-actions">
        <ConnectButton creds={creds} />
        <a className="vbtn" href="/play/" target="_blank" rel="noreferrer"
           title="Песочница: пиши .lvns — играет сразу в браузере">▶ Playground</a>
        <input
          className="field token"
          type="password"
          placeholder="admin token"
          title="Admin token (server -admin-token)"
          value={creds.token}
          onChange={(e) => creds.setToken(e.target.value)}
        />
      </div>
    </header>
  );
}

// «Коннект» — одна кнопка между «у меня есть ИИ» и «мой ИИ работает в этой
// студии». Скачивает файл, который можно целиком вставить в чат с любой
// моделью: внутри адрес ЭТОГО сервера, ключ записи и весь язык (1600 строк),
// так что дальше человеку достаточно сказать словами, какую игру он хочет.
//
// Скачивание идёт через fetch, а не через <a href>: ссылка не умеет слать
// заголовок Authorization, а класть ключ в query-параметр значит оставить его
// в истории браузера, в логах прокси и в реферерах.
function ConnectButton({ creds }) {
  const [busy, setBusy] = useState(false);

  async function download() {
    if (!creds.token) {
      alert("Сначала введи admin token справа — файл содержит ключ записи и без него не выдаётся.");
      return;
    }
    setBusy(true);
    try {
      const r = await fetch("/v1/admin/agent-bundle", {
        headers: { Authorization: "Bearer " + creds.token },
      });
      if (!r.ok) throw new Error(r.status === 401 ? "неверный admin token" : "сервер ответил " + r.status);
      const blob = await r.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = "elvin-agent.md";
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    } catch (e) {
      alert("Не получилось скачать: " + e.message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <button className="vbtn" onClick={download} disabled={busy}
            title="Скачать файл для ИИ: адрес студии, ключ и весь язык одним файлом — вставь его в чат с ИИ">
      {busy ? "…" : "🔌 Коннект"}
    </button>
  );
}
