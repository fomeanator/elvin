# Every command, once — the witness example

This folder is **not a genre guide**. It is the witness that keeps
[`CAPABILITIES.md`](../CAPABILITIES.md) honest.

The rule the repository enforces: **nothing lives in the documentation that no
example compiles.** A documented construct that nothing exercises is how a
feature can stay documented while being broken (or absent) for months — the
docs are the API an AI agent writes against, so a phantom feature in them is a
direct cause of wrong code.

The twelve genre examples already cover the everyday commands. This one covers
the leftovers, so that every construct the docs claim works has at least one
place where it is compiled by CI:

| Construct | Where it appears here |
|---|---|
| `text_pace` | typing speed for the whole scene |
| `preload` | warming the background and a sound effect |
| `save` / `load` | the "watch the reel again" rewind |
| `wait` | short pauses between beats |
| `tint` / `blur` | the warm veil and the beam coming up |
| `anim` props `rotation` `scale` `alpha` `screen_x` `scaley` | the projector's fidgeting |
| easing `linear` `inOutSine` `outCubic` `outBack` `inBack` | one on each tween |
| `loop=yoyo` / `loop=restart` / `loop=once` | sway, rattle, snap |
| `defanim` + `play` | the `rattle` animation, defined once and stamped on |
| `anim … stop` | clearing the script lanes |
| `mode=queue` | two steps in order on one channel |
| `move … interp=spline orient=true` | a path through waypoints, nose along the tangent |
| `interp=step` | values snapping key to key |
| option `requires_stat` + `min` | the lamp gate (the option is **hidden**, not greyed) |

## The gate

`tools/lvnconv/lvn/docs_contract_test.go` reads `howto/CAPABILITIES.md` and
fails if:

1. an op documented in §1 is not in the validator's `KnownOps` (a phantom op),
   or an op in `KnownOps` is not documented (an undocumented one);
2. the file both claims and denies the same construct (`✅` here, `❌` there);
3. a construct the file claims works has **no witness** — no gated example
   compiles it.

CI already compiles and validates every `howto/*/*.lvns` down to zero warnings,
so a witness is a real compile, not a promise.

```sh
cd tools/lvnconv && go build -o /tmp/lvnconv .
/tmp/lvnconv convert -i ../../howto/every-command/every-command.lvns -o /tmp/ec.lvn
/tmp/lvnconv validate /tmp/ec.lvn        # OK ... 0 warning(s)
```

A witness proves the construct **compiles**. Proving each one *behaves* the same
in every runtime is a different net — the op-ownership table in
`conformance/`.
