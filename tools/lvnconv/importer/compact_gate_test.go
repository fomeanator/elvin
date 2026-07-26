package importer

// compact_gate_test.go — the compaction pass, pinned against the repo's own
// gated examples (howto/*/*.lvns + examples/*.lvns).
//
// The corpus this pass was tuned on is partner content that is not in git, so
// this is the version that runs everywhere: every gated example is compiled,
// decompiled twice (literally and compacted), and both are recompiled. The
// compiled documents must be byte-identical — the invariant the whole pass
// rests on, checked on content anyone can see.
//
// KNOWN BLIND SPOT: this corpus does not contain a single dotted assignment,
// so terseAssignments — the pass written specifically for `Relationships.X`
// counters — never fires here. A green run says nothing about it. That shape
// is covered by table instead, next to the pass itself:
// internal/lvns/compact_terse_test.go. Before trusting this gate for a pass,
// check the corpus actually contains the shape the pass looks for.

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

func gatedExamples(t *testing.T) []string {
	t.Helper()
	root := filepath.Join("..", "..", "..")
	var files []string
	for _, pat := range []string{
		filepath.Join(root, "howto", "*", "*.lvns"),
		filepath.Join(root, "examples", "*.lvns"),
	} {
		got, err := filepath.Glob(pat)
		if err != nil {
			t.Fatalf("glob %s: %v", pat, err)
		}
		files = append(files, got...)
	}
	if len(files) == 0 {
		t.Fatal("no gated examples found under howto/ or examples/")
	}
	return files
}

func asArticy(d *lvns.Doc) *articy.Doc {
	out := &articy.Doc{Scene: d.Scene}
	for _, c := range d.Script {
		out.Script = append(out.Script, articy.Cmd(c))
	}
	return out
}

// TestGatedExamplesCompactionIsLossless is the invariant, stated as a test:
// compaction may rewrite the source however it likes, but the document it
// compiles to must not move by one byte.
func TestGatedExamplesCompactionIsLossless(t *testing.T) {
	for _, f := range gatedExamples(t) {
		src, err := os.ReadFile(f)
		if err != nil {
			t.Fatalf("%s: %v", f, err)
		}
		doc, err := lvns.Convert(string(src))
		if err != nil {
			t.Fatalf("%s: gated example does not compile: %v", f, err)
		}
		raw := toLvnsRaw(asArticy(doc))
		compact := ToLvns(asArticy(doc))

		rawDoc, err := lvns.Convert(string(raw))
		if err != nil {
			t.Errorf("%s: literal decompile does not recompile: %v", filepath.Base(f), err)
			continue
		}
		compactDoc, err := lvns.Convert(string(compact))
		if err != nil {
			t.Errorf("%s: compacted decompile does not recompile: %v", filepath.Base(f), err)
			continue
		}
		a, _ := json.Marshal(rawDoc)
		b, _ := json.Marshal(compactDoc)
		if string(a) != string(b) {
			t.Errorf("%s: compaction changed the compiled document\n--- compacted source ---\n%s", filepath.Base(f), compact)
		}
	}
}

// TestGatedExamplesDecompileRecompiles pins the OTHER half: whatever the
// decompiler writes has to be a source file the compiler accepts. Six of the
// corpus chapters used to fail this outright (`anim: prop required`) because
// an animation's structured payload had no spelling in the flat k=v form.
func TestGatedExamplesDecompileRecompiles(t *testing.T) {
	for _, f := range gatedExamples(t) {
		src, err := os.ReadFile(f)
		if err != nil {
			t.Fatalf("%s: %v", f, err)
		}
		doc, err := lvns.Convert(string(src))
		if err != nil {
			continue
		}
		out := ToLvns(asArticy(doc))
		if _, err := lvns.Convert(string(out)); err != nil {
			t.Errorf("%s: decompiled source does not recompile: %v", filepath.Base(f), err)
		}
		// And the story itself has to survive: same commands, same order,
		// once the documented control-flow lowering is set aside.
		back, err := lvns.Convert(string(out))
		if err != nil {
			continue
		}
		want, got := storyOps(doc.Script), storyOps(back.Script)
		if len(want) != len(got) {
			t.Errorf("%s: %d story commands became %d", filepath.Base(f), len(want), len(got))
			continue
		}
		for i := range want {
			x, _ := json.Marshal(want[i])
			y, _ := json.Marshal(got[i])
			if string(x) != string(y) {
				t.Errorf("%s: command %d drifted\n  was %s\n  now %s", filepath.Base(f), i, x, y)
				break
			}
		}
	}
}

// storyOps drops label/goto/if — the decompiler prunes unreferenced labels and
// fall-through jumps on purpose, and mints its own for the arrow-if form.
func storyOps(s []lvns.Cmd) []lvns.Cmd {
	var out []lvns.Cmd
	for _, c := range s {
		switch c["op"] {
		case "label", "goto", "if":
			continue
		}
		out = append(out, c)
	}
	return out
}

// TestCompactionActuallyCompacts guards the other direction: a pass that
// silently stopped firing would keep every invariant and deliver nothing.
func TestCompactionActuallyCompacts(t *testing.T) {
	var raw, compact int
	for _, f := range gatedExamples(t) {
		src, err := os.ReadFile(f)
		if err != nil {
			t.Fatal(err)
		}
		doc, err := lvns.Convert(string(src))
		if err != nil {
			continue
		}
		raw += len(toLvnsRaw(asArticy(doc)))
		compact += len(ToLvns(asArticy(doc)))
	}
	t.Logf("gated examples: decompiled %d bytes → %d compacted (%.1f%%)",
		raw, compact, 100*float64(compact-raw)/float64(raw))
	if compact >= raw {
		t.Errorf("compaction saved nothing on the gated examples (%d → %d)", raw, compact)
	}
	// A spot check that the mechanism is the documented one.
	doc, err := lvns.Convert(strings.Join([]string{
		"actor id=\"Vera\" position=\"right\" sprite_url=\"/art/vera.png\" enter=\"fade\" show=true emotion=\"happy\"",
		"actor id=\"Vera\" position=\"right\" sprite_url=\"/art/vera.png\" enter=\"fade\" show=true emotion=\"sad\"",
		"actor id=\"Vera\" position=\"right\" sprite_url=\"/art/vera.png\" enter=\"fade\" show=true",
	}, "\n"))
	if err != nil {
		t.Fatal(err)
	}
	out := string(ToLvns(asArticy(doc)))
	if !strings.Contains(out, "def Vera_in actor ") || !strings.Contains(out, "\nVera_in emotion=\"happy\"\n") {
		t.Errorf("expected a Vera_in preset and terse call sites, got:\n%s", out)
	}
}
