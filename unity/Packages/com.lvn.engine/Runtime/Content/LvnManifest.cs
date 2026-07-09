using System.Collections.Generic;

namespace Lvn.Content
{
    /// <summary>
    /// The content manifest the client fetches from a backend: the catalog of
    /// titles and the top-level (boot/menu) asset set. Plain serializable POCOs —
    /// deserialize your server's JSON into these (field names match the bundled
    /// Go server template) and hand the result to <see cref="DownloadManager"/>.
    /// Everything is optional and null-safe: a host that ships a single bundled
    /// chapter can leave <see cref="titles"/> null and drive a chapter directly.
    /// </summary>
    public sealed class LvnManifest
    {
        /// <summary>Top-level assets the client warms at boot/menu (shared UI
        /// chrome, title covers, chapter loading backgrounds), keyed by content
        /// path → <see cref="LvnAssetMeta"/>. When present this is the
        /// authoritative boot set; when empty the manager falls back to
        /// <see cref="DownloadManager.FallbackBootUi"/> + a manifest walk.</summary>
        public Dictionary<string, LvnAssetMeta> assets;

        /// <summary>The title catalog (anthology). Optional.</summary>
        public List<LvnTitle> titles;

        /// <summary>Hub collections grouping titles into browsable tiles
        /// (expeditions/dates/reality). Drives the <c>ui.browse.layout = "hub"</c>
        /// screen flow; ignored by the default carousel. Optional.</summary>
        public List<LvnCollection> collections;

        /// <summary>The game's default MAIN HEROINE — a sprite catalog entity id.
        /// The concept a single-heroine game (e.g. Time Romance) leans on: her
        /// wardrobe fronts the skin shop and her portrait can front the profile.
        /// A title may override it with <see cref="LvnTitle.hero"/>. The skin shop
        /// itself still holds outfits for EVERY actor across all novels — this is
        /// just which one it opens on.</summary>
        public string hero;

        /// <summary>Manifest-driven theme for the built-in novel screens (loading,
        /// title card, name input). Optional — components use defaults when null.</summary>
        public LvnUiConfig ui;

        /// <summary>Language codes this content ships string catalogs for
        /// (<c>["en", "ru"]</c> → <c>&lt;script&gt;.en.json</c> sidecars exist).
        /// Non-empty enables the language picker in Settings; the script's
        /// inline text (the original) is always an implicit option.</summary>
        public List<string> languages;

        /// <summary>The sprite/entity catalog, keyed by id. Scripts reference these
        /// ids (e.g. <c>actor id="mara" pose="sitting"</c>) instead of raw urls; the
        /// client resolves an id to its ordered layer urls and composites them. A
        /// simple sprite is just a one-layer entity; a character is a multi-layer
        /// entity parameterised by axes. Optional.</summary>
        public Dictionary<string, LvnSpriteEntity> sprites;

        /// <summary>Product economy rules layered over the wallet (chapter-entry
        /// gating). Optional — null, or an empty chapter_currency, means chapters
        /// are free and nothing gates entry (the default for every existing
        /// novel).</summary>
        public LvnEconomyConfig economy;
    }

    /// <summary>
    /// Economy rules that gate content behind currency. Today: the chapter-entry
    /// gate (spend N of a currency — typically the regenerating "energy" — to
    /// start a chapter). All strings are optional with neutral English fallbacks;
    /// content localizes them here.
    /// </summary>
    public sealed class LvnEconomyConfig
    {
        /// <summary>Currency spent to ENTER a chapter (e.g. "energy"). Empty/null
        /// disables the gate entirely.</summary>
        public string chapter_currency;
        /// <summary>Amount spent per chapter entry; default 1 when a currency is set.</summary>
        public int? chapter_cost;
        /// <summary>Chapter ids that never charge (onboarding/tutorial). The first
        /// chapter can be listed here to keep it free.</summary>
        public List<string> free_chapters;

        // Gate popup copy (optional; English fallbacks in NovelApp).
        public string gate_title;    // e.g. "Not enough energy"
        public string gate_message;  // e.g. "You need 1 energy to open this chapter."
        public string gate_buy;      // confirm button → store; default "Store"
        public string gate_cancel;   // cancel button; default "Not now"
        public string gate_denied;   // shown when still short after the store; default gate_title
    }

    /// <summary>
    /// A catalog entry: an ordered list of full-frame layer URL templates plus
    /// default axis values. Mirrors the engine's cast model — to draw the entity
    /// in a state, fill each template's <c>{axis}</c> tokens from the command's
    /// axis values (overlaid on <see cref="defaults"/>) and stack the layers. A
    /// layer whose token stays unresolved is skipped, so optional parts only
    /// appear when an axis supplies them.
    /// </summary>
    public sealed class LvnSpriteEntity
    {
        /// <summary>Optional display name (e.g. a speaker label).</summary>
        public string name;
        /// <summary>Optional speaker/name colour (hex) — light entity data.</summary>
        public string color;
        /// <summary>Ordered layers, bottom-to-top. Each layer is a URL template
        /// (with optional <c>{axis}</c> tokens) plus an optional <c>when</c>
        /// condition for conditional display. A simple sprite is one plain layer.</summary>
        public List<LvnLayer> layers;
        /// <summary>Default axis values (axis → value), overridden per-command.</summary>
        public Dictionary<string, string> defaults;
        /// <summary>Allowed values per axis (axis → values) — drives the authoring
        /// dropdowns and validation; optional (free-form when absent).</summary>
        public Dictionary<string, List<string>> axes;
        /// <summary>Renderer kind: <c>static</c> (default) | <c>rigged</c> (named
        /// transform animations) | <c>spine</c> | <c>live2d</c> (future).</summary>
        public string kind;

        /// <summary>The width/height ratio of the box the layers were AUTHORED in.
        /// When set, the renderer locks the actor's on-screen box to this aspect
        /// (shrinking within the placed width/height) — layered/boned art keeps
        /// pixel-exact registration on every screen instead of each layer
        /// letterboxing differently. Unset (0) = legacy percent box.</summary>
        public float aspect;
        /// <summary>Named animations (name → tracks). A <c>rigged</c> entity plays
        /// these via <c>actor play="name"</c>; <c>auto:true</c> animations loop on
        /// show. See <see cref="LvnAnim"/>.</summary>
        public Dictionary<string, LvnAnim> anim;

        /// <summary>For <c>kind: "spine"</c>: the exported skeleton's files. The
        /// runtime builds the skeleton from these at load (no Unity assets) —
        /// requires the optional spine-unity integration to be installed.</summary>
        public LvnSpineRef spine;

        /// <summary>The entity's wardrobe, keyed by axis (<c>"armor"</c>,
        /// <c>"outfit"</c>…): each slot lists the axis values as purchasable /
        /// equippable items. Presence of this block puts the character in the
        /// wardrobe screen; the layers themselves already handle "nothing
        /// equipped" (an unset axis skips its layer). Optional.</summary>
        public Dictionary<string, LvnWardrobeSlot> wardrobe;
    }

    /// <summary>One wardrobe slot — a themed group of items behind one axis
    /// (the "Armor" tab). Items map to the axis' values; buying uses the
    /// wallet's sku inventory, so ownership is server-authoritative.</summary>
    public sealed class LvnWardrobeSlot
    {
        public string name;               // tab label; default: the axis id
        public string icon;               // content url — the in-story sheet's tab icon
        /// <summary>Can the slot be emptied (item taken off)? Default true —
        /// matches the layer model where an unset axis draws nothing.</summary>
        public bool? removable;
        /// <summary>Story variable this axis drives (e.g. "Wardrobe.mainCh_Clothes").
        /// When set, equipping the axis in the in-story sheet writes the picked value
        /// back into the novel's state so its logic sees the choice. Empty = wardrobe-
        /// only (no write-back). JSON key is "var" (a C# keyword, hence the alias).</summary>
        [Newtonsoft.Json.JsonProperty("var")]
        public string storyVar;
        public List<LvnWardrobeItem> items;
    }

    /// <summary>One wardrobe item: an axis value with shop presentation. No
    /// price (or 0) = free, owned from the start.</summary>
    public sealed class LvnWardrobeItem
    {
        public string value;    // the axis value this item sets (required)
        public string name;     // display name; default: the value
        public string icon;     // content url (a layer png works fine)
        public string currency; // price currency; with price>0 the item is bought
        public long price;
        public string rarity;   // optional tint key ("rare"/"epic"/…) → WardrobeConfig.rarity_colors
    }

    /// <summary>A Spine export reference: content urls of the three files the
    /// Spine editor produces, plus the import scale and the idle to auto-play.</summary>
    public sealed class LvnSpineRef
    {
        public string json;    // skeleton (.json export)
        public string atlas;   // .atlas text
        public string texture; // the atlas page image
        public float scale = 1f;
        /// <summary>Animation to loop on show (e.g. "idle"/"walk").</summary>
        public string auto;
        /// <summary>How the skeleton sizes itself to the screen — a
        /// self-contained container that depends on nothing but the canvas and
        /// its own posed bounds. Mirrors spine-unity's LayoutScaleMode:
        /// <c>"width"</c> (DEFAULT) width-to-width, height follows the aspect;
        /// <c>"height"</c> height-to-height; <c>"cover"</c> fill both (crop);
        /// <c>"contain"</c> fit inside (letterbox). Times <see cref="scale"/>.</summary>
        public string fit = "width";
        /// <summary>Optional static background image that BELONGS to the
        /// skeleton: rendered behind it inside the same container, so it scales,
        /// moves and drags together with the Spine and stays perfectly aligned
        /// (the base plate the animated overlay was authored on top of).</summary>
        public string bg;
    }

    /// <summary>A named animation: a set of tracks tweened over <c>duration</c>
    /// seconds, optionally looping. Engine-agnostic data — the runtime tweens an
    /// actor's transform; the authoring panel and language server read the names
    /// for autocomplete/validation.</summary>
    public sealed class LvnAnim
    {
        /// <summary>Loop forever (idle/breathe) vs play once (a gesture).</summary>
        public bool loop;
        /// <summary>When looping, ping-pong (forward then back) instead of
        /// restarting — with easing this is the cheap path to idle motion.</summary>
        public bool yoyo;
        /// <summary>Total length in seconds.</summary>
        public float duration = 1f;
        /// <summary>Auto-run: <c>"true"</c> loops on show (idle/blink);
        /// reserved <c>"speaking"</c> runs while the actor talks (v2). Null = manual.</summary>
        public string auto;
        /// <summary>The animated channels.</summary>
        public List<LvnAnimTrack> tracks;
    }

    /// <summary>One animated property over time. <c>keys</c> is a list of
    /// <c>[time, value]</c> pairs (time in seconds, 0..duration).</summary>
    public sealed class LvnAnimTrack
    {
        /// <summary>Target layer id (<c>eyes</c>, <c>mouth</c>, …) for per-layer
        /// blink/lip-sync; null = the whole actor's transform.</summary>
        public string layer;
        /// <summary>Property: <c>x</c>/<c>y</c> (translate by a fraction of own size) |
        /// <c>screen_x</c>/<c>screen_y</c> (move the whole actor across the screen,
        /// fraction of the screen) | <c>scale</c> (uniform) | <c>scalex</c>/<c>scaley</c>
        /// (squash/stretch) | <c>rotation</c> (degrees) | <c>alpha</c> | <c>frame</c>
        /// (swap the layer's sprite by an axis value — blink/lip-sync/curl).</summary>
        public string prop;
        /// <summary>For <c>prop:"frame"</c> — which axis the frame values name
        /// (e.g. <c>eyes</c>, <c>mouth</c>). The layer's url template is resolved
        /// with this axis = the keyed value.</summary>
        public string axis;
        /// <summary>Easing curve: <c>linear</c> | <c>inOutSine</c> | <c>outCubic</c> |
        /// <c>outBack</c>. Default linear.</summary>
        public string ease;
        /// <summary>Interpolation between keys: <c>linear</c> (default) | <c>spline</c>
        /// (smooth Catmull-Rom through the keys) | <c>step</c>. Forward-compatible —
        /// the linear sampler treats unknown values as linear.</summary>
        public string interp;
        /// <summary>On the <c>screen_x</c> track of a path pair (<c>move …
        /// orient=true</c>): rotate the actor to face along the path tangent.</summary>
        public bool orient;
        /// <summary><c>[[time, value], …]</c>. Value is a number for transforms,
        /// or an axis value string for <c>frame</c> tracks.</summary>
        public List<object[]> keys;
    }

    /// <summary>One title (a series of chapters grouped into seasons).</summary>
    public sealed class LvnTitle
    {
        public string id;
        /// <summary>Display name shown on the carousel card (falls back to id).</summary>
        public string name;
        /// <summary>Short tagline under the name on the carousel card.</summary>
        public string subtitle;
        /// <summary>Cover art for the menu carousel.</summary>
        public string cover_url;
        public List<LvnSeason> seasons;
        /// <summary>Optional per-title UI theme override — layered over the global
        /// manifest.ui when this title's chapters play, so each game can have its
        /// own dialogue/choice look (e.g. a fantasy frame for an RPG).</summary>
        public LvnUiConfig ui;
        /// <summary>Optional CG gallery: the curated list of unlockable art. An
        /// item unlocks forever the first time a <c>bg</c> with its url is shown;
        /// the quick menu grows a Gallery entry when this list is non-empty.</summary>
        public List<LvnGalleryItem> gallery;

        // ── the hub/collection browse model (ui.browse.layout = "hub") ──
        /// <summary>Content type — an author tag mirroring the title's collection
        /// (<c>expedition</c>/<c>date</c>/<c>reality</c>). Purely informational for
        /// the engine; access/completion is driven by <see cref="unlock"/> and the
        /// script's <c>global.*</c> flags.</summary>
        public string type;
        /// <summary>Detail-card presentation on the hub's title screen: big image +
        /// description + a Play button. Falls back to name/subtitle/cover_url.</summary>
        public LvnCardArt card;
        /// <summary>Access gate — an expression over the player's <c>global.*</c>
        /// stats (e.g. <c>"exp_1_done"</c> or <c>"rep >= 5 &amp;&amp; date_a_done"</c>).
        /// Empty/absent = always available. When false the card shows locked and a
        /// tap explains why (see <see cref="locked_hint"/>).</summary>
        public string unlock;
        /// <summary>Shown in a popup when a locked card is tapped ("Пройди
        /// Экспедицию 1"). Optional.</summary>
        public string locked_hint;
        /// <summary>Cost to START this title from the hub (typically 1 energy for an
        /// expedition). Null/zero = free. Charged on Play via the wallet; too little
        /// → a "buy?" popup routes to the store.</summary>
        public LvnCost cost;
        /// <summary>The main heroine / player character of this title — a sprite
        /// catalog entity id. Her wardrobe is the default one the skin shop opens,
        /// and her portrait can front the profile. Falls back to
        /// <see cref="LvnManifest.hero"/>.</summary>
        public string hero;
    }

    /// <summary>A named group of titles shown as one hub tile (an "expeditions",
    /// "dates" or "reality" collection). Titles are listed explicitly and in
    /// order; a title may appear in more than one collection.</summary>
    public sealed class LvnCollection
    {
        public string id;
        public string name;      // "Экспедиции"
        public string subtitle;  // optional line under the name
        public string type;      // author tag applied to the group
        public LvnCardArt card;  // the hub tile's art/description
        public List<string> titles; // ordered title ids in this collection
    }

    /// <summary>Card art + copy for a hub tile or a title's detail screen.</summary>
    public sealed class LvnCardArt
    {
        public string image;       // content url (big card image)
        public string description; // body text on the detail screen
    }

    /// <summary>A currency price (hub entry cost). Amount 0 = free.</summary>
    public sealed class LvnCost
    {
        public string currency; // e.g. "energy"
        public int amount;
    }

    /// <summary>One unlockable gallery CG.</summary>
    public sealed class LvnGalleryItem
    {
        /// <summary>Stable id the unlock is stored under — keep it constant across
        /// releases or players lose their unlocks.</summary>
        public string id;
        /// <summary>The CG's content url — must match the <c>bg</c> url that shows it.</summary>
        public string url;
        /// <summary>Optional caption shown in the gallery.</summary>
        public string name;
    }

    /// <summary>A season — an ordered group of chapters within a title.</summary>
    public sealed class LvnSeason
    {
        public List<LvnChapter> chapters;
    }

    /// <summary>One playable chapter and its release set.</summary>
    public sealed class LvnChapter
    {
        public string id;
        /// <summary>Sequence number within the title. The auto-continue / look-ahead
        /// logic orders by this (not array position), so out-of-order or pilot
        /// (number 0) entries don't break the chain.</summary>
        public int number;
        /// <summary>Episode display name ("Эпизод 3. …") — shown by the chapter
        /// picker and the Continue label. Optional; importers emit it.</summary>
        public string name;
        /// <summary>URL of the chapter's <c>.lvn</c> script.</summary>
        public string script_url;
        /// <summary>Loading-screen background, painted the instant the chapter opens.</summary>
        public string bg_url;
        /// <summary>The chapter's prioritized release set: content path →
        /// <see cref="LvnAssetMeta"/> (critical gates Play; the rest streams in
        /// during play). Fed to the <see cref="AssetScheduler"/>.</summary>
        public Dictionary<string, LvnAssetMeta> assets;
    }
}
