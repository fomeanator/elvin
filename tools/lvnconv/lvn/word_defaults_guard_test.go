package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// У одного ключа — одно английское умолчание, и оно не пустое.
//
// Ключ словаря называет подпись, а умолчание — её английский оригинал. Два
// разных умолчания у одного ключа значат, что перевод накрывает ОДНО из двух
// мест, а второе живёт своей жизнью: новелла задала слово, а экран всё равно
// показывает другое. Пустое умолчание ещё хуже — не назвавшая ключ новелла
// получает пустую кнопку.
func TestOneKeyOneDefault(t *testing.T) {
	root := repoRoot(t)
	calls := regexp.MustCompile(`(?:LvnWords\.Of|Word|\bL)\("([^"]+)",\s*"([^"]*)"`)
	seen := map[string]map[string]string{} // ключ → умолчание → где впервые
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell", "com.lvn.engine.services"} {
		dir := filepath.Join(root, "unity", "Packages", pkg, "Runtime")
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			rel, _ := filepath.Rel(root, path)
			for i, line := range strings.Split(string(b), "\n") {
				for _, m := range calls.FindAllStringSubmatch(line, -1) {
					key, def := m[1], m[2]
					if seen[key] == nil {
						seen[key] = map[string]string{}
					}
					if _, ok := seen[key][def]; !ok {
						seen[key][def] = fmt.Sprintf("%s:%d", filepath.ToSlash(rel), i+1)
					}
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	var bad []string
	for key, defs := range seen {
		if _, empty := defs[""]; empty {
			bad = append(bad, fmt.Sprintf("%q: пустое умолчание (%s)", key, defs[""]))
		}
		if len(defs) > 1 {
			var parts []string
			for d, where := range defs {
				parts = append(parts, fmt.Sprintf("%q (%s)", d, where))
			}
			sort.Strings(parts)
			bad = append(bad, fmt.Sprintf("%q: %s", key, strings.Join(parts, " ≠ ")))
		}
	}
	if len(bad) > 0 {
		sort.Strings(bad)
		t.Fatalf("ключ словаря назван по-разному:\n  %s\n\nОдин ключ — одно умолчание:"+
			" иначе перевод накроет одно место, а второе останется английским.",
			strings.Join(bad, "\n  "))
	}
}
