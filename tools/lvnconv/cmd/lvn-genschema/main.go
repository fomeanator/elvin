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
	// ДВА ИСХОДНИКА, ОДНА СХЕМА. Облик описан в LvnUiConfig, каталог — в
	// LvnManifest; для игрока это один файл, и гейт обязан знать обе половины.
	base := filepath.Join(root, "..", "..", "unity", "Packages", "com.lvn.engine", "Runtime", "Content")
	schema := lvn.ManifestSchema{}
	for _, name := range []string{"LvnUiConfig.cs", "LvnManifest.cs"} {
		raw, err := os.ReadFile(filepath.Join(base, name))
		if err != nil {
			fmt.Fprintln(os.Stderr, "не читается", name, err)
			os.Exit(1)
		}
		for cls, fields := range lvn.ScrapeManifestSchema(string(raw)) {
			if _, clash := schema[cls]; clash {
				fmt.Fprintln(os.Stderr, "класс", cls, "объявлен в обоих исходниках — снимок был бы неоднозначен")
				os.Exit(1)
			}
			schema[cls] = fields
		}
	}
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
