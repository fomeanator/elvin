package main

// `lvnconv locale` — build and refresh the per-language string catalogs the
// runtime loads beside a script (`<script>.<lang>.json`, see LvnPlayer.Strings).
// Inline-authored text is keyed by the source string itself (gettext-style);
// articy imports are keyed by their stable text_id — both live in one catalog.
// Re-running after a script edit keeps existing translations, prefills new
// lines with the source text (a loaded catalog must never render a blank), and
// reports what a translator still owes.

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

func cmdLocale(args []string) {
	fs := newFlagSet("locale")
	langs := fs.String("lang", "", "target language code(s), comma-separated: -lang ru or -lang ru,en")
	check := fs.Bool("check", false, "report catalog coverage without writing (exit 1 when a catalog misses keys)")
	prune := fs.Bool("prune", false, "drop catalog keys the script no longer contains")
	_ = fs.Parse(args)
	if *langs == "" || fs.NArg() == 0 {
		die("locale: usage: lvnconv locale -lang <code>[,<code>…] [-check] [-prune] <script.lvns|script.lvn>…")
	}

	fail := false
	for _, path := range fs.Args() {
		doc := loadScript(path)
		keys := lvn.ExtractStrings(doc)
		base := strings.TrimSuffix(strings.TrimSuffix(path, ".lvns"), ".lvn")
		for _, lang := range strings.Split(*langs, ",") {
			lang = strings.TrimSpace(lang)
			if lang == "" {
				continue
			}
			catPath := base + "." + lang + ".json"
			existing := readCatalog(catPath)
			merged, st := lvn.MergeCatalog(existing, keys, *prune)
			report(catPath, st)
			if *check {
				if len(st.Missing) > 0 {
					fail = true
				}
				continue
			}
			if err := os.WriteFile(catPath, encodeCatalog(keys, merged), 0o644); err != nil {
				die("locale: " + err.Error())
			}
		}
	}
	if fail {
		os.Exit(1)
	}
}

// loadScript parses a .lvn, or compiles a .lvns in memory first — catalogs are
// extracted from the compiled form either way, so both spellings of a chapter
// yield identical keys.
func loadScript(path string) *lvn.Doc {
	if !strings.HasSuffix(path, ".lvns") {
		return loadLvn(path)
	}
	src, err := os.ReadFile(path)
	if err != nil {
		die("locale: " + err.Error())
	}
	compiled, err := lvns.Convert(string(src))
	if err != nil {
		die("locale: " + path + ": " + err.Error())
	}
	doc, err := lvn.Parse(mustJSON(compiled))
	if err != nil {
		die("locale: " + path + ": " + err.Error())
	}
	return doc
}

func readCatalog(path string) map[string]string {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil // no catalog yet — every key is new
	}
	var m map[string]string
	if err := json.Unmarshal(data, &m); err != nil {
		die("locale: " + path + ": " + err.Error())
	}
	return m
}

// encodeCatalog emits the catalog in script order (translators read it top to
// bottom alongside the story), with any kept stale keys sorted at the end.
func encodeCatalog(keys []string, merged map[string]string) []byte {
	var stale []string
	inScript := map[string]bool{}
	for _, k := range keys {
		inScript[k] = true
	}
	for k := range merged {
		if !inScript[k] {
			stale = append(stale, k)
		}
	}
	sort.Strings(stale)

	buf := &bytes.Buffer{}
	buf.WriteString("{\n")
	all := append(append([]string{}, keys...), stale...)
	for i, k := range all {
		kj, _ := json.Marshal(k)
		vj, _ := json.Marshal(merged[k])
		fmt.Fprintf(buf, "  %s: %s", kj, vj)
		if i < len(all)-1 {
			buf.WriteString(",")
		}
		buf.WriteString("\n")
	}
	buf.WriteString("}\n")
	return buf.Bytes()
}

func report(catPath string, st lvn.CatalogStats) {
	fmt.Fprintf(os.Stderr, "%s: %d string(s), %d new, %d stale, %d untranslated\n",
		catPath, st.Total, len(st.Missing), len(st.Stale), st.Untranslated)
}
