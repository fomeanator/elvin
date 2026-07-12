// Smoke the extension's two engines outside VS Code: the language core loads
// and answers, and the wasm compiler runs under Node and reports diagnostics.
// `node build.mjs && node test-node.mjs`
import { createRequire } from "module";
import { readFileSync } from "fs";
import { dirname, join } from "path";
import { fileURLToPath } from "url";
import assert from "assert";

const here = dirname(fileURLToPath(import.meta.url));
const lang = await import(join(here, "lib/lvn-lang/index.js"));

const src = `scene t
Mara: Hello!
- Stay -> stay
:stay
Mara: Good.
-> missing_label
`;

// language core
const comp = lang.completionAt("sa", lang.labelsIn(src), {}, {}, null);
assert(comp && comp.items.some((i) => i.text === "say"), "completion offers 'say'");
const syms = lang.documentSymbols(src);
assert(syms.some((s) => s.kind === "label" && s.name === "stay"), "outline sees :stay");
const def = lang.definitionAt(src, 3, 11); // 'stay' in the choice target
assert(def && def.line === 4, "go-to-definition finds :stay");

// wasm compiler under Node
const require = createRequire(import.meta.url);
require(join(here, "lib/wasm_exec.js"));
const go = new globalThis.Go();
const { instance } = await WebAssembly.instantiate(readFileSync(join(here, "lib/lvns.wasm")), go.importObject);
go.run(instance);
const out = globalThis.lvnsCompile(src);
assert(out && typeof out.ok === "boolean", "wasm compiler answered");
assert(String(out.errors || out.warnings).includes("missing_label"), "compiler flags the dangling jump");

console.log("ok: completion, outline, definition, wasm diagnostics");
process.exit(0);
