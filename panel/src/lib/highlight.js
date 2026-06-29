// A tiny line-oriented syntax highlighter for LVNScript (.lvns). Returns HTML
// (escaped) with <span class="t-*"> token wrappers, rendered behind a
// transparent <textarea>. LVNScript is line-based, so we classify per line.

const OPS = new Set([
  "say", "choice", "bg", "actor", "obj", "fade", "dim", "flash", "tint", "blur",
  "camera", "particles", "audio", "wait", "preload", "text_pace",
  "label", "goto", "if", "set", "inc", "hint", "call", "return",
]);

const esc = (s) =>
  s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

// Highlight `key="value"` / `key=number` / `key=true` attribute pairs.
function attrs(s) {
  return esc(s).replace(/([\w-]+)(=)("[^"]*"|[^\s]+)/g, (_m, k, eq, v) => {
    let cls = "t-val";
    if (v.startsWith('"')) cls = "t-str";
    else if (/^-?\d/.test(v)) cls = "t-num";
    else if (v === "true" || v === "false") cls = "t-bool";
    return `<span class="t-attr">${k}</span>${eq}<span class="${cls}">${v}</span>`;
  });
}

// Narration / dialogue body — dark-yellow, with {var} tokens kept distinct, so
// prose reads clearly apart from commands.
function prose(s) {
  const inner = esc(s).replace(/\{[^{}]*\}/g, (m) => `<span class="t-var">${m}</span>`);
  return `<span class="t-prose">${inner}</span>`;
}

// Does the text after an op keyword actually parse as that command? Mirrors the
// converter so a malformed op line (e.g. `fade to="black"3 …`) is NOT shown as a
// command — it falls through to prose, matching how it compiles (to narration).
function validOpLine(op, rest) {
  rest = rest.trim();
  if (op === "return" || op === "label") return rest === "" || /^\S+$/.test(rest);
  if (op === "goto" || op === "call") return /^\S+$/.test(rest);
  if (rest === "") return true;
  let s = rest;
  const pair = /^[\w-]+\s*=\s*("[^"]*"|[^\s"]+)\s*/;
  while (s.length) {
    const m = pair.exec(s);
    if (!m) return false;
    s = s.slice(m[0].length);
  }
  return true;
}

function choiceBody(s) {
  const arrow = s.indexOf("->");
  if (arrow < 0) return prose(s);
  const before = s.slice(0, arrow);
  const after = s.slice(arrow + 2);
  const am = after.match(/^(\s*)(\S+)(.*)$/);
  let html = prose(before) + `<span class="t-arrow">-&gt;</span>`;
  if (am) html += am[1] + `<span class="t-label">${esc(am[2])}</span>` + attrs(am[3]);
  else html += esc(after);
  return html;
}

function line(raw) {
  const lead = raw.match(/^(\s*)/)[0];
  const body = raw.slice(lead.length);
  if (body === "") return "";

  if (body.startsWith("//")) return lead + `<span class="t-comment">${esc(body)}</span>`;
  if (/^:\S/.test(body)) return lead + `<span class="t-label">${esc(body)}</span>`;

  let m = body.match(/^(scene|actor_map)\b(.*)$/);
  if (m) return lead + `<span class="t-kw">${m[1]}</span>` + attrs(m[2]);

  if (body.startsWith("- ")) return lead + `<span class="t-arrow">-</span>` + choiceBody(body.slice(1));

  m = body.match(/^([a-z_]+)\b(.*)$/);
  if (m && OPS.has(m[1]) && validOpLine(m[1], m[2])) return lead + `<span class="t-kw">${m[1]}</span>` + attrs(m[2]);

  // Speaker line: `Name: text` or `Name [emo]: text`
  m = body.match(/^([^:[\]]+?)(\s*\[[^\]]*\])?:\s(.*)$/);
  if (m) {
    const emo = m[2] ? `<span class="t-emo">${esc(m[2])}</span>` : "";
    return lead + `<span class="t-speaker">${esc(m[1])}</span>` + emo +
      `<span class="t-colon">:</span> ` + prose(m[3]);
  }

  return lead + prose(body);
}

export function highlightLvns(src) {
  return (src || "").split("\n").map(line).join("\n");
}
