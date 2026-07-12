# Examples

The minimal sources the docs and CI point at — nothing else lives here:

| File | Used by |
|---|---|
| `hello.lvns` | the README quickstart; the CI compile-gate |
| `hello.ink` | the Ink front-end smoke (README + CI) |
| `ext-grammar.json` | `docs/embedding.md` — a host-op declaration example |

Compiled `.lvn` files are machine artifacts — build them with
`lvnconv convert`, don't commit them. Full, playable examples of every genre
live in [`howto/`](../howto/).
