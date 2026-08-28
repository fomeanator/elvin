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
        /// <summary>ТЕМП ПОСТАНОВКИ. 0.75 — сцена играет свои переходы на
        /// четверть быстрее заявленных секунд, не трогая их соотношений: автор
        /// пишет привычные 0.35, а на экране они читаются собранно.
        ///
        /// <para>Это характер СЦЕНЫ; общий темп всего движения — ручка
        /// <see cref="LvnMotion.Tempo"/>, и <see cref="Motion"/> учитывает
        /// обе. Настраивается из <c>ui.stage.motion_scale</c>.</para></summary>
        public static float MotionDurationScale = 0.75f;

        /// <summary>Сколько на самом деле длится сценическое движение,
        /// заявленное как <paramref name="seconds"/>. Единственное место, где
        /// темп применяется: раньше на каждое движение приходилось помнить про
        /// умножение, и половина мест про него забывала.</summary>
        public static float Motion(float seconds) => LvnMotion.Sec(seconds * MotionDurationScale);

        /// <summary>То же в миллисекундах — для UITK-переходов сцены.</summary>
        public static int MotionMs(float seconds) => Mathf.Max(1, Mathf.RoundToInt(Motion(seconds) * 1000f));

        /// <summary>СМЕНА ОБЛИКА: старый вид растворяется над новым. Эмоция и
        /// поза — короче (0.20), примерка одежды — чуть длиннее (0.28): наряд
        /// меняет силуэт целиком, и глазу нужно время его прочесть. Оба жили
        /// числами внутри рендерера, хотя это ритм постановки. Ключи манифеста:
        /// <c>ui.stage.look_swap</c> и <c>ui.stage.wardrobe_swap</c>.</summary>
        public static float LookSwapSeconds = 0.20f;
        public static float WardrobeSwapSeconds = 0.28f;

        // Defaults derive from the "Полночь" design tokens (LvnTokens) so the whole
        // app is one coherent look out of the box; any field is still overridable.
        [Header("Dialogue")]
        // Тон — из токенов действующей темы, своя лишь плотность: полупрозрачный
        // диалог (рыночная норма 75–90%) поверх плотных листов оболочки. Раньше
        // тон стоял числами и молча расходился бы со сменой темы, хотя строкой
        // выше обещано обратное.
        public Color PanelColor = LvnTokens.Panel(0.86f);
        public Color TextColor = LvnTokens.Text;
        public Color SpeakerColor = LvnTokens.Accent;
        public Font Font;
        [Tooltip("Optional Resources path to a Font for dialogue/choices text " +
                 "(e.g. \"Fonts/Serif\"); used when Font is unset.")]
        public string FontResourcePath = LvnFonts.DefaultPath;
        public int BodyFontSize = 46;
        public int SpeakerFontSize = 34;
        public float PanelCornerRadius = 28f;

        // МАТОВОЕ СТЕКЛО: 0 — обычная заливка (как было), 1 — размытая сцена под
        // окном во всю силу. Даёт то, чего плоская прозрачность дать не может:
        // сквозь простую прозрачность читается ТЕКСТУРА фона и мешает буквам, а
        // сквозь размытие проходит только его свет и цвет.
        //
        // Требует камеры мира (канвас-сцена). Там, где её нет, поле молча
        // ничего не меняет — окно остаётся плоским, а не пропадает.
        [Tooltip("Frosted glass under the dialogue panel: 0 = flat fill, 1 = full blur of the scene behind it.")]
        [Range(0f, 1f)] public float PanelGlass = 0f;
        [Tooltip("Frosted glass under choice buttons; same scale as PanelGlass.")]
        [Range(0f, 1f)] public float ChoiceGlass = 0f;

        // КАК ОКНО ПОЯВЛЯЕТСЯ И УХОДИТ. Виды из общего набора движка
        // (LvnAppear): fade, rise, pop, drop, unfold, slide_up/down/left/right.
        // Пусто — мгновенно, как было. Шейдера здесь быть не может: у элемента
        // UI Toolkit нет материала, поэтому окно двигается и тает, а не горит.
        [Tooltip("Dialogue box entrance/exit: fade|rise|pop|drop|unfold|slide_* (empty = instant).")]
        public string BoxAppear = "fade";
        [Tooltip("Dialogue box entrance duration, seconds.")]
        public float BoxAppearDuration = 0.286f; // +30% (Илья 25.08): табличке дали дольше ехать
        [Tooltip("Short breath between the old card fading out and the next beat appearing.")]
        public float BeatPause = 0.06f;

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
        public string BoxAlign = "center"; // market median: inset box, not a full-bleed bar
        [Tooltip("Box width as a percent of the screen when BoxAlign isn't " +
                 "\"stretch\" and BoxWidthPercent is unset. The box is exactly this " +
                 "wide (it does NOT shrink to the text). 0 = default 80%.")]
        public float BoxMaxWidthPercent = 0f;
        [Tooltip("Fixed box width as a percent of the screen (overrides " +
                 "BoxMaxWidthPercent). The box never shrinks to the text; the height " +
                 "grows instead. 0 = use BoxMaxWidthPercent / default.")]
        public float BoxWidthPercent = 92f; // 4% side insets (mobile-VN median)
        [Tooltip("Max box height as a percent of the screen — caps the vertical " +
                 "growth (0 = unbounded, the box grows as tall as the text needs).")]
        public float BoxMaxHeightPercent = 0f; // 0 = no cap (a hard cap squeezed the plate/marker out of the box)

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
        public float BottomLiftPercent = 4f; // the box floats off the bottom edge
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

        // ПЕРЕХОДЫ ПО УМОЛЧАНИЮ — это свойство постановки, а не каждой реплики.
        // Новый герой въезжает от своей стороны, уходящий растворяется на месте.
        [Tooltip("Default actor entrance when the command has no enter= (drift = enter from its side; empty = instant).")]
        public string ActorEnter = "drift";
        [Tooltip("Default actor exit when the command has no exit=.")]
        // Entrance has direction; departure gives the eye no second lateral
        // journey and simply dissolves the actor in place.
        public string ActorExit = "fade";
        [Tooltip("Default actor transition duration in seconds.")]
        public float ActorTransition = 0.35f;
        // Смена фона: прежний кадр растворяется над новым (резкий своп читался
        // как склейка). 0 — прежнее поведение, мгновенно.
        [Tooltip("Background crossfade on change, seconds (0 = hard cut).")]
        // Полсекунды — столько живёт смена фона. Треть была слишком резкой:
        // кадр не сменялся, а дёргался (Илья, живой просмотр перехода).
        public float BgCrossfadeSeconds = 0.5f;
        [Tooltip("Long-press art view gesture (hide UI while held). ui.stage.long_press.")]
        public bool LongPressArtView = true;

        [Tooltip("Опоздавший на медленной сети актёр входит вовремя тёмной @mini-заготовкой; полный арт проявляет его кроссфейдом. ui.stage.loading_silhouette.")]
        public bool LoadingSilhouette = true;

        [Tooltip("Тап-салют: \"hearts\" — сердечки из точки касания (7HS-жанр). Пусто = выкл. ui.stage.tap_burst.")]
        public string TapBurst = "";
        [Tooltip("Default obj entrance when the command has no enter=.")]
        public string ObjectEnter = "fade";
        [Tooltip("Default obj exit when the command has no exit=.")]
        public string ObjectExit = "fade";
        [Tooltip("Default obj transition duration in seconds.")]
        public float ObjectTransition = 0.35f;
        [Tooltip("Multiplier on a positioned actor's horizontal offset from centre. " +
                 "1 = default (left=0.25/right=0.75); 0.6 pulls left/right in by 10% of " +
                 "the screen (→0.35/0.65); 0 stacks everyone centre. Ignored when the op " +
                 "sets an explicit x=.")]
        public float ActorSpread = 1f;
        [Tooltip("Speaker focus: \"solo\" — only the current speaker is visible, others fade out " +
                 "(classic novel); empty/none — no automatic focus. RGB dimming is disabled.")]
        public string SpeakerFocus = "none";

        [Header("Reveal")]
        [Tooltip("Typewriter speed in characters per second.")]
        public float CharsPerSecond = 72f;
        [Tooltip("Visible characters placed on screen immediately; only the remainder is typed.")]
        public int InitialVisibleCharacters = 40;
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
        public float ChoiceMinWidthPercent = 82f;
        public float ChoiceMaxWidthPercent = 82f; // fixed width — the market norm
        [Tooltip("Vertical gap between stacked choice buttons (px).")]
        public float ChoiceSpacing = 38f; // ~2% of reference height

        /// <summary>Minimum height of one choice button (reference px). The
        /// market norm is ~6.5% of screen height — a comfortable thumb target.</summary>
        public float ChoiceMinHeight = 125f;
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
        // `type` — луп клавиатуры на время проявления строки (не по-глифовый цокот).
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
        [Tooltip("Show the Stats menu item (live story variables; hidden anyway when there are none).")]
        public bool MenuShowStats = true;
        [Tooltip("Allow EDITING variables from the Stats panel — a debug/QA switch, not for players.")]
        public bool MenuStatsEdit = false;

        /// <summary>Stats-panel curation (manifest ui.menu.stats_show / stats_hide):
        /// whitelist (when non-empty, only these roots/prefixes) and blacklist of
        /// variable paths — keeps imported technical tables out of the panel.</summary>
        [HideInInspector] public System.Collections.Generic.List<string> MenuStatsShow;
        [HideInInspector] public System.Collections.Generic.List<string> MenuStatsHide;

        /// <summary>Chrome text overrides by stable key (manifest ui.menu.labels) —
        /// how a Russian novel gets «Сохранить» instead of "Save". Null/missing
        /// keys fall back to the engine's English defaults.</summary>
        [HideInInspector] public System.Collections.Generic.Dictionary<string, string> MenuLabels;
        // Спрятанные пункты квик-меню (ui.menu.hide) — по стабильным ключам.
        [HideInInspector] public System.Collections.Generic.List<string> MenuHidden;
        // Тап по строке истории отматывает главу (ui.menu.history_jump).
        [HideInInspector] public bool MenuHistoryJump = true;

        // Двухползунковый звук («Музыка»+«Звук») в плейбек-настройках квик-меню.
        // ui.settings.simple_audio; полный экран настроек читает конфиг сам.
        [HideInInspector] public bool SimpleAudioSliders;
    }


    /// <summary>
    /// АВТОРСКОЕ СЛОВО — одна дорога от <c>ui.menu.labels</c> до надписи.
    ///
    /// <para>Читали его в трёх местах и тремя способами: своим методом в меню,
    /// развёрнутым условием у поля ввода, а в диалоге не читали вовсе — там
    /// фраза «Не хватает…» была вписана в движок ПО-РУССКИ, и английская сборка
    /// показывала её как есть.</para>
    ///
    /// <para>Расширение, а не метод, нарочно: тема бывает не задана, и вызов на
    /// пустой теме обязан вернуть умолчание, а не упасть.</para>
    ///
    /// <para>СЛОВАРЕЙ В МАНИФЕСТЕ ДВА: <c>ui.menu.labels</c> (подписи меню) и
    /// <c>ui.words</c> (всё остальное). Автор обязан был помнить, какое слово в
    /// какой класть, а ошибка выдавала английский текст МОЛЧА — на этом
    /// спотыкается даже тот, кто систему знает. Теперь слово ищется в обоих:
    /// сперва тема (она ближе к экрану и может переопределить), затем общий
    /// словарь, и только потом английское умолчание движка.</para>
    /// </summary>
    public static class VnThemeWords
    {
        public static string Word(this VnTheme t, string key, string fallback)
        {
            // ПЕРЕВОД СИЛЬНЕЕ АВТОРСКОГО СЛОВАРЯ. Подписи меню автор задаёт на
            // своём языке, и они стояли впереди перевода: игрок переключал
            // язык, получал английские реплики — и русское меню поверх них.
            // Меню он читает первым, поэтому именно оно и решает, «работает ли
            // переключатель».
            if (Lvn.Content.LvnWords.TryTranslated(key, out var tr)) return tr;
            if (t?.MenuLabels != null && t.MenuLabels.TryGetValue(key, out var v)
                && !string.IsNullOrEmpty(v)) return v;
            return Lvn.Content.LvnWords.Of(key, fallback);
        }
    }
}
