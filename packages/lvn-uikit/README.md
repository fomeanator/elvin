# `@fomean/lvn-uikit`

Reusable visual primitives for Elvin Script (`.lvns`). The first theme is
**Arcplate**: smoked blue-black surfaces, warm bone typography, a restrained
brass edge, coral health, cold-blue resources, and one clipped corner with an
illuminated seam.

The package works with the engine that exists today. It uses only `include`,
`func`, reactive `text`, and `obj`; it does not require a new runtime op.

## Install

During local development:

```json
{
  "dependencies": {
    "@fomean/lvn-uikit": "file:packages/lvn-uikit"
  }
}
```

Then run:

```sh
lvnconv deps update
```

After publishing the package repository, replace the `file:` reference with a
pinned Git tag:

```json
"@fomean/lvn-uikit": "github:fomeanator/lvn-uikit@v0.1.0"
```

## Full HUD

```lvns
include "@fomean/lvn-uikit/hud.lvns"

uikit_hud_title = "THE NORTHERN PASS"
uikit_hud_subtitle = "Stormfront · 18:42"
uikit_hp = 73
uikit_hp_max = 100
uikit_hp_label = "VITALS"
uikit_resource = 46
uikit_resource_max = 80
uikit_resource_label = "AETHER"
uikit_badge_label = "DAY"
uikit_badge_value = "07"

uikit_hud_show()

// Later:
uikit_hp = uikit_hp - 12
uikit_hud_refresh()

// On a screen transition:
uikit_hud_hide()
```

Text values are reactive after `show()`. Number changes require
`uikit_hud_refresh()` because numeric `obj` fields do not yet accept
expressions; the fills use ten intentional visual steps.

## Individual modules

| Include | Public functions | State |
|---|---|---|
| `hp-bar.lvns` | `uikit_hp_show`, `uikit_hp_refresh`, `uikit_hp_hide` | `uikit_hp`, `uikit_hp_max`, `uikit_hp_label` |
| `resource-bar.lvns` | `uikit_resource_show`, `uikit_resource_refresh`, `uikit_resource_hide` | `uikit_resource`, `uikit_resource_max`, `uikit_resource_label` |
| `badge.lvns` | `uikit_badge_show`, `uikit_badge_hide` | `uikit_badge_label`, `uikit_badge_value` |
| `panel.lvns` | `uikit_panel_show/hide`, `uikit_modal_show/hide` | `uikit_panel_*`, `uikit_modal_*` |
| `hud.lvns` | `uikit_hud_show`, `uikit_hud_refresh`, `uikit_hud_hide` | all HUD state above |

All files are safe to include together: Elvin Script includes are idempotent.
All public variables, functions, labels, object ids, and text ids use the
`uikit_` prefix to avoid collisions in the current scene-wide namespace.

## Layout contract

Version `0.1` targets portrait narrative games:

- the HUD occupies the upper 15% of the viewport;
- the information panel sits in the lower third, above the dialogue layer;
- the modal is centered;
- object `z` values are `80–90`, leaving character/world sprites beneath it;
- copy should remain short because reactive `text` has no width/wrap property.

The theme contains no baked text. Games keep their own language and typeface.
For a second visual theme, retain the same filenames and transparent bounds;
the `.lvns` API does not have to change.

See [`examples/demo.lvns`](examples/demo.lvns) for a complete state/update loop.
