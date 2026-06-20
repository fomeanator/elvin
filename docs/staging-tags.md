# Staging-tag vocabulary

Front-ends let writers stage a slide with **tags** instead of hand-writing
`.lvn` commands. In Ink they ride on `# tag: args`; in articy:draft they live
in a fragment's *StageDirections* — the **same syntax**, so a writer moving
between tools keeps one vocabulary. Each tag compiles to one or more `.lvn`
commands.

**Hard rule:** a tag not in this vocabulary is a build error with the offending
slide named — never a silent skip. Register a tag once and it is free for every
slide thereafter.

## Tag reference

Syntax: `# name: arg1 arg2 key=value …`. Bare leading args are positional;
`key=value` pairs set named fields. Values coerce to bool/number/null/string.

| Tag | Args | Compiles to |
|---|---|---|
| `# bg:` | `id? url? key=value…` | `bg` command |
| `# actor:` | `id key=value…` (e.g. `mara position=left emotion=smile`) | `actor` command |
| `# fade:` | `to? duration?` (default `black 0.5`) | `fade` command |
| `# dim:` | `alpha? duration?` (default `0.4 0.5`) | `dim` command |
| `# camera:` | `action amplitude=… factor=… duration=…` | `camera` command |
| `# particles:` | `type [off]` | `particles` command |
| `# audio:` | `channel action url? key=value…` | `audio` command |
| `# wait:` | `ms` (default 500) | `wait` command |
| `# preload:` | `url1 url2 …` (kind inferred from extension) | `preload` command |
| `# hint:` | `text` or `off` | `hint` command |
| `# set:` | `key value` | `set` command |
| `# inc:` | `key by` | `inc` command |
| `# style:` | `style-name` | sets `style` on the slide's `say` |
| `# say:` | `key=value…` | extra fields merged into the slide's `say` |
| `# todo:` | (free text) | ignored — an author note |

## Mapping notes per front-end

**Ink.** Knots/stitches become labels; diverts become `goto`; `-> END` becomes
`goto __end`. `*` choices are once-only (gated with a `__once_*` flag), `+`
choices repeat. Visit counts (`{knot}` in a condition) lower to `__seen_<label>`
with an auto-`inc` after the label. Tunnels `-> x ->` / `->->` become
`call` / `return`. Text alternatives (`{a|b}`, `{&cycle}`, `{!once}`) pass
through to the runtime's alternatives module.

**articy:draft.** A DialogueFragment becomes a `say` (Speaker → nameplate). A
Hub or a multi-connection output pin fans out into a `choice` (option captions
from MenuText). Jumps become `goto`, Conditions become `if`-expr, Instructions
(`x += 1;`) become `set`/`inc`, GlobalVariables become dotted `ns.var` keys.
Inbound-edge counting includes Jump targets so no `goto` dangles.

## Frame types and effect grammar

A host game may layer **frame types** (slide composition templates) and a fuller
effect grammar — e.g. `frame_dialog` / `frame_wide` / `frame_detail` /
`frame_cutscene`, transitions `flash` / `tint_cold` / `tint_warm` / `blur`, and
`text_pace`. These are game-level conventions registered in the same way: add
the tag to the vocabulary, implement the module once, and it is available to
every slide. The engine ships the core set above; extend per project.
