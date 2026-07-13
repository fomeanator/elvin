# UITK layer modernization — decisions

> Status: **P0 implemented** — LvnPanel shared settings + orientation match,
> SafeAreaElement on VnStage chrome/menu, GameHud safe-area inset (bar bleeds,
> content drops below the notch), LvnFonts SDF pipeline with per-asset OS
> fallback chain (Latin/Cyrillic → CJK via fallbackFontAssetTable) and
> cascaded chapter glyph pre-warm. Custom fonts ship with the CONTENT:
> manifest `ui.dialogue.font` accepts either a Resources name (baked font)
> or a `/content/fonts/*.ttf` url — downloaded into the versioned disk cache
> (offline-safe), loaded via LvnFonts.FromFile, applied with a live chrome
> rebuild and warmed with the current chapter corpus.
> Remaining from P0 scope: shell screens
> adopt SafeAreaElement gradually; emoji come later as a sprite asset (colour
> emoji don't survive SDF).

Based on a five-track research sweep (architecture/theming, layout/adaptive,
text/fonts, input/events, performance/animation) over Unity 6-era (2024-2026)
official guidance and production practice. Sources are listed per section at
the bottom-linked reports; this file records the DECISIONS for the engine.

## Verdicts on what we already do

| Current practice | Verdict |
|---|---|
| Pure C#-built VisualElement trees (no UXML) | **Keep.** Fully supported; UXML buys artist workflow we don't have. |
| JSON → inline `style.*` themer (manifest.ui) | **Keep.** USS `--variables` have no public runtime setter; inline styles are THE sanctioned path for open-ended server values. |
| `RefreshLabels` dirty-check (`el.text != t`) | Keep — matches best practice. |
| `display:none` for hidden chrome | Keep (never `opacity:0` for idle panels). |
| Legacy `Font` + `Resources.Load` + `style.unityFont` | **Kill.** Legacy non-SDF path. |
| Default PanelSettings scale mode + px font sizes | **Kill.** No device independence. |
| `experimental.animation` (ValueAnimation) for veils/fades | **Retire.** Officially still experimental; pooled lifecycle causes the races we hit. The Stop()/KeepAlive fix is a valid patch, not the destination. |
| Hand-rolled long-press/drift math on the stage root | **Refactor** into reusable PointerManipulators. |

## P0 — foundation (removes most of the "default колхоз")

1. **PanelSettings**: `ScaleWithScreenSize`, referenceResolution **1080×1920**,
   `MatchWidthOrHeight` with **Match = 0** (width-stable portrait). All font
   sizes/paddings are then authored in reference px and scale for free.
   One **shared PanelSettings** for every UIDocument (stage + shell) —
   separate instances break focus/navigation and waste atlases; layering is
   `sortingOrder` only.
2. **SafeArea root**: a container reading `Screen.safeArea`, converting via
   `RuntimePanelUtils.ScreenToPanel` with Y inversion, applied as padding,
   recomputed on `GeometryChangedEvent`. Chrome (dialogue/choices/HUD/menus)
   lives inside; the stage/veil layer stays full-bleed outside it.
3. **Text pipeline**: TextCore `FontAsset` + `PanelTextSettings` assigned to
   `PanelSettings.textSettings`; global fallback chain
   `primary (Latin+Cyrillic) → emoji sprite/OS → CJK (later)`.
   Runtime/server fonts via `FontAsset.CreateFontAsset` from bytes — no
   Resources. **Glyph pre-warm at chapter load** from the localization
   catalog (`TryAddCharacters`) so the typewriter never rasterizes mid-reveal.
   SDF everywhere; atlas 2048 max on mobile, padding sized for outline/shadow.

## P1 — animation & rendering

4. **FxLayer → opacity fast-path**: replace color-lerp veils with two
   full-screen elements (black veil, white veil) animated by **opacity**
   (GPU fast-path: opacity/translate/scale only), via USS transitions or a
   single owned tween; `usageHints = DynamicTransform` on animated elements.
   Never animate layout properties or churn USS classes on hot paths.
5. **Typewriter → `TextElement.PostProcessTextVertices`** (we're on 6000.4):
   full string set once, per-glyph alpha ramp before render — no re-layout,
   no tag hacks; unlocks wave/shake per-glyph effects later.
6. **Dynamic atlas discipline**: max 2048; size filter so full-screen bgs stay
   OUT of the atlas; call `RuntimePanelUtils.ResetDynamicAtlas()` at chapter
   boundaries (next to the existing `UnloadWhere`) to defragment after
   remote-sprite churn.

## P2 — input

7. **Manipulators**: `TapManipulator` / `LongPressManipulator` /
   `DragManipulator` (pointerId-aware, `CapturePointer` on down, px drift
   threshold to split tap vs drag) replacing the manual math in VnStage.
   Advance-on-tap stays a **bubble-up** handler on the root; interactive
   children `StopPropagation`; decorative layers `PickingMode.Ignore`.
8. **uGUI coexistence**: single `EventSystem` + `InputSystemUIInputModule`;
   stacking governed exclusively by PanelSettings sortingOrder vs canvas
   sortingOrder; stage hotspots gated by `IsPointerOverGameObject()`.
9. **Callback lifecycle**: QuizU-style `EventRegistry` (IDisposable) so
   chapter/screen teardown can't leak handlers; cancel tweens on
   `DetachFromPanelEvent`.

## P3 — nice-to-have

10. Form-factor USS class (`phone / tablet / landscape`) toggled in C# off
    resolved root width — the media-query substitute; chrome moves to
    flex-grow/percent with px only as min/max clamps.
11. Gamepad/keyboard: seed `element.Focus()` after screen swaps + root
    `NavigationMove/Submit/Cancel` (bubble-up) handlers.
12. Split fast-churn HUD into its own UIDocument (shared PanelSettings) so
    its refresh doesn't dirty static chrome.
13. Unity 6 runtime DataBinding: NOT adopting wholesale (boilerplate, silent
    failures); candidate only for wallet/HUD reactive surfaces later.
14. Watch ATG (Advanced Text Generator): default at 6.5, HarfBuzz shaping for
    CJK, static FontAssets deprecated under it — plan migration when we bump.

## Device test matrix (regression set)

iPhone SE3 (750×1334, 16:9, overflow check) · iPhone 15/16 (Dynamic Island
safe-area) · 16 Pro Max · 1080×2340 Android (reference) · 20:9 flagship
(punch-hole) · iPad Pro 11" 4:3 (reflow, not letterbox).
