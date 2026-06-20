# Changelog

All notable changes to the LVN Engine package are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/); versions are SemVer.

## [0.1.0] — 2026-06-20

### Added
- `LvnDocument` — parse the `.lvn` container (Newtonsoft-backed command list).
- `LvnPlayer` — the interpreter: cursor, variable bag, and flow control for
  `goto` / `if` / `choice` / `call`-`return`, with autosave snapshot/restore.
- `ILvnStage` — the host contract (say, choice, stage commands, end).
- `LvnOption`, `StagingOps` — choice presentation and the op registry.
- Pluggable `ExprEvaluator` hook for string `expr` conditions.
- **Hello LVN** sample: a console host that plays a bundled `.lvn`.

### Known gaps (planned)
- Built-in Pratt expression evaluator (currently a host-supplied hook).
- Effect modules (camera, particles, tint) and the layered-sprite compositor.
- Premium meta-shell template (hub / life-card / paywall).
