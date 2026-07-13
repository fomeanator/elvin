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
- [ ] Verify actor perceived size: catalog `aspect` shrink + transparent art
      margins make defaults look smaller than 0.93 H — audit per-entity.
