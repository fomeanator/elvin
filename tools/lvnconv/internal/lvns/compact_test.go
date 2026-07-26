package lvns

import (
	"encoding/json"
	"strings"
	"testing"
)

// sameDoc is the property every test here asserts: whatever Compact did to the
// text, the compiled document did not move.
func sameDoc(t *testing.T, before, after string) {
	t.Helper()
	a, err := Convert(before)
	if err != nil {
		t.Fatalf("input does not compile: %v\n%s", err, before)
	}
	b, err := Convert(after)
	if err != nil {
		t.Fatalf("compacted source does not compile: %v\n%s", err, after)
	}
	ja, _ := json.Marshal(a)
	jb, _ := json.Marshal(b)
	if string(ja) != string(jb) {
		t.Fatalf("compaction changed the document\n  was %s\n  now %s\n--- source ---\n%s", ja, jb, after)
	}
}

func compact(t *testing.T, src string) string {
	t.Helper()
	out := string(Compact([]byte(src)))
	sameDoc(t, src, out)
	return out
}

// The pass exists for this shape: the same staging boilerplate wrapped around
// every line of dialogue, differing only in the emotion.
func TestCompactHoistsActorBoilerplate(t *testing.T) {
	src := strings.Join([]string{
		"scene chapter",
		"",
		"actor_map Вера=Vera",
		"",
		`actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png" emotion="happy"`,
		"Вера: Раз.",
		`actor exit="fade" id="Vera" show=false`,
		`actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png" emotion="sad"`,
		"Вера: Два.",
		`actor exit="fade" id="Vera" show=false`,
		`actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png"`,
		"Вера: Три.",
		`actor exit="fade" id="Vera" show=false`,
		"",
	}, "\n")
	out := compact(t, src)

	for _, want := range []string{
		`def Vera_in actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png"`,
		`def Vera_out actor exit="fade" id="Vera" show=false`,
		"\nVera_in emotion=\"happy\"\n",
		"\nVera_in emotion=\"sad\"\n",
		"\nVera_in\n",
		"\nVera_out\n",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("missing %q in:\n%s", want, out)
		}
	}
	if len(out) >= len(src) {
		t.Errorf("no saving: %d → %d", len(src), len(out))
	}
	// The presets belong under the header, before the first statement.
	if strings.Index(out, "def Vera_in") < strings.Index(out, "actor_map Вера=Vera") {
		t.Errorf("preset block precedes the actor_map header:\n%s", out)
	}
}

// Idempotence: running the pass over its own output must change nothing. A
// pass that keeps finding "more" to do would churn the three-way merge on
// every re-import even when nothing about the story changed.
func TestCompactIsIdempotent(t *testing.T) {
	src := strings.Join([]string{
		`actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png" emotion="happy"`,
		`actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png" emotion="sad"`,
		`actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png" emotion="calm"`,
		"",
	}, "\n")
	once := compact(t, src)
	twice := string(Compact([]byte(once)))
	if once != twice {
		t.Errorf("second pass changed the file:\n--- once ---\n%s\n--- twice ---\n%s", once, twice)
	}
}

// A preset name is a LINE PREFIX at parse time. If it collides with a word that
// already starts a line — a speaker, a variable, a directive — that line stops
// meaning what it said.
func TestCompactNeverShadowsAnExistingFirstWord(t *testing.T) {
	// A narrator whose name is exactly what the namer would pick.
	src := strings.Join([]string{
		"actor_map Vera_in=Vera",
		`actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png" emotion="happy"`,
		"Vera_in: Раз.",
		`actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png" emotion="sad"`,
		"Vera_in: Два.",
		`actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png" emotion="calm"`,
		"Vera_in: Три.",
		"",
	}, "\n")
	out := compact(t, src) // sameDoc would fail loudly if the speaker got eaten
	if strings.Contains(out, "\ndef Vera_in ") {
		t.Errorf("preset shadowed the speaker `Vera_in`:\n%s", out)
	}

	// The dangerous spelling: with an emotion tag the speaker IS the first
	// field, so a `def Vera_in …` here would rewrite the line into an actor
	// command and delete the dialogue outright.
	tagged := strings.ReplaceAll(src, "Vera_in: ", "Vera_in [happy]: ")
	out = compact(t, tagged)
	if strings.Contains(out, "\ndef Vera_in ") {
		t.Errorf("preset shadowed the tagged speaker `Vera_in`:\n%s", out)
	}
	if strings.Count(out, "Vera_in [happy]:") != 3 {
		t.Errorf("dialogue lines lost:\n%s", out)
	}
}

func TestCompactNeverNamesAPresetAfterAnOp(t *testing.T) {
	var src strings.Builder
	for i := 0; i < 4; i++ {
		src.WriteString(`actor enter="fade" id="say" position="right" show=true sprite_url="/art/x.png" emotion="a"` + "\n")
	}
	out := compact(t, src.String())
	for _, line := range strings.Split(out, "\n") {
		if !strings.HasPrefix(line, "def ") {
			continue
		}
		name := strings.Fields(line)[1]
		if KnownOps[name] || reservedNames[name] {
			t.Errorf("preset named after a keyword: %q", name)
		}
	}
}

// `set key=… expr=…` IS an assignment; `set key=… value=…` is not — the parser
// has one assignment rule and it always produces `expr`.
func TestCompactTersesExprSetsOnly(t *testing.T) {
	src := strings.Join([]string{
		`set expr="Relationships.Roman +1" key="Relationships.Roman"`,
		`set key="Music.KOGOT" value=true`,
		`set default=true key="Wardrobe.Felix" value=0`,
		"",
	}, "\n")
	out := compact(t, src)
	if !strings.Contains(out, "Relationships.Roman = Relationships.Roman +1") {
		t.Errorf("dotted expr set not tersed:\n%s", out)
	}
	if !strings.Contains(out, `set key="Music.KOGOT" value=true`) {
		t.Errorf("a value set must keep its spelling:\n%s", out)
	}
	if !strings.Contains(out, `set default=true key="Wardrobe.Felix" value=0`) {
		t.Errorf("a declared default must keep its spelling:\n%s", out)
	}
}

// An expression that reads as syntax once it leaves its quotes stays quoted.
func TestCompactLeavesUnsafeExpressionsAlone(t *testing.T) {
	for _, expr := range []string{
		`a // b`, `«verse»`, `-1`, `{a:1}`, `x == 1`,
	} {
		src := "set expr=" + quoted(expr) + ` key="Ns.Var"` + "\n"
		out := string(Compact([]byte(src)))
		if _, err := Convert(out); err != nil {
			t.Fatalf("expr %q produced source that does not compile: %v\n%s", expr, err, out)
		}
		sameDoc(t, src, out)
	}
}

func quoted(s string) string { return `"` + strings.ReplaceAll(s, `"`, `\"`) + `"` }

// Prose inside a multi-line «…» is not source, and must never be parsed as a
// statement or rewritten.
func TestCompactLeavesMultiLineProseAlone(t *testing.T) {
	src := strings.Join([]string{
		"Автор: «Строка раз",
		`actor id="Vera" show=true`,
		"Строка три»",
		`actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png" emotion="a"`,
		`actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png" emotion="b"`,
		`actor enter="fade" id="Vera" position="right" show=true sprite_url="/art/vera.png" emotion="c"`,
		"",
	}, "\n")
	out := compact(t, src)
	if !strings.Contains(out, "Строка раз\nactor id=\"Vera\" show=true\nСтрока три»") {
		t.Errorf("the «…» block was rewritten:\n%s", out)
	}
}

// The pass is a no-op on anything it cannot prove: a source that does not
// compile comes back byte-for-byte.
func TestCompactIsANoOpOnBrokenSource(t *testing.T) {
	src := "actor id=\"x\" show=\n"
	if got := string(Compact([]byte(src))); got != src {
		t.Errorf("broken source was rewritten:\n%s", got)
	}
}

// A line that names the same key twice cannot be factored: which value wins
// depends on token order, and preset expansion reorders tokens.
func TestCompactSkipsDuplicateKeys(t *testing.T) {
	var src strings.Builder
	for i := 0; i < 4; i++ {
		src.WriteString(`actor id="Vera" id="Nina" show=true position="left" sprite_url="/art/vera.png"` + "\n")
	}
	out := string(Compact([]byte(src.String())))
	if strings.Contains(out, "def ") {
		t.Errorf("factored a line with a duplicate key:\n%s", out)
	}
}

// The compactor must not fire on two uses or on a saving too small to justify
// the name the reader now has to look up.
func TestCompactDoesNotFireOnWeakCandidates(t *testing.T) {
	src := strings.Join([]string{
		`actor id="a" show=true`,
		`actor id="a" show=true`,
		`actor id="a" show=true`,
		`actor id="a" show=true`,
		"",
	}, "\n")
	out := compact(t, src)
	if strings.Contains(out, "def ") {
		t.Errorf("a 22-byte statement is not worth a preset:\n%s", out)
	}
}
