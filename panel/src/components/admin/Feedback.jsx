import { useState } from "react";
import { adminFeedback } from "../../lib/api.js";
import { useAsync, fmt } from "../adminShared.jsx";
import { Page, LoadState, Empty } from "./ui.jsx";
import { WINDOWS, windowQuery, windowLabel } from "../../lib/analytics.js";

// Отзывы из игры.
//
// Рядом со «Сборками» намеренно: первый вопрос к любому отзыву — «на какой
// версии это было», и разбивка по сборкам сразу показывает, чинит ли новая
// то, на что жаловались в прошлой.
//
// Текст здесь — половина записи. Вторая половина (глава, место в сценарии,
// реплика на экране, хвост лога) приезжает сама, потому что тестер её назвать
// не может: он не знает ни индексов команд, ни номеров сборок.

const KIND_LABEL = { bug: "баг", idea: "идея", other: "прочее" };

export default function Feedback({ token }) {
  const [winKey, setWinKey] = useState("d30");
  const preset = WINDOWS.find((w) => w.key === winKey) || WINDOWS[0];
  const win = { days: preset.days };
  const q = windowQuery(win);
  const rep = useAsync(() => adminFeedback(q, token), [q, token]);
  const d = rep.data || {};
  const items = d.feedback || [];
  const builds = Object.entries(d.by_build || {}).sort((a, b) => b[1] - a[1]);

  return (
    <Page
      title="Отзывы"
      description={"Что писали из игры за " + windowLabel(win) +
        ". Глава, место в сценарии и хвост лога собираются сами — тестер их назвать не может."}
      actions={
        <>
          <div className="adm-seg">
            {WINDOWS.filter((w) => w.key !== "today").map((w) => (
              <button key={w.key} className={"adm-seg-btn" + (winKey === w.key ? " on" : "")}
                      onClick={() => setWinKey(w.key)}>
                {w.label}
              </button>
            ))}
          </div>
          <button className="adm-iconbtn" onClick={rep.reload} title="обновить">⟳</button>
        </>
      }
    >
      <LoadState loading={rep.loading} error={rep.error}>
        <div className="adm-kpis tight">
          <Stat label="отзывов" value={fmt(d.total || 0)} />
          {builds.slice(0, 4).map(([b, n]) => (
            <Stat key={b} label={"сборка " + b} value={fmt(n)} />
          ))}
        </div>

        {!items.length ? (
          <Empty text="Отзывов за это окно нет. Кнопка появляется в сборке со свежим клиентом." />
        ) : (
          <div className="adm-list">
            {items.map((f, i) => (
              <section className="adm-panel" key={i}>
                <header className="adm-panel-head">
                  <h2>{KIND_LABEL[f.kind] || f.kind || "отзыв"}</h2>
                  <span className="adm-dim">
                    {f.ts?.replace("T", " ").replace("Z", "")}
                    {f.build ? " · сборка " + f.build : ""}
                    {f.device ? " · " + f.device : ""}
                  </span>
                </header>
                <p className="fb-text">{f.text}</p>
                <p className="adm-dim">
                  {f.title ? "новелла " + f.title : "вне новеллы"}
                  {f.chapter ? " · глава " + f.chapter : ""}
                  {f.label ? " · метка " + f.label : ""}
                  {f.at ? " · команда #" + f.at : ""}
                </p>
                {f.line && <p className="adm-dim">На экране было: «{f.line}»</p>}
                {f.log && (
                  <details>
                    <summary className="adm-dim">хвост лога</summary>
                    <pre className="fb-log">{f.log}</pre>
                  </details>
                )}
              </section>
            ))}
          </div>
        )}
      </LoadState>
    </Page>
  );
}

function Stat({ label, value }) {
  return (
    <div className="adm-kpi as-stat">
      <span className="adm-kpi-value">{value}</span>
      <span className="adm-kpi-label">{label}</span>
    </div>
  );
}
