package adpd

import (
	"encoding/json"
	"fmt"
	"math"
	"sort"
)

// ── model emission (articy JSON-export shape) ────────────────────────────────

const dlgID = "dialogue-root-0000-0000-000000000000"

func nodeID(o uint32) string {
	return fmt.Sprintf("node-%08d-0000-0000-000000000000", o)
}

// bfs returns the nodes reachable from starts (capped at maxN), in visit order.
func bfs(fl flow, starts []uint32, maxN int) ([]uint32, map[uint32]bool) {
	seen := map[uint32]bool{}
	var reach []uint32
	queue := append([]uint32{}, starts...)
	for len(queue) > 0 && len(reach) < maxN {
		x := queue[0]
		queue = queue[1:]
		if seen[x] {
			continue
		}
		seen[x] = true
		reach = append(reach, x)
		for _, e := range fl.succ[x] {
			queue = append(queue, e.dst)
		}
	}
	return reach, seen
}

func buildExport(fl flow, start uint32, maxN int, gvars []nsVars) export {
	reach, seen := bfs(fl, []uint32{start}, maxN)
	return emitModels(fl, reach, seen, []uint32{start}, gvars)
}

// buildExportAll emits the WHOLE novel: every flow node, reachable through a
// synthetic chapter hub that fans out to all in-degree-0 roots plus one entry per
// otherwise-unreachable pocket (sub-flows entered by Jump). Nothing is dropped.
func buildExportAll(fl flow, gvars []nsVars) export {
	indeg := map[uint32]int{}
	for _, es := range fl.succ {
		for _, e := range es {
			indeg[e.dst]++
		}
	}
	var roots []uint32
	for n := range fl.nodes {
		if indeg[n] == 0 {
			roots = append(roots, n)
		}
	}
	sort.Slice(roots, func(i, j int) bool { return roots[i] < roots[j] })

	entries := append([]uint32{}, roots...)
	_, seen := bfs(fl, roots, math.MaxInt32)
	// surface every remaining pocket by adding its lowest node as an entry.
	for {
		min := uint32(math.MaxUint32)
		for n := range fl.nodes {
			if !seen[n] && n < min {
				min = n
			}
		}
		if min == math.MaxUint32 {
			break
		}
		entries = append(entries, min)
		_, more := bfs(fl, []uint32{min}, math.MaxInt32)
		for n := range more {
			seen[n] = true
		}
	}
	reach := make([]uint32, 0, len(seen))
	for n := range seen {
		reach = append(reach, n)
	}
	sort.Slice(reach, func(i, j int) bool { return reach[i] < reach[j] })
	return emitModels(fl, reach, seen, entries, gvars)
}

// emitModels builds the articy-export model. Every dialogue fragment carries a
// StableId (its articy GUID) so the back-end can stamp the say/option with a key
// that survives reimport — the importer's localization pass keys its catalog off
// it, and saves/analytics stay valid across content edits.
func emitModels(fl flow, reach []uint32, seen map[uint32]bool, entries []uint32, gvars []nsVars) export {
	outsOf := func(o uint32) []edge {
		var es []edge
		for _, e := range fl.succ[o] {
			if seen[e.dst] {
				es = append(es, e)
			}
		}
		return es
	}
	conns := func(es []edge) []any {
		var c []any
		for _, e := range es {
			c = append(c, map[string]any{"Target": nodeID(e.dst)})
		}
		if len(c) == 0 {
			c = []any{map[string]any{"Target": dlgID}}
		}
		return c
	}
	onePin := func(es []edge) []any {
		return []any{map[string]any{"Text": "", "Connections": conns(es)}}
	}
	emptyIn := []any{map[string]any{"Text": "", "Connections": []any{}}}

	speakers := map[string]bool{}
	var models []model
	for _, o := range reach {
		es := outsOf(o)
		switch {
		case fl.logic[o].expr != "" && fl.logic[o].cond:
			// Condition → two output pins (true/false), split by source pin.
			models = append(models, model{Type: "Condition", Properties: map[string]any{
				"Id": nodeID(o), "Expression": fl.logic[o].expr,
				"InputPins": emptyIn, "OutputPins": conditionPins(es),
			}})
		case fl.logic[o].expr != "":
			models = append(models, model{Type: "Instruction", Properties: map[string]any{
				"Id": nodeID(o), "Expression": fl.logic[o].expr,
				"InputPins": emptyIn, "OutputPins": onePin(es),
			}})
		case fl.text[o] != "":
			sp := fl.sp[o]
			if sp != "" {
				speakers[sp] = true
			}
			text, menu := fl.text[o], fl.text[o]
			menu = truncateRunes(menu, 80)
			// StableId is the fragment's articy GUID — a key that survives reimport,
			// so saves, analytics and localization catalogs stay valid across content
			// edits. The back-end carries it onto the say/option for the importer's
			// localization pass (see importer.Localize).
			key := fl.guid[o]
			if key == "" {
				key = nodeID(o)
			}
			props := map[string]any{
				"Id": nodeID(o), "Text": text, "MenuText": menu, "Speaker": sp,
				"StableId": key, "InputPins": emptyIn, "OutputPins": onePin(es),
			}
			// The fragment's marker colour (emotion cue) — omitted at the default.
			if col := fl.color[o]; col != "" {
				props["BackgroundColor"] = col
			}
			models = append(models, model{Type: "DialogueFragment", Properties: props})
		default:
			models = append(models, model{Type: "Hub", Properties: map[string]any{
				"Id": nodeID(o), "DisplayName": "", "InputPins": emptyIn, "OutputPins": onePin(es),
			}})
		}
	}

	var spNames []string
	for s := range speakers {
		spNames = append(spNames, s)
	}
	sort.Strings(spNames)
	for _, s := range spNames {
		models = append(models, model{Type: "Entity", Properties: map[string]any{"Id": s, "DisplayName": s}})
	}
	// The dialogue's entry: a single start, or a synthetic "chapters" hub that
	// fans out to every chapter root / pocket entry so nothing is unreachable.
	const hubID = "chapter-hub-0000-0000-000000000000"
	entry := dlgID
	if len(entries) == 1 {
		entry = nodeID(entries[0])
	} else {
		var hubConns []any
		for i, e := range entries {
			hubConns = append(hubConns, map[string]any{"Target": nodeID(e)})
			_ = i
		}
		models = append(models, model{Type: "Hub", Properties: map[string]any{
			"Id": hubID, "DisplayName": "Главы",
			"InputPins":  []any{map[string]any{"Text": "", "Connections": []any{}}},
			"OutputPins": []any{map[string]any{"Text": "", "Connections": hubConns}},
		}})
		entry = hubID
	}
	models = append(models, model{Type: "Dialogue", Properties: map[string]any{
		"Id": dlgID, "TechnicalName": "chapter", "DisplayName": "chapter",
		"InputPins":  []any{map[string]any{"Text": "", "Connections": []any{map[string]any{"Target": entry}}}},
		"OutputPins": []any{map[string]any{"Text": "", "Connections": []any{}}},
	}})
	return export{GlobalVariables: gvars, Packages: []pkg{{Models: models}}}
}

// truncateRunes caps s at n runes (not bytes), appending "…" when it trims, so
// multi-byte text (Cyrillic, CJK) is never cut mid-character into mojibake.
func truncateRunes(s string, n int) string {
	r := []rune(s)
	if len(r) <= n {
		return s
	}
	return string(r[:n]) + "…"
}

// conditionPins splits a condition's outgoing edges into two pins (true/false)
// by source pin id, padding to the two pins convert.go expects.
func conditionPins(es []edge) []any {
	bySrc := map[uint32][]edge{}
	var order []uint32
	for _, e := range es {
		if _, ok := bySrc[e.srcPin]; !ok {
			order = append(order, e.srcPin)
		}
		bySrc[e.srcPin] = append(bySrc[e.srcPin], e)
	}
	sort.Slice(order, func(i, j int) bool { return order[i] < order[j] })
	var pins []any
	for _, sp := range order {
		grp := bySrc[sp]
		// Emit every connection on the pin, not just the first — a condition pin
		// that fans out to several targets would otherwise silently drop all but
		// one (lost content, and the coverage self-check would disagree).
		conns := make([]any, 0, len(grp))
		for _, e := range grp {
			conns = append(conns, map[string]any{"Target": nodeID(e.dst)})
		}
		pins = append(pins, map[string]any{"Text": "", "Connections": conns})
	}
	for len(pins) < 2 {
		pins = append(pins, map[string]any{"Text": "", "Connections": []any{map[string]any{"Target": dlgID}}})
	}
	return pins[:2]
}

// ── export JSON shapes (mirror internal/articy's expected input) ─────────────

type export struct {
	GlobalVariables []nsVars `json:"GlobalVariables"`
	Packages        []pkg    `json:"Packages"`
}
type pkg struct {
	Models []model `json:"Models"`
}
type model struct {
	Type       string         `json:"Type"`
	Properties map[string]any `json:"Properties"`
}
type nsVars struct {
	Namespace string    `json:"Namespace"`
	Variables []varDecl `json:"Variables"`
}
type varDecl struct {
	Variable string `json:"Variable"`
	Type     string `json:"Type"`
	Value    string `json:"Value"`
}

func marshalExport(ex export) ([]byte, error) { return json.Marshal(ex) }
