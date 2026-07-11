package lvn

import (
	"os"
	"path/filepath"
	"testing"
)

func extFixture(t *testing.T) *ExtGrammar {
	t.Helper()
	g, err := ParseExtGrammar([]byte(`{
	  "name": "minigames",
	  "ops": {
	    "minigame": {
	      "doc": "Runs a host mini-game; the story waits for Resume().",
	      "fields": ["difficulty", "timeout"],
	      "required": ["id"],
	      "enums": {"difficulty": ["easy", "normal", "hard"]}
	    }
	  }
	}`))
	if err != nil {
		t.Fatalf("fixture: %v", err)
	}
	return g
}

// A declared host op with valid fields is as quiet as a built-in.
func TestExtDeclaredOpValidatesClean(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"minigame","id":"river","difficulty":"hard"}]}`)
	if issues := ValidateExt(d, extFixture(t)); len(issues) != 0 {
		t.Fatalf("declared host op must not warn, got %v", issues)
	}
}

// Without a declaration the same op keeps the advisory unknown-op warning.
func TestExtUndeclaredOpStillWarns(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"minigame","id":"river"}]}`)
	if issues := Validate(d); !hasWarn(issues, `unknown op "minigame"`) {
		t.Fatalf("expected unknown-op warning without a grammar, got %v", issues)
	}
}

func TestExtUnknownFieldWarnsWithSuggestion(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"minigame","id":"r","dificulty":"hard"}]}`)
	issues := ValidateExt(d, extFixture(t))
	if !hasWarn(issues, `unknown field "dificulty"`) || !hasWarn(issues, `did you mean "difficulty"`) {
		t.Fatalf("expected field typo warning with suggestion, got %v", issues)
	}
}

func TestExtMissingRequiredFieldIsAnError(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"minigame","difficulty":"easy"}]}`)
	if issues := ValidateExt(d, extFixture(t)); !hasError(issues, `requires field "id"`) {
		t.Fatalf("expected required-field error, got %v", issues)
	}
}

func TestExtEnumViolationWarnsWithSuggestion(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"minigame","id":"r","difficulty":"hardd"}]}`)
	issues := ValidateExt(d, extFixture(t))
	if !hasWarn(issues, `outside its declared set`) || !hasWarn(issues, `did you mean "hard"`) {
		t.Fatalf("expected enum warning with suggestion, got %v", issues)
	}
}

// A typo of a DECLARED host op gets a targeted suggestion.
func TestExtOpTypoSuggestsDeclaredOp(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"minigme","id":"r"}]}`)
	if issues := ValidateExt(d, extFixture(t)); !hasWarn(issues, `did you mean the declared host op "minigame"`) {
		t.Fatalf("expected declared-op suggestion, got %v", issues)
	}
}

// Declaration bugs fail loudly: unknown JSON keys, core-op redeclaration,
// enums on undeclared fields.
func TestExtGrammarParseRejectsBadDeclarations(t *testing.T) {
	cases := map[string]string{
		"unknown key":     `{"ops":{"x":{"filds":["a"]}}}`,
		"core op":         `{"ops":{"say":{"fields":["text"]}}}`,
		"enum off fields": `{"ops":{"x":{"fields":["a"],"enums":{"b":["v"]}}}}`,
		"no ops":          `{"name":"empty"}`,
	}
	for name, src := range cases {
		if _, err := ParseExtGrammar([]byte(src)); err == nil {
			t.Fatalf("%s: expected a parse error", name)
		}
	}
}

// The conventional sidecar is found beside the file and one directory up.
func TestFindExtGrammarSidecar(t *testing.T) {
	root := t.TempDir()
	scripts := filepath.Join(root, "scripts")
	if err := os.MkdirAll(scripts, 0o755); err != nil {
		t.Fatal(err)
	}
	decl := `{"ops":{"minigame":{"required":["id"]}}}`
	if err := os.WriteFile(filepath.Join(root, "ext-grammar.json"), []byte(decl), 0o644); err != nil {
		t.Fatal(err)
	}

	g, path, err := FindExtGrammar(filepath.Join(scripts, "ch1.lvn"))
	if err != nil || g == nil {
		t.Fatalf("expected the parent-dir sidecar, got g=%v path=%q err=%v", g, path, err)
	}
	if _, ok := g.Ops["minigame"]; !ok {
		t.Fatalf("sidecar ops not loaded: %v", g.Ops)
	}

	if g, _, err := FindExtGrammar(filepath.Join(t.TempDir(), "lone.lvn")); g != nil || err != nil {
		t.Fatalf("no sidecar must be (nil, nil), got g=%v err=%v", g, err)
	}

	// A present-but-broken sidecar is an error, never silently skipped.
	if err := os.WriteFile(filepath.Join(scripts, "ext-grammar.json"), []byte(`{"ops":{}}`), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, _, err := FindExtGrammar(filepath.Join(scripts, "ch1.lvn")); err == nil {
		t.Fatal("broken sidecar must surface an error")
	}
}

// A label-reference field: the target participates like a goto's — it must
// exist, and it stops counting as dead.
func TestExtLabelFieldTargetsAndValidates(t *testing.T) {
	g, err := ParseExtGrammar([]byte(`{"ops":{"minigame":{"required":["id"],"labels":["on_lose"]}}}`))
	if err != nil {
		t.Fatal(err)
	}
	// The declared target keeps the label alive (no dead-label warning) …
	d := parse(t, `{"scene":"t","script":[
	 {"op":"minigame","id":"r","on_lose":"failed"},
	 {"op":"goto","label":"__end"},
	 {"op":"label","id":"failed"},
	 {"op":"say","text":"lost"}]}`)
	for _, is := range ValidateExt(d, g) {
		if contains(is.Msg, "never targeted") {
			t.Fatalf("label referenced from a declared label field must not be dead: %v", is)
		}
	}
	// … and a missing target is an error, exactly like a dangling goto.
	d2 := parse(t, `{"scene":"t","script":[{"op":"minigame","id":"r","on_lose":"nowhere"}]}`)
	if issues := ValidateExt(d2, g); !hasError(issues, `undefined label "nowhere"`) {
		t.Fatalf("expected undefined-label error, got %v", issues)
	}
}
