package importer

// template.go makes the articy → novel importer configurable per project.
//
// Every authoring convention the pipeline used to hardcode for the first bundle
// novel ("Холодный расчёт") — role names that clear the stage, the scene-marker
// pattern, the wardrobe variable layout, audio-cue variable prefixes, staging
// sides, premium pricing — now lives in a Template. DefaultTemplate() holds those
// original conventions, so nothing changes for that novel; a project whose articy
// export uses different names ships its own JSON template (LoadTemplate) instead
// of forking the importer.
//
// A template file is overlay-by-presence: it starts from DefaultTemplate() and a
// JSON file overrides only the fields it states (LoadTemplate json.Unmarshals into
// the populated default). So a template that only renames the protagonist need
// say nothing about audio or emotions.

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
)

// Template captures every novel-authoring convention the articy importer keys
// off. Resolve it through (*Template).resolve() before use — that fills a nil
// receiver with DefaultTemplate() and compiles the derived lookups (regex, role
// sets). Callers that received a Template from LoadTemplate/ResolveTemplate get
// an already-compiled one.
type Template struct {
	Name string `json:"name"`

	Staging     StagingTemplate    `json:"staging"`
	Wardrobe    WardrobeTemplate   `json:"wardrobe"`
	Audio       []AudioCueTemplate `json:"audio"`
	Backgrounds BackgroundTemplate `json:"backgrounds"`
	Stats       StatsTemplate      `json:"stats"`

	// VarChapterPrefixes marks variable-name prefixes as CHAPTER-scoped: they
	// land in the declaration's "chapter" block and reset on every fresh
	// chapter entry (Temp.* by convention). Everything else is game-scoped.
	VarChapterPrefixes []string `json:"var_chapter_prefixes,omitempty"`

	// EmotionColors overlays the marker-colour → emotion legend (#rrggbb → token).
	// Merged over the built-in legend (emotion.go) and under Options.EmotionColors.
	EmotionColors map[string]string `json:"emotion_colors,omitempty"`

	// SpeakerNames maps an articy/roster speaker label to the DISPLAY name the
	// nameplate shows ("Bandit" → "Бандит"). Applied to say `who` (the actor id
	// keeps riding who_id, so highlighting is unaffected) and to the matching
	// entity's display name. Empty → labels pass through as authored.
	SpeakerNames map[string]string `json:"speaker_names,omitempty"`

	// VarAliases maps a variable-name PREFIX to its canonical spelling —
	// the content-typo rescue. Live case: Cold's chapters 1-2/5-6 write
	// `Relationship.Matvey` (singular) while the declared namespace, the
	// stat bars, and every later chapter use `Relationships.` — so early
	// relationship points vanished into an undeclared variable and the
	// partner's bars sat at 0 through chapter 1. Applied to set/inc keys
	// and expressions at import; the articy source still deserves the real
	// fix, this keeps every REIMPORT correct meanwhile.
	VarAliases map[string]string `json:"var_aliases,omitempty"`

	// SpeakerAliases maps an ALIAS articy speaker label to the CANONICAL
	// roster label whose art it should resolve through — the manual "these N
	// labels are the same person" declaration a name-normalizing heuristic can
	// only SUGGEST, never safely apply on its own (a project can legitimately
	// author the protagonist's art under a short roster nickname — "ГГ" —
	// while every dialogue line uses the full "Главный герой" label; nothing
	// short of an author saying so can tell that apart from two distinct
	// characters who happen to share an abbreviation). Only affects cast (art)
	// lookups — role classification (narrator/protagonist/npc) still comes
	// from narrator_roles/protagonist_roles/protagonist_speaker_labels as
	// today, so list every observed label there too.
	SpeakerAliases map[string]string `json:"speaker_aliases,omitempty"`

	// Derived (built by compile()); never serialized.
	sceneMarker    *regexp.Regexp
	narratorSet    map[string]bool
	protagSet      map[string]bool
	protagSpeakSet map[string]bool
}

// StagingTemplate governs AutoStage: who clears the stage, who the player
// character is, which side each stands on, and how a scene marker reads.
type StagingTemplate struct {
	// NarratorRoles never get an on-stage sprite — their line clears the stage
	// (narrator, choice echo, articy bookkeeping "speakers", UI plates).
	NarratorRoles []string `json:"narrator_roles"`
	// ProtagonistRoles are the player character's dialogue labels — staged on
	// ProtagonistSide when they have a sprite.
	ProtagonistRoles []string `json:"protagonist_roles"`
	ProtagonistSide  string   `json:"protagonist_side"` // "left"
	NpcSide          string   `json:"npc_side"`         // "right"

	// SceneMarkerRegex matches a narration line that is really a background cue,
	// capturing the location name in group 1 (e.g. "Сцена 3. Двор" → "Двор").
	SceneMarkerRegex string `json:"scene_marker_regex"`
	BgExt            string `json:"bg_ext"` // ".jpg" — extension AutoStage's bg url uses

	// ProtagonistLabel is the dialogue label the protagonist is staged under when
	// she has no articy avatar (bundle convention): AutoStage stages her, and the
	// on-stage actor id is Slug(ProtagonistLabel).
	ProtagonistLabel string `json:"protagonist_label"` // "Главный герой"
	// ProtagonistSpeakerLabels are the articy labels whose say `who` is rewritten
	// to PlayerTemplate — the visible name is the one the player entered.
	ProtagonistSpeakerLabels []string `json:"protagonist_speaker_labels"`
	PlayerVar                string   `json:"player_var"`      // "player"
	PlayerTemplate           string   `json:"player_template"` // "{player}"
	// ProtagonistMirror faces the protagonist into the scene (her exported art
	// faces the opposite way to her staging side).
	ProtagonistMirror bool `json:"protagonist_mirror"`

	// PlaceholderProtagonist stages a labelled grey placeholder for a
	// protagonist role that has no roster art, instead of leaving her
	// invisible. ON by default — per the partner (who has shipped these
	// novels commercially): "новелл без героев не бывает, это дыры в
	// файлах" — a spriteless protagonist is a content gap to surface, not a
	// first-person design to respect. The placeholder makes the hole
	// visible in one glance; an author who truly wants an invisible
	// protagonist sets placeholder_protagonist=false in the template.
	PlaceholderProtagonist *bool `json:"placeholder_protagonist,omitempty"`
}

// WardrobeTemplate governs the wardrobe substitution: which story flag opens the
// in-script wardrobe, the variable layout of the outfit catalog, and pricing.
type WardrobeTemplate struct {
	FlagKey   string `json:"flag_key"`   // "Open.Wardrobe" — opens/closes the block
	VarPrefix string `json:"var_prefix"` // "Wardrobe." — outfit variable namespace

	ProtagonistHairVar    string `json:"protagonist_hair_var"`    // "Wardrobe.mainCh_Hair"
	ProtagonistClothesVar string `json:"protagonist_clothes_var"` // "Wardrobe.mainCh_Clothes"

	HairLabel      string `json:"hair_label"`       // "Причёска"
	HairInfix      string `json:"hair_infix"`       // "hair" — art file infix
	OutfitLabel    string `json:"outfit_label"`     // "Одежда"
	OutfitInfix    string `json:"outfit_infix"`     // "clothes"
	NpcOutfitLabel string `json:"npc_outfit_label"` // "Наряд"

	Premium PremiumTemplate `json:"premium"`
}

// PremiumTemplate prices a wardrobe item flagged "[premium]" in the catalog.
type PremiumTemplate struct {
	Currency string `json:"currency"` // "crystals"
	Price    int    `json:"price"`    // 20
	Rarity   string `json:"rarity"`   // "rare"
}

// AudioCueTemplate turns a truthy `set <VarPrefix><name>=true` into an audio play
// op on Channel, playing PathPrefix+name+Ext.
type AudioCueTemplate struct {
	VarPrefix  string `json:"var_prefix"`  // "Music." / "Sound."
	Channel    string `json:"channel"`     // "music" / "sfx"
	PathPrefix string `json:"path_prefix"` // "/content/audio/music/"
	Ext        string `json:"ext"`         // ".ogg"
}

// BackgroundTemplate governs real-фон matching.
type BackgroundTemplate struct {
	// MinFileSize skips small files as autostage placeholders, not real art.
	MinFileSize int64 `json:"min_file_size"` // 200000
}

// StatsTemplate declares which global variables surface as player-facing
// stats on the title detail page (and the in-game stats panel): bipolar trait
// pairs (one proportional bar each, no fixed max — it fills by pos/(pos+neg))
// and a per-character relationship namespace (one bar per declared Integer
// variable, auto-discovered from this project's own GlobalVariables export —
// no roster to hand-maintain when a character is added or renamed).
type StatsTemplate struct {
	Pairs []StatPairTemplate `json:"pairs,omitempty"`

	// RelationshipNamespace is the GlobalVariables Set name whose Integer
	// member variables each become one stat ("Relationships" → one bar per
	// character). Empty disables relationship stats entirely.
	RelationshipNamespace string `json:"relationship_namespace,omitempty"`
	// RelationshipMax is the display ceiling for the relationship bars —
	// cosmetic only, never clamps or gates the underlying value.
	RelationshipMax int `json:"relationship_max,omitempty"`
}

// StatPairTemplate is one bipolar trait axis ("Спокойствие" ↔ "Ярость"):
// two counters whose relative weight renders as one bar.
type StatPairTemplate struct {
	PosKey   string `json:"pos_key"`
	PosLabel string `json:"pos_label"`
	NegKey   string `json:"neg_key"`
	NegLabel string `json:"neg_label"`
}

// TitleStat is one entry of the manifest's title.stats (Title.PlayerStats) —
// either a bipolar pair or a single 0..Max meter.
type TitleStat struct {
	Kind         string `json:"kind"` // "pair" | "single"
	Key          string `json:"key,omitempty"`
	Label        string `json:"label,omitempty"`
	Max          int    `json:"max,omitempty"`
	Relationship bool   `json:"relationship,omitempty"`
	PosKey       string `json:"pos_key,omitempty"`
	PosLabel     string `json:"pos_label,omitempty"`
	NegKey       string `json:"neg_key,omitempty"`
	NegLabel     string `json:"neg_label,omitempty"`
}

// TitleStats flattens Stats into the manifest's title.stats list: the
// declared trait pairs, then one relationship stat per Integer variable this
// project actually declares under RelationshipNamespace — so a character
// added or renamed in articy shows up (or disappears) without touching the
// template. The label comes from SpeakerNames (falls back to the raw
// variable name when the roster has no display name for it).
func (t *Template) TitleStats(globalVars []articy.GlobalVarInfo) []TitleStat {
	t = t.resolve()
	var out []TitleStat
	for _, p := range t.Stats.Pairs {
		if p.PosKey == "" || p.NegKey == "" {
			continue
		}
		out = append(out, TitleStat{
			Kind: "pair", PosKey: p.PosKey, PosLabel: p.PosLabel, NegKey: p.NegKey, NegLabel: p.NegLabel,
		})
	}
	if t.Stats.RelationshipNamespace != "" {
		max := t.Stats.RelationshipMax
		if max <= 0 {
			max = 20
		}
		for _, gv := range globalVars {
			if gv.Namespace != t.Stats.RelationshipNamespace || gv.Type != "Integer" {
				continue
			}
			label := t.SpeakerNames[gv.Variable]
			if label == "" {
				label = gv.Variable
			}
			out = append(out, TitleStat{
				Kind: "single", Key: gv.Namespace + "." + gv.Variable, Label: label,
				Max: max, Relationship: true,
			})
		}
	}
	return out
}

// DefaultTemplate returns the built-in "cold" template — the authoring
// conventions of the first bundle novel ("Холодный расчёт"). It is the fallback
// for every import that doesn't select another template, so the pipeline's
// default behaviour is unchanged. Returns a fresh, already-compiled pointer.
func DefaultTemplate() *Template {
	t := &Template{
		Name: "cold",
		Staging: StagingTemplate{
			NarratorRoles: []string{
				"Автор", "Игрок", "Выбор пути", "Информация",
				"Эпизод", "Отношения", "Туториал", "Смена ГГ", "Подсказка",
			},
			ProtagonistRoles:         []string{"Главный герой", "ГГ"},
			ProtagonistSide:          "left",
			NpcSide:                  "right",
			SceneMarkerRegex:         `^\s*Сцена\s+\d+\.\s*(.+?)\.?\s*$`,
			BgExt:                    ".jpg",
			ProtagonistLabel:         "Главный герой",
			ProtagonistSpeakerLabels: []string{"Главный герой", "ГГ", "Игрок"},
			PlayerVar:                "player",
			PlayerTemplate:           "{player}",
			ProtagonistMirror:        true,
		},
		VarChapterPrefixes: []string{"Temp."},
		Wardrobe: WardrobeTemplate{
			FlagKey:               "Open.Wardrobe",
			VarPrefix:             "Wardrobe.",
			ProtagonistHairVar:    "Wardrobe.mainCh_Hair",
			ProtagonistClothesVar: "Wardrobe.mainCh_Clothes",
			HairLabel:             "Причёска",
			HairInfix:             "hair",
			OutfitLabel:           "Одежда",
			OutfitInfix:           "clothes",
			NpcOutfitLabel:        "Наряд",
			Premium:               PremiumTemplate{Currency: "crystals", Price: 20, Rarity: "rare"},
		},
		Audio: []AudioCueTemplate{
			{VarPrefix: "Music.", Channel: "music", PathPrefix: "/content/audio/music/", Ext: ".ogg"},
			{VarPrefix: "Sound.", Channel: "sfx", PathPrefix: "/content/audio/sfx/", Ext: ".ogg"},
		},
		Backgrounds: BackgroundTemplate{MinFileSize: 200000},
		// The marker-colour → emotion legend, made explicit on the template so it is
		// self-describing and overridable per project. Identical to emotion.go's
		// built-in default legend (which remains the base emotionTable overlays).
		EmotionColors: map[string]string{
			"ffff00": "surprised",
			"00b050": "happy",
			"d6006e": "flirt",
			"0c0c0c": "fear",
			"7030a0": "sad",
			"0070c0": "thoughtfulness",
		},
	}
	_ = t.compile()
	return t
}

// resolve returns a usable template: DefaultTemplate() for a nil receiver, and a
// self-compiled one otherwise (so a hand-built Template works without a prior
// LoadTemplate). Cheap to call repeatedly — compile is idempotent per pointer.
func (t *Template) resolve() *Template {
	if t == nil {
		return DefaultTemplate()
	}
	if t.sceneMarker == nil || t.narratorSet == nil {
		_ = t.compile()
	}
	return t
}

// compile builds the derived lookups (scene-marker regex + role sets) from the
// declarative fields. Called by DefaultTemplate/LoadTemplate/resolve.
func (t *Template) compile() error {
	pat := t.Staging.SceneMarkerRegex
	if pat == "" {
		pat = `^\s*Сцена\s+\d+\.\s*(.+?)\.?\s*$`
	}
	re, err := regexp.Compile(pat)
	if err != nil {
		return fmt.Errorf("scene_marker_regex %q: %w", pat, err)
	}
	t.sceneMarker = re
	t.narratorSet = toSet(t.Staging.NarratorRoles)
	t.protagSet = toSet(t.Staging.ProtagonistRoles)
	t.protagSpeakSet = toSet(t.Staging.ProtagonistSpeakerLabels)
	// Resolve SpeakerNames chains (A→B, B→C) to their fixed point once, so
	// applying the map any number of times gives the same answer. The rename
	// passes legitimately run twice on a bundle import (Run on doc.Script,
	// PostProcessBundle on the compiled .lvn) — without this a chained config
	// produced A→B on the first pass and B→C on the second, i.e. the result
	// depended on which import path you took. Cycles stop at len(map) hops.
	if len(t.SpeakerNames) > 0 {
		resolved := make(map[string]string, len(t.SpeakerNames))
		for k, v := range t.SpeakerNames {
			seen := 0
			for {
				next, ok := t.SpeakerNames[v]
				if !ok || next == v || seen >= len(t.SpeakerNames) {
					break
				}
				v = next
				seen++
			}
			resolved[k] = v
		}
		t.SpeakerNames = resolved
	}
	return nil
}

func toSet(ss []string) map[string]bool {
	m := make(map[string]bool, len(ss))
	for _, s := range ss {
		if s != "" {
			m[s] = true
		}
	}
	return m
}

// ── role queries ─────────────────────────────────────────────────────────────

func (t *Template) isNarrator(who string) bool      { return t.resolve().narratorSet[who] }
func (t *Template) isProtagonist(who string) bool   { return t.resolve().protagSet[who] }
func (t *Template) isProtagSpeaker(who string) bool { return t.resolve().protagSpeakSet[who] }

// sceneMarkerMatch returns the captured location of a scene-marker line, or "".
func (t *Template) sceneMarkerMatch(text string) (string, bool) {
	m := t.resolve().sceneMarker.FindStringSubmatch(text)
	if m == nil || len(m) < 2 {
		return "", false
	}
	return m[1], true
}

// protagonistStageID is the on-stage actor id the protagonist is staged under
// (Slug of the staging label). Empty when the template sets no label.
func (t *Template) protagonistStageID() string {
	return Slug(strings.TrimSpace(t.resolve().Staging.ProtagonistLabel))
}

// protagonistActorIDs are every distinct on-stage actor id the protagonist can
// appear under: her StoryName slug plus the staging-label slug. Unifies the sites
// that used to spell out Slug("Главный герой") and the story id side by side.
func (t *Template) protagonistActorIDs(storyName string) []string {
	seen := map[string]bool{}
	var out []string
	for _, id := range []string{Slug(strings.TrimSpace(storyName)), t.protagonistStageID()} {
		if id != "" && !seen[id] {
			seen[id] = true
			out = append(out, id)
		}
	}
	return out
}

// ── loading & registry ───────────────────────────────────────────────────────

// ParseTemplateJSON overlays raw JSON onto DefaultTemplate() and compiles it —
// the byte-oriented twin of LoadTemplate, for callers that already hold the
// template body in memory (the admin CRUD PUT validator, a detect-roles
// unsaved-draft preview) instead of a file path.
func ParseTemplateJSON(data []byte) (*Template, error) {
	t := DefaultTemplate()
	if err := json.Unmarshal(data, t); err != nil {
		return nil, fmt.Errorf("template: %w", err)
	}
	if err := t.compile(); err != nil {
		return nil, fmt.Errorf("template: %w", err)
	}
	return t, nil
}

// LoadTemplate reads a JSON template file and overlays it onto DefaultTemplate()
// (overlay-by-presence: only fields the file states override the defaults), then
// compiles it. So a partial template file is valid and inherits the rest.
func LoadTemplate(path string) (*Template, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	t, err := ParseTemplateJSON(b)
	if err != nil {
		return nil, fmt.Errorf("template %s: %w", filepath.Base(path), err)
	}
	return t, nil
}

// ResolveTemplate selects the template for an import from a name-or-path:
//
//   - "" or "default"/"cold"        → the built-in DefaultTemplate();
//   - a path to an existing file    → LoadTemplate(that file);
//   - a bare name and a non-empty dir → LoadTemplate(dir/<name>.json).
//
// So the server/CLI can offer both a built-in default and drop-in <name>.json
// templates without any per-novel code.
func ResolveTemplate(nameOrPath, dir string) (*Template, error) {
	s := strings.TrimSpace(nameOrPath)
	if s == "" || s == "default" {
		s = "cold" // the built-in default's name
	}
	// An explicit path (has a separator or a .json suffix and exists) loads directly.
	if strings.HasSuffix(s, ".json") || strings.ContainsAny(s, `/\`) {
		return LoadTemplate(s)
	}
	// THE AUTHOR'S FILE ALWAYS WINS: a <name>.json under the templates dir
	// overrides a built-in of the same name. The code's DefaultTemplate is only
	// the fallback when no file exists — author conventions are content, not
	// code (live-hit: editing cold.json's emotion legend silently did nothing
	// because "cold" short-circuited to the built-in without reading the file).
	if dir != "" {
		p := filepath.Join(dir, s+".json")
		if _, err := os.Stat(p); err == nil {
			return LoadTemplate(p)
		}
	}
	if s == "cold" {
		return DefaultTemplate(), nil
	}
	return nil, fmt.Errorf("unknown import template %q (no built-in and no %s.json under templates dir)", s, s)
}
