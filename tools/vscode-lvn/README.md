# Elvin Script for VS Code

Language support for **`.lvns`** — the plain-text narrative-game language of
the [Elvin engine](https://github.com/fomeanator/elvin). This is
the same language core the web Studio uses, in your editor — which is where
AI-assisted authoring actually happens.

- **Real diagnostics** — every keystroke runs the actual `lvnconv` compiler
  (WASM): dangling jumps, unknown ops, duplicate labels, half-configured
  timed choices. Zero warnings here = the CI gate passes.
- **Completion** — ops with signatures and docs, attributes and enum values,
  speakers from `actor_map` (with the `Name [emotion]` variant), labels after
  `->`/`goto`, snippets for whole constructs.
- **Hover** — op signatures, label definition/jump counts, variable
  set/read counts, emotion validity.
- **Go to definition** on label references; **outline** of scenes and labels.
- **Ghost suggestions** — rule-based inline completions for the next token.
- **`ext-grammar.json` aware** — your project's host ops (beside the script
  or one level up) complete and validate like built-ins.

## Install

Until it's on the Marketplace, package and install locally:

```sh
cd tools/vscode-lvn
npm run package                       # builds lib/ + emits elvin-lvns-<ver>.vsix
code --install-extension elvin-lvns-*.vsix
```

`npm run build` assembles `lib/` from the shared sources: `../lvn-lang/src`
(the language core) and a fresh `lvns.wasm` (falls back to the playground's
prebuilt if Go isn't installed). The extension itself contains no language
knowledge — it's a thin adapter, so the language only ever evolves in one
place.
