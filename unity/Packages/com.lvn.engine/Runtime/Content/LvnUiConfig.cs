using System.Collections.Generic;

namespace Lvn.Content
{
    /// <summary>
    /// Manifest-driven theme for the engine's built-in novel screens — the
    /// loading screen, the chapter title card, and the name-input screen. Every
    /// field is optional; the components fall back to sensible defaults when a
    /// value is absent, so a host can ship an empty <c>ui</c> block and still get
    /// a working set of screens, then override piece by piece from the manifest.
    ///
    /// <para>Colors are hex strings (<c>"#rrggbb"</c> or <c>"#rrggbbaa"</c>);
    /// image fields are content urls resolved through <c>ILvnAssets</c>; layout
    /// numbers are screen fractions (0..1) or pixels as noted. No UnityEngine
    /// types here, so the whole theme is plain serializable data.</para>
    /// </summary>
    public sealed class LvnUiConfig
    {
        public LoadingScreenConfig loading;
        public TitleCardConfig title;
        public NameInputConfig name_input;
        public BootScreenConfig boot;
        public CarouselConfig carousel;
        public BrowseConfig browse;
        public HudConfig hud;
        public StageConfig stage;
        public DialogueConfig dialogue;
        public ChoicesConfig choices;
        public MenuConfig menu;
        public SoundsConfig sounds;
        public AuthConfig auth;
        public StoreConfig store;
        public PopupConfig popup;
        public SettingsConfig settings;
        public WardrobeConfig wardrobe;
        public TransitionsConfig transitions;
        public ChapterEndConfig chapter_end;
    }

    /// <summary>Between-screen choreography: how the shell's surfaces hand off
    /// to each other (chapter loader → live scene, entry pacing). One manifest
    /// block tunes the whole app's cinematic pacing; the title card's own
    /// fade/hold live in <see cref="TitleCardConfig"/>, the loading floor for a
    /// real download in <see cref="LoadingScreenConfig.min_seconds"/>.</summary>
    public sealed class TransitionsConfig
    {
        public float? screen_fade;    // loader → live-scene crossfade, seconds; default 0.35
        public float? loading_floor;  // min loader hold when everything is cached; default 0.25
        public float? backdrop_grace; // max wait for the first bg before revealing anyway; default 2.0
    }

    /// <summary>The wardrobe overlay: a live layered preview of the character
    /// plus slot tabs with buy/equip cards. The ITEMS live with the character
    /// (<c>sprites.&lt;id&gt;.wardrobe</c>); this block is only the screen's
    /// look and strings. Every field optional.</summary>
    public sealed class WardrobeConfig
    {
        public string entity;         // character to open by default (else: first with a wardrobe)
        public string title;          // default "Wardrobe"
        public string scrim_color;    // default #000000b3
        public string panel_color;    // default #14141af7
        public string title_color;    // default #f4ecd8
        public string text_color;     // default #f2eee1
        public string dim_text_color; // default #9a948a
        public string card_color;     // item card fill; default #1c1c22
        public string accent_color;   // equipped ring / action fill; default #c8a050
        public string accent_text_color; // action button text; default #14141a
        public string preview_bg_color;  // behind the character; default #101015
        public string preview_bg_image;  // optional content-url scene behind the heroine
        public float? corner_radius;  // default 12

        public string equip_text;     // default "Equip"
        public string equipped_text;  // default "Equipped"
        public string remove_text;    // default "Take off"
        public string confirm_text;   // the in-story sheet's commit button; default "Choose"
        public string insufficient_text; // shown when a buy fails for funds; default "Not enough"
        public string close_text;     // default "Close"
        public string empty_text;     // no wardrobe entities; default "The wardrobe is empty"
        public string menu_label;     // quick-menu entry; default "Wardrobe"
        public bool? show_menu_item;  // default true
        // The player's COLLECTION view: every wardrobe surface lists only
        // outfits accumulated along the way (staged/offered by the story) or
        // bought — not the full catalog. Story wardrobe moments always show
        // the author's full set for the beat. Default false (catalog/shop).
        public bool? collection_only;

        public Dictionary<string, string> rarity_colors; // rarity key → hex (card ring tint)
        public Dictionary<string, string> currency_icons; // currency → content url (balance pills)
    }

    /// <summary>The boot auth screen — the game's customizable face over the
    /// silent device sign-in: backdrop + logo + welcome text, an optional
    /// nickname field and a start button, with a status line reflecting the
    /// connection underneath. Not a gate: Start always works, online or
    /// offline. Absent section keeps the old behaviour (no screen, silent
    /// registration).</summary>
    public sealed class AuthConfig
    {
        public bool? enabled;         // show the screen after boot; default true when the section exists
        public string bg_url;         // full-screen backdrop
        public string logo_url;       // logo art, centred horizontally
        public float? logo_width;     // screen fraction; default 0.5
        public float? logo_y;         // logo centre y, screen fraction; default 0.28
        public string title;          // default "Welcome"
        public string subtitle;       // extra line under the title; default hidden
        public bool? ask_nickname;    // show the nickname field; default FALSE — the novel asks the name, not the app
        public string name_prompt;    // field label; default "Your name"
        public string default_name;   // pre-filled value (a previously saved name wins)
        public int? max_length;       // default 24
        public string start_text;     // the big button; default "Start"

        public string bg_color;       // default #101015
        public string panel_color;    // bottom panel fill; default #000000a6
        public string title_color;    // default #f4ecd8
        public string subtitle_color; // default #cbb98f
        public string text_color;     // field/button text; default #f4ecd8
        public string field_color;    // default #1c1c22 (used when no field art)
        public string button_color;   // default #c8a050 (used when no button art)
        public string button_text_color; // default #14141a
        public string status_color;   // connection line; default #9a948a
        public string field_url;      // optional text-field background art
        public string button_url;     // optional start-button art

        // status line strings (the localization hook)
        public string signing_text;   // default "Connecting…"
        public string signed_text;    // default "Connected"
        public string offline_text;   // default "Offline — progress stays on this device"

        // platform sign-in buttons — shown only when the host plugged the
        // matching LvnPlatformAuth flow AND the flag allows it
        public bool? show_google;     // default true (when the hook exists)
        public bool? show_apple;      // default true (when the hook exists)
        public string google_text;    // default "Sign in with Google"
        public string apple_text;     // default "Sign in with Apple"
        public string provider_done_text; // status after a provider sign-in; default "Signed in"
    }

    /// <summary>The currency store overlay: packs from the server's IAP catalog
    /// (<c>/v1/iap/catalog</c>) rendered as buy cards over a scrim. Every field
    /// optional — the engine's neutral dark look is the default; the section's
    /// mere presence adds the quick-menu entry.</summary>
    public sealed class StoreConfig
    {
        public string title;          // default "Store"
        public string scrim_color;    // fullscreen backdrop; default #000000b3
        public string panel_color;    // sheet fill; default #14141af7
        public string title_color;    // default #f4ecd8
        public string text_color;     // card titles / balances; default #f2eee1
        public string dim_text_color; // bonus line, status; default #9a948a
        public string card_color;     // pack card fill; default #1c1c22
        public string buy_color;      // buy button fill; default #c8a050
        public string buy_text_color; // buy button text; default #14141a
        public float? corner_radius;  // sheet/card rounding; default 12

        public string buy_text;       // button label when a pack has no price; default "Get"
        public string ad_text;        // rewarded-ad card button; default "Watch ad"
        public string close_text;     // default "Close"
        public string empty_text;     // no packs / server unreachable; default "The shop is closed right now"
        public string bonus_text;     // "{0}" = bonus amount; default "+{0} bonus"
        public string menu_label;     // quick-menu entry; default "Store"
        public bool? show_menu_item;  // add the quick-menu entry; default true

        public Dictionary<string, string> currency_icons; // currency → content url (cards + HUD pills)
        public Dictionary<string, string> currency_names; // currency → display name (default: the raw key)

        // Sections: packs are grouped by their catalog `section` id, shown in the
        // order sections first appear in the (server-sorted) catalog. This maps a
        // section id to a display heading; a missing id falls back to the raw id,
        // and packs with no section render as one unlabelled group (legacy).
        public Dictionary<string, string> section_titles;
        public string section_title_color; // section heading color; default title_color

        // "Pay from Russia" (or any region) banner, pinned at the top of each
        // section. When pay_banner_url is set the banner opens it via the
        // LvnWebView seam (in-app web view if the host plugged one, else the
        // system browser). Shown only to RU-region users unless pay_banner_always.
        public string pay_banner_text;   // e.g. "Как оплатить из России →"
        public string pay_banner_url;    // instructions page; empty → no banner
        public string pay_banner_color;  // banner fill; default a warm accent tint
        public string pay_banner_text_color; // banner text; default text_color
        public bool? pay_banner_always;  // show to everyone, not just RU; default false
    }

    /// <summary>The universal popup/dialog overlay (PopupScreen): a modal card
    /// centered over everything, used for warnings, confirmations and errors
    /// ("not enough energy", "buy currency?"). Every field optional — the
    /// engine's neutral dark look is the default. Buttons are supplied per-call,
    /// but the two default button labels (OK / Cancel) come from here so a host
    /// can localize them once.</summary>
    public sealed class PopupConfig
    {
        public string scrim_color;     // fullscreen backdrop; default #000000b3
        public string panel_color;     // card fill; default #14141af7
        public string title_color;     // default #f4ecd8
        public string text_color;      // message body; default #e8e4d8
        public string button_color;    // secondary button fill; default #ffffff14
        public string button_text_color;   // secondary button text; default text_color
        public string primary_color;   // primary/confirm button fill; default #c8a050
        public string primary_text_color;  // primary button text; default #14141a
        public float? corner_radius;   // card/button rounding; default 12
        public string ok_text;         // default OK/alert button; default "OK"
        public string cancel_text;     // default cancel button; default "Cancel"
    }

    /// <summary>The full settings overlay (SettingsScreen): master sound switch,
    /// language, player id + copy, account/sign-in status, app version, social
    /// links and Terms/Privacy. All strings optional with English fallbacks;
    /// social links and legal urls live here. Distinct from the quick-menu's
    /// in-game settings panel (playback tweaks) — this is the app-level screen.</summary>
    public sealed class SettingsConfig
    {
        public string title;           // default "Settings"
        public string scrim_color;     // default #000000b3
        public string panel_color;     // default #14141af7
        public string title_color;     // default #f4ecd8
        public string text_color;      // row labels; default #f2eee1
        public string dim_text_color;  // values/secondary; default #9a948a
        public string accent_color;    // toggle-on / links; default #c8a050
        public float? corner_radius;   // default 12
        public string close_text;      // default "Close"
        public string menu_label;      // quick-menu entry; default "Settings"
        public bool? show_menu_item;   // add the quick-menu entry; default true

        // Row labels / values (all localizable).
        public string sound_label;     // "Sound"
        public string on_text;         // "On"
        public string off_text;        // "Off"
        public string language_label;  // "Language"
        public string original_lang_text; // the script's inline language; default "Original"
        public string uid_label;       // "Player ID"
        public string copy_text;       // "Copy"
        public string copied_text;     // "Copied"
        public string account_label;   // "Account"
        public string signed_in_text;  // "Signed in"; provider appended as " · {name}"
        public string device_text;     // provider name for a device-only account; default "device"
        public string sign_in_text;    // "Sign in"
        public string version_label;   // "Version"

        // Legal + socials, opened via the LvnWebView seam.
        public string terms_url;
        public string terms_text;      // "Terms of Use"
        public string privacy_url;
        public string privacy_text;    // "Privacy Policy"
        public System.Collections.Generic.List<SocialLink> social; // 2-3 clickable icons
    }

    /// <summary>One social link in the settings screen — a clickable icon (or its
    /// name as a fallback) that opens a url via the web-view seam.</summary>
    public sealed class SocialLink
    {
        public string name; // "Discord" — shown when no icon, and as the tooltip
        public string icon; // content url for the icon (optional)
        public string url;  // opened via LvnWebView.Open
    }

    /// <summary>UI interaction sounds — short one-shot clips played by the stage
    /// on top of the story's audio channels, scaled by the player's SFX volume.
    /// Every field optional: a missing url simply keeps that interaction silent,
    /// so a novel without UI audio ships nothing and hears nothing.</summary>
    public sealed class SoundsConfig
    {
        public string click;   // content url: tap-to-advance / reveal-complete blip
        public string choice;  // content url: picking a choice button
        public string type;    // content url: typewriter tick (very short; throttled)
        public float? volume;  // 0..1 scale applied to all three; default 1
    }

    /// <summary>The in-game quick menu (StageMenu): floating buttons, the sheet,
    /// save/load slots, history and settings panels. Every field optional — the
    /// engine's neutral dark look is the default.</summary>
    public sealed class MenuConfig
    {
        public string bg_color;       // sheet/panel fill; default #14141af7
        public string text_color;     // items and labels; default #f2eee1
        public string dim_text_color; // secondary text (previews, narration); default #ccc7bd
        public string fab_color;      // floating-button fill; default #00000059
        public string scrim_color;    // fullscreen backdrop; default #0000008c
        public float? corner_radius;  // sheet/panel rounding; default 12
        public bool? show_rollback;   // the ↩ button; default true
        public bool? show_menu;       // the ☰ button; default true
        public bool? stats;           // the Stats item (live story variables); default true
        public bool? stats_edit;      // let the Stats panel EDIT variables (debug/QA); default false
        // What the Stats panel shows. An articy import carries hundreds of
        // technical variables (name tables, music/sound toggles, cutscene
        // flags) that would drown the player's actual stats, so a title
        // curates: stats_show — when non-empty, ONLY these roots/prefixes
        // appear (e.g. ["Relationships", "Way"]); stats_hide — these are
        // dropped (applied after). A prefix matches itself and its subtree.
        public List<string> stats_show;
        public List<string> stats_hide;

        /// <summary>Text overrides for every chrome string, keyed by a stable id —
        /// the localization hook ("save" → "Сохранить"). Known keys: save, load,
        /// quick_save, history, auto, skip, settings, gallery, stats, exit, close, autosave, slot,
        /// empty, quick_slot, overwrite_q ({0} = the slot label), overwrite, cancel,
        /// text_speed, auto_advance, auto_delay, music, ambient, sfx, voice, language,
        /// language_original,
        /// window_opacity, reduce_motion, skip_read_only. Missing keys fall back
        /// to English.</summary>
        public Dictionary<string, string> labels;
    }

    /// <summary>On-stage character framing: where a bottom-anchored actor sits and
    /// how big. Maps onto <c>VnTheme.ActorBaselineY</c>/<c>ActorScale</c>. Lets a novel
    /// tune the "standard pose" from the manifest without touching the engine.</summary>
    public sealed class StageConfig
    {
        public float? actor_y;      // bottom-anchored feet baseline (screen fraction); 1 = screen bottom, >1 sinks
        public float? actor_scale;  // multiplier on the default actor size; 1 = default
        public float? actor_spread; // multiplier on left/right offset from centre; 1 = default, <1 = closer to centre
    }

    /// <summary>In-game dialogue box: colours, fonts, padding and the typewriter
    /// reveal. Maps onto the engine's <c>VnTheme</c> so the whole game — not just
    /// the shell screens — is themeable from the manifest. Every field optional.</summary>
    public sealed class DialogueConfig
    {
        public string panel_color;       // box + nameplate fill; default #0d0d14cc
        public string text_color;        // body text; default #f5f5f5
        public string speaker_color;     // name text; default #ffd166

        public float? body_size;         // px; default 34
        public float? speaker_size;      // px; default 24
        public float? corner_radius;     // px; default 12

        public string align;             // box placement: "stretch" (default bottom bar) | "center" | "left" | "right"; non-stretch hugs the text
        public float? max_width_percent; // content-hug cap when align != stretch; default 80
        public float? width_percent;     // fixed box width % (>0 overrides the content-hug); default hug
        public float? max_height_percent;// cap box height as a screen %; default unbounded

        // Free popup: set x/y (a screen % 0..100) to float the box anywhere on
        // screen instead of docking it to the bottom — the universal popup mode.
        public float? x_percent;         // horizontal position 0=left … 100=right
        public float? y_percent;         // vertical position 0=top … 100=bottom
        public string anchor;            // which box point lands on (x,y): "center" (default), "bottom-center", "top-left", …

        public float? edge_padding;      // inset from screen edges; default 24
        public float? bottom_padding;    // gap to screen bottom; default 28
        public float? bottom_lift_percent; // lift the docked box up by this % of screen height; default 0
        public float? dock_top_percent;    // anchor docked box by its TOP at this % → grows DOWN; <0 = bottom-anchored
        public float? panel_padding_x;   // body inner padding; default 22
        public float? panel_padding_y;   // default 18
        public float? panel_min_height;  // default 128
        public float? name_padding_x;    // nameplate inner padding; default 14
        public float? name_padding_y;    // default 4

        public float? chars_per_second;  // typewriter speed; default 45
        public float? fade_width;        // soft per-glyph fade, trailing chars; default 5

        public string font;              // Resources path to a Font (e.g. "Fonts/Serif")
        public bool? nvl;                // NVL mode: tall full-screen text panel; default false
        public float? nvl_top;           // NVL top inset, screen fraction; default 0.12

        public string panel_image;       // content url: body-panel background sprite (overrides panel_color)
        public string name_image;        // content url: nameplate background sprite
        public int? panel_slice;         // 9-slice border px for the panel/name sprites; default 0 (stretch)
    }

    /// <summary>In-game choice buttons: colours, font, width and spacing.</summary>
    public sealed class ChoicesConfig
    {
        public string color;             // button fill; default #1f1f29eb
        public string hover_color;       // default #33333eF5
        public string text_color;        // default #f5f5f5
        public string cost_color;        // cost/lock label; default #e6a33b

        public string align;             // horizontal placement: "center" (default) | "left" | "right"
        public string valign;            // vertical placement: "center" (default) | "top" | "bottom"
        public float? y_percent;         // free vertical: top of the stack at this screen % (overrides valign)
        public float? min_height;        // one button's min height, reference px; default 125 (~6.5% H)

        public float? font_size;         // px; default 28
        public float? min_width_percent; // default 58
        public float? max_width_percent; // default 86
        public float? spacing;           // gap between buttons; default 10
        public float? padding_x;         // button inner padding; default 20
        public float? padding_y;         // default 12
        public float? corner_radius;     // default 10

        public string button_image;       // content url: button background sprite (overrides color)
        public string button_hover_image; // content url: hovered-button sprite (defaults to button_image)
        public int? button_slice;         // 9-slice border px for button sprites; default 0 (stretch)
    }

    /// <summary>The app boot / preload splash shown at launch (logo + progress).</summary>
    public sealed class BootScreenConfig
    {
        public string bg_color;          // default #0a0a0e
        public string bg_url;            // optional splash backdrop
        public string logo_url;          // optional centred logo
        public float? logo_width;        // screen fraction; default 0.5
        public float? logo_y;            // logo centre y; default 0.4

        public string bar_track_color;   // default #ffffff22
        public string bar_fill_color;    // default #c8a050
        public string bar_fill_url;      // optional fill art
        public float? bar_y;             // default 0.86
        public float? bar_width;         // default 0.6
        public float? bar_height;        // default 0.014
        public bool? show_percent;       // default true
        public string percent_color;     // default #cfc8bd
        public float? min_seconds;       // default 1.0
    }

    /// <summary>The title slider / carousel on the main menu.</summary>
    /// <summary>The hub/collection browse flow (BrowseHub) — an alternative to the
    /// carousel selected by <c>layout = "hub"</c>. Three themeable screens: the hub
    /// (game title + collection tiles), a collection (title cards), and a title
    /// detail (image + description + Play). Every colour/label optional.</summary>
    public sealed class BrowseConfig
    {
        public string layout;            // "carousel" (default) | "hub"

        public string bg_color;          // screen background; default #101015
        public string title;             // hub headline; default the app/product name
        public string subtitle;          // hub sub-line; default "Выбери…"
        public string title_color;       // headings; default #f4ecd8
        public string text_color;        // body/desc; default #e8e4d8
        public string dim_text_color;    // secondary; default #9a948a
        public string card_color;        // tile/card fill; default #35c88f (green like the mock)
        public string card_text_color;   // text on cards; default #14141a
        public string accent_color;      // buttons; default #35c88f
        public string accent_text_color; // button text; default #14141a
        public float? card_radius;       // px; default 16

        public string play_text;         // detail Play button; default "Играть"
        public string back_text;         // back button; default "‹"
        public string locked_text;       // lock badge on a gated card; default "🔒"
        public string cost_text;         // cost chip, "{0}" = amount; default "{0}"
        public string all_text;          // slider "see all" link; default "Все"
        public string more_text;         // card details button; default "Подробнее"
        public string featured_text;     // featured-banner eyebrow; default "Рекомендуем"
        public string continue_text;     // resume banner label; default "Продолжить"
        public string library_text;      // auto row for un-collected titles; default "Новеллы"
        public string nav_home;          // bottom nav labels
        public string nav_store;
        public string nav_wardrobe;
        public string nav_gallery;
        public string nav_profile;
    }

    public sealed class CarouselConfig
    {
        public string bg_color;          // default #101015
        public float? card_width;        // screen fraction; default 0.62
        public float? card_height;       // screen fraction; default 0.62
        public float? card_gap;          // screen fraction; default 0.06
        public string card_bg_color;     // default #1c1c22 (behind a missing cover)
        public float? card_radius;       // px; default 18

        public string title_color;       // default #f4ecd8
        public float? title_size;        // px; default 40
        public string subtitle_color;    // default #cbb98f
        public float? subtitle_size;     // px; default 22

        public string play_text;         // default "Play"
        public string continue_text;     // Play label when there's progress; default "Continue"
        public string chapters_text;     // the chapter-picker button; default "Chapters"
        public string play_color;        // default #f4ecd8
        public string play_bg_color;     // default #3a3a44
        public string dot_color;         // page-dot inactive; default #ffffff55
        public string dot_active_color;  // default #f4ecd8
    }

    /// <summary>The between-chapters screen (manifest <c>ui.chapter_end</c>).
    /// Present → the chapter loop pauses on "Конец главы" with continue/menu
    /// buttons; absent → chapters flow seamlessly (historical behaviour).</summary>
    public sealed class ChapterEndConfig
    {
        public string title;                  // default "Конец главы"
        public string continue_label;         // default "Продолжить" (hidden on the last chapter)
        public string menu_label;             // default "В меню"
        public string bg_color;               // scrim; default #0A080Deb
        public string title_color;            // default #f5eed9
        public float? title_size;             // default 64
        public string subtitle_color;         // chapter name; default #ccb88f
        public float? subtitle_size;          // default 34
        public string button_color;           // primary (continue); default #8c3659
        public string button_secondary_color; // menu; default #ffffff1a
        public string button_text_color;      // default #f7f3e6
        public float? button_radius;          // default 26
    }

    /// <summary>The in-game top HUD: chapter progress + currency pills.</summary>
    public sealed class HudConfig
    {
        public string bg_color;          // strip background; default #00000088
        public float? height;            // screen fraction; default 0.07
        public string progress_icon_url; // optional icon left of the percent
        public string progress_color;    // default #f4ecd8
        public bool? show_progress;      // default true

        /// <summary>"always" (default) keeps the bar up through the chapter;
        /// "choices" hides it during plain reading and shows it only while a
        /// choice is on screen (where costs/balances matter) — the corner-minimal
        /// reading surface the genre leaders use.</summary>
        public string mode;

        public string pill_bg_color;     // default #00000066
        public string pill_text_color;   // default #f4ecd8
        public string default_currency_icon_url; // fallback pill icon
        public string regen_ready_text;  // shown on a refill countdown that hit 0; default "…"
    }

    /// <summary>Look and behaviour of the loading screen (background, scrim, and a
    /// progress bar with optional track/fill/frame art).</summary>
    public sealed class LoadingScreenConfig
    {
        public string bg_color;          // backdrop behind everything; default #000000
        public string bg_url;            // optional static backdrop image
        public string fog_url;           // optional atmospheric overlay (fades in late)

        public string scrim_color;       // dark wash over the bg; default #000000
        public float? scrim_opacity;     // default 0.65

        public string bar_track_url;     // bar background art (optional)
        public string bar_fill_url;      // bar fill art (optional)
        public string bar_frame_url;     // bar frame overlay art (optional)
        public string bar_fill_color;    // solid fill when no fill art; default #c8a050
        public string bar_track_color;   // solid track when no track art; default #ffffff22

        public float? bar_x;             // bar centre x, screen fraction; default 0.5
        public float? bar_y;             // bar centre y, screen fraction; default 0.82
        public float? bar_width;         // screen fraction; default 0.7
        public float? bar_height;        // screen fraction; default 0.018
        public float? fill_span_percent; // fill width at 100%; default 100 (use 90 for sprite caps)

        public bool? show_percent;       // default true
        public bool? show_file;          // default true
        public bool? show_hint;          // default true
        public string percent_color;     // default #ffffff
        public string hint_color;        // default #cfc8bd
        public string file_color;        // default #9a948a

        public string[] tips;            // rotating hint lines during loading
        public float? min_seconds;       // hold the screen at least this long; default 0
    }

    /// <summary>The "Chapter N / chapter name" reveal card.</summary>
    public sealed class TitleCardConfig
    {
        public string frame_url;         // optional decorative frame behind the text
        public string fog_url;           // optional fog that fades in with the card

        public string chapter_color;     // default #f4ecd8
        public string subtitle_color;    // default #cbb98f
        public float? chapter_size;      // px; default 64
        public float? subtitle_size;     // px; default 34

        public float? hold_seconds;      // how long the card stays; default 2.5
        public float? fade_seconds;      // fade-in duration; default 0.6
    }

    /// <summary>The character name-input screen.</summary>
    public sealed class NameInputConfig
    {
        public bool? enabled;            // the manifest switch; default true when the section exists
        public string bg_url;            // full-screen backdrop
        public string hero_url;          // optional character art
        public string field_url;         // optional text-field background art
        public string button_url;        // optional confirm-button art
        public string badge_url;         // optional speaker-name badge art

        public string bg_color;          // default #101015
        public string prompt;            // default "Enter your name"
        public string speaker_label;     // default "Name"
        public string default_name;      // pre-filled value; default ""
        public string confirm_text;      // default "Confirm"
        public int? max_length;          // default 24

        public string prompt_color;      // default #cbb98f
        public string text_color;        // default #f4ecd8
        public string field_color;       // default #1c1c22 (used when no field art)
        public string button_color;      // accent fill; default: the dialogue speaker colour
        public string button_text_color; // default #14141a (dark on the accent)
    }
}
