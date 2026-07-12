# Your first game in 15 minutes

From an empty file to a playable scene with name input, a timed choice and
a state-driven branch. You need nothing but this repository, Go and (for
step 4) Unity. Every snippet here is compiled and verified — copy with
confidence.

---

## Step 0. Try it without installing (30 seconds)

If the server is already running somewhere, open **`/play/`**: you write
`.lvns` on the left, it plays instantly on the right (timers and input
included). The "Share" button gives you a link, "⬇ HTML" — a single file
that plays from disk. Below is the full path.

## Step 1. Start the server and open the IDE (2 minutes)

From the repository root:

```sh
scripts/fetch-demo-content.sh   # demo content lives in a separate repo
go run ./server -content ./server/content -addr :8077 -admin-token devtoken -studio
```

Open **http://localhost:8077/panel** — this is Elvin Studio. Paste the admin
token `devtoken` in the top right. You will see a library of demo novels —
open them and peek at how things are done.

Click **"＋ New novel"**, name it and open it. In the script tab click
**"+ Add the first chapter"** — the chapter editor opens.

## Step 2. Write a scene (5 minutes)

Paste this into the chapter (and hit Save to publish it to the app):

```
scene first_game

A dark room. It smells of dust and old books.
input var=name prompt="What is your name?" default="Guest" max=20
Voice: At last, {name}. I have been waiting for you.
Voice: The door slammed shut. You have five seconds to decide.

choice timeout=5 timeout_goto=frozen
- Look for the light switch -> light
- Run to the door -> door

:light
courage = 1
You found the switch. The room turned out to be a library.
-> finale

:door
courage = 0
The door would not budge. But your eyes adjusted to the dark.
-> finale

:frozen
courage = 0
You froze. Sometimes that is a choice too.
-> finale

:finale
if courage >= 1 -> brave_end
Voice: Caution is not weakness, {name}.
-> __end
:brave_end
Voice: Bold, {name}. I like that.
```

What's in here — the whole skeleton of the genre in 30 lines:

| Line | What it does |
|---|---|
| `A dark room…` | Plain text = narration. `Voice: …` = a character's line. |
| `input var=name …` | Input overlay: what the player types lives in `{name}` in every line. |
| `choice timeout=5 timeout_goto=frozen` | Timer for the next menu: a countdown bar; miss it and you go to the `frozen` branch. |
| `- Option -> label` | Menu items — lines starting with `- `. |
| `courage = 1` | A variable. Declaration = assignment. |
| `if courage >= 1 -> brave_end` | A state-driven branch. |
| `:label` / `-> label` / `-> __end` | Jump target / jump / end of chapter. |

The IDE checks the script as you type (the same validator as in the
compiler) — errors and warnings show up in the Problems panel before you
ever run anything.

## Step 3. Check with the compiler (1 minute)

The same quality gate that every example in `howto/` has passed:

```sh
cd tools/lvnconv && go build -o /tmp/lvnconv .
/tmp/lvnconv convert -i my-chapter.lvns -o /tmp/out.lvn
/tmp/lvnconv validate /tmp/out.lvn        # target: OK … 0 warning(s)
```

Dangling jumps, typos in commands, unreachable labels — all caught here,
with hints on how to fix them.

## Step 4. Play it (5 minutes, Unity required)

1. Open the **`sandbox/`** folder in Unity Hub (the engine attaches itself
   as a local package).
2. Hit **Play**. The sandbox points at `http://127.0.0.1:8077` — your novel
   is already in the carousel.
3. Leave Unity in Play and edit the script in the IDE: changes land in the
   running scene in ~2 seconds (live reload), usually right on the same
   line.

No Unity is not a dead end either: the game's structure (the whole plot,
branches, variables) is visible right after step 3 — art and launch can
wait.

## Where to go next

- Characters with emotions, backgrounds, animation → [`CHEATSHEET.md`](CHEATSHEET.md)
  (the whole language on one page) and the **Characters** tab in the IDE.
- Ready-made patterns — inventory, shop, dice, gallery, voice-over →
  [`recipes.md`](recipes.md).
- Your genre end to end (12 templates with working examples) →
  [`README.md`](README.md).
- Hand your friends an APK → the Export button in the IDE (or `docs/releasing.md`).
