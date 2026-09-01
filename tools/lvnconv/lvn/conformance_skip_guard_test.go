package lvn

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Случай, не идущий в браузер, обязан объяснить почему.
//
// В корпусе согласия поле `runtimes` перечисляет реализации, которые случай
// проверяют. Отсутствие «js» выглядит одинаково в двух совершенно разных
// ситуациях: браузерный порт этого НЕ УМЕЕТ (законно) — и про него просто
// забыли (расхождение, которое корпус и заведён ловить).
//
// Пока причина не написана, отличить их нельзя, а значит нельзя и заметить,
// когда «не умеет» превратится в «уже умеет, но никто не включил».
func TestCasesOutsideBrowserExplainThemselves(t *testing.T) {
	scanned := 0
	root := repoRoot(t)
	dir := filepath.Join(root, "conformance", "cases")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("корпус не читается: %v", err)
	}
	var mute []string
	for _, e := range entries {
		scanned++
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		b, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			t.Fatalf("%s: %v", e.Name(), err)
		}
		var c struct {
			Runtimes []string `json:"runtimes"`
			JsSkip   string   `json:"js_skip"`
		}
		if err := json.Unmarshal(b, &c); err != nil {
			t.Fatalf("%s: %v", e.Name(), err)
		}
		inBrowser := false
		for _, r := range c.Runtimes {
			if r == "js" {
				inBrowser = true
			}
		}
		if !inBrowser && strings.TrimSpace(c.JsSkip) == "" {
			mute = append(mute, e.Name())
		}
	}
	if len(mute) > 0 {
		t.Fatalf("случаи не идут в браузер и не говорят почему:\n  %s\n\nДобавьте поле \"js_skip\""+
			" с причиной: «порт этого не умеет» и «про него забыли» выглядят одинаково,"+
			" и без причины не заметить, когда первое станет вторым.",
			strings.Join(mute, "\n  "))
	}
	// Порог пустоты: обход, не нашедший ни одного файла, зеленеет ни о чём.
	atLeast(t, scanned, 20, "разобранных случаев")

}
