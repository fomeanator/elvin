package lvn

import (
	"bytes"
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

	// Нет проектного файла — путь пустой, но грамматика НЕ пустая: операции
	// движка известны всегда (см. withServiceOps). Раньше здесь возвращался
	// nil, и каждый проект был обязан переписать список первопартийных
	// операций себе — Time Romance переписал четыре из тринадцати.
	if g, path, err := FindExtGrammar(filepath.Join(t.TempDir(), "lone.lvn")); err != nil || path != "" {
		t.Fatalf("no sidecar must report an empty path, got path=%q err=%v", path, err)
	} else if _, ok := g.Ops["minigame"]; ok {
		t.Fatal("чужая проектная операция не должна протекать между вызовами")
	} else if _, ok := g.Ops["wallet_earn"]; !ok {
		t.Fatal("операции движка обязаны быть известны без проектного файла")
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

// КОПИЯ ГРАММАТИКИ СЕРВИСНЫХ ОПЕРАЦИЙ НЕ ОТСТАЁТ ОТ ОРИГИНАЛА.
//
// Валидатор носит встроенную копию (`service-ops.json`), потому что знать свои
// операции — обязанность движка: без этого каждый проект переписывал их список
// себе, и Time Romance объявил четыре из тринадцати — а `leaderboard_submit`,
// рабочий и зарегистрированный, числился неизвестной командой.
func TestEmbeddedServiceGrammarMatchesThePackage(t *testing.T) {
	root := filepath.Join("..", "..", "..")
	canon, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(
		"unity/Packages/com.lvn.engine.services/ext-grammar.json")))
	if err != nil {
		t.Fatalf("грамматика пакета услуг: %v", err)
	}
	mine, err := os.ReadFile("service-ops.json")
	if err != nil {
		t.Fatalf("встроенная копия: %v", err)
	}
	if !bytes.Equal(bytes.TrimSpace(canon), bytes.TrimSpace(mine)) {
		t.Fatal("service-ops.json отстал от com.lvn.engine.services/ext-grammar.json — обновите копию")
	}
}

// Проектная грамматика ДОПОЛНЯЕТ движковую, а не заменяет её.
func TestProjectGrammarExtendsRatherThanReplaces(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "ext-grammar.json"),
		[]byte(`{"name":"proj","ops":{"my_op":{"fields":["a"]}}}`), 0o644); err != nil {
		t.Fatal(err)
	}
	g, _, err := FindExtGrammar(filepath.Join(dir, "chapter.lvn"))
	if err != nil || g == nil {
		t.Fatalf("FindExtGrammar: %v", err)
	}
	if _, ok := g.Ops["my_op"]; !ok {
		t.Fatal("проектная операция потерялась")
	}
	if _, ok := g.Ops["leaderboard_submit"]; !ok {
		t.Fatal("первопартийная операция должна остаться известной рядом с проектной")
	}
}

// Без проектной грамматики операции движка всё равно известны.
func TestServiceOpsKnownWithoutAnySidecar(t *testing.T) {
	g, path, err := FindExtGrammar(filepath.Join(t.TempDir(), "chapter.lvn"))
	if err != nil {
		t.Fatalf("FindExtGrammar: %v", err)
	}
	if path != "" {
		t.Fatalf("проектной грамматики быть не должно, а найдено: %s", path)
	}
	if g == nil || len(g.Ops) == 0 {
		t.Fatal("операции движка обязаны быть известны и без файла в проекте")
	}
}
