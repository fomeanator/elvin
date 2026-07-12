package importer

import (
	"sort"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
)

// BuildCatalog derives a manifest `sprites` catalog from the compiled script so an
// imported novel arrives with its whole cast already listed in the panel — one
// entity per actor/obj id, ready to re-art. Each entity's layer points at the same
// url collectArt resolved: the real (matted) sprite when the project shipped one,
// otherwise a labelled placeholder — so "insert the sprites that exist" happens
// automatically, and everything else is a visible dummy.
//
// Any emotion/pose values the script actually uses on an id are recorded as the
// entity's `axes` (the states it's shown in). The layer stays concrete (the sample
// url) — the author templatizes with {axis} tokens in the panel when real per-state
// art arrives; recording the axes means the roster already knows every state.
//
// Returns the sprites map (to merge into manifest.sprites) plus placeholder art for
// any id that had no concrete sprite_url at all (so no entity ever points at a 404).
func BuildCatalog(doc *articy.Doc) (map[string]any, []ArtFile) {
	type ent struct {
		sample string                     // a concrete sprite_url seen for this id
		axes   map[string]map[string]bool // axis -> set of values used
	}
	ents := map[string]*ent{}
	var order []string
	get := func(id string) *ent {
		e, ok := ents[id]
		if !ok {
			e = &ent{axes: map[string]map[string]bool{}}
			ents[id] = e
			order = append(order, id)
		}
		return e
	}

	for _, c := range doc.Script {
		op, _ := c["op"].(string)
		if op != "actor" && op != "obj" {
			continue
		}
		id, _ := c["id"].(string)
		if id == "" {
			continue
		}
		e := get(id)
		if u, _ := c["sprite_url"].(string); u != "" {
			e.sample = u
		}
		for _, ax := range []string{"emotion", "pose"} {
			if v, _ := c[ax].(string); v != "" {
				if e.axes[ax] == nil {
					e.axes[ax] = map[string]bool{}
				}
				e.axes[ax][v] = true
			}
		}
	}

	sprites := map[string]any{}
	var extra []ArtFile
	for _, id := range order {
		e := ents[id]
		layer := e.sample
		if layer == "" {
			// No concrete art referenced (e.g. an emotion-only actor): give it a
			// placeholder so the entity is drawable.
			layer = "/content/art/" + id + ".png"
			extra = append(extra, ArtFile{Rel: "art/" + id + ".png", Data: Placeholder(id, phCharW, phCharH)})
		}
		entity := map[string]any{"name": id, "layers": []any{layer}}
		if len(e.axes) > 0 {
			axes := map[string]any{}
			for _, ax := range sortedKeys(e.axes) {
				vals := sortedSet(e.axes[ax])
				arr := make([]any, len(vals))
				for i, v := range vals {
					arr[i] = v
				}
				axes[ax] = arr
			}
			entity["axes"] = axes
		}
		sprites[id] = entity
	}
	return sprites, extra
}

func sortedKeys(m map[string]map[string]bool) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

func sortedSet(m map[string]bool) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}
