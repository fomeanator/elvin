// Elvin Script for VS Code — a thin adapter over the shared language core:
// lib/lvn-lang/ (completion/hover/definition/outline/ghost — the SAME code the
// web Studio's Monaco uses) plus lib/lvns.wasm (the REAL lvnconv compiler) for
// diagnostics. `node build.mjs` copies both in; this file contains no language
// knowledge of its own.
const vscode = require("vscode");
const fs = require("fs");
const path = require("path");
const { pathToFileURL } = require("url");

let lang = null; // the lvn-lang ESM namespace, loaded on activate
let compile = null; // lvnsCompile(src, extGrammarJSON) from the wasm, when it loads

async function loadWasm(ctx) {
  try {
    require(path.join(ctx.extensionPath, "lib", "wasm_exec.js")); // defines globalThis.Go
    const go = new globalThis.Go();
    const bytes = fs.readFileSync(path.join(ctx.extensionPath, "lib", "lvns.wasm"));
    const { instance } = await WebAssembly.instantiate(bytes, go.importObject);
    go.run(instance); // resolves only on exit — the compiler stays resident
    compile = globalThis.lvnsCompile;
  } catch (e) {
    console.warn("lvns.wasm unavailable — diagnostics disabled:", e.message);
  }
}

// ── context: actor_map aliases + the project's ext-grammar sidecar ─────────
function actorMapOf(text) {
  const m = {};
  for (const l of text.split("\n")) {
    const mm = l.match(/^\s*actor_map\s+(\S+)\s*=\s*(\S+)/);
    if (mm) m[mm[1]] = mm[2];
  }
  return m;
}

// ext-grammar.json beside the file or one level up — the same convention as
// lvnconv's FindExtGrammar, so the editor and the CLI agree on host ops.
const extCache = new Map(); // dir → { grammar, raw, at }
function extGrammarFor(docPath) {
  const dir = path.dirname(docPath);
  const hit = extCache.get(dir);
  if (hit && Date.now() - hit.at < 5000) return hit;
  let out = { grammar: null, raw: "", at: Date.now() };
  for (const d of [dir, path.dirname(dir)]) {
    const p = path.join(d, "ext-grammar.json");
    try {
      const raw = fs.readFileSync(p, "utf8");
      out = { grammar: JSON.parse(raw), raw, at: Date.now() };
      break;
    } catch { /* not there / unparsable → treated as absent */ }
  }
  extCache.set(dir, out);
  return out;
}

function ctxFor(doc) {
  const { grammar } = extGrammarFor(doc.uri.fsPath);
  return { catalog: {}, actorMap: actorMapOf(doc.getText()), extGrammar: grammar };
}

// ── diagnostics: the real compiler's verdict, per keystroke (debounced) ────
function refreshDiagnostics(doc, collection) {
  if (!compile || doc.languageId !== "lvns") return;
  const { raw } = extGrammarFor(doc.uri.fsPath);
  const res = compile(doc.getText(), raw || undefined);
  const diags = [];
  const push = (block, sev) => {
    for (const lineText of (block || "").split("\n")) {
      const t = lineText.trim();
      if (!t) continue;
      const m = t.match(/line (\d+)(?:\s+[\w-]+)?:\s*(.*)/);
      const lineNo = m ? Math.max(0, parseInt(m[1], 10) - 1) : 0;
      diags.push(new vscode.Diagnostic(new vscode.Range(lineNo, 0, lineNo, 999), m ? m[2] : t, sev));
    }
  };
  if (!res.ok) push(res.errors, vscode.DiagnosticSeverity.Error);
  push(res.warnings, vscode.DiagnosticSeverity.Warning);
  collection.set(doc.uri, diags);
}

// ── providers: mirror the web Studio's Monaco adapter shapes ───────────────
const COMPLETION_KIND = {
  op: vscode.CompletionItemKind.Keyword,
  directive: vscode.CompletionItemKind.Keyword,
  snippet: vscode.CompletionItemKind.Snippet,
  attr: vscode.CompletionItemKind.Field,
  value: vscode.CompletionItemKind.Value,
  emotion: vscode.CompletionItemKind.EnumMember,
  speaker: vscode.CompletionItemKind.User,
  label: vscode.CompletionItemKind.Reference,
};

async function activate(ctx) {
  lang = await import(pathToFileURL(path.join(ctx.extensionPath, "lib", "lvn-lang", "index.js")).href);
  void loadWasm(ctx);

  const sel = { language: "lvns" };
  const collection = vscode.languages.createDiagnosticCollection("lvns");
  ctx.subscriptions.push(collection);

  let timer = null;
  const schedule = (doc) => {
    clearTimeout(timer);
    timer = setTimeout(() => refreshDiagnostics(doc, collection), 300);
  };
  ctx.subscriptions.push(
    vscode.workspace.onDidChangeTextDocument((e) => schedule(e.document)),
    vscode.workspace.onDidOpenTextDocument((d) => schedule(d)),
  );
  vscode.workspace.textDocuments.forEach((d) => schedule(d));

  ctx.subscriptions.push(vscode.languages.registerCompletionItemProvider(sel, {
    provideCompletionItems(doc, pos) {
      const lineToCaret = doc.lineAt(pos.line).text.slice(0, pos.character);
      const { catalog, actorMap, extGrammar } = ctxFor(doc);
      const r = lang.completionAt(lineToCaret, lang.labelsIn(doc.getText()), catalog, actorMap, extGrammar);
      if (!r) return [];
      const start = pos.character - (r.token ? r.token.length : 0);
      const range = new vscode.Range(pos.line, start, pos.line, pos.character);
      return r.items.map((it) => {
        const item = new vscode.CompletionItem(it.label || it.text, COMPLETION_KIND[it.kind] ?? vscode.CompletionItemKind.Text);
        let insert = it.text;
        if (it.kind === "op" || it.kind === "directive") insert = it.text + " ";
        else if (it.kind === "attr") insert = it.text + "=";
        else if (it.kind === "emotion") insert = it.text + "]: ";
        else if (it.kind === "speaker") {
          if (it.emote) { insert = it.text + " ["; item.command = { command: "editor.action.triggerSuggest", title: "" }; }
          else insert = it.text + ": ";
        }
        if (it.kind === "snippet") item.insertText = new vscode.SnippetString(it.body);
        else item.insertText = insert;
        item.range = range;
        const docEntry = lang.OP_DOCS[it.text];
        if (docEntry) { item.detail = docEntry[0]; item.documentation = docEntry[1]; }
        else item.detail = it.kind;
        return item;
      });
    },
  }, " ", '"', "[", "=", ">", "-"));

  ctx.subscriptions.push(vscode.languages.registerHoverProvider(sel, {
    provideHover(doc, pos) {
      const c = ctxFor(doc);
      const h = lang.hoverAt(doc.getText(), pos.line + 1, pos.character, c);
      if (!h) return null;
      let md = "";
      if (h.kind === "op") md = "`" + h.sig + "`\n\n" + h.desc;
      else if (h.kind === "entity") md = "**" + h.id + "**";
      else if (h.kind === "emotion") md = (h.ok ? "✓ " : "⚠ ") + "`" + h.emo + "` — " + (h.ok ? "emotion of " + h.charId : "not an emotion of " + h.charId + (h.emos && h.emos.length ? " (try: " + h.emos.join(", ") + ")" : ""));
      else if (h.kind === "label") md = "`:" + h.name + "`" + (h.defined ? " — defined at line " + h.defLine + " · " + h.refs + " jump(s)" : " — ⚠ undefined label");
      else if (h.kind === "var") md = "`" + h.name + (h.lastVal ? " " + h.lastVal : "") + "` — variable · set " + h.sets + "× · read " + h.uses + "×";
      else return null;
      return new vscode.Hover(new vscode.MarkdownString(md));
    },
  }));

  ctx.subscriptions.push(vscode.languages.registerDefinitionProvider(sel, {
    provideDefinition(doc, pos) {
      const d = lang.definitionAt(doc.getText(), pos.line + 1, pos.character);
      if (!d) return null;
      return new vscode.Location(doc.uri, new vscode.Position(d.line - 1, d.col ?? 0));
    },
  }));

  ctx.subscriptions.push(vscode.languages.registerDocumentSymbolProvider(sel, {
    provideDocumentSymbols(doc) {
      return (lang.documentSymbols(doc.getText()) || []).map((s) => new vscode.DocumentSymbol(
        s.name, s.detail || "",
        s.kind === "scene" ? vscode.SymbolKind.File : vscode.SymbolKind.Key,
        new vscode.Range(s.line - 1, 0, s.line - 1, 999),
        new vscode.Range(s.line - 1, 0, s.line - 1, 999),
      ));
    },
  }));

  ctx.subscriptions.push(vscode.languages.registerInlineCompletionItemProvider(sel, {
    provideInlineCompletionItems(doc, pos) {
      const lineText = doc.lineAt(pos.line).text;
      if (pos.character !== lineText.length) return { items: [] };
      const g = lang.predictGhost(lineText, ctxFor(doc));
      if (!g) return { items: [] };
      return { items: [{ insertText: g, range: new vscode.Range(pos.line, pos.character, pos.line, pos.character) }] };
    },
  }));
}

function deactivate() {}

module.exports = { activate, deactivate };
