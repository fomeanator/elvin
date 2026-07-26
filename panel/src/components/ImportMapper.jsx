import { useEffect, useState } from "react";
import { getImportTemplate, putImportTemplate, detectRoles } from "../lib/api.js";
import { LoadState } from "./admin/ui.jsx";
import { useJsonDoc, JsonCard } from "./admin/jsonTools.jsx";

// ImportMapper: the "author builds the mapper by hand" screen — a preview
// of what a Template does and doesn't already handle for a project, with
// per-speaker controls for what auto-detection can't safely decide on its
// own (role, alias merges, display names). Opens as a modal step inside
// LibraryHome's BundleModal, once the articy archive is staged+extracted.
//
// dir: the extracted project directory (see stageExtractArticy).
// varsPath: the same bundle's staged -vars.xlsx, when the author picked one.
// The import reads the emotion legend and the protagonist's roster art from
// that sheet; a preview run without it warns about holes the import doesn't
// have. Passing it makes the preview and the import agree.
// initialTemplateName: the template to start editing from ("" → "default").
// onSaved(name): called after a successful PUT — LibraryHome sets
// bundle.template to it and closes the mapper.

const ROLE_OPTIONS = [
  { value: "protagonist", label: "Протагонист" },
  { value: "npc", label: "Персонаж" },
  { value: "narrator", label: "Рассказчик" },
];

function roleOf(draft, who) {
  const st = (draft && draft.staging) || {};
  if ((st.narrator_roles || []).includes(who)) return "narrator";
  if ((st.protagonist_roles || []).includes(who)) return "protagonist";
  return "npc";
}

function withoutFrom(list, who) {
  return (list || []).filter((x) => x !== who);
}

function setRole(draft, who, role, defaults) {
  const st = { ...(draft.staging || {}) };
  // Templates are overlay-by-presence: writing `narrator_roles: []` REPLACES
  // the default's 9 narrator roles with nothing, silently turning «Автор»
  // into a staged NPC. When a partial template doesn't declare a list, seed
  // it from the built-in default before touching it — never from undefined.
  const seed = (cur, key) => (cur !== undefined ? cur : (defaults && defaults[key]) || []);
  st.narrator_roles = withoutFrom(seed(st.narrator_roles, "narrator_roles"), who);
  st.protagonist_roles = withoutFrom(seed(st.protagonist_roles, "protagonist_roles"), who);
  st.protagonist_speaker_labels = withoutFrom(seed(st.protagonist_speaker_labels, "protagonist_speaker_labels"), who);
  if (role === "narrator") st.narrator_roles = [...st.narrator_roles, who];
  if (role === "protagonist") {
    st.protagonist_roles = [...st.protagonist_roles, who];
    st.protagonist_speaker_labels = [...st.protagonist_speaker_labels, who];
  }
  return { ...draft, staging: st };
}

function setAlias(draft, who, canon) {
  const aliases = { ...(draft.speaker_aliases || {}) };
  if (canon) aliases[who] = canon; else delete aliases[who];
  return { ...draft, speaker_aliases: aliases };
}

function setDisplayName(draft, who, name) {
  const names = { ...(draft.speaker_names || {}) };
  if (name) names[who] = name; else delete names[who];
  return { ...draft, speaker_names: names };
}

function setSceneMarkerRegex(draft, pattern) {
  return { ...draft, staging: { ...(draft.staging || {}), scene_marker_regex: pattern } };
}

// buildSceneMarkerRegex turns ONE real example line + the location substring
// the author picked out of it into a regex — no regex syntax required from
// the author. Escapes everything literal, then generalizes the two things
// that vary line-to-line even within one convention: runs of digits (scene
// numbers) → \d+, runs of spaces → \s+/\s*. A trailing "." on the tail is
// made optional, matching how these markers are usually punctuated.
function buildSceneMarkerRegex(example, location) {
  const text = (example || "").trim();
  const loc = (location || "").trim();
  const idx = loc ? text.indexOf(loc) : -1;
  if (idx === -1) return "";
  const esc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&").replace(/\d+/g, "\\d+").replace(/ +/g, "\\s+");
  const before = esc(text.slice(0, idx));
  let after = text.slice(idx + loc.length);
  let trailingDot = "";
  if (after.endsWith(".")) { trailingDot = "\\.?"; after = after.slice(0, -1); }
  // No literal anchor left = a pattern that matches EVERY line. The natural
  // hasty input — pasting the whole line into the "location" field — produced
  // exactly that, the preview cheerfully showed 100% (it only tests against
  // scene-marker MISSES), and AutoStage then deleted the entire narration,
  // turning every prose line into a 404 background. Refuse to build it.
  if (!before && !esc(after)) return "";
  return "^\\s*" + before + "(.+?)" + esc(after) + trailingDot + "\\s*$";
}

function setPlaceholderProtagonist(draft, on) {
  return { ...draft, staging: { ...(draft.staging || {}), placeholder_protagonist: on } };
}

function setEmotionColor(draft, hex, emotion) {
  const colors = { ...(draft.emotion_colors || {}) };
  const key = hex.replace(/^#/, "");
  if (emotion) colors[key] = emotion; else delete colors[key];
  return { ...draft, emotion_colors: colors };
}

// collisionFor: the first alias suggestion touching `who`, if any — the
// mapper pre-fills the alias field from it instead of making the author
// retype a name the heuristic already found.
function collisionFor(report, who) {
  for (const c of (report && report.alias_collisions) || []) {
    if (c.a === who) return c.b;
    if (c.b === who) return c.a;
  }
  return "";
}

export default function ImportMapper({ dir, varsPath, initialTemplateName, creds, notify, onSaved, onCancel }) {
  const [templateName, setTemplateName] = useState(initialTemplateName || "");
  const [draft, setDraft] = useState(null);
  const [report, setReport] = useState(null);
  const [roleDefaults, setRoleDefaults] = useState(null); // built-in staging lists, for partial-template seeding
  const [loading, setLoading] = useState(true);
  const [recomputing, setRecomputing] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      setLoading(true);
      setError(null);
      try {
        const wantDefault = !initialTemplateName || initialTemplateName === "default";
        const [tpl, rep, base] = await Promise.all([
          getImportTemplate(initialTemplateName || "default", creds.token),
          detectRoles(dir, { template: initialTemplateName, vars: varsPath }, creds.token),
          // A PARTIAL template inherits the built-in staging lists; editing a
          // role must seed from those, not from undefined (which would save
          // `narrator_roles: []` and wipe all 9 default narrator roles).
          wantDefault ? null : getImportTemplate("default", creds.token).catch(() => null),
        ]);
        if (cancelled) return;
        setDraft(tpl);
        setReport(rep);
        setRoleDefaults(((base || tpl) || {}).staging || null);
      } catch (e) {
        if (!cancelled) setError(e.message);
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dir, varsPath]);

  async function recompute() {
    setRecomputing(true);
    try {
      setReport(await detectRoles(dir, { draft, vars: varsPath }, creds.token));
    } catch (e) {
      notify("✗ " + e.message, "err");
    } finally {
      setRecomputing(false);
    }
  }

  async function save() {
    const name = templateName.trim();
    if (!name) { notify("Назови шаблон.", "err"); return; }
    setSaving(true);
    try {
      await putImportTemplate(name, draft, creds.token);
      notify(`✓ Шаблон «${name}» сохранён`, "ok");
      onSaved(name);
    } catch (e) {
      notify("✗ " + e.message, "err");
    } finally {
      setSaving(false);
    }
  }

  const mutate = (fn) => setDraft((d) => fn(d));

  return (
    <div className="sp-chooser" onClick={onCancel}>
      <div className="sp-chooser-box mapper-modal" onClick={(e) => e.stopPropagation()}>
        <h3>Настроить роли импорта</h3>
        <LoadState loading={loading} error={error}>
          {report && draft && (
            <>
              <MapperWarnings
                report={report}
                draft={draft}
                onSceneMarkerChange={(v) => mutate((d) => setSceneMarkerRegex(d, v))}
                onEmotionChange={(hex, emo) => mutate((d) => setEmotionColor(d, hex, emo))}
                onPlaceholderProtagonistChange={(on) => mutate((d) => setPlaceholderProtagonist(d, on))}
              />
              <div className="adm-tablewrap mapper-tablewrap">
                <table className="adm-table dense">
                  <thead>
                    <tr>
                      <th>Спикер</th>
                      <th className="num">Реплик</th>
                      <th>Арт</th>
                      <th>Роль</th>
                      <th>Объединить с…</th>
                      <th>Имя на экране</th>
                    </tr>
                  </thead>
                  <tbody>
                    {(report.speakers || []).map((s) => (
                      <SpeakerRow
                        key={s.who}
                        s={s}
                        role={roleOf(draft, s.who)}
                        alias={(draft.speaker_aliases || {})[s.who] || ""}
                        aliasSuggestion={collisionFor(report, s.who)}
                        displayName={(draft.speaker_names || {})[s.who] || ""}
                        onRole={(role) => mutate((d) => setRole(d, s.who, role, roleDefaults))}
                        onAlias={(canon) => mutate((d) => setAlias(d, s.who, canon))}
                        onDisplayName={(name) => mutate((d) => setDisplayName(d, s.who, name))}
                      />
                    ))}
                  </tbody>
                </table>
              </div>
              <AdvancedJsonPanel draft={draft} onApply={(doc) => mutate(() => doc)} />
            </>
          )}
        </LoadState>
        <label className="adv-field mapper-name-field">
          <span>Сохранить как шаблон</span>
          <input className="field wide" placeholder="soviet" value={templateName}
                 onChange={(e) => setTemplateName(e.target.value)} />
        </label>
        <div className="novel-modal-actions">
          <button className="btn-ghost" onClick={recompute} disabled={loading || recomputing || !draft}>
            {recomputing ? "Пересчитываем…" : "Пересчитать"}
          </button>
          <div className="grow" />
          <button className="btn-ghost" onClick={onCancel}>Отмена</button>
          <button className="btn btn-primary" onClick={save} disabled={loading || saving || !draft}>
            {saving ? "Сохраняем…" : "Сохранить как шаблон ▸"}
          </button>
        </div>
      </div>
    </div>
  );
}

function MapperWarnings({ report, draft, onSceneMarkerChange, onEmotionChange, onPlaceholderProtagonistChange }) {
  const hitRate = report.scene_marker_hit_rate || 0;
  const showSceneWarning = report.scene_marker_candidates > 0 && hitRate < 0.5;
  const misses = report.emotion_color_misses || [];
  // The preview builds its cast from the ARTICY roster only. When the bundle
  // also carries a -vars.xlsx, the import injects the protagonist's roster art
  // from the spreadsheet (server: xlsx_protagonist) — so "she'll stay
  // invisible" is false, and pushing the author to enable the grey placeholder
  // is a decision made on wrong data. Say what will actually happen instead.
  const xlsxProtagonist = report.xlsx_protagonist || "";
  const xlsxError = report.xlsx_error || "";
  if (!showSceneWarning && !misses.length && !xlsxError && !(report.protagonist_without_art || []).length) return null;
  // unset (undefined/null) = ON: per the partner, a spriteless protagonist
  // is always a hole in the files, never a design — the placeholder makes
  // the hole visible. Explicit false opts a project out.
  const pp = draft.staging && draft.staging.placeholder_protagonist;
  const placeholderOn = pp !== false;
  return (
    <div className="mapper-warnings">
      {xlsxError && (
        <div className="mapper-warn">
          ⚠ Таблица переменных (.xlsx) не прочиталась: {xlsxError}. Превью посчитано БЕЗ неё —
          легенда эмоций и арт протагониста из ростера здесь не учтены, импорт даст другой результат.
        </div>
      )}
      {(report.protagonist_without_art || []).length > 0 && (
        <div className="mapper-warn">
          {xlsxProtagonist ? (
            <>
              ℹ В articy-ростере нет арта для: {report.protagonist_without_art.join(", ")} — но таблица
              переменных даёт её ростер «{xlsxProtagonist}», и импорт поставит её настоящей.
              Плейсхолдер не понадобится.
            </>
          ) : (
            <>
              ⚠ Без арта: {report.protagonist_without_art.join(", ")} — будет показан серым плейсхолдером
              (дыра в файлах видна сразу). Правильный фикс — алиас на реального персонажа в таблице ниже
              или дослать портрет.
            </>
          )}
          <label className="mapper-checkbox-row">
            <input type="checkbox" checked={placeholderOn}
                   onChange={(e) => onPlaceholderProtagonistChange(e.target.checked)} />
            <span>Показывать серый плейсхолдер, если у роли-протагониста нет арта</span>
          </label>
        </div>
      )}
      {showSceneWarning && (
        <div className="mapper-warn">
          ⚠ Маркер сцены совпадает только с {Math.round(hitRate * 100)}% строк рассказчика.
          <SceneMarkerBuilder samples={report.scene_marker_misses || []} onApply={onSceneMarkerChange} />
          <label className="adv-field mapper-inline-field">
            <span>staging.scene_marker_regex (можно править и вручную)</span>
            <input className="field wide" value={(draft.staging && draft.staging.scene_marker_regex) || ""}
                   onChange={(e) => onSceneMarkerChange(e.target.value)} />
          </label>
        </div>
      )}
      {misses.length > 0 && (
        <div className="mapper-warn">
          ⚠ {misses.length} цвет(ов) маркера без эмоции
          {report.xlsx_emotion_colors ? ` (легенда из .xlsx учтена: ${report.xlsx_emotion_colors} цв.)` : ""}:
          <div className="mapper-color-list">
            {misses.map((c) => (
              <label key={c.Hex} className="mapper-color-row">
                <span className="mapper-swatch" style={{ background: c.Hex }} />
                <code>{c.Hex}</code>
                <span className="mapper-color-count">×{c.Count}</span>
                <input className="field" placeholder="эмоция" defaultValue={c.Emotion || ""}
                       onBlur={(e) => onEmotionChange(c.Hex, e.target.value.trim())} />
              </label>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// SceneMarkerBuilder: build a scene-marker pattern from a REAL example line
// instead of asking the author to write regex. Pick one of the actual
// unmatched narration lines from this project, select (or retype) the
// substring that's the location name, and a working pattern is generated —
// the raw regex field right below stays available for manual fine-tuning.
function SceneMarkerBuilder({ samples, onApply }) {
  const [example, setExample] = useState(samples[0] || "");
  const [location, setLocation] = useState("");
  const pattern = buildSceneMarkerRegex(example, location);
  let matchCount = 0;
  if (pattern) {
    try {
      const re = new RegExp(pattern);
      matchCount = samples.filter((s) => re.test(s)).length;
    } catch { /* mid-edit, ignore */ }
  }
  return (
    <div className="mapper-scenebuilder">
      <label className="adv-field mapper-inline-field">
        <span>Пример строки из текста (реальная строка этого проекта)</span>
        {samples.length > 0 ? (
          <select className="field wide" value={example} onChange={(e) => setExample(e.target.value)}>
            {samples.map((s, i) => <option key={i} value={s}>{s}</option>)}
          </select>
        ) : (
          <input className="field wide" value={example} onChange={(e) => setExample(e.target.value)}
                 placeholder="Сцена 5. Кухня." />
        )}
      </label>
      <label className="adv-field mapper-inline-field">
        <span>Что в этой строке — название места? (вставь ровно как в примере)</span>
        <input className="field wide" value={location} onChange={(e) => setLocation(e.target.value)}
               placeholder="Кухня" />
      </label>
      {pattern && (
        <div className="mapper-scenebuilder-preview">
          <code>{pattern}</code>
          <span className="mapper-color-count">
            {matchCount}/{samples.length} примеров совпадёт
          </span>
          <button className="btn-ghost sm" onClick={() => onApply(pattern)}>Применить</button>
        </div>
      )}
    </div>
  );
}

// AdvancedJsonPanel: the escape hatch for everything the table above doesn't
// cover — wardrobe layout, audio-cue prefixes, stats config, var_chapter_
// prefixes — the WHOLE Template as JSON, using the same editor/dirty-
// tracking kit as the admin config/manifest cards (JsonCard). Collapsed by
// default so a non-technical author never has to see it; "Применить"
// applies to the in-memory draft only — still goes through the normal
// "Сохранить как шаблон" button below to persist.
function AdvancedJsonPanel({ draft, onApply }) {
  const [open, setOpen] = useState(false);
  const doc = useJsonDoc(draft);
  if (!open) {
    return (
      <button className="btn-ghost mapper-advanced-toggle" onClick={() => setOpen(true)}>
        Продвинутый режим (гардероб, аудио, статы — сырой JSON) ▾
      </button>
    );
  }
  return (
    <div className="mapper-advanced">
      <div className="mapper-advanced-head">
        <span>Весь шаблон целиком — для полей, которых нет в таблице выше</span>
        <button className="btn-ghost sm" onClick={() => setOpen(false)}>Свернуть</button>
      </div>
      <JsonCard doc={doc} onSave={onApply} saveLabel="Применить к черновику" height={280} />
    </div>
  );
}

function SpeakerRow({ s, role, alias, aliasSuggestion, displayName, onRole, onAlias, onDisplayName }) {
  return (
    <tr className="adm-row">
      <td className="adm-cell-main" title={(s.sample_lines || []).join("\n")}>{s.who}</td>
      <td className="num">{s.line_count}</td>
      <td>{s.has_art ? <span title={s.art_stem}>✓</span> : <span className="mapper-noart">—</span>}</td>
      <td>
        <select className="field" value={role} onChange={(e) => onRole(e.target.value)}>
          {ROLE_OPTIONS.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
        </select>
      </td>
      <td>
        <input className="field" placeholder={aliasSuggestion ? `предложено: ${aliasSuggestion}` : "имя роли с артом"}
               value={alias} onChange={(e) => onAlias(e.target.value)} />
        {aliasSuggestion && alias !== aliasSuggestion && (
          <button className="btn-ghost mapper-suggest-btn" onClick={() => onAlias(aliasSuggestion)}>
            🔗 {aliasSuggestion}
          </button>
        )}
      </td>
      <td>
        <input className="field" placeholder={s.who} value={displayName}
               onChange={(e) => onDisplayName(e.target.value)} />
      </td>
    </tr>
  );
}
