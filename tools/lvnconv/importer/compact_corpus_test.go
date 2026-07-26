package importer

// compact_corpus_test.go — the measuring stick for the .lvns compaction pass.
//
// It runs the panel's real round trip over a directory of .lvns files
// (source → lvns.Convert → decompile → source') and reports how much noise the
// compactor removed, while asserting the only property that matters: the
// COMPILED document is byte-identical with and without compaction.
//
// The corpus this was tuned on is partner content that does not live in git,
// so the test is opt-in: point LVN_CORPUS at a directory of .lvns files
// (e.g. server/content/scripts) to run it. Without it the test skips.

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

type corpusStat struct {
	name                    string
	baseLines, compactLines int
	baseBytes, compactBytes int
	baseRepeat, compRepeat  int // bytes on lines byte-identical to an earlier line
	baseStage, compStage    int // bytes spent on staging statements
	defs                    int
}

func docFromLvns(src string) *articy.Doc {
	d, err := lvns.Convert(src)
	if err != nil {
		return nil
	}
	out := &articy.Doc{Scene: d.Scene}
	for _, c := range d.Script {
		out.Script = append(out.Script, articy.Cmd(c))
	}
	return out
}

func compiledJSON(src string) ([]byte, error) {
	d, err := lvns.Convert(src)
	if err != nil {
		return nil, err
	}
	return json.Marshal(d)
}

// lineStats counts the file, the noise in it (bytes spent on lines that repeat
// one already written earlier — the shape of the partner's complaint) and how
// many bytes go to STAGING rather than to the story.
func lineStats(src string) (lines, bytes, repeat, stage, defs int) {
	seen := map[string]bool{}
	for _, l := range strings.Split(strings.TrimRight(src, "\n"), "\n") {
		t := strings.TrimSpace(l)
		if t == "" {
			continue
		}
		lines++
		bytes += len(l) + 1
		if seen[t] {
			repeat += len(l) + 1
		}
		seen[t] = true
		if strings.HasPrefix(t, "def ") {
			defs++
		}
		// Everything that is not dialogue, narration, a label, a jump or a
		// choice option is staging: the author's overhead per line of story.
		if isStagingLine(t) {
			stage += len(l) + 1
		}
	}
	return
}

// isStagingLine: a statement (or a preset call) rather than story text.
func isStagingLine(t string) bool {
	w := t
	if i := strings.IndexAny(t, " \t"); i > 0 {
		w = t[:i]
	}
	switch {
	case strings.HasPrefix(t, ":"), strings.HasPrefix(t, "-"), strings.HasPrefix(t, "#"),
		strings.HasPrefix(t, "{"), t == "}":
		return true
	case lvns.KnownOps[w], w == "def", w == "scene", w == "actor_map":
		return true
	case strings.Contains(t, ": "):
		return false // dialogue
	}
	// A preset call site is a bare identifier optionally followed by k=v.
	if strings.ContainsAny(w, "«»") {
		return false
	}
	rest := strings.TrimSpace(strings.TrimPrefix(t, w))
	return rest == "" || strings.Contains(rest, "=")
}

func corpusFiles(t *testing.T) []string {
	t.Helper()
	dir := os.Getenv("LVN_CORPUS")
	if dir == "" {
		t.Skip("set LVN_CORPUS=<dir with .lvns files> to run the corpus benchmark")
	}
	files, err := filepath.Glob(filepath.Join(dir, "*.lvns"))
	if err != nil || len(files) == 0 {
		t.Skipf("no .lvns in %s", dir)
	}
	sort.Strings(files)
	return files
}

// TestCorpusDecompileFidelity measures the decompiler's fidelity over the
// corpus: compile a source, decompile it (compaction and all), recompile — is
// the STORY the same command for command? Control flow is excluded: pruning
// unreferenced labels and fall-through gotos is the documented lowering, and
// counting it as drift would flag every legitimate file.
func TestCorpusDecompileFidelity(t *testing.T) {
	files := corpusFiles(t)
	clean, drifted, skipped, cmds, badCmds := 0, 0, 0, 0, 0
	for _, f := range files {
		raw, err := os.ReadFile(f)
		if err != nil {
			t.Fatal(err)
		}
		first, err := lvns.Convert(string(raw))
		if err != nil {
			skipped++
			continue
		}
		second, err := lvns.Convert(string(ToLvns(docFromLvns(string(raw)))))
		if err != nil {
			t.Errorf("%s: decompiled source does not recompile: %v", filepath.Base(f), err)
			drifted++
			continue
		}
		a, b := storyOps(first.Script), storyOps(second.Script)
		cmds += len(a)
		if len(a) != len(b) {
			drifted++
			t.Errorf("%s: %d story commands became %d", filepath.Base(f), len(a), len(b))
			continue
		}
		bad := 0
		for i := range a {
			x, _ := json.Marshal(a[i])
			y, _ := json.Marshal(b[i])
			if string(x) != string(y) {
				bad++
			}
		}
		badCmds += bad
		if bad > 0 {
			drifted++
		} else {
			clean++
		}
	}
	t.Logf("story fidelity: %d files clean, %d with drift, %d source-uncompilable; %d/%d commands drifted (%.4f%%)",
		clean, drifted, skipped, badCmds, cmds, 100*float64(badCmds)/float64(cmds+1))
}

// TestCorpusVerifierAcceptsCompacted runs the importer's own safety net
// (VerifyLvnsRoundTrip — what the CLI and both server import endpoints report
// on) over every compacted sidecar the corpus produces.
func TestCorpusVerifierAcceptsCompacted(t *testing.T) {
	files := corpusFiles(t)
	flagged := 0
	for _, f := range files {
		raw, err := os.ReadFile(f)
		if err != nil {
			t.Fatal(err)
		}
		doc := docFromLvns(string(raw))
		if doc == nil {
			continue
		}
		if w := VerifyLvnsRoundTrip(doc.Script, ToLvns(doc)); len(w) > 0 {
			flagged++
			t.Errorf("%s: %v", filepath.Base(f), w)
		}
	}
	t.Logf("VerifyLvnsRoundTrip: %d/%d sidecars flagged", flagged, len(files))
}

func TestCompactCorpus(t *testing.T) {
	files := corpusFiles(t)

	var tot corpusStat
	var rows []corpusStat
	skipped := 0
	for _, f := range files {
		raw, rerr := os.ReadFile(f)
		if rerr != nil {
			t.Fatalf("%s: %v", f, rerr)
		}
		doc := docFromLvns(string(raw))
		if doc == nil {
			skipped++
			continue // source itself does not compile — not our business here
		}
		base := string(toLvnsRaw(doc))
		compact := string(ToLvns(doc))

		bj, berr := compiledJSON(base)
		if berr != nil {
			t.Errorf("%s: BASELINE does not recompile: %v", filepath.Base(f), berr)
			continue
		}
		cj, cerr := compiledJSON(compact)
		if cerr != nil {
			t.Errorf("%s: COMPACT does not recompile: %v", filepath.Base(f), cerr)
			continue
		}
		if string(bj) != string(cj) {
			t.Errorf("%s: compaction changed the compiled document", filepath.Base(f))
			continue
		}

		var st corpusStat
		st.name = filepath.Base(f)
		st.baseLines, st.baseBytes, st.baseRepeat, st.baseStage, _ = lineStats(base)
		st.compactLines, st.compactBytes, st.compRepeat, st.compStage, st.defs = lineStats(compact)
		rows = append(rows, st)
		tot.baseLines += st.baseLines
		tot.compactLines += st.compactLines
		tot.baseBytes += st.baseBytes
		tot.compactBytes += st.compactBytes
		tot.baseRepeat += st.baseRepeat
		tot.compRepeat += st.compRepeat
		tot.baseStage += st.baseStage
		tot.compStage += st.compStage
		tot.defs += st.defs
	}

	sort.Slice(rows, func(i, j int) bool {
		return rows[i].baseBytes-rows[i].compactBytes > rows[j].baseBytes-rows[j].compactBytes
	})
	n := len(rows)
	if n > 10 {
		n = 10
	}
	for _, r := range rows[:n] {
		t.Logf("%-24s lines %6d→%6d  bytes %8d→%8d (%+.1f%%)",
			r.name, r.baseLines, r.compactLines, r.baseBytes, r.compactBytes,
			100*float64(r.compactBytes-r.baseBytes)/float64(r.baseBytes+1))
	}
	t.Logf("FILES %d (skipped %d), presets emitted %d", len(rows), skipped, tot.defs)
	t.Logf("TOTAL lines %d→%d  bytes %d→%d (%+.2f%%)",
		tot.baseLines, tot.compactLines, tot.baseBytes, tot.compactBytes,
		100*float64(tot.compactBytes-tot.baseBytes)/float64(tot.baseBytes+1))
	t.Logf("REPEATED bytes (on lines byte-identical to one already written): %d→%d (%.1f%%→%.1f%% of the file)",
		tot.baseRepeat, tot.compRepeat,
		100*float64(tot.baseRepeat)/float64(tot.baseBytes),
		100*float64(tot.compRepeat)/float64(tot.compactBytes))
	t.Logf("STAGING bytes (everything that is not the story): %d→%d (%.1f%%→%.1f%% of the file)",
		tot.baseStage, tot.compStage,
		100*float64(tot.baseStage)/float64(tot.baseBytes),
		100*float64(tot.compStage)/float64(tot.compactBytes))
}
