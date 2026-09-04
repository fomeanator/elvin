package lvn

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// ГЕЙТ СОДЕРЖИМОГО СУДИТ ПО КОДУ ВОЗВРАТА, А НЕ ПО ТЕКСТУ ВЫВОДА.
//
// Прежняя редакция гейта жила прямо в workflow и сверяла вывод валидатора с
// образцом " 0 warning(s)". На битом скрипте валидатор печатает
// «FAIL: 1 error(s), 0 warning(s)» — строка образцу УДОВЛЕТВОРЯЕТ, а код
// возврата терялся в подстановке $(…). Замерено: глава с опечаткой в имени
// команды проходила гейт целиком, то есть страж раздаваемых образцов пропускал
// ровно то, ради чего заведён.
//
// Отсюда два правила, и оба структурные:
//
//	один экземпляр   гейт живёт в qa/lvns-gate.sh, workflow его ЗОВЁТ;
//	                 копия в YAML снова разойдётся и снова молча;
//	не по тексту     ни один workflow не смеет сравнивать вывод lvnconv со
//	                 строкой — счётчики в ней меняются, а смысл теряется.
//
// Гейт обязан уметь падать: у скрипта есть -selftest, подкладывающий ту самую
// опечатку и требующий отказа. Workflow зовёт сперва его.
func TestContentGateLivesInOnePlaceAndJudgesByExitCode(t *testing.T) {
	root := repoRoot(t)

	gate := filepath.Join(root, "qa", "lvns-gate.sh")
	body, err := os.ReadFile(gate)
	if err != nil {
		t.Fatalf("гейт содержимого пропал (%v) — раздаваемые образцы никто не сторожит", err)
	}
	for _, must := range []string{"-selftest", "validate -strict"} {
		if !strings.Contains(string(body), must) {
			t.Errorf("qa/lvns-gate.sh потерял %q — гейт перестал быть строгим или разучился падать", must)
		}
	}

	dir := filepath.Join(root, ".github", "workflows")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("workflow-файлы не прочитаны: %v", err)
	}

	seen, callsGate := 0, false
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".yml") {
			continue
		}
		seen++
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			t.Fatalf("%s: %v", e.Name(), err)
		}
		// Судим ИСПОЛНЯЕМОЕ, а не прозу: строки-комментарии выкидываем. Иначе
		// объяснение прежней ошибки, цитирующее сломанный образец, само
		// краснило бы стража — поймано первым же прогоном.
		text := withoutComments(string(raw))

		// Счётчик в сообщении инструмента — не приговор. Сравнение с ним
		// проходит на «FAIL: 1 error(s), 0 warning(s)», и это уже случалось.
		if strings.Contains(text, "warning(s)") {
			t.Errorf("%s судит вывод lvnconv по тексту (\"warning(s)\") — "+
				"строка отказа удовлетворяет образцу; судить надо кодом возврата", e.Name())
		}
		if strings.Contains(text, "qa/lvns-gate.sh") {
			callsGate = true
			if !strings.Contains(text, "lvns-gate.sh -selftest") {
				t.Errorf("%s зовёт гейт, но не его самопроверку — "+
					"зелёный гейт, не умеющий падать, ничего не значит", e.Name())
			}
		}
	}

	sawSources(t, seen, 1, "workflow-файлов")
	if !callsGate {
		t.Error("гейт содержимого не зовёт ни один workflow — он существует, но не стоит на пути")
	}
}

// withoutComments убирает строки, которые целиком комментарий, — и в YAML, и в
// шелле внутри `run:` они начинаются одинаково.
func withoutComments(s string) string {
	var kept []string
	for _, ln := range strings.Split(s, "\n") {
		if strings.HasPrefix(strings.TrimSpace(ln), "#") {
			continue
		}
		kept = append(kept, ln)
	}
	return strings.Join(kept, "\n")
}
