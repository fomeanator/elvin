// Pure helpers for the analytics screens: the window model, the formatting of
// the server's ratios, and the ranking rules the drop-off view applies on top
// of what /v1/analytics/funnel already sorted.

// ── window ──────────────────────────────────────────────────────────────────
// The server accepts three shapes (parseAnalyticsWindow): ?day=, ?days=N and
// ?from=&to=. The panel keeps ONE object and renders it to a query string, so
// every report on the page asks for exactly the same window.

export const todayISO = () => new Date().toISOString().slice(0, 10);

export const WINDOWS = [
  { key: "today", label: "Сегодня" },
  { key: "d7", label: "7 дней", days: 7 },
  { key: "d30", label: "30 дней", days: 30 },
];

// windowQuery renders a window to the query string (no leading "?").
// An out-of-range custom range is NOT silently clamped here: the server
// answers 400 with the rule, and swallowing it would hide a real mistake.
export function windowQuery(win) {
  const w = win || {};
  if (w.from && w.to) return "from=" + encodeURIComponent(w.from) + "&to=" + encodeURIComponent(w.to);
  if (w.days > 1) return "days=" + Number(w.days);
  if (w.day) return "day=" + encodeURIComponent(w.day);
  return "";
}

// windowLabel: what the header says the numbers cover.
export function windowLabel(win) {
  const w = win || {};
  if (w.from && w.to) return w.from === w.to ? w.from : w.from + " — " + w.to;
  if (w.days > 1) return "последние " + w.days + " дн.";
  return w.day || "сегодня";
}

// ── formatting ──────────────────────────────────────────────────────────────

// pct renders the server's 0..1 ratios. null/undefined is "—", not "0%":
// "no data" and "zero" are different answers and a dashboard that conflates
// them lies quietly.
export function pct(x, digits = 0) {
  if (x == null || Number.isNaN(Number(x))) return "—";
  return (Number(x) * 100).toFixed(digits).replace(/\.0+$/, "") + "%";
}

// share: a 0..1 ratio for a bar width, clamped so a rounding artefact can't
// push a bar past its track.
export function share(n, total) {
  const t = Number(total) || 0;
  if (t <= 0) return 0;
  return Math.max(0, Math.min(1, Number(n || 0) / t));
}

// ── drop-off ranking ────────────────────────────────────────────────────────

export const DROP_KIND = {
  in_chapter: {
    label: "внутри главы",
    hint: "вошли и не дошли до конца — длинная глава, софтлок или краш",
  },
  after_chapter: {
    label: "после главы",
    hint: "дочитали и не вернулись — экран конца, гейт энергии, пейволл, слабый клиффхэнгер",
  },
  failures: {
    label: "ошибки",
    hint: "события отказа на входах в главу — чинит разработка, не сценарист",
  },
};

export const dropKindLabel = (kind) => (DROP_KIND[kind] || { label: kind }).label;
export const dropKindHint = (kind) => (DROP_KIND[kind] || { hint: "" }).hint;

// severity grades a drop point for colour. Deliberately двухфакторная: a big
// rate on a tiny base is noise (the server's min-sample already filters the
// worst of it), and a small rate on a huge base is still a lot of people.
// "high" needs BOTH a real share and real bodies.
export function severity(drop) {
  const d = drop || {};
  const rate = Number(d.rate) || 0;
  const lost = Number(d.lost) || 0;
  if (rate >= 0.4 && lost >= 10) return "high";
  if (rate >= 0.25 || lost >= 25) return "med";
  return "low";
}

// worstOf picks the top n drops. The server already sorts by players lost;
// this only guards against a null body and caps the render.
export function worstOf(drops, n = 8) {
  return (drops || []).slice(0, n);
}

// funnelMax: the widest bar in a funnel — every step's bar is drawn relative
// to the FIRST step's starts, not to its own, so the shrinking is visible.
export function funnelMax(steps) {
  let m = 0;
  for (const s of steps || []) m = Math.max(m, Number(s.starts) || 0);
  return m || 1;
}

// biggestLeak: one sentence for the top of the funnel view. Returns null when
// the funnel has no ranked drop at all (a healthy or too-small novel).
export function biggestLeak(funnel) {
  const d = ((funnel || {}).dropoffs || [])[0];
  if (!d) return null;
  return {
    chapter: d.name || d.chapter,
    kind: d.kind,
    lost: d.lost,
    rate: d.rate,
    text: `${d.name || d.chapter}: ${d.lost} игроков потеряно ${dropKindLabel(d.kind)} (${pct(d.rate)})`,
  };
}

// gapsPending: the health report's blind spots that are still blind. An entry
// flips to seen>0 by itself the day the client starts sending the event, so
// the list is a live to-do rather than a doc nobody opens.
export function gapsPending(gaps) {
  return (gaps || []).filter((g) => !Number(g.seen));
}
