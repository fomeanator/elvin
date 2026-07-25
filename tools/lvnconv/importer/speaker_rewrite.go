package importer

import (
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
)

// cmdsAsOps reinterprets a []articy.Cmd as []map[string]any (Cmd's underlying
// type) without copying the maps — mutating an element of the result mutates
// the SAME map doc.Script holds. Lets the ops-level rewrite core below (shared
// with the bundle's decodeScriptOps-based callers in bundle_wire.go) run
// directly on doc.Script in Run/runMultiChapter, before compilation.
func cmdsAsOps(cs []articy.Cmd) []map[string]any {
	out := make([]map[string]any, len(cs))
	for i, c := range cs {
		out[i] = map[string]any(c)
	}
	return out
}

// applyProtagonistSpeakerRename rewrites every `say` op's `who` that matches
// tpl.isProtagSpeaker to the template's player-name template ("{player}"), in
// place. Core of bundle_wire.go's renameSpeakerInScript (which wraps this
// around decodeScriptOps for the compiled bundle .lvn) — ALSO called directly
// on doc.Script by Run/runMultiChapter, so an ordinary (non-bundle) import
// with a correctly authored Template gets the same rewrite: this only needs
// the Template, never the bundle's xlsx roster data.
func applyProtagonistSpeakerRename(ops []map[string]any, tpl *Template) bool {
	tpl = tpl.resolve()
	playerTmpl := tpl.Staging.PlayerTemplate
	if playerTmpl == "" {
		playerTmpl = "{player}"
	}
	changed := false
	for _, op := range ops {
		if op == nil {
			continue
		}
		if n, _ := op["op"].(string); n != "say" {
			continue
		}
		if who, _ := op["who"].(string); tpl.isProtagSpeaker(who) {
			op["who"] = playerTmpl
			changed = true
		}
	}
	return changed
}

// applySpeakerNameOverrides rewrites say `who` labels through the template's
// SpeakerNames map, in place. Core of bundle_wire.go's applySpeakerNames say-
// loop — ALSO called directly on doc.Script by Run/runMultiChapter.
func applySpeakerNameOverrides(ops []map[string]any, tpl *Template) bool {
	names := tpl.resolve().SpeakerNames
	if len(names) == 0 {
		return false
	}
	changed := false
	for _, op := range ops {
		if op == nil {
			continue
		}
		if n, _ := op["op"].(string); n != "say" {
			continue
		}
		who, _ := op["who"].(string)
		if who == "" || strings.HasPrefix(who, "{") {
			continue // narration / already the player template
		}
		if display := displayNameFor(names, who); display != "" && display != who {
			op["who"] = display
			changed = true
		}
	}
	return changed
}

// applySpeakerNameOverridesToSprites is applySpeakerNameOverrides's twin for a
// sprites catalog's entity display names (cast cards, wardrobe headers).
// Core of bundle_wire.go's applySpeakerNames entity loop — ALSO called by
// Run/runMultiChapter over the BuildCatalog output.
func applySpeakerNameOverridesToSprites(sprites map[string]any, tpl *Template) {
	names := tpl.resolve().SpeakerNames
	if len(names) == 0 {
		return
	}
	for id, ent := range sprites {
		m, ok := ent.(map[string]any)
		if !ok {
			continue
		}
		cur, _ := m["name"].(string)
		key := cur
		if key == "" {
			key = id
		}
		if display := displayNameFor(names, key); display != "" {
			m["name"] = display
		}
	}
}
