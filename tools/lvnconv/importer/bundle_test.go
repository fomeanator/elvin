package importer

import (
	"encoding/json"
	"strings"
	"testing"
)

// The title-level variable declaration replaces per-chapter boilerplate:
// keys split into game/chapter scopes by template prefix, and the partner's
// inline `set default=true` copies strip from BOTH script forms — while an
// intentional override (different value) stays.
func TestVarsDeclarationAndStrip(t *testing.T) {
	vars := []VarDecl{
		{Key: "Way.Moral", Default: "0"},
		{Key: "Temp.saveHair", Default: "11"},
		{Key: "Remember.Lie", Default: "false"},
	}
	decl, typed := buildVarsDeclaration(nil, vars, nil)
	var root map[string]map[string]any
	if err := json.Unmarshal(decl, &root); err != nil {
		t.Fatalf("declaration is not JSON: %v", err)
	}
	if _, ok := root["game"]["Way.Moral"]; !ok {
		t.Error("Way.Moral must be game-scoped")
	}
	if _, ok := root["chapter"]["Temp.saveHair"]; !ok {
		t.Error("Temp.* must be chapter-scoped")
	}
	if root["game"]["Remember.Lie"] != false {
		t.Errorf("bool default lost: %v", root["game"]["Remember.Lie"])
	}

	lvn := []byte(`{"scene":"chapter","script":[
		{"op":"set","default":true,"key":"Way.Moral","value":0},
		{"op":"set","default":true,"key":"Way.Moral","value":5},
		{"op":"set","key":"Way.Moral","value":3},
		{"op":"say","text":"hi"}]}`)
	sf := ScriptFile{Rel: "scripts/x.lvn", Data: lvn}
	stripDeclaredDefaults(&sf, typed)
	var doc struct{ Script []map[string]any }
	_ = json.Unmarshal(sf.Data, &doc)
	if len(doc.Script) != 3 {
		t.Fatalf("want 3 ops after strip (boilerplate gone, override+plain set+say stay), got %d: %s", len(doc.Script), sf.Data)
	}

	src := "set default=true key=\"Way.Moral\" value=0\nset default=true key=\"Way.Moral\" value=5\nГерой: привет\n"
	lf := ScriptFile{Rel: "scripts/x.lvns", Data: []byte(src)}
	stripDeclaredDefaultLines(&lf, typed)
	out := string(lf.Data)
	if strings.Contains(out, "value=0") || !strings.Contains(out, "value=5") || !strings.Contains(out, "Герой") {
		t.Fatalf("lvns strip wrong: %q", out)
	}
}
