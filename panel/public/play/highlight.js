// Featherweight .lvns syntax highlighting: a <pre> painted behind a
// transparent-text <textarea>, kept in scroll/size lockstep. No Monaco, no
// dependencies — the token grammar is a handful of line-level regexes that
// mirror how the converter reads a line.

const RULES = [
  { re: /^(\s*)(\/\/.*)$/, cls: ["", "cmt"] },
  { re: /^(\s*)(scene|actor_map)(\s.*)?$/, cls: ["", "kw", ""] },
  { re: /^(\s*)(:[\wА-Яа-яЁё_]+)\s*$/, cls: ["", "lbl"] },
  { re: /^(\s*)(->)(\s*)([\wА-Яа-яЁё_]+)?(.*)$/, cls: ["", "kw", "", "lbl", ""] },
  { re: /^(\s*)(-)(\s)(.*)$/, cls: ["", "kw", "", "opt"] },
  { re: /^(\s*)(if|for|while|func|return|call|goto|else)(\b.*)?$/, cls: ["", "kw", ""] },
  { re: /^(\s*)(bg|actor|obj|say|choice|input|voice|wait|audio|fade|dim|flash|tint|blur|camera|particles|text|text_pace|anim|move|play|defanim|preload|save|load|set|inc|ext|hint)(\s.*|$)/, cls: ["", "cmd", ""] },
  { re: /^(\s*)([\wА-Яа-яЁё_]+)(\s*=\s*)(.*)$/, cls: ["", "var", "kw", ""] },
  { re: /^(\s*)([^:\n]{1,60})(\s*(?:\[[^\]]*\])?\s*:)(.*)$/, cls: ["", "who", "kw", ""] },
];

const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;");

// Inline decorations applied inside already-classified line chunks.
function inline(s) {
  return esc(s)
    .replace(/("[^"]*"|«[^»]*»)/g, '<i class="str">$1</i>')
    .replace(/\{[^{}]+\}/g, '<i class="interp">$&</i>');
}

export function highlight(src) {
  return src.split("\n").map((line) => {
    for (const { re, cls } of RULES) {
      const m = re.exec(line);
      if (!m) continue;
      let out = "";
      for (let g = 1; g < m.length; g++) {
        const part = m[g] ?? "";
        const c = cls[g - 1];
        out += c ? `<i class="${c}">${inline(part)}</i>` : inline(part);
      }
      return out;
    }
    return inline(line); // plain narration
  }).join("\n");
}

/** Wire a textarea + backdrop <pre> pair. */
export function attach(textarea, backdrop) {
  const paint = () => { backdrop.innerHTML = highlight(textarea.value) + "\n"; };
  const sync = () => {
    backdrop.scrollTop = textarea.scrollTop;
    backdrop.scrollLeft = textarea.scrollLeft;
  };
  textarea.addEventListener("input", paint);
  textarea.addEventListener("scroll", sync);
  paint();
  return paint;
}
