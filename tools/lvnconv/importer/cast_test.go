package importer

import (
	"testing"

	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/articy"
)

func TestBuildCatalog(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "actor", "id": "bob", "sprite_url": "/content/art/bob.png"},
		{"op": "actor", "id": "mara", "emotion": "happy"},
		{"op": "actor", "id": "mara", "emotion": "sad"},
		{"op": "say", "who": "bob", "text": "hi"},
		{"op": "bg", "id": "yard", "sprite_url": "/content/bg/yard.jpg"},
	}}

	sprites, extra := BuildCatalog(doc)

	if len(sprites) != 2 {
		t.Fatalf("want 2 entities (bob, mara), got %d", len(sprites))
	}

	// bob: concrete art, no axes.
	bob := sprites["bob"].(map[string]any)
	if got := bob["layers"].([]any)[0].(string); got != "/content/art/bob.png" {
		t.Errorf("bob layer = %q, want the resolved art url", got)
	}
	if _, ok := bob["axes"]; ok {
		t.Errorf("bob should have no axes")
	}

	// mara: no concrete sprite → placeholder art emitted; emotion axis with both values.
	mara := sprites["mara"].(map[string]any)
	axes := mara["axes"].(map[string]any)
	em := axes["emotion"].([]any)
	if len(em) != 2 || em[0].(string) != "happy" || em[1].(string) != "sad" {
		t.Errorf("mara emotion axis = %v, want [happy sad]", em)
	}
	if len(extra) != 1 || extra[0].Rel != "art/mara.png" {
		t.Errorf("want a placeholder for mara, got %v", extra)
	}
}
