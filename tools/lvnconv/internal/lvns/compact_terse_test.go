package lvns

// compact_terse_test.go — the shapes terseAssignments actually meets.
//
// The corpus this pass was tuned on is partner content that is not in git, and
// the public gate (importer/compact_gate_test.go) compiles howto/ and examples/
// — where there is not ONE dotted assignment. So the corpus gate passes over
// this pass without ever running it, and the only thing standing between a
// rewrite and a changed story is the table below.
//
// Every case asserts the same property as the rest of the file: the source may
// be rewritten however the pass likes, the COMPILED DOCUMENT may not move.

import (
	"strings"
	"testing"
)

// terseCases are spelled as the decompiler emits them — `set key=… expr=…` —
// because that is the only shape the pass looks at.
func TestTerseAssignmentRoundTrips(t *testing.T) {
	cases := []struct {
		name string
		stmt string
	}{
		{"dotted key", `set key="Relationships.Roman" expr="Relationships.Roman + 1"`},
		{"deeply dotted", `set key="a.b.c.d" expr="a.b.c.d + 1"`},
		{"cyrillic key", `set key="Отношения.Роман" expr="Отношения.Роман + 1"`},
		{"cyrillic bare key", `set key="репутация" expr="репутация + 3"`},
		{"plain number", `set key="a.b" expr="5"`},
		{"negative literal", `set key="a.b" expr="-5"`},
		{"function call", `set key="a.b" expr="max(a.b, 3)"`},
		{"nested call", `set key="a.b" expr="min(max(a.b, 0), 10)"`},
		{"comparison", `set key="a.b" expr="a.b > 2"`},
		{"equality", `set key="a.b" expr="a.b == 2"`},
		{"boolean", `set key="a.b" expr="true"`},
		{"string literal", `set key="a.b" expr="\"hello\""`},
		{"index", `set key="a.b" expr="get(list, 0)"`},
		{"underscore key", `set key="_hidden.flag" expr="1"`},
		{"key that is a substring of an op", `set key="setting.volume" expr="1"`},
		{"expr mentioning an op name", `set key="a.b" expr="bg + 1"`},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			src := "scene s\n" + c.stmt + "\n-> __end\n"
			// compact() asserts the document is unchanged, and fails loudly if
			// the rewritten source no longer compiles at all.
			compact(t, src)
		})
	}
}

// The point of the pass: a dotted key MUST come back short, otherwise it is
// doing nothing on the only shape it was written for.
func TestTerseAssignmentActuallyFiresOnDottedKeys(t *testing.T) {
	src := "scene s\n" + `set key="Relationships.Roman" expr="Relationships.Roman + 1"` + "\n-> __end\n"
	out := compact(t, src)
	if !strings.Contains(out, "Relationships.Roman = Relationships.Roman + 1") {
		t.Errorf("the dotted assignment was not shortened:\n%s", out)
	}
}

// A real chapter meets BOTH passes at once — staging boilerplate to factor into
// a preset and dotted counters to shorten — and they run back to back over the
// same slice. Cross-pass interaction is the one thing neither pass's own tests
// can see: terseAssignments turns a quoted key into a first word, which is
// exactly the namespace presetFactor mints its names in.
func TestTersedKeysAndPresetsSurviveEachOther(t *testing.T) {
	src := strings.Join([]string{
		"scene s",
		`actor id="Vera" position="right" sprite_url="/art/v.png" enter="fade" show=true emotion="happy"`,
		`set key="Relationships.Vera" expr="Relationships.Vera + 1"`,
		`actor id="Vera" position="right" sprite_url="/art/v.png" enter="fade" show=true emotion="sad"`,
		`set key="Relationships.Vera" expr="Relationships.Vera - 1"`,
		`actor id="Vera" position="right" sprite_url="/art/v.png" enter="fade" show=true`,
		"-> __end",
		"",
	}, "\n")
	out := compact(t, src)

	// Idempotence across the pair: a second run must find nothing new, or every
	// re-import churns the three-way merge over text that did not change.
	if again := string(Compact([]byte(out))); again != out {
		t.Errorf("compaction is not idempotent across the two passes\n--- once ---\n%s\n--- twice ---\n%s", out, again)
	}
}

// A key that already means something as a LINE PREFIX cannot be shortened: the
// short form puts it first on the line, where the parser reads a preset name
// or an op before it ever considers an assignment.
func TestTerseAssignmentNeverShadowsAPresetName(t *testing.T) {
	src := strings.Join([]string{
		"scene s",
		"def hud text hud x=3 y=5",
		`set key="hud" expr="1"`,
		"-> __end",
		"",
	}, "\n")
	compact(t, src) // the property does the judging
}
