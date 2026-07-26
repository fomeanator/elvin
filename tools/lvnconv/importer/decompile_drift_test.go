package importer

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// TestCorpusDriftKinds classifies WHAT still drifts on a decompile→recompile
// over the corpus, and shows one example of each class. It is a diagnostic,
// not a gate: it prints, it does not fail. TestCorpusDecompileFidelity is the
// one that counts, and the gated-example tests are the ones that run in CI.
//
// As of the compaction work the remaining classes are all PARSER limits, not
// decompiler choices: a string "false"/"true" or a string "1" cannot be
// spelled in a `k=v` pair (parseKeyValue coerces them), and a say text with
// real newlines is flattened by oneLine.
func TestCorpusDriftKinds(t *testing.T) {
	files := corpusFiles(t)
	kinds := map[string]int{}
	samples := map[string]string{}
	for _, f := range files {
		raw, _ := os.ReadFile(f)
		a, err := lvns.Convert(string(raw))
		if err != nil {
			continue
		}
		doc := docFromLvns(string(raw))
		b, err := lvns.Convert(string(toLvnsRaw(doc)))
		if err != nil {
			kinds["RECOMPILE-ERROR"]++
			samples["RECOMPILE-ERROR"] = filepath.Base(f) + ": " + err.Error()
			continue
		}
		// Control-flow lowering (pruned labels, dropped fall-through gotos,
		// synthetic if-else labels) is documented and deliberate — strip it so
		// what remains is the STORY.
		strip := func(s []lvns.Cmd) []lvns.Cmd {
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
		a.Script, b.Script = strip(a.Script), strip(b.Script)
		n := len(a.Script)
		if len(b.Script) != n {
			kinds[fmt.Sprintf("LEN %d→%d", n, len(b.Script))]++
			continue
		}
		for i := range a.Script {
			x, _ := json.Marshal(a.Script[i])
			y, _ := json.Marshal(b.Script[i])
			if string(x) == string(y) {
				continue
			}
			ao, _ := a.Script[i]["op"].(string)
			bo, _ := b.Script[i]["op"].(string)
			key := ao + "→" + bo
			// which field differs?
			var fields []string
			all := map[string]bool{}
			for k := range a.Script[i] {
				all[k] = true
			}
			for k := range b.Script[i] {
				all[k] = true
			}
			for k := range all {
				u, _ := json.Marshal(a.Script[i][k])
				v, _ := json.Marshal(b.Script[i][k])
				if string(u) != string(v) {
					fields = append(fields, k)
				}
			}
			sort.Strings(fields)
			key += " [" + fmt.Sprint(fields) + "]"
			kinds[key]++
			if _, seen := samples[key]; !seen {
				samples[key] = filepath.Base(f) + " #" + fmt.Sprint(i) + "\n  A=" + string(x) + "\n  B=" + string(y)
			}
		}
	}
	type kv struct {
		k string
		n int
	}
	var list []kv
	for k, n := range kinds {
		list = append(list, kv{k, n})
	}
	sort.Slice(list, func(i, j int) bool { return list[i].n > list[j].n })
	for _, e := range list {
		t.Logf("%6d  %s\n        %s", e.n, e.k, samples[e.k])
	}
}
