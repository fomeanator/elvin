# 🚚 From articy files to a published novel — the route

The other guides in `howto/` teach you to *write* a novel. This one is for the
other job: you were handed a folder of files by a writing team and you have to
turn it into a playable, updatable novel — **and keep updating it without
throwing away the fixes you made along the way.**

It is a route, not a reference. Do the steps in order; each one ends with
something you can look at to know whether it worked.

The example next door, [`after-import.lvns`](after-import.lvns), is one beat of
a real import with every line explained — read it once before step 3 so the
output is not a surprise. It is regenerated from the importer by a test, so it
cannot drift.

---

## 0. What you were handed

| File | Required | What the importer takes from it |
|---|---|---|
| articy project (`.rar`/`.zip`) | **yes** | the story itself: dialogue, choices, jumps, variable instructions |
| backgrounds `.zip` | no | real backgrounds, matched to scene markers by location name |
| characters `.zip` | no | per-character emotion art → a layered cast entity |
| heroine `.zip` | no | the protagonist's hair/outfit art (usually a copy of her character folder) |
| variables `.xlsx` | no | variable defaults, the roster, locations, **the wardrobe catalog**, and the cell-colour → emotion legend |

Without the four optional packs you still get a **playable story** — grey
placeholder art, no music. That is a legitimate first import: get the story in,
then add art.

> The spreadsheet is the Rosetta Stone. Roles, locations, outfits and the
> emotion legend all come from it, so an import without it loses more than art.

---

## 1. Look before you import — `detect`

An import of a full novel takes minutes and writes hundreds of files. Find out
what the importer *thinks* your project is **first**:

```sh
lvnconv detect <extracted-articy-project-dir>            # built-in "cold" template
lvnconv detect <dir> -template <name> -template-dir <d>  # or your own
```

It writes nothing. It prints a JSON report:

- **`speakers[]`** — every speaking label, how many lines it has, whether art was
  found for it, and the **role** the template assigned (narrator / protagonist /
  npc). This is the single highest-value screen in the whole pipeline.
- **`scene_marker_hit_rate`** + `scene_marker_misses` — how many narrator lines
  the location regex recognised. A low rate means your backgrounds will not
  change.
- **`emotion_lines_mapped` / `emotion_color_misses`** — how much of the colour
  legend matched.
- **`protagonist_without_art`**, **`alias_collisions`**, **`warnings`**.

**Checkpoint.** Before going further:

- the protagonist's label has role `protagonist` (not `npc`);
- narrator labels («Автор», «Рассказчик»…) have role `narrator` — a narrator
  classified as an NPC gets *staged as a character* and stands in every scene;
- the same person written two ways («ГГ» and «Главный герой») shows up in
  `alias_collisions`;
- the scene-marker hit rate is high enough that locations actually change.

In the panel the same report is the **«Настроить роли»** button in the import
dialog: it stages and extracts the archive, then shows the speakers with a
role dropdown per line — and saves the result as a template for you.

## 2. Teach the importer your conventions — a template

Everything project-specific is a **template**, never code: which roles narrate,
who the player character is and which side they stand on, how a scene-marker
line reads, **the wardrobe variable layout**, audio-cue prefixes, premium
pricing, the emotion legend.

Templates are overlay-by-presence — start from the defaults and override only
what differs. They live in `<content>/import-templates/<name>.json`; the field
reference is that folder's `README.md`, and `cold.json` is the built-in default
written out in full.

**Checkpoint.** Re-run `detect -template <name>` and watch the roles change.
This loop costs seconds; the import loop costs minutes.

## 3. Import

**Panel** (the normal route): *Library → import novel*, drop in the five files,
name it, pick the template, **Импортировать**. Uploads start the moment you pick
a file.

**CLI**, story-only (no art packs, no spreadsheet):

```sh
lvnconv import <extracted-project-dir> -content ./server/content -id my-novel -name "My Novel"
```

> ⚠ The five-file **bundle** import exists only behind the server
> (`POST /v1/admin/import-bundle`); there is no `lvnconv bundle`. A CLI-only
> author is limited to the story-only import above.

## 4. Read the output

An import is not "done" or "failed" — it is a report, and every number in it is
a place your novel can be quietly wrong. From the CLI you get all of it on
stderr; from the API, in the JSON response.

| Signal | Means | Do |
|---|---|---|
| `files: N new, N updated, N unchanged, N kept (hand-edited)` | what the write did to each file | see step 6 |
| **`CONFLICTS (n)`** | a file you edited **and** the export changed. Nothing was overwritten; the new version is parked at `<file>.incoming` | open both, decide, write the winner into the real file (see step 6) |
| `linearizer: … trapped … connectivity … jumps resolved` | how much of the articy flow was read as real connections vs. stitched by guesswork | low connectivity = a fragmented story graph; fix the flow in articy |
| `warning: …` from the linearizer | story the import dragged in by guess, or lost | read every one |
| `bg unmatched` | scene markers with no background file | rename the art or fix the location sheet |
| `lvn_check` (API) | the structural gate: dangling jumps, duplicate labels | errors here mean the chapter cannot play |

**Checkpoint.** Open the novel and play the first chapter. Then check the three
things imports get wrong most often:

1. **Does the background change** when the scene does? (scene markers)
2. **Does the protagonist speak under the player's name** and stand facing into
   the scene? (roles + `mirror`)
3. **Does the dressing scene open the real wardrobe sheet**, not a list of text
   choices? (the `Open.Wardrobe` block — see
   [`../wardrobe/README.md`](../wardrobe/README.md))

## 5. Fix by hand, inside the engine

This is the step the whole pipeline exists for. Open the chapter in the panel's
script editor: you edit the **`.lvns`** source, and **Save to app** writes both
the `.lvns` and the compiled `.lvn`.

> Editing a `.lvns` any other way (on disk, in git) changes **nothing** on its
> own — the runtime plays the `.lvn`. Run `lvnconv convert -i x.lvns -o x.lvn`
> yourself, or the two files silently disagree.

Things that are *meant* to be hand-fixed here, because articy cannot express
them:

- opening the wardrobe for an **NPC** (`wardrobe_show char="Felix"`) — the
  importer only ever opens it for the protagonist;
- a costume the story forces at a particular beat (`actor Мира outfit="3"`);
- staging polish: who stands where, an `anim`, a `fade` on a hard cut;
- a line of dialogue that reads badly in the target language.

## 6. Re-import without losing your work

A re-import is a **three-way merge**, not an overwrite. The importer remembers a
hash of everything it wrote last time and compares three states:

| previous import | file on disk | new export | result |
|---|---|---|---|
| = | = | changed | **updated** |
| = | changed | = | **kept** — your edit stays |
| = | changed | changed | **CONFLICT** — nothing overwritten, new version at `<file>.incoming` |

So the loop is: writers change articy → re-import → read the conflicts → merge
the few files that genuinely collided. Your fixes from step 5 survive.

**Closing a conflict** means writing the version you chose into the real file —
either your merge, or the parked `.incoming` bytes — and *then* letting the tool
record it. Deleting the `.incoming` file by hand is not enough: the baseline
still holds the pre-conflict hash, so the very next import raises the same
conflict again.

**Two things the merge does NOT cover — know them before you rely on it:**

- **The cast is replaced, not merged.** `manifest.json` entities are spliced in
  **whole, by id**. A wardrobe catalog, layer tweak or slot rename you made in
  the panel's cast editor is gone after the next import of the same title. Fix
  the source (the `.xlsx`), not the manifest — or keep hand-authored characters
  under ids the import never emits.
- **Re-importing means the same title id.** The merge is keyed to it, and chapter
  files are named `scripts/<title-id>-chNN.lvn`. Import the same novel under a
  new id and you get a second novel, not an update — no merge, duplicated art,
  two entries in the library.

## 7. Publish

Content under `content/` is served to players immediately (no caching), so
"publish" is mostly about the manifest and about knowing what changed:

- **Save to app** on a chapter → both files written, live in about two seconds.
  Every write passes the structural gate first: a chapter with a dangling jump
  is refused with a 422 rather than shipped.
- The panel keeps an **editorial history** of every text write (manifest,
  scripts, templates) and can roll one back.
- Manifest edits go to a draft and land with **publish**.
- A packaged build (APK) is a separate export step — see `docs/releasing.md`.

---

## Where everything lives after an import

```
content/
  manifest.json              titles + chapter table of contents + THE CAST (sprites)
  scripts/<id>-ch01.lvn      what the runtime plays      ← generated
  scripts/<id>-ch01.lvns     what you edit               ← generated, then yours
  scripts/<id>-ch01.lvns.incoming   a parked conflict    ← delete once merged
  bg/…  art/…  audio/…       copied art
  import-templates/<n>.json  your project's conventions
  .lvn-import/               the merge baseline — bookkeeping, never edit
```

## Potholes, in the order people fall into them

1. **Importing before running `detect`.** Every role mistake multiplies by the
   number of chapters.
2. **A partial template that writes `"narrator_roles": []`.** Overlay-by-presence
   means an empty list *replaces* the nine defaults — and your narrator becomes a
   character standing in every scene.
3. **Re-importing under a new name.** You get a duplicate novel and no merge.
4. **Hand-editing the cast in the panel** and expecting it to survive. It will
   not; the spreadsheet wins.
5. **Editing `.lvns` outside the panel and not compiling.** The game keeps
   playing the old `.lvn`.
6. **Ignoring the linearizer's connectivity number.** A story that reads fine in
   chapter 1 can be a third unreachable in chapter 24 — that has happened, in
   production, to a real novel.
7. **Assuming an unterminated `Open.Wardrobe` block is fine.** The importer
   leaves the whole block alone, and the raw text picker ships.
