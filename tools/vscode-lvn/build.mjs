// Assemble lib/ from the shared sources — the extension carries copies, so a
// .vsix is self-contained. Run before packaging: `npm run build`.
//
//   lib/lvn-lang/    ← ../lvn-lang/src (the language core the web IDE uses)
//   lib/lvns.wasm    ← built from ../lvnconv/wasm (or copied from the
//                      playground's prebuilt if Go is unavailable)
//   lib/wasm_exec.js ← Go's JS shim (same copy the playground ships)
import { cpSync, mkdirSync, existsSync, copyFileSync, writeFileSync } from "fs";
import { execSync } from "child_process";
import { dirname, join } from "path";
import { fileURLToPath } from "url";

const here = dirname(fileURLToPath(import.meta.url));
const lib = join(here, "lib");
mkdirSync(join(lib, "lvn-lang"), { recursive: true });

cpSync(join(here, "../lvn-lang/src"), join(lib, "lvn-lang"), { recursive: true });
// The copies are ES modules but the extension package is CJS — mark the
// subtree so Node doesn't have to sniff (and warn) on every import.
writeFileSync(join(lib, "lvn-lang", "package.json"), JSON.stringify({ type: "module" }) + "\n");

const play = join(here, "../../panel/public/play");
copyFileSync(join(play, "wasm_exec.js"), join(lib, "wasm_exec.js"));

let built = false;
try {
  execSync("go build -o " + JSON.stringify(join(lib, "lvns.wasm")) + " .", {
    cwd: join(here, "../lvnconv/wasm"),
    env: { ...process.env, GOOS: "js", GOARCH: "wasm" },
    stdio: "pipe",
  });
  built = true;
} catch { /* no Go toolchain — fall back to the playground's prebuilt */ }
if (!built) {
  if (!existsSync(join(play, "lvns.wasm"))) {
    console.error("no Go toolchain and no prebuilt lvns.wasm — diagnostics would be dead");
    process.exit(1);
  }
  copyFileSync(join(play, "lvns.wasm"), join(lib, "lvns.wasm"));
}
console.log("lib/ ready (wasm " + (built ? "built fresh" : "copied from the playground") + ")");
