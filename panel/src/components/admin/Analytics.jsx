import { useState } from "react";
import { analyticsSummary, analyticsFunnel, analyticsHealth } from "../../lib/api.js";
import { useAsync, fmt } from "../adminShared.jsx";
import { Page, LoadState, Empty } from "./ui.jsx";
import {
  WINDOWS, todayISO, windowQuery, windowLabel, pct, share,
  dropKindLabel, dropKindHint, severity, funnelMax, gapsPending,
} from "../../lib/analytics.js";

// Аналитика — the last step of the author's path. Three views, in the order
// the questions actually get asked:
//
//   Разрезы  — кто во что играет (по новеллам, по авторам, по дням/часам)
//   Обрывы   — ГДЕ теряются игроки: воронка по главам и ранжированные утечки
//   Здоровье — что ломается: отказы, неизвестные опы, промахи по ассетам,
//              и — отдельно — чего этот лог ПОКА не знает (blind spots)
//
// The drop-off view is the one the owner asked to be useful to engineering as
// well as to sales: it never mixes "вошли и не дошли" with "дочитали и не
// вернулись", because those two are fixed by completely different people.

const VIEWS = [
  { key: "cuts", label: "Разрезы" },
  { key: "drops", label: "Обрывы" },
  { key: "health", label: "Здоровье" },
];

export default function Analytics({ token }) {
  const [winKey, setWinKey] = useState("d7");
  const [customDay, setCustomDay] = useState(todayISO);
  const [view, setView] = useState("cuts");

  const preset = WINDOWS.find((w) => w.key === winKey) || WINDOWS[0];
  const win = winKey === "today" ? { day: customDay } : { days: preset.days };
  const q = windowQuery(win);

  const summary = useAsync(() => analyticsSummary(q, token), [q, token]);
  const d = summary.data || {};

  return (
    <Page
      title="Аналитика"
      description={"Что происходит с игроками за " + windowLabel(win) + ". Атрибуция по автору проставляется в момент события — по манифесту, а не задним числом."}
      actions={
        <>
          <div className="adm-seg">
            {WINDOWS.map((w) => (
              <button key={w.key} className={"adm-seg-btn" + (winKey === w.key ? " on" : "")} onClick={() => setWinKey(w.key)}>
                {w.label}
              </button>
            ))}
          </div>
          {winKey === "today" && (
            <input type="date" className="field admin-date" value={customDay}
                   onChange={(e) => e.target.value && setCustomDay(e.target.value)} />
          )}
          <button className="adm-iconbtn" onClick={summary.reload} title="обновить">⟳</button>
        </>
      }
    >
      <div className="adm-kpis tight">
        <Stat label="событий" value={fmt(d.total || 0)} />
        <Stat label="игроков" value={fmt(d.unique_users || 0)} />
        <Stat label="стартов глав" value={fmt(sumDays(d.by_day, "chapter_starts"))} />
        <Stat label="дочитано глав" value={fmt(sumDays(d.by_day, "chapter_finishes"))} />
        <Stat label="доля отказов" value={pct((d.signals || {}).fail_event_share, 1)}
              tone={(d.signals || {}).fail_event_share > 0.02 ? "bad" : ""} />
        <Stat label="игроков с ошибкой" value={pct((d.signals || {}).player_fail_share, 0)}
              tone={(d.signals || {}).player_fail_share > 0.1 ? "bad" : ""} />
      </div>

      <div className="adm-seg wide">
        {VIEWS.map((v) => (
          <button key={v.key} className={"adm-seg-btn" + (view === v.key ? " on" : "")} onClick={() => setView(v.key)}>
            {v.label}
          </button>
        ))}
      </div>

      {view === "cuts" && <Cuts summary={summary} />}
      {view === "drops" && <Drops token={token} q={q} titles={d.by_title || []} />}
      {view === "health" && <Health token={token} q={q} />}
    </Page>
  );
}

const sumDays = (days, key) => (days || []).reduce((n, r) => n + (Number(r[key]) || 0), 0);

function Stat({ label, value, tone }) {
  return (
    <div className={"adm-kpi as-stat" + (tone ? " " + tone : "")}>
      <span className="adm-kpi-value">{value}</span>
      <span className="adm-kpi-label">{label}</span>
    </div>
  );
}

// ── Разрезы ─────────────────────────────────────────────────────────────────
function Cuts({ summary }) {
  const d = summary.data || {};
  const byTitle = d.by_title || [];
  const byAuthor = d.by_author || [];
  const byDay = d.by_day || [];
  const byHour = d.by_hour || [];
  const cov = d.coverage || {};
  const names = Object.entries(d.by_name || {}).sort((a, b) => b[1] - a[1]).slice(0, 12);
  const maxName = names.length ? names[0][1] : 1;
  const maxDay = Math.max(1, ...byDay.map((r) => r.events || 0));
  const maxHour = Math.max(1, ...byHour);

  return (
    <LoadState loading={summary.loading} error={summary.error} empty={!d.total}
               emptyText="За это окно событий нет.">
      <section className="adm-panel">
        <header className="adm-panel-head"><h2>По новеллам</h2></header>
        {!byTitle.length ? <Empty text="Ни одно событие не принесло title." hint="Клиент должен слать props.title на каждом Track внутри новеллы." /> : (
          <div className="adm-tablewrap">
            <table className="adm-table">
              <thead>
                <tr><th>новелла</th><th>автор</th><th className="num">игроки</th><th className="num">старты</th>
                  <th className="num">финиши</th><th>дочитываемость</th><th className="num">отказы</th></tr>
              </thead>
              <tbody>
                {byTitle.map((t) => (
                  <tr key={t.title}>
                    <td>
                      <span className="adm-cell-main">{t.name || t.title}</span>
                      {t.name && <code className="adm-cell-sub adm-nomargin">{t.title}</code>}
                    </td>
                    <td>{t.author ? <span className="pill">{t.author}</span> : <span className="muted">—</span>}</td>
                    <td className="num">{fmt(t.players)}</td>
                    <td className="num">{fmt(t.chapter_starts)}</td>
                    <td className="num">{fmt(t.chapter_finishes)}</td>
                    <td><Meter value={t.chapter_completion} /></td>
                    <td className={"num " + (t.fail_share > 0.02 ? "amt-minus" : "muted")}>{pct(t.fail_share, 1)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="adm-panel">
        <header className="adm-panel-head"><h2>По авторам</h2><span className="adm-dim">вид менеджера</span></header>
        {!byAuthor.length ? (
          <Empty text="Ни у одной новеллы не заполнен author в манифесте."
                 hint="Автор проставляется в манифесте и штампуется в событие в момент записи." />
        ) : (
          <div className="adm-tablewrap">
            <table className="adm-table">
              <thead>
                <tr><th>автор</th><th className="num">новелл</th><th className="num">игроки</th>
                  <th className="num">события</th><th>дочитываемость</th><th className="num">отказы</th></tr>
              </thead>
              <tbody>
                {byAuthor.map((a) => (
                  <tr key={a.author || "—"}>
                    <td><span className="adm-cell-main">{a.author || "—"}</span></td>
                    <td className="num">{fmt(a.titles)}</td>
                    <td className="num">{fmt(a.players)}</td>
                    <td className="num">{fmt(a.events)}</td>
                    <td><Meter value={a.chapter_completion} /></td>
                    <td className={"num " + (a.fail_share > 0.02 ? "amt-minus" : "muted")}>{pct(a.fail_share, 1)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <div className="adm-cols2">
        <section className="adm-panel">
          <header className="adm-panel-head"><h2>По дням</h2></header>
          <ul className="adm-bars">
            {byDay.map((r) => (
              <li key={r.day} className="adm-bars-row">
                <span className="adm-bars-name">{r.day.slice(5)}</span>
                <span className="adm-bars-track"><span className="adm-bars-fill" style={{ width: Math.max(2, share(r.events, maxDay) * 100) + "%" }} /></span>
                <span className="adm-bars-val">{fmt(r.events)}</span>
              </li>
            ))}
          </ul>
        </section>
        <section className="adm-panel">
          <header className="adm-panel-head"><h2>Топ событий</h2></header>
          <ul className="adm-bars">
            {names.map(([n, v]) => (
              <li key={n} className="adm-bars-row">
                <span className="adm-bars-name">{n}</span>
                <span className="adm-bars-track"><span className="adm-bars-fill" style={{ width: Math.max(2, share(v, maxName) * 100) + "%" }} /></span>
                <span className="adm-bars-val">{fmt(v)}</span>
              </li>
            ))}
          </ul>
        </section>
      </div>

      <section className="adm-panel">
        <header className="adm-panel-head"><h2>По часам (UTC)</h2></header>
        <div className="an-hours">
          {byHour.map((v, h) => (
            <div key={h} className="an-hour" title={h + ":00 — " + fmt(v)}>
              <span className="an-hour-bar" style={{ height: Math.max(2, share(v, maxHour) * 100) + "%" }} />
              <span className="an-hour-lbl">{h % 6 === 0 ? h : ""}</span>
            </div>
          ))}
        </div>
      </section>

      <p className="adm-dim adm-foot-note">
        Покрытие: событий без игрока {fmt(cov.events_without_user)}, без новеллы {fmt(cov.events_without_title)}
        {(cov.titles_without_author || []).length > 0 && <> · без автора: {(cov.titles_without_author || []).join(", ")}</>}
        {d.bad_lines > 0 && <> · битых строк в логе: {fmt(d.bad_lines)}</>}
      </p>
    </LoadState>
  );
}

function Meter({ value }) {
  const v = Number(value) || 0;
  return (
    <span className="an-meter" title={pct(value, 1)}>
      <span className="an-meter-track"><span className="an-meter-fill" style={{ width: Math.max(2, Math.min(1, v) * 100) + "%" }} /></span>
      <span className="an-meter-val">{pct(value, 0)}</span>
    </span>
  );
}

// ── Обрывы ──────────────────────────────────────────────────────────────────
function Drops({ token, q, titles }) {
  const [title, setTitle] = useState("");
  const [min, setMin] = useState(5);
  const query = q + (q ? "&" : "") + "min=" + Math.max(1, Number(min) || 1);
  const rep = useAsync(() => analyticsFunnel(query, title, token), [query, title, token]);
  const d = rep.data || {};

  return (
    <>
      <div className="an-controls">
        <label className="adm-dim">
          Новелла{" "}
          <select className="field adm-titlepick" value={title} onChange={(e) => setTitle(e.target.value)}>
            <option value="">все (лидерборд утечек)</option>
            {titles.map((t) => <option key={t.title} value={t.title}>{t.name || t.title}</option>)}
          </select>
        </label>
        <label className="adm-dim" title="ниже этого числа игроков шаг не попадает в рейтинг — «падение на 90%» на двух игроках не сигнал">
          Порог выборки{" "}
          <input type="number" min="1" className="field adm-minbox" value={min}
                 onChange={(e) => setMin(e.target.value)} />
        </label>
        <button className="adm-iconbtn" onClick={rep.reload} title="обновить">⟳</button>
      </div>

      <LoadState loading={rep.loading} error={rep.error}
                 empty={!title && !(d.dropoffs || []).length && !(d.stop_points || []).length}
                 emptyText="Данных для воронки пока нет.">
        {title ? <TitleFunnel rep={d} /> : <Leaderboard rep={d} />}
      </LoadState>
    </>
  );
}

function Leaderboard({ rep }) {
  const drops = rep.dropoffs || [];
  const stops = rep.stop_points || [];
  return (
    <>
      <section className="adm-panel">
        <header className="adm-panel-head">
          <h2>Где теряются игроки</h2>
          <span className="adm-dim">ранжировано по абсолютно потерянным</span>
        </header>
        {!drops.length ? <Empty text="Ни один шаг не набрал выборки выше порога." /> : (
          <div className="adm-tablewrap">
            <table className="adm-table">
              <thead><tr><th>глава</th><th>новелла</th><th>тип обрыва</th><th className="num">потеряно</th><th>доля</th><th className="num">остановились тут</th></tr></thead>
              <tbody>
                {drops.map((x, i) => (
                  <tr key={i}>
                    <td>
                      <span className="adm-cell-main">{x.name || x.chapter}</span>
                      {x.position ? <span className="adm-dim"> · #{x.position}</span> : null}
                    </td>
                    <td className="muted">{x.title}</td>
                    <td><span className={"adm-drop " + x.kind} title={dropKindHint(x.kind)}>{dropKindLabel(x.kind)}</span></td>
                    <td className={"num sev-" + severity(x)}>{fmt(x.lost)}</td>
                    <td><Meter value={x.rate} /></td>
                    <td className="num muted">{fmt(x.players_stopped_here || 0)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="adm-dim adm-foot-note">
          «{dropKindLabel("in_chapter")}» — {dropKindHint("in_chapter")}. «{dropKindLabel("after_chapter")}» — {dropKindHint("after_chapter")}.
        </p>
      </section>

      <section className="adm-panel">
        <header className="adm-panel-head">
          <h2>Последняя увиденная глава</h2>
          <span className="adm-dim">без порядка глав и без chapter_finish — спорить не с чем</span>
        </header>
        {!stops.length ? <Empty text="Пока никто не остановился внутри отслеживаемой главы." /> : (
          <div className="adm-tablewrap">
            <table className="adm-table">
              <thead><tr><th>глава</th><th>новелла</th><th className="num">игроков</th><th className="num">старты</th><th>дочитываемость</th></tr></thead>
              <tbody>
                {stops.map((s, i) => (
                  <tr key={i}>
                    <td><span className="adm-cell-main">{s.name || s.chapter}</span></td>
                    <td className="muted">{s.title}</td>
                    <td className="num">{fmt(s.players_stopped_here)}</td>
                    <td className="num muted">{fmt(s.starts)}</td>
                    <td><Meter value={s.completion} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </>
  );
}

function TitleFunnel({ rep }) {
  const f = rep.funnel || {};
  const steps = f.steps || [];
  const drops = f.dropoffs || [];
  const top = funnelMax(steps);
  const notes = f.notes || [];

  return (
    <>
      <section className="adm-panel">
        <header className="adm-panel-head">
          <h2>{f.name || f.title}</h2>
          <span className="adm-dim">
            порядок глав: {f.order === "manifest" ? "из манифеста" : "по первому событию"} · вошли: {fmt(f.entrants)}
          </span>
        </header>
        {notes.map((n, i) => <p key={i} className="adm-dim">⚠ {n}</p>)}
        {!steps.length ? <Empty text="У этой новеллы нет ни одного chapter_start за окно." /> : (
          <ul className="an-funnel">
            {steps.map((s) => (
              <li key={s.chapter} className="an-funnel-step">
                <span className="an-funnel-name">
                  {s.name || s.chapter}
                  {s.off_manifest && <span className="pill warn-pill" title="главы нет в манифесте — переименована или удалена">вне манифеста</span>}
                </span>
                <span className="an-funnel-track">
                  <span className="an-funnel-fill" style={{ width: share(s.starts, top) * 100 + "%" }} />
                  <span className="an-funnel-done" style={{ width: share(s.finishes, top) * 100 + "%" }} />
                </span>
                <span className="an-funnel-nums">
                  <b>{fmt(s.starts)}</b> вошли · {fmt(s.finishes)} дочитали
                  {s.lost_in_chapter > 0 && <span className="amt-minus"> · −{fmt(s.lost_in_chapter)} внутри</span>}
                  {s.lost_after_chapter > 0 && <span className="amt-minus"> · −{fmt(s.lost_after_chapter)} после</span>}
                  {s.fail_events > 0 && <span className="amt-minus"> · {fmt(s.fail_events)} отказов</span>}
                </span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="adm-panel">
        <header className="adm-panel-head"><h2>Точки обрыва этой новеллы</h2></header>
        {!drops.length ? <Empty text="Ни один шаг не набрал выборки выше порога." /> : (
          <div className="adm-tablewrap">
            <table className="adm-table">
              <thead><tr><th>глава</th><th>тип</th><th className="num">потеряно</th><th>доля</th><th className="num">база</th></tr></thead>
              <tbody>
                {drops.map((x, i) => (
                  <tr key={i}>
                    <td>{x.name || x.chapter}</td>
                    <td><span className={"adm-drop " + x.kind} title={dropKindHint(x.kind)}>{dropKindLabel(x.kind)}</span></td>
                    <td className={"num sev-" + severity(x)}>{fmt(x.lost)}</td>
                    <td><Meter value={x.rate} /></td>
                    <td className="num muted">{fmt(x.base)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="adm-panel">
        <header className="adm-panel-head"><h2>Главы</h2></header>
        <div className="adm-tablewrap">
          <table className="adm-table">
            <thead><tr><th>глава</th><th className="num">события</th><th className="num">старты</th><th className="num">финиши</th><th>дочитываемость</th><th className="num">отказы</th></tr></thead>
            <tbody>
              {(rep.chapters || []).map((c) => (
                <tr key={c.chapter}>
                  <td><span className="adm-cell-main">{c.name || c.chapter}</span></td>
                  <td className="num muted">{fmt(c.events)}</td>
                  <td className="num">{fmt(c.starts)}</td>
                  <td className="num">{fmt(c.finishes)}</td>
                  <td><Meter value={c.completion} /></td>
                  <td className={"num " + (c.fail_events ? "amt-minus" : "muted")}>{fmt(c.fail_events || 0)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </>
  );
}

// ── Здоровье ────────────────────────────────────────────────────────────────
function Health({ token, q }) {
  const rep = useAsync(() => analyticsHealth(q, token), [q, token]);
  const d = rep.data || {};
  const s = d.sessions || {};
  const pending = gapsPending(d.gaps);

  return (
    <LoadState loading={rep.loading} error={rep.error}>
      <div className="adm-kpis tight">
        <Stat label="событий отказа" value={fmt(d.fail_events || 0)} tone={d.fail_events ? "bad" : ""} />
        <Stat label="доля отказов" value={pct(d.fail_event_share, 2)} />
        <Stat label={"сессий (" + (s.basis || "—") + ")"} value={fmt(s.total || 0)} />
        <Stat label="сессий с отказом" value={pct(s.share, 1)} tone={s.share > 0.1 ? "bad" : ""} />
        <Stat label="битых строк" value={fmt(d.bad_lines || 0)} tone={d.bad_lines ? "bad" : ""} />
      </div>
      {s.note && <p className="adm-dim adm-foot-note">{s.note}</p>}

      <div className="adm-cols2">
        <CountPanel title="Отказы по имени события" rows={d.failures_by_name} empty="Отказов не было." />
        <CountPanel title="Неизвестные опы" rows={d.unknown_ops}
                    empty="Клиент не прислал ни одного unknown_op." />
      </div>
      <CountPanel title="Промахи по ассетам" rows={d.asset_failures}
                  empty="Ни одного asset_fail — либо всё грузится, либо клиент их не шлёт." />

      <section className="adm-panel">
        <header className="adm-panel-head">
          <h2>Худшие главы по отказам</h2>
          <span className="adm-dim">это чинит разработка, не сценарист</span>
        </header>
        {!(d.worst_chapters || []).length ? <Empty text="Отказов по главам нет." /> : (
          <div className="adm-tablewrap">
            <table className="adm-table">
              <thead><tr><th>глава</th><th>новелла</th><th className="num">отказов</th><th>на вход</th><th className="num">остановились</th></tr></thead>
              <tbody>
                {(d.worst_chapters || []).map((c, i) => (
                  <tr key={i}>
                    <td><span className="adm-cell-main">{c.name || c.chapter}</span></td>
                    <td className="muted">{c.title}</td>
                    <td className={"num sev-" + severity(c)}>{fmt(c.lost)}</td>
                    <td><Meter value={c.rate} /></td>
                    <td className="num muted">{fmt(c.players_stopped_here || 0)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="adm-panel">
        <header className="adm-panel-head">
          <h2>Слепые зоны</h2>
          <span className="adm-dim">{pending.length} из {(d.gaps || []).length} ещё не закрыты</span>
        </header>
        <ul className="an-gaps">
          {(d.gaps || []).map((g) => (
            <li key={g.event} className={"an-gap" + (Number(g.seen) ? " seen" : "")}>
              <div className="an-gap-head">
                <code>{g.event}</code>
                <span className={Number(g.seen) ? "tag-ok" : "muted"}>
                  {Number(g.seen) ? "приходит (" + fmt(g.seen) + ")" : "не приходит"}
                </span>
              </div>
              <p className="adm-dim">{g.blind_spot}</p>
              {!Number(g.seen) && <p className="an-gap-fix"><b>что добавить в клиент:</b> {g.client_fix}</p>}
            </li>
          ))}
        </ul>
      </section>

      <p className="adm-dim adm-foot-note">
        Роллап: дней в кэше {fmt((d.rollup || {}).cached_days)} · сверток {fmt((d.rollup || {}).folds)} ·
        пересборок {fmt((d.rollup || {}).rebuilds)} · прочитано {fmt((d.rollup || {}).bytes_folded)} b ·
        схема v{(d.rollup || {}).schema}
      </p>
    </LoadState>
  );
}

function CountPanel({ title, rows, empty }) {
  const list = rows || [];
  const max = Math.max(1, ...list.map((r) => r.count || 0));
  return (
    <section className="adm-panel">
      <header className="adm-panel-head"><h2>{title}</h2></header>
      {!list.length ? <p className="adm-dim">{empty}</p> : (
        <ul className="adm-bars">
          {list.map((r) => (
            <li key={r.name} className="adm-bars-row">
              <span className="adm-bars-name" title={r.name}>{r.name}</span>
              <span className="adm-bars-track"><span className="adm-bars-fill bad" style={{ width: Math.max(2, share(r.count, max) * 100) + "%" }} /></span>
              <span className="adm-bars-val">{fmt(r.count)}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
