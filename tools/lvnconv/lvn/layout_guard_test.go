package lvn

import (
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

// ЧЕГО НЕ ХВАТАЕТ РАСКЛАДКЕ — сводка в одном месте.
//
// Стражи вежливы: не нашёл Unity-пакет — пропущу, нет node — пропущу, нет
// server/content — пропущу. Каждый пропуск разумен по отдельности, а вместе
// они дают зелёный прогон, который не проверил ничего. Хуже всего, что узнать
// об этом неоткуда: пропуск выглядит как успех и в выводе `go test`, и в
// отчёте Unity (`qa/run-all.sh` до сих пор считал Skipped за Passed).
//
// Этот тест ничего не проверяет по существу — он ПЕЧАТАЕТ, что именно
// отсутствует, и потому какие стражи сегодня промолчали. При
// `LVN_REQUIRE_LAYOUT=1` (машина разработчика, CI репозитория) неполная
// раскладка становится ошибкой: там всё обязано быть на месте.
func TestLayoutIsComplete(t *testing.T) {
	root := repoRoot(t)

	need := map[string]string{
		"unity/Packages/com.lvn.engine":          "движок: без него молчат dialect/conformance/shell-стражи",
		"unity/Packages/com.lvn.engine.shell":    "оболочка: молчат стражи подписей и слов",
		"howto":                                  "примеры автора: молчит doc-gate свидетелей",
		"examples":                               "второй источник свидетелей (ui-demo и другие)",
		"conformance/cases":                      "корпус соответствия: молчат оба рантайм-прогона",
		"panel/public/play/core.js":              "браузерный плеер: молчат его корпус и контракт диспетчеризации",
		"tools/lvnconv/internal/lvns/convert.go": "транскодер: без него не с чем сверять C#-порт",
		"unity/Packages/com.lvn.engine/Editor/LvnsCompiler.cs": "C#-порт: молчит golden-корпус",
	}

	var missing []string
	for rel, why := range need {
		if _, err := os.Stat(filepath.Join(root, filepath.FromSlash(rel))); err != nil {
			missing = append(missing, rel+" — "+why)
		}
	}
	sort.Strings(missing)

	if len(missing) == 0 {
		return
	}
	msg := "раскладка неполна, эти проверки сегодня молчат:\n  " + strings.Join(missing, "\n  ")
	if os.Getenv("LVN_REQUIRE_LAYOUT") != "" {
		t.Fatal(msg + "\nокружение требует полной раскладки (LVN_REQUIRE_LAYOUT)")
	}
	t.Log(msg)
}
