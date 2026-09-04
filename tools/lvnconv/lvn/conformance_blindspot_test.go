package lvn

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// СЛЕПОТА, ЗАПИСАННАЯ В КОММЕНТАРИИ, — ЭТО НЕ ЗАЩИТА.
//
// `ops-owners.json` честно говорит: браузерный прогон сверяет остановки,
// переменные и выражения, а блок `expect.scene` не смотрит ВОВСЕ. Решение
// осознанное — достраивать проверку значит вкладываться в замороженный
// рантайм.
//
// Но запись не мешает написать сценический случай с `js` в runtimes: он
// пройдёт, не проверив ничего, и его зелёный цвет будет означать «браузер
// ставит сцену правильно», хотя браузер её даже не смотрел. Самый дорогой сорт
// теста — тот, который врёт уверенно.
//
// Замер 04.09: таких случаев ноль. Правило удерживает этот ноль.
func TestСценическийСлучайНеОбъявляетБраузер(t *testing.T) {
	dir := filepath.Join(repoRoot(t), "conformance", "cases")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("корпус не читается: %v", err)
	}

	checked := 0
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			t.Fatalf("%s: %v", e.Name(), err)
		}
		var one map[string]any
		var many []map[string]any
		if err := json.Unmarshal(raw, &many); err != nil {
			if err := json.Unmarshal(raw, &one); err != nil {
				t.Fatalf("%s не разбирается: %v", e.Name(), err)
			}
			many = []map[string]any{one}
		}
		for _, c := range many {
			checked++
			if !declaresRuntime(c, "js") {
				continue
			}
			if expectsScene(c) {
				t.Errorf("%s: случай объявляет рантайм «js» и проверяет сцену.\n"+
					"Браузерный прогон блок expect.scene НЕ СМОТРИТ (см. ops-owners.json) —\n"+
					"случай пройдёт, ничего не проверив, и его зелёный цвет будет врать.\n"+
					"Либо уберите «js» из runtimes, либо уберите проверку сцены.", e.Name())
			}
		}
	}
	if checked == 0 {
		t.Fatal("не прочитано ни одного случая — страж потерял корпус и молчит впустую")
	}
}

func declaresRuntime(c map[string]any, want string) bool {
	rts, _ := c["runtimes"].([]any)
	for _, r := range rts {
		if s, _ := r.(string); s == want {
			return true
		}
	}
	return false
}

func expectsScene(c map[string]any) bool {
	exp, _ := c["expect"].(map[string]any)
	if exp == nil {
		return false
	}
	_, ok := exp["scene"]
	return ok
}
