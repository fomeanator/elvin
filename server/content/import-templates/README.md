# Import templates

A **template** tells the articy→novel bundle importer the authoring conventions of
a project: which dialogue roles narrate (clear the stage), who the player character
is and which side they stand on, how a scene-marker line reads, the wardrobe
variable layout, audio-cue variable prefixes, and premium pricing. It replaces the
values the importer used to hardcode for the first novel («Холодный расчёт»).

## Selecting a template

Pass a `template` field to `POST /v1/admin/import-bundle`:

- multipart upload — add a text part `template=<name>`;
- local JSON mode — add `"template": "<name>"` to the body.

Resolution:

| `template` value        | resolves to                                             |
|-------------------------|---------------------------------------------------------|
| *(empty)* / `default` / `cold` | the built-in **cold** template (unchanged behaviour) |
| `<name>`                | `import-templates/<name>.json` in this directory        |
| `path/to/file.json`     | that file directly                                      |

An unknown name is a `400` — a typo never silently falls back.

## Adding a template

Drop a `<name>.json` here. **Overlay-by-presence**: a template starts from the
built-in `cold` defaults and overrides *only* the fields it states — so a template
for an English project that just renames roles need not repeat the audio or emotion
sections. See `example-en.json`.

`cold.json` is the full, explicit reference: the built-in defaults written out. Copy
it as a starting point when a project differs in many fields.

## Field reference

See `tools/lvnconv/importer/template.go` — the `Template` struct documents every
field inline. In short:

- **staging** — `narrator_roles`, `protagonist_roles`, `protagonist_side`/`npc_side`,
  `scene_marker_regex` (group 1 = location), `bg_ext`, `protagonist_label`,
  `protagonist_speaker_labels`, `player_var`/`player_template`, `protagonist_mirror`.
- **wardrobe** — `flag_key` (opens the in-script wardrobe block), `var_prefix`,
  `protagonist_hair_var`/`protagonist_clothes_var`, slot labels/infixes, `premium`.
- **audio** — a list of `{var_prefix, channel, path_prefix, ext}` cue rules.
- **emotion_colors** — `#rrggbb`→token legend, merged over the built-in one.
- **backgrounds** — `min_file_size` (bytes) below which a фон file is treated as a
  placeholder, not real art.
