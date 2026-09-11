# LVN visual standards

Reference canvas: **1080×1920 portrait** (all % of screen H/W). Sources:
(a) engine-defaults survey — Ren'Py (authoritative gui.rpy numbers),
Naninovel, TyranoBuilder, Dialogic 2, Fungus; (b) screenshot-measured survey
of portrait story apps — **Romance Club** (key reference), Choices, Episode,
Chapters, My Story. Full research in session notes; medians below.

## Where the market sits

| Element | Engines (16:9 median) | Mobile portrait (median) | LVN today | **LVN standard** |
|---|---|---|---|---|
| Dialogue box anchoring | flush bottom | flush 0–5% margin; premium (RC) floats with 18–26% gap | flush, 0 margin | **4% bottom margin** (theme can float RC-style via `bottom_lift`) |
| Dialogue box width | full-bleed | **90–95% W** (side insets) | 100% stretch | **92% W** (4% side insets) |
| Dialogue box height | 28–33% H | 18–22% H, auto-grow to ~30% | auto, min 128px (6.7%) | **auto; min ~10% H, cap 30% H** |
| Box corner radius | — | ~2–3% W (24–32px) | 12px | **28px** |
| Box opacity | opaque themes | translucent 75–90% | opaque-ish dark | **~85% alpha** (keep dark default) |
| Name plate | top-left of box (~4% H text) | small tab fused to the box top edge; premium centers | plate above box, left | keep tab-left; **centered = premium theme option** |
| Body text size | **3% H** (33px@1080p land.) | **~4% H line (~44px)**, 4–6 lines max | 42px (2.2% H) | **46px**, speaker 34px |
| Sprite height | full-body ≈ 90–100% H | **90–100% H**, head ~4–6% from top | 0.93 H default | **0.93–1.0 H; feet at the VIEWPORT bottom** |
| Sprite anchor | feet at screen bottom | feet at/below bottom, **char BEHIND the box** | feet at content-rect bottom (≠ viewport on tall screens — bug) | **feet at viewport bottom, always behind the box** |
| Sprite x-positions | 25 / 50 / 75% W | 1 char centered; 2 chars at ⅓/⅔ | 12/25/38/50/62/75/88 ✓ | keep ✓ |
| Choice buttons | centered, 55–62% W, 3% H gaps | **stacked lower-third/center, 80–85% W, 6–8% H each, ~2% gaps** | center, 58–86% W fluid, 10px gap | **82% W fixed, min-height 6.5% H, 2% H gap, stack center at ~58% H** |
| Quick/system UI | bottom row or corner | **single corner button, <6% H, HIDDEN during dialogue** | FABs top-right @8.5% ✓ corner | keep corner; **auto-hide with chrome-hide, consider hide-while-reading** |
| Top HUD (progress/currency) | — | **not shown over dialogue** — menus/chapter screens only | 7% H bar always visible | keep bar, but **product guidance: hide during reading** (backlog) |
| Toast / hint | top ~6% H | **center / upper-mid pill** | top 5% (under HUD → clash) | **top-center at 12% H** (clear of HUD), pill, maxW 72% ✓ |
| Chapter title | none shipped | **full-bleed art + centered title** | loader+title over live scene ✓ (Liminal flow) | keep flow; title block at **36–40% H** |
| Loading bar | — | on full-art splash | y=82%, w=70% ✓ | keep ✓ |

## The LVN standard (normative defaults)

1. **Dialogue**: 92% W, 4% bottom margin, radius 28, panel alpha ~0.85,
   min-height 10% H, max 30% H; body 46px, speaker 34px, padding 28/22.
2. **Characters**: full-body 0.93–1.0 H, feet on the **viewport** bottom
   edge, always rendered behind the dialogue box. One speaker → center;
   two → left/right (25/75). `ui.stage.actor_scale` multiplies.
3. **Choices**: fixed 82% W, min-height 125px (6.5% H), 38px gaps, stacked
   with the stack center at ~58% H (lower-middle).
4. **Hint/toast**: top-center pill at 12% H, max 72% W.
5. **HUD**: corner-minimal philosophy; the reading surface belongs to the
   story. (Auto-hide during dialogue — product backlog.)
6. **Title card**: over the live scene (see entry choreography), text block
   36–40% H, title ~64px / subtitle ~34px.
7. Two shipped looks: **flush** (default, mass-casual) and **floating**
   (Romance-Club-like: `bottom_lift ~18%`, ornate frame) — both via theme,
   no code.

## Height scale: metres, not eyeballing (TR-45)

A character's size is **their height in metres**, not a share of the screen.
The scene declares how many metres fit in the frame (`ui.stage.meters`,
default 2 — a room's ceiling); the character declares their own height
(`sprites.<id>.meters`); the on-screen height is the division. Whoever puts
the doll up — script, menu, wardrobe, cutscene — gets the same height,
because it comes from the same two numbers. Height is measured against the
**figure**, not the canvas (`sprites.<id>.content`), so 1.70 and 1.90 stand
side by side with a true 20 cm apart, whatever the art was drawn on.

Reference scale (author-facing, all in metres):

| Who | Height | Share of a 2.0 m frame |
|---|---|---|
| Adult woman | 1.62–1.75 | 0.81–0.875 |
| Adult man | 1.78–1.92 | 0.89–0.96 |
| Teenager | 1.50–1.62 | 0.75–0.81 |
| Child (7–10) | 1.20–1.35 | 0.60–0.68 |
| Seated figure | ~0.72 of their standing height | — |

Rules that follow from it:

- **Never** scale a person with `w=`/`h=` in a line of script. Those answer
  "how much room to take", not "how tall are you", and they drift apart the
  moment the same person appears in another room.
- **Bigger** means a taller character or a closer camera (`ui.stage.meters`),
  never a zoom stacked on top of them: the showcase zoom used to multiply the
  frame share, and 1.90 m favourites walked off the top of the screen.
- Characters with **no height named** take `ui.stage.default_meters` (1.7 for
  an adult cast). Leave it unset and they keep the legacy screen shares —
  silence must not hand a height to props, signs and backdrops, which the same
  command also places.
- A **figure that is not standing** — lying, fallen, a body on the ground —
  is not measured by height at all: its extent runs along the floor, and a
  standing height would stretch it up the frame. Leave `meters` unset for
  those. On Time Romance 21 of the 24 entities without a height are exactly
  this case, which is why the default must never be applied silently.
- The showcase menu is a **shelf, not a room**: its doll is measured by the
  showcase frame (`ui.browse.doll_height`) unless the menu names metres itself.

## Known deviations to fix in engine defaults

- [x] `VnTheme.BottomLiftPercent` 0 → 4 (%, dialogue off the bottom edge)
- [x] Dialogue side insets: stretch → 92% W default (`BoxAlign center` + width)
- [x] `PanelCornerRadius` 12 → 28; panel alpha 0.86; BoxMaxHeight 30%
- [x] `BodyFontSize` 42 → 46; `SpeakerFontSize` 30 → 34
- [x] Choice: fixed width 82%, min-height 125px (`ChoiceMinHeight` + manifest
      `ui.choices.min_height`), `ChoiceSpacing` 10 → 38 (stack stays centered)
- [x] Hint top 5% → 12%; TitleCard block 34% → 38%
- [x] (already fixed in engine) actor feet anchor to the canvas *content rect*, which does not
      equal the viewport on non-reference aspect ratios → feet float above
      the screen bottom ("герои начинаются не снизу"). Anchor the content
      rect bottom to the viewport bottom.
- [x] Verify actor perceived size: audited alpha bboxes per entity (2026-07-13).
      Main cast is tight vertically — codel 97.6% content height (0 bottom
      margin), hill 96.1% (0 bottom), doll 96% — so 0.93 H defaults read as
      designed and feet meet the viewport bottom. hill's catalog `aspect`
      0.78125 equals its file aspect (no shrink). The only padded family is
      pixel frame-grids (LPC hero: content 38% of the 128px cell, margins on
      all sides) — inherent to sprite-sheet cells; authors compensate with
      per-line `h=`, and a catalog `content_rect`/auto-trim is the future fix
      if a pixel title needs standard sizing.
