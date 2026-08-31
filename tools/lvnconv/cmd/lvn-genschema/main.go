// Генератор схемы манифеста из C#-DTO.
//
// Схема живёт в ОДНОМ месте — `LvnUiConfig.cs`. Её копия, написанная руками,
// стала бы очередным зеркалом, которое разойдётся: этой болезнью занят весь
// разбор 31.08. Поэтому копия не пишется, а СНИМАЕТСЯ, и её свежесть держит
// страж (как у grammar.js).
//
//	go run ./cmd/lvn-genschema
package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"

	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

func main() {
	root, err := os.Getwd()
	if err != nil {
		panic(err)
	}
	src := filepath.Join(root, "..", "..", "unity", "Packages", "com.lvn.engine",
		"Runtime", "Content", "LvnUiConfig.cs")
	raw, err := os.ReadFile(src)
	if err != nil {
		fmt.Fprintln(os.Stderr, "не читается", src, err)
		os.Exit(1)
	}
	schema := lvn.ScrapeManifestSchema(string(raw))
	if len(schema) < 15 {
		fmt.Fprintf(os.Stderr, "снялось всего %d классов — похоже, разбор промахнулся\n", len(schema))
		os.Exit(1)
	}
	out, err := json.MarshalIndent(schema, "", "  ")
	if err != nil {
		panic(err)
	}
	dst := filepath.Join(root, "lvn", "manifest-fields.json")
	if err := os.WriteFile(dst, append(out, '\n'), 0o644); err != nil {
		panic(err)
	}
	fmt.Printf("снято %d классов из LvnUiConfig.cs → lvn/manifest-fields.json\n", len(schema))
}
