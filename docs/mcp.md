# lvn-mcp — the LVN toolchain as an MCP server

Connect the LVN compiler and docs to any MCP-capable agent (Claude Code,
Claude Desktop, or anything speaking the protocol) and it can build a
narrative game end-to-end: write `.lvns`, check it against the same quality
gate every shipped example passes, and read the authoring docs on demand.

## Install

```sh
cd tools/lvnconv && go build -o ~/bin/lvn-mcp ./cmd/lvn-mcp
```

Register with Claude Code:

```sh
claude mcp add lvn -- ~/bin/lvn-mcp
```

(or run from source: `claude mcp add lvn -- go run <repo>/tools/lvnconv/cmd/lvn-mcp`)

Set `LVN_REPO=<repo root>` in the environment if the agent's working
directory is outside the repository — `lvn_doc` needs it to find the docs.

## Tools

| Tool | Input | What it does |
|---|---|---|
| `lvns_check` | `source` (.lvns text), optional `ext_grammar` (ext-grammar.json content) | Compile + structural validation. Returns `{ok, commands, errors[], warnings[]}`. The bar: `ok: true`, zero warnings. With `ext_grammar`, declared host ops (`ext …`) validate like built-ins instead of warning as unknown. |
| `lvns_convert` | `source` | Compile to the runnable `.lvn` JSON container. |
| `lvn_doc` | `name` | Read an authoring doc: `tutorial`, `cheatsheet`, `capabilities`, `language`, `recipes`, `agents`, `format`, `embedding`. |

## The agent workflow

1. `lvn_doc agents` (or `cheatsheet`) — load the mental model once.
2. Write the script; `lvns_check` after every meaningful edit — the
   validator's messages say *how to fix*, not just what broke.
3. `lvns_convert` when green → hand the `.lvn` to the content server /
   runtime.

Transport: newline-delimited JSON-RPC 2.0 over stdio (MCP standard). The
server is stateless and pure — safe to run many in parallel.
