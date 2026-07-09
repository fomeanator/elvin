using System;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// The look-and-feel props for the reference component set: colours, font,
    /// sizes and reveal timing. One theme is shared by the dialogue box, choice
    /// list and stage, so a game restyles everything by editing these fields in
    /// the Inspector — no USS file required. This is the "constructor" knob set;
    /// for a bespoke skin, ignore the components and style your own from the
    /// same <see cref="LvnPlayer"/>.
    /// </summary>
    [Serializable]
    public class VnTheme
    {
        // Defaults derive from the "Полночь" design tokens (LvnTokens) so the whole
        // app is one coherent look out of the box; any field is still overridable.
        [Header("Dialogue")]
        public Color PanelColor = LvnTokens.PanelBg;
        public Color TextColor = LvnTokens.Text;
        public Color SpeakerColor = LvnTokens.Accent;
        public Font Font;
        [Tooltip("Optional Resources path to a Font for dialogue/choices text " +
                 "(e.g. \"Fonts/Serif\"); used when Font is unset.")]
        public string FontResourcePath = "";
        public int BodyFontSize = 42;
        public int SpeakerFontSize = 30;
        public float PanelCornerRadius = 12f;

        [Tooltip("NVL mode: a tall full-width text panel covering the scene, " +
                 "instead of the bottom ADV dialogue strip.")]
        public bool Nvl = false;
        [Tooltip("NVL top inset as a screen fraction (0..1); only used when Nvl is on.")]
        public float NvlTop = 0.12f;

        [Header("Dialogue layout")]
        [Tooltip("Horizontal placement of the bottom dialogue box: " +
                 "\"stretch\" (classic edge-to-edge bar) | \"center\" | \"left\" | " +
                 "\"right\". Non-stretch gives the box a FIXED width (see " +
                 "BoxWidthPercent/BoxMaxWidthPercent) and lets its HEIGHT grow with " +
                 "the text — the Liminal-style centred box.")]
        public string BoxAlign = "stretch";
        [Tooltip("Box width as a percent of the screen when BoxAlign isn't " +
                 "\"stretch\" and BoxWidthPercent is unset. The box is exactly this " +
                 "wide (it does NOT shrink to the text). 0 = default 80%.")]
        public float BoxMaxWidthPercent = 0f;
        [Tooltip("Fixed box width as a percent of the screen (overrides " +
                 "BoxMaxWidthPercent). The box never shrinks to the text; the height " +
                 "grows instead. 0 = use BoxMaxWidthPercent / default.")]
        public float BoxWidthPercent = 0f;
        [Tooltip("Max box height as a percent of the screen — caps the vertical " +
                 "growth (0 = unbounded, the box grows as tall as the text needs).")]
        public float BoxMaxHeightPercent = 0f;

        [Header("Dialogue as free popup")]
        [Tooltip("Free-popup horizontal position as a screen percent (0=left … " +
                 "100=right). Set X or Y (>=0) to float the box anywhere on screen " +
                 "instead of docking it to the bottom. <0 = not a free popup.")]
        public float BoxXPercent = -1f;
        [Tooltip("Free-popup vertical position as a screen percent (0=top … " +
                 "100=bottom). <0 = not a free popup.")]
        public float BoxYPercent = -1f;
        [Tooltip("Which point of the box lands on (X,Y): combine left/center/right " +
                 "with top/center/bottom, e.g. \"center\", \"bottom-center\", " +
                 "\"top-left\". Default \"center\".")]
        public string BoxAnchor = "center";
        [Tooltip("Horizontal inset of the dialogue block from the screen edges (px).")]
        public float EdgePadding = 24f;
        [Tooltip("Gap below the dialogue block to the screen bottom (px).")]
        public float BottomPadding = 28f;
        [Tooltip("Lift the whole docked dialogue box up by this percent of screen " +
                 "height (0 = flush to the bottom, 15 = a bit higher). Free-popup / NVL " +
                 "modes ignore it.")]
        public float BottomLiftPercent = 0f;
        [Tooltip("Anchor the docked box by its TOP at this percent of screen height " +
                 "so it GROWS DOWNWARD as the text lengthens (instead of upward). " +
                 "<0 (default) = bottom-anchored (grows up, uses BottomLiftPercent).")]
        public float DockTopPercent = -1f;
        [Tooltip("Body panel inner horizontal / vertical padding (px).")]
        public float PanelPaddingX = 22f;
        public float PanelPaddingY = 18f;
        [Tooltip("Minimum body-panel height so short lines don't collapse (px).")]
        public float PanelMinHeight = 128f;
        [Tooltip("Nameplate inner horizontal / vertical padding (px).")]
        public float NamePaddingX = 14f;
        public float NamePaddingY = 4f;

        [Header("Stage / actors")]
        [Tooltip("Vertical baseline for a staged character: the screen fraction where " +
                 "their bottom-anchored feet land. 1 = flush to the screen bottom; >1 " +
                 "sinks them (feet off-screen, head lower — the usual portrait framing).")]
        public float ActorBaselineY = 1f;
        [Tooltip("Multiplier on the default actor size (standard VN framing is already " +
                 "large). 1 = default; 1.2 = 20% bigger. Per-op width=/height= override.")]
        public float ActorScale = 1f;
        [Tooltip("Multiplier on a positioned actor's horizontal offset from centre. " +
                 "1 = default (left=0.25/right=0.75); 0.6 pulls left/right in by 10% of " +
                 "the screen (→0.35/0.65); 0 stacks everyone centre. Ignored when the op " +
                 "sets an explicit x=.")]
        public float ActorSpread = 1f;

        [Header("Reveal")]
        [Tooltip("Typewriter speed in characters per second.")]
        public float CharsPerSecond = 45f;
        [Tooltip("Soft per-glyph fade-in width, in trailing characters.")]
        public float FadeWidth = 5f;

        [Header("Choices")]
        public Color ChoiceColor = LvnTokens.Surface;
        public Color ChoiceHoverColor = LvnTokens.SurfaceHi;
        public Color ChoiceTextColor = LvnTokens.Text;
        public Color ChoiceCostColor = LvnTokens.Gold;
        public int ChoiceFontSize = 36;

        [Header("Choices placement")]
        [Tooltip("Horizontal placement of the choice stack: \"center\" (default) | " +
                 "\"left\" | \"right\".")]
        public string ChoiceAlign = "center";
        [Tooltip("Vertical placement of the choice stack: \"center\" (default) | " +
                 "\"top\" | \"bottom\". Ignored when ChoiceYPercent is set.")]
        public string ChoiceVAlign = "center";
        [Tooltip("Free vertical position: the top of the choice stack at this screen " +
                 "percent (0=top … 100=bottom). <0 = use ChoiceVAlign instead.")]
        public float ChoiceYPercent = -1f;

        [Header("Choices layout")]
        [Tooltip("Button width as a percent of the screen (min / max).")]
        public float ChoiceMinWidthPercent = 58f;
        public float ChoiceMaxWidthPercent = 86f;
        [Tooltip("Vertical gap between stacked choice buttons (px).")]
        public float ChoiceSpacing = 10f;
        [Tooltip("Choice button inner horizontal / vertical padding (px).")]
        public float ChoicePaddingX = 20f;
        public float ChoicePaddingY = 12f;
        [Tooltip("Choice button corner radius (px).")]
        public float ChoiceCornerRadius = 10f;

        [Header("Background images")]
        [Tooltip("Optional background sprite for the dialogue body panel; overrides " +
                 "PanelColor when set. Use PanelSlice for a 9-sliced frame.")]
        public Sprite PanelSprite;
        [Tooltip("Optional background sprite for the nameplate; overrides PanelColor when set.")]
        public Sprite PlateSprite;
        [Tooltip("Optional background sprite for choice buttons; overrides ChoiceColor when set.")]
        public Sprite ChoiceSprite;
        [Tooltip("Optional background sprite for a hovered choice button; " +
                 "falls back to ChoiceSprite when unset.")]
        public Sprite ChoiceHoverSprite;

        [Tooltip("9-slice border in px for the dialogue/nameplate sprites; 0 = stretch.")]
        public int PanelSlice = 0;
        [Tooltip("9-slice border in px for choice-button sprites; 0 = stretch.")]
        public int ChoiceSlice = 0;

        // Content urls for the sprites above (manifest-driven). VnStage resolves them
        // through ILvnAssets, assigns the matching Sprite field, then re-applies the
        // theme — so the in-game UI skins from the manifest, not just the Inspector.
        // Leave empty to use an Inspector-assigned Sprite (or fall back to a colour).
        [HideInInspector] public string PanelImageUrl;
        [HideInInspector] public string PlateImageUrl;
        [HideInInspector] public string ChoiceImageUrl;
        [HideInInspector] public string ChoiceHoverImageUrl;

        // UI interaction sounds (manifest ui.sounds) — content urls resolved through
        // ILvnAssets like the image urls above. Empty url = that interaction is silent.
        [HideInInspector] public string ClickSoundUrl;
        [HideInInspector] public string ChoiceSoundUrl;
        [HideInInspector] public string TypeSoundUrl;
        [HideInInspector] public float UiSoundVolume = 1f;

        [Header("Quick menu (StageMenu)")]
        [Tooltip("Sheet / panel background of the in-game quick menu.")]
        public Color MenuBgColor = LvnTokens.PanelBg;
        [Tooltip("Primary text of menu items, slots and settings labels.")]
        public Color MenuTextColor = LvnTokens.Text;
        [Tooltip("Secondary text (slot previews, narration in history).")]
        public Color MenuDimTextColor = LvnTokens.TextDim;
        [Tooltip("Floating-button (↩ / ☰) fill.")]
        public Color MenuFabColor = new Color(0f, 0f, 0f, 0.35f);
        [Tooltip("Fullscreen backdrop behind an open menu.")]
        public Color MenuScrimColor = new Color(0f, 0f, 0f, 0.55f);
        [Tooltip("Sheet / panel corner rounding (px).")]
        public float MenuCornerRadius = LvnTokens.RadiusSm;
        [Tooltip("Show the ↩ rollback floating button.")]
        public bool MenuShowRollback = true;
        [Tooltip("Show the ☰ quick-menu floating button (hiding it removes the whole menu).")]
        public bool MenuShowMenu = true;

        /// <summary>Chrome text overrides by stable key (manifest ui.menu.labels) —
        /// how a Russian novel gets «Сохранить» instead of "Save". Null/missing
        /// keys fall back to the engine's English defaults.</summary>
        [HideInInspector] public System.Collections.Generic.Dictionary<string, string> MenuLabels;
    }
}
