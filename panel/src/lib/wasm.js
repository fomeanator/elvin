// Loads the Go WASM build of the lvnconv pipeline (the SAME converter + validator
// the CLI uses — one source of truth) and exposes window.lvnsCompile(src).

let ready = false;
let loading = null;

export function ensureWasm() {
  if (ready) return Promise.resolve(true);
  if (loading) return loading;
  loading = (async () => {
    try {
      if (typeof window.Go !== "function") throw new Error("wasm_exec.js not loaded");
      const go = new window.Go();
      const res = await WebAssembly.instantiateStreaming(fetch("/lvns.wasm"), go.importObject);
      go.run(res.instance);
      ready = true;
      return true;
    } catch (e) {
      loading = null; // let the next call retry instead of being stuck on a rejected promise
      throw e;
    }
  })();
  return loading;
}

// Returns { ok, json, errors, warnings }. extGrammar is the project's optional
// host-op declaration (an object); the wasm side takes it as a JSON string and
// then validates declared `ext` ops like built-ins.
export function compileLvns(src, extGrammar) {
  if (typeof window.lvnsCompile !== "function") {
    return { ok: false, errors: "compiler not ready" };
  }
  return window.lvnsCompile(src, extGrammar ? JSON.stringify(extGrammar) : "");
}
