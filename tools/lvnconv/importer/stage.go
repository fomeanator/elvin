package importer

import (
	"regexp"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
)

// Slug turns a display name into an id/filename token (spaces → underscores).
func Slug(s string) string { return strings.ReplaceAll(strings.TrimSpace(s), " ", "_") }

// Localize moves every dialogue/option string out of the script into a catalog
// and replaces it with a text_id reference, returning the catalog (key → string).
// The key is the line's reimport-stable id (the articy GUID the front-end stamped
// on the command); a line without one falls back to keying on its own text. The
// catalog is written beside the script as <chapter>.<lang>.json, which the runtime
// loads per locale — so translating a novel is just shipping more catalogs against
// the same keys, the flow/choices/logic untouched.
//
// Run AFTER AutoStage: staging reads inline say text (scene markers), which this
// pass removes.
func Localize(doc *articy.Doc) map[string]string {
	catalog := map[string]string{}
	// suffix namespaces a choice caption's key. A choice option and the spoken
	// line share the SAME fragment stable id (the option's caption is the target
	// fragment's MenuText; the walked target is emitted as a say with its Text) —
	// so without a distinct suffix they collide on one catalog key and the button
	// ends up showing the full spoken line. "#opt" keeps them separate.
	move := func(m map[string]any, suffix string) {
		text, _ := m["text"].(string)
		if text == "" {
			return
		}
		key, _ := m["id"].(string)
		if key == "" {
			key = text // no stable id available — the text is its own key
		}
		key += suffix
		catalog[key] = text
		m["text_id"] = key
		delete(m, "text")
		delete(m, "id")
	}
	for _, c := range doc.Script {
		switch c["op"] {
		case "say":
			move(c, "")
		case "choice":
			if opts, ok := c["options"].([]any); ok {
				for _, o := range opts {
					if m, ok := o.(articy.Cmd); ok {
						move(m, "#opt")
					}
				}
			}
		}
	}
	return catalog
}

// StripStableIds removes the reimport-stable line ids the front-end stamps on
// says/options. They are only needed as localization keys; when a chapter is not
// localized they would just bloat the .lvn, so the non-localized pipeline drops
// them.
func StripStableIds(doc *articy.Doc) {
	for _, c := range doc.Script {
		switch c["op"] {
		case "say":
			delete(c, "id")
		case "choice":
			if opts, ok := c["options"].([]any); ok {
				for _, o := range opts {
					if m, ok := o.(articy.Cmd); ok {
						delete(m, "id")
					}
				}
			}
		}
	}
}

// ── auto-staging ─────────────────────────────────────────────────────────────

// AutoStage enriches a dialogue script with a first pass of staging by rule.
//
// Mobile single-speaker model: only the CURRENT speaker is on screen, centred.
// A character with art speaks → they fade in (and whoever was there fades out);
// the narrator or any speaker with no sprite → the stage clears. A scene-marker
// line ("Сцена N. <Location>") becomes a `bg` and clears the stage. Which roles
// narrate, which is the protagonist (staged on which side), and how a scene
// marker reads all come from the Template (nil → DefaultTemplate). The result is
// regular .lvn ops the author can refine in the editor.
func AutoStage(doc *articy.Doc, cast map[string]string, tpl *Template) {
	tpl = tpl.resolve()
	bgExt := tpl.Staging.BgExt
	if bgExt == "" {
		bgExt = ".jpg"
	}
	protagSide := tpl.Staging.ProtagonistSide
	if protagSide == "" {
		protagSide = "left"
	}
	npcSide := tpl.Staging.NpcSide
	if npcSide == "" {
		npcSide = "right"
	}

	out := make([]articy.Cmd, 0, len(doc.Script))
	current := ""    // the single character on screen ("" = empty stage)
	curEmotion := "" // the emotion the on-screen character was last shown with
	hide := func() {
		if current != "" {
			out = append(out, articy.Cmd{"op": "actor", "id": Slug(current), "show": false, "exit": "fade"})
			current, curEmotion = "", ""
		}
	}
	for _, c := range doc.Script {
		if c["op"] == "say" {
			text, _ := c["text"].(string)
			who, _ := c["who"].(string)
			if loc, ok := tpl.sceneMarkerMatch(text); ok {
				hide()
				id := Slug(loc)
				out = append(out, articy.Cmd{"op": "bg", "id": id, "sprite_url": "/content/bg/" + id + bgExt})
				continue // a scene marker is a background, not a spoken line
			}
			if spr, ok := cast[who]; ok && !tpl.isNarrator(who) {
				// a character with a sprite speaks → show ONLY them: the protagonist on
				// their side, everyone else on the other (the usual mobile framing). The
				// fragment's marker emotion rides onto the actor; a re-show is also
				// emitted when the SAME speaker changes to a new (non-empty) emotion, so
				// the expression tracks the dialogue without ever putting two up at once.
				emo, _ := c["emotion"].(string)
				if who != current || (emo != "" && emo != curEmotion) {
					side := npcSide
					if tpl.isProtagonist(who) {
						side = protagSide
					}
					if who != current {
						hide() // swap out whoever was there
					}
					actor := articy.Cmd{"op": "actor", "id": Slug(who), "show": true,
						"position": side, "sprite_url": "/content/art/" + spr, "enter": "fade"}
					if emo != "" {
						actor["emotion"] = emo
					}
					out = append(out, actor)
					current, curEmotion = who, emo
				}
			} else {
				hide() // narrator / off-screen voice → clear the stage
			}
		}
		out = append(out, c)
	}
	doc.Script = out
}

// ── filename matching (disk ↔ .lvn) ──────────────────────────────────────────

// hashSuffixRe strips articy's "(ABCD)" hex tag from an exported asset filename:
// "Тимур_Обычный(00A9)" → "Тимур_Обычный".
var hashSuffixRe = regexp.MustCompile(`\([0-9A-Fa-f]+\)$`)

func stripHash(base string) string { return hashSuffixRe.ReplaceAllString(base, "") }

// normKey folds a name to a match key that is stable across Unicode forms and
// case. macOS stores filenames decomposed (NFD) while the .adpd strings are
// composed (NFC); in Cyrillic only й and ё differ between the two. We canonicalise
// by decomposing those precomposed letters and dropping combining marks, so an
// NFC "Тимур_Обычный" and an NFD one collapse to the same key.
func normKey(s string) string {
	var b strings.Builder
	for _, r := range s {
		switch r {
		case 'й':
			r = 'и'
		case 'Й':
			r = 'И'
		case 'ё':
			r = 'е'
		case 'Ё':
			r = 'Е'
		}
		if r >= 0x0300 && r <= 0x036F { // combining diacritical marks
			continue
		}
		b.WriteRune(r)
	}
	return strings.ToLower(strings.TrimSpace(b.String()))
}
