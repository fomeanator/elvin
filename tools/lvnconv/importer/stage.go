package importer

import (
	"fmt"
	"regexp"
	"sort"
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

// PricePremiumChoices turns the converter's neutral "premium" option marker
// into a REAL price from the project template: a display cost line plus a
// wallet_cost the runtime spends through the host wallet before the pick goes
// through. Without a configured premium price the marker is dropped and the
// option stays free — direct converts keep their historical behaviour.
func PricePremiumChoices(doc *articy.Doc, tpl *Template) {
	p := tpl.resolve().Wardrobe.Premium
	for _, c := range doc.Script {
		if c["op"] != "choice" {
			continue
		}
		opts, _ := c["options"].([]any)
		for _, o := range opts {
			opt, ok := o.(articy.Cmd)
			if !ok {
				if m, ok2 := o.(map[string]any); ok2 {
					opt = m
				} else {
					continue
				}
			}
			if opt["premium"] != true {
				continue
			}
			delete(opt, "premium")
			if p.Currency == "" || p.Price <= 0 {
				continue
			}
			opt["cost"] = fmt.Sprintf("%d %s", p.Price, p.Currency)
			opt["wallet_cost"] = map[string]any{"currency": p.Currency, "amount": p.Price}
		}
	}
}

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
				// The say carries its actor id EXPLICITLY: the runtime highlights
				// (undims) the speaker by who_id, not by matching the display name.
				// Without it the protagonist never lit back up — her `who` becomes
				// "{player}" (the entered name) and can't match the staged id.
				c["who_id"] = Slug(who)
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
	doc.Script = normalizeStageVisibility(out)
}

// normalizeStageVisibility makes the single-speaker staging robust to CONTROL
// FLOW. AutoStage balances shows/hides in physical array order, but a choice
// branch that re-shows an actor and then `goto`s into a shared tail leaves that
// actor on screen when the tail shows a DIFFERENT one — the "two characters
// standing inside each other" bug (the importer drops the branch's exit hide on
// the merge). A forward data-flow over the real edges (goto / if.then/else /
// choice option gotos, plus fall-through) computes the set of actors that may
// be visible entering each command; before every `actor show:true id=Y` it
// injects an instant `show:false` for any OTHER possibly-visible actor. On the
// path where that actor is up it hides (fixing the collision); on the path
// where it isn't, hiding a not-shown actor is a runtime no-op — so the fix costs
// nothing on the clean paths and only fires exactly where branches merge.
func normalizeStageVisibility(script []articy.Cmd) []articy.Cmd {
	n := len(script)
	if n == 0 {
		return script
	}
	labelAt := map[string]int{}
	for i, c := range script {
		if c["op"] == "label" {
			if id, _ := c["id"].(string); id != "" {
				labelAt[id] = i
			}
		}
	}
	target := func(lbl string) (int, bool) { j, ok := labelAt[lbl]; return j, ok }

	// Successor edges. goto/if/choice reroute (no fall-through); everything
	// else falls through to i+1.
	succ := make([][]int, n)
	for i, c := range script {
		switch c["op"] {
		case "goto":
			if j, ok := target(str(c["label"])); ok {
				succ[i] = []int{j}
			}
		case "if":
			for _, k := range []string{"then", "else"} {
				if j, ok := target(str(c[k])); ok {
					succ[i] = append(succ[i], j)
				}
			}
		case "choice":
			if opts, ok := c["options"].([]any); ok {
				for _, o := range opts {
					if j, ok := target(gotoOf(o)); ok {
						succ[i] = append(succ[i], j)
					}
				}
			}
		default:
			if i+1 < n {
				succ[i] = []int{i + 1}
			}
		}
	}
	preds := make([][]int, n)
	for i := range succ {
		for _, s := range succ[i] {
			preds[s] = append(preds[s], i)
		}
	}

	// Forward data-flow to a fixpoint: in[i] = ∪ out[preds]; out[i] =
	// transfer(in[i], cmd). Union merge + deterministic transfer converges.
	in := make([]map[string]bool, n)
	out := make([]map[string]bool, n)
	for i := range in {
		in[i] = map[string]bool{}
		out[i] = map[string]bool{}
	}
	// Transfer under the single-speaker invariant AutoStage targets: a show
	// makes that actor the ONLY one on stage, a scene bg clears it, a hide
	// drops the named actor. Modelling show as {id} (not an accumulating union)
	// keeps the possibly-visible set to "the last actor shown on each incoming
	// path", so the defensive hides injected at a merge are exactly the branch
	// tails that differ — not every actor ever shown upstream.
	transfer := func(vis map[string]bool, c articy.Cmd) map[string]bool {
		switch c["op"] {
		case "actor":
			id := str(c["id"])
			if id == "" {
				break
			}
			if c["show"] == true {
				return map[string]bool{id: true} // sole occupant
			}
			r := map[string]bool{}
			for k := range vis {
				if k != id {
					r[k] = true
				}
			}
			return r
		case "bg":
			return map[string]bool{} // scene change clears the stage
		}
		r := map[string]bool{}
		for k := range vis {
			r[k] = true
		}
		return r
	}
	for pass := 0; pass < n+2; pass++ {
		changed := false
		for i := 0; i < n; i++ {
			merged := map[string]bool{}
			for _, p := range preds[i] {
				for k := range out[p] {
					merged[k] = true
				}
			}
			no := transfer(merged, script[i])
			if !sameSet(no, out[i]) {
				in[i], out[i] = merged, no
				changed = true
			} else {
				in[i] = merged
			}
		}
		if !changed {
			break
		}
	}

	// Inject the defensive hides before each show.
	result := make([]articy.Cmd, 0, n)
	for i, c := range script {
		if c["op"] == "actor" && c["show"] == true {
			shown := str(c["id"])
			for _, other := range sortedSetKeys(in[i]) {
				if other != shown {
					result = append(result, articy.Cmd{
						"op": "actor", "id": other, "show": false, "exit": "none",
					})
				}
			}
		}
		result = append(result, c)
	}
	return result
}

func gotoOf(o any) string {
	switch m := o.(type) {
	case articy.Cmd:
		return str(m["goto"])
	case map[string]any:
		return str(m["goto"])
	}
	return ""
}

func sameSet(a, b map[string]bool) bool {
	if len(a) != len(b) {
		return false
	}
	for k := range a {
		if !b[k] {
			return false
		}
	}
	return true
}

func sortedSetKeys(m map[string]bool) []string {
	ks := make([]string, 0, len(m))
	for k := range m {
		ks = append(ks, k)
	}
	sort.Strings(ks)
	return ks
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
