package importer

// Regression tests for the 2026-07-25 audit findings: the round-trip safety
// guard (VerifyLvnsRoundTrip) and the decompiler/parser fixes for unbalanced
// guillemets, «…»-wrapped lines, declared defaults on simple keys, choice
// attributes, and host-defined ops. Each test names the audit finding it
// pins (see AUDIT-OPUS-FINDINGS.md).

import (
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

func mustRoundTrip(t *testing.T, doc *articy.Doc) *lvns.Doc {
	t.Helper()
	src := ToLvns(doc)
	rec, err := lvns.Convert(string(src))
	if err != nil {
		t.Fatalf("recompile failed: %v\n--- .lvns ---\n%s", err, src)
	}
	return rec
}

// O2: an author line OPENING a « it never closes (a verse split across says)
// must not make the parser swallow the following lines into one string.
func TestRoundTripUnbalancedGuillemetKeepsEverySay(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "say", "who": "Автор", "text": "«Союз нерушимый республик свободных"},
		{"op": "say", "who": "Автор", "text": "Сплотила навеки Великая Русь!"},
		{"op": "say", "who": "Автор", "text": "Единый, могучий Советский Союз!»"},
		{"op": "say", "who": "ГГ", "text": "Видела такое в «Ну, погоди!"},
	}}
	rec := mustRoundTrip(t, doc)
	says := sayTexts(rec.Script)
	if len(says) != 4 {
		t.Fatalf("say count drifted: got %d (%q), want 4", len(says), says)
	}
	for i, want := range []string{
		"«Союз нерушимый республик свободных",
		"Сплотила навеки Великая Русь!",
		"Единый, могучий Советский Союз!»",
		"Видела такое в «Ну, погоди!",
	} {
		if says[i] != want {
			t.Errorf("say %d: got %q want %q", i, says[i], want)
		}
	}
}

// O12: a line fully wrapped in «…» is the author's quotation, not syntax —
// the guillemets must survive the round-trip.
func TestRoundTripGuillemetWrapSurvives(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "say", "who": "Аня", "text": "«Мы нежность, мы нежность...»"},
	}}
	rec := mustRoundTrip(t, doc)
	says := sayTexts(rec.Script)
	if len(says) != 1 || says[0] != "«Мы нежность, мы нежность...»" {
		t.Fatalf("wrap lost: %q", says)
	}
}

// O15: `set default=true` with a SIMPLE key must keep the default flag — the
// short `k = v` form can't carry it, so the generic form must be used.
func TestRoundTripSimpleKeyDefaultFlagSurvives(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "set", "key": "gold", "value": float64(0), "default": true},
	}}
	rec := mustRoundTrip(t, doc)
	found := false
	for _, c := range rec.Script {
		if c["op"] == "set" && str(c["key"]) == "gold" {
			found = true
			if c["default"] != true {
				t.Fatalf("default flag lost: %v", c)
			}
		}
	}
	if !found {
		t.Fatal("set gold vanished")
	}
}

// O19: a variable named after a directive word must not round-trip into an
// INJECTED op (`return = 0` used to parse as a `return` statement).
func TestRoundTripDirectiveNamedKeySafe(t *testing.T) {
	for _, key := range []string{"def", "return", "scene", "call", "if", "voice"} {
		doc := &articy.Doc{Script: []articy.Cmd{
			{"op": "set", "key": key, "value": float64(1)},
			{"op": "say", "who": "A", "text": "after"},
		}}
		rec := mustRoundTrip(t, doc)
		sets, rets := 0, 0
		for _, c := range rec.Script {
			switch c["op"] {
			case "set":
				if str(c["key"]) == key {
					sets++
				}
			case "return":
				rets++
			}
		}
		if sets != 1 || rets != 0 {
			t.Errorf("key %q: set=%d return=%d, want 1/0", key, sets, rets)
		}
	}
}

// O17: attributes on the choice op itself (timeout…) must survive.
func TestRoundTripChoiceTimeoutSurvives(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "choice", "timeout": float64(5), "timeout_goto": "late", "options": []any{
			map[string]any{"text": "Да", "goto": "yes"},
		}},
		{"op": "label", "id": "yes"},
		{"op": "label", "id": "late"},
		{"op": "say", "text": "end"},
	}}
	rec := mustRoundTrip(t, doc)
	for _, c := range rec.Script {
		if c["op"] == "choice" {
			if c["timeout"] == nil || str(c["timeout_goto"]) != "late" {
				t.Fatalf("choice attrs lost: %v", c)
			}
			return
		}
	}
	t.Fatal("choice vanished")
}

// O5: a host-defined op (LvnOps.Register) must round-trip via the `ext`
// spelling instead of failing recompile as an unknown command.
func TestRoundTripHostOpViaExt(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "leaderboard_submit", "board": "quiz", "score": float64(10)},
	}}
	src := ToLvns(doc)
	if !strings.Contains(string(src), "ext leaderboard_submit") {
		t.Fatalf("host op not spelled as ext: %s", src)
	}
	rec, err := lvns.Convert(string(src))
	if err != nil {
		t.Fatalf("recompile failed: %v", err)
	}
	found := false
	for _, c := range rec.Script {
		if c["op"] == "leaderboard_submit" && str(c["board"]) == "quiz" {
			found = true
		}
	}
	if !found {
		t.Fatalf("host op lost: %v", rec.Script)
	}
}

// The systemic guard: a drifting sidecar must be REPORTED, never silent.
// choice bodies are a currently-known-lossy field (audit O3) — the guard's
// whole job is that such loss shows up as a warning.
func TestVerifyLvnsRoundTripFlagsBodyLoss(t *testing.T) {
	script := []articy.Cmd{
		{"op": "choice", "options": []any{
			map[string]any{"text": "Спросить", "expr": "!_once_1", "body": []any{
				map[string]any{"op": "set", "key": "_once_1", "value": true},
				map[string]any{"op": "goto", "label": "q1"},
			}},
		}},
		{"op": "label", "id": "q1"},
		{"op": "say", "text": "ответ"},
	}
	doc := &articy.Doc{Script: script}
	warnings := VerifyLvnsRoundTrip(script, ToLvns(doc))
	if len(warnings) == 0 {
		t.Fatal("expected the guard to flag choice-body loss, got none")
	}
	joined := strings.Join(warnings, "\n")
	if !strings.Contains(joined, "bodies") && !strings.Contains(joined, "set") {
		t.Fatalf("warning doesn't mention the loss: %q", warnings)
	}
}

// And the clean case: a faithful sidecar produces zero warnings.
func TestVerifyLvnsRoundTripCleanScriptSilent(t *testing.T) {
	script := []articy.Cmd{
		{"op": "say", "who": "Mara", "text": "Привет"},
		{"op": "set", "key": "Way.Moral", "value": float64(1)},
		{"op": "choice", "options": []any{
			map[string]any{"text": "Да", "goto": "y", "wallet_cost": map[string]any{"currency": "crystals", "amount": float64(20)}},
		}},
		{"op": "label", "id": "y"},
		{"op": "say", "text": "конец"},
	}
	doc := &articy.Doc{Script: script}
	if w := VerifyLvnsRoundTrip(script, ToLvns(doc)); len(w) != 0 {
		t.Fatalf("clean script flagged: %q", w)
	}
}

// P2 (partner complaint): the wardrobe swap leaves the removed pickers'
// branch bodies as dead code at the chapter tail — removeUnreachableOps must
// drop exactly them and keep every reachable op (including call targets and
// if fall-throughs).
func TestRemoveUnreachableOpsDropsWardrobeTails(t *testing.T) {
	ops := []map[string]any{
		{"op": "say", "text": "start"},
		{"op": "if", "expr": "x", "then": "a"}, // no else → falls through
		{"op": "call", "label": "sub"},         // returns → falls through
		{"op": "goto", "label": "end"},
		// dead wardrobe tail (was reachable only via the removed picker):
		{"op": "say", "who": "{player}", "text": "Хвостик."},
		{"op": "set", "key": "Wardrobe.mainCh_Hair", "value": float64(12)},
		{"op": "goto", "label": "end"},
		// live targets:
		{"op": "label", "id": "a"},
		{"op": "say", "text": "then-branch"},
		{"op": "label", "id": "sub"},
		{"op": "say", "text": "sub"},
		{"op": "return"},
		{"op": "label", "id": "end"},
		{"op": "say", "text": "fin"},
	}
	kept := removeUnreachableOps(ops)
	var texts []string
	for _, c := range kept {
		if c["op"] == "say" {
			texts = append(texts, str(c["text"]))
		}
	}
	want := []string{"start", "then-branch", "sub", "fin"}
	if len(texts) != len(want) {
		t.Fatalf("says after prune: %q, want %q", texts, want)
	}
	for i := range want {
		if texts[i] != want[i] {
			t.Fatalf("says after prune: %q, want %q", texts, want)
		}
	}
	for _, c := range kept {
		if k := str(c["key"]); k == "Wardrobe.mainCh_Hair" {
			t.Fatal("dead wardrobe tail survived")
		}
	}
}
