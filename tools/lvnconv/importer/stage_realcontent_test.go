package importer

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sort"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
)

// Verification on REAL partner content: run the control-flow visibility
// normalizer over every committed cold-*.lvn and count the defensive hides it
// injects — each is a branch-merge point where two actors would otherwise share
// a slot (the "two characters in one slot" bug). Gated on the partner content
// being present, so a clean clone stays green.
func TestNormalizeVisibilityOnColdContent(t *testing.T) {
	root := filepath.Join("..", "..", "..", "server", "content", "scripts")
	files, _ := filepath.Glob(filepath.Join(root, "cold-*.lvn"))
	if len(files) == 0 {
		t.Skip("нет партнёрского cold-контента на этой машине")
	}
	sort.Strings(files)
	totalBefore, totalInjected, chaptersWithFix := 0, 0, 0
	for _, f := range files {
		raw, err := os.ReadFile(f)
		if err != nil {
			continue
		}
		var doc struct {
			Script []map[string]any `json:"script"`
		}
		if json.Unmarshal(raw, &doc) != nil {
			continue
		}
		script := make([]articy.Cmd, len(doc.Script))
		for i, c := range doc.Script {
			script[i] = articy.Cmd(c)
		}
		fixed := normalizeStageVisibility(script)
		injected := len(fixed) - len(script) // normalize only ADDS hides
		totalBefore += len(script)
		totalInjected += injected
		if injected > 0 {
			chaptersWithFix++
			t.Logf("%s: +%d защитных hide", filepath.Base(f), injected)
		}
	}
	t.Logf("ИТОГО: %d глав, %d команд, вставлено %d защитных hide в %d главах",
		len(files), totalBefore, totalInjected, chaptersWithFix)
	if totalInjected == 0 {
		t.Log("коллизий слияния веток в cold не найдено (или контент уже переимпортирован)")
	}
}
