package importer

// detect.go previews what an import WOULD do — every speaker's role, art
// status and line count, plus scene-marker/emotion-legend hit rates and
// heuristic alias-collision suggestions — without writing anything. It is
// the data source behind the panel's import-mapper screen: an author sees
// exactly what the Template does and doesn't already handle for THIS
// project before committing to a full import.

import (
	"fmt"
	"sort"
	"strings"
	"unicode"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/adpd"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
)

// loadAllDocs converts a project to its per-chapter articy.Doc set using the
// SAME resolution Run/runMultiChapter use (BuildChaptersJSONReport first,
// BuildExportJSONReport(-1,0) fallback) — a SIMPLIFIED, read-only duplicate
// (no reachesEnd/chapterFallback bookkeeping) built ONLY for DetectRoles'
// preview. Deliberately NOT wired back into Run/runMultiChapter: those carry
// fallback semantics (a chapter that can't reach an ending collapses back to
// one whole-novel chapter) this read-only preview doesn't need, and reusing
// them here would risk an import-path regression for a reporting feature.
func loadAllDocs(projectDir string) ([]*articy.Doc, int, error) {
	if chs, _, err := adpd.BuildChaptersJSONReport(projectDir); err == nil && len(chs) >= 2 {
		docs := make([]*articy.Doc, 0, len(chs))
		for i, ch := range chs {
			doc, cerr := articy.Convert(ch.JSON, "")
			if cerr != nil {
				return nil, 0, fmt.Errorf("chapter %d convert: %w", i+1, cerr)
			}
			docs = append(docs, doc)
		}
		return docs, len(docs), nil
	}
	js, _, err := adpd.BuildExportJSONReport(projectDir, -1, 0)
	if err != nil {
		return nil, 0, fmt.Errorf("adpd export: %w", err)
	}
	doc, err := articy.Convert(js, "")
	if err != nil {
		return nil, 0, fmt.Errorf("articy convert: %w", err)
	}
	return []*articy.Doc{doc}, 1, nil
}

// SpeakerDetect is one distinct say `who` this project uses. Narrator-role
// speakers are INCLUDED (role:"narrator") so the mapper can reclassify them
// back — they additionally feed the SceneMarker aggregates.
type SpeakerDetect struct {
	Who         string   `json:"who"`
	LineCount   int      `json:"line_count"`
	HasArt      bool     `json:"has_art"`
	ArtStem     string   `json:"art_stem,omitempty"`
	Role        string   `json:"role"` // "narrator" | "protagonist" | "npc"
	SampleLines []string `json:"sample_lines,omitempty"`
}

// AliasCollision is a HEURISTIC suggestion, never an automatic merge: two
// speakers with mismatched art status whose labels look like the same
// person (a roster nickname like "ГГ" vs a dialogue label like "Главный
// герой"). The mapper UI shows it as a pre-filled suggestion; the author
// confirms by writing Template.SpeakerAliases.
type AliasCollision struct {
	A       string `json:"a"`
	B       string `json:"b"`
	AHasArt bool   `json:"a_has_art"`
	BHasArt bool   `json:"b_has_art"`
	Reason  string `json:"reason"` // normalized-name-match | initials-match | substring-match
}

// DetectReport is DetectRoles' full result.
type DetectReport struct {
	ProjectDir   string          `json:"project_dir"`
	TemplateName string          `json:"template_name"`
	Chapters     int             `json:"chapters"`
	Speakers     []SpeakerDetect `json:"speakers"`

	SceneMarkerCandidates int     `json:"scene_marker_candidates"` // narrator-role say lines
	SceneMarkerMatches    int     `json:"scene_marker_matches"`
	SceneMarkerHitRate    float64 `json:"scene_marker_hit_rate"`
	// SceneMarkerMisses is a handful of REAL narrator lines that did NOT match
	// the current scene_marker_regex — raw material for the mapper's example-
	// based pattern builder (an author picks the location substring out of a
	// real line instead of writing regex by hand).
	SceneMarkerMisses []string `json:"scene_marker_misses,omitempty"`

	EmotionLinesTotal  int         `json:"emotion_lines_total"`
	EmotionLinesMapped int         `json:"emotion_lines_mapped"`
	EmotionColorMisses []ColorStat `json:"emotion_color_misses,omitempty"`

	ProtagonistWithoutArt []string         `json:"protagonist_without_art,omitempty"`
	AliasCollisions       []AliasCollision `json:"alias_collisions,omitempty"`
	Warnings              []string         `json:"warnings,omitempty"`
}

// DetectRoles previews a Template's classification against a project WITHOUT
// writing anything: every speaker's line count/art/current role, scene-marker
// and emotion-legend hit rates, and heuristic alias collisions. tpl == nil →
// DefaultTemplate(), the same convention as every other importer entry point
// — pass an unsaved DRAFT template (ParseTemplateJSON) to preview edits
// before saving them. Reuses adpd.Cast plus the same load path Run/
// runMultiChapter use (loadAllDocs), so a detect run reports exactly what an
// import would do.
func DetectRoles(projectDir string, tpl *Template) (*DetectReport, error) {
	tpl = tpl.resolve()
	cast, err := adpd.Cast(projectDir)
	if err != nil {
		return nil, fmt.Errorf("adpd cast: %w", err)
	}
	applySpeakerAliasesToCast(cast, tpl)

	docs, chapters, err := loadAllDocs(projectDir)
	if err != nil {
		return nil, err
	}

	type agg struct {
		lines   int
		samples []string
	}
	speakers := map[string]*agg{}
	var order []string
	sceneCandidates, sceneMatches := 0, 0
	var sceneMisses []string
	probe := newColorProbe(tpl.EmotionColors)

	for _, doc := range docs {
		probe.scan(doc)
		for _, c := range doc.Script {
			if c["op"] != "say" {
				continue
			}
			who, _ := c["who"].(string)
			if who == "" {
				continue
			}
			text, _ := c["text"].(string)
			if tpl.isNarrator(who) {
				sceneCandidates++
				if _, ok := tpl.sceneMarkerMatch(text); ok {
					sceneMatches++
				} else if len(sceneMisses) < 8 && text != "" {
					sceneMisses = append(sceneMisses, text)
				}
				// NO continue: narrator speakers still get a Speakers row.
				// Excluding them made the mapper's "Рассказчик" role a
				// one-way trap — assign it, hit "Пересчитать", and the row
				// vanished with no UI way back.
			}
			a, ok := speakers[who]
			if !ok {
				a = &agg{}
				speakers[who] = a
				order = append(order, who)
			}
			a.lines++
			if len(a.samples) < 3 && text != "" {
				a.samples = append(a.samples, text)
			}
		}
	}
	// Highest-volume speakers first: the narrator/protagonist a project's
	// OWN vocabulary doesn't match (a different language or convention than
	// the active template) are almost always the top few by line count —
	// sorting this way surfaces them to reclassify immediately instead of
	// burying them alphabetically among ordinary NPCs.
	sort.Slice(order, func(i, j int) bool {
		li, lj := speakers[order[i]].lines, speakers[order[j]].lines
		if li != lj {
			return li > lj
		}
		return order[i] < order[j]
	})

	rep := &DetectReport{
		ProjectDir: projectDir, TemplateName: tpl.Name, Chapters: chapters,
		SceneMarkerCandidates: sceneCandidates, SceneMarkerMatches: sceneMatches,
		SceneMarkerMisses: sceneMisses,
	}
	if sceneCandidates > 0 {
		rep.SceneMarkerHitRate = float64(sceneMatches) / float64(sceneCandidates)
	}
	for _, cs := range probe.stats() {
		rep.EmotionLinesTotal += cs.Count
		if cs.Emotion != "" {
			rep.EmotionLinesMapped += cs.Count
		} else {
			rep.EmotionColorMisses = append(rep.EmotionColorMisses, cs)
		}
	}

	// Never nil: the mapper iterates report.speakers, and a project whose
	// every speaker is narrator-classified used to marshal `"speakers":null`.
	rep.Speakers = []SpeakerDetect{}
	for _, who := range order {
		a := speakers[who]
		spr, ok := cast[who]
		hasArt := ok && spr != "" && spr != sentinelNoArt
		role := "npc"
		switch {
		case tpl.isNarrator(who):
			role = "narrator"
		case tpl.isProtagonist(who):
			role = "protagonist"
		}
		sd := SpeakerDetect{Who: who, LineCount: a.lines, HasArt: hasArt, Role: role, SampleLines: a.samples}
		if hasArt {
			sd.ArtStem = spr
		}
		rep.Speakers = append(rep.Speakers, sd)
		if role == "protagonist" && !hasArt {
			rep.ProtagonistWithoutArt = append(rep.ProtagonistWithoutArt, who)
		}
	}

	rep.AliasCollisions = detectAliasCollisions(rep.Speakers, cast)
	if len(rep.ProtagonistWithoutArt) > 0 {
		// Ambiguous by design, not a defect: a spriteless protagonist could be
		// a genuine art gap (fix with an alias, or turn on
		// staging.placeholder_protagonist for a placeholder box) OR a
		// deliberate first-person/self-insert novel where she's meant to
		// stay invisible — nothing in the script distinguishes the two, so
		// this is only ever a heads-up, never an auto-applied fix.
		rep.Warnings = append(rep.Warnings, fmt.Sprintf(
			"protagonist role(s) with no roster art: %s — she'll stay invisible on stage (may be intentional for a first-person novel); if this is really a missing portrait, add a speaker_alias to real art or set staging.placeholder_protagonist=true for a labelled placeholder",
			strings.Join(rep.ProtagonistWithoutArt, ", ")))
	}
	if sceneCandidates > 0 && rep.SceneMarkerHitRate < 0.5 {
		rep.Warnings = append(rep.Warnings, fmt.Sprintf(
			"scene_marker_regex matches only %.0f%% of narrator lines — check staging.scene_marker_regex against this project's convention",
			rep.SceneMarkerHitRate*100))
	}
	return rep, nil
}

// detectAliasCollisions flags name pairs with DIFFERENT art status whose
// labels look like the same person — only a suggestion, never applied.
// Candidates come from two sources: script speakers (may lack art) and the
// raw cast/roster (every name WITH art, including one a script never
// actually speaks under) — the Soviet novel's protagonist is exactly this
// case, her portrait registered under a short roster nickname ("ГГ") that
// every dialogue line bypasses in favour of the full "Главный герой" label,
// so comparing speakers against EACH OTHER alone would never see it: "ГГ"
// never appears as a say `who` at all, only in the roster.
func detectAliasCollisions(speakers []SpeakerDetect, cast map[string]string) []AliasCollision {
	var out []AliasCollision
	seen := map[[2]string]bool{}
	add := func(a, b string, aArt, bArt bool) {
		if a == "" || b == "" || a == b {
			return
		}
		key := [2]string{a, b}
		if a > b {
			key = [2]string{b, a}
		}
		if seen[key] {
			return
		}
		reason := ""
		switch {
		case normKey(a) == normKey(b):
			reason = "normalized-name-match"
		case initials(a) == b || initials(b) == a:
			reason = "initials-match" // "Главный герой" → "ГГ"
		case looseContains(a, b):
			reason = "substring-match"
		}
		if reason == "" {
			return
		}
		seen[key] = true
		out = append(out, AliasCollision{A: a, B: b, AHasArt: aArt, BHasArt: bArt, Reason: reason})
	}

	artNames := map[string]bool{}
	for k, v := range cast {
		if v != "" && v != sentinelNoArt {
			artNames[k] = true
		}
	}
	for _, s := range speakers {
		if s.HasArt || s.Role == "narrator" {
			continue // resolved, or a narrator (aliases are meaningless there)
		}
		for name := range artNames {
			add(s.Who, name, s.HasArt, true)
		}
	}
	// Two SPEAKERS (both appear in dialogue) with mismatched art status —
	// covers pairs neither of which has a standalone roster-only alias.
	for i := 0; i < len(speakers); i++ {
		for j := i + 1; j < len(speakers); j++ {
			a, b := speakers[i], speakers[j]
			if a.HasArt != b.HasArt {
				add(a.Who, b.Who, a.HasArt, b.HasArt)
			}
		}
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].A != out[j].A {
			return out[i].A < out[j].A
		}
		return out[i].B < out[j].B
	})
	return out
}

func initials(s string) string {
	var b strings.Builder
	for _, w := range strings.Fields(s) {
		r := []rune(w)
		if len(r) > 0 {
			b.WriteRune(unicode.ToUpper(r[0]))
		}
	}
	return b.String()
}

func looseContains(a, b string) bool {
	if len([]rune(a)) < 3 || len([]rune(b)) < 3 {
		return false
	}
	la, lb := strings.ToLower(a), strings.ToLower(b)
	return strings.Contains(la, lb) || strings.Contains(lb, la)
}
