package lvn

import (
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// ОБРАЗЦЫ ПРОДОЛЖЕНИЯ НЕ ДОЛЖНЫ РАСХОДИТЬСЯ С КОМПИЛЯТОРОМ.
//
// ResumeAfterEditTests играет НАСТОЯЩИЙ вывод lvnconv, вложенный в тест
// строкой. В этом вся его ценность: между рукописным JSON и живой главой лежат
// компилятор и формат контейнера, и якорь может потеряться именно там.
//
// Но вложенная строка — это снимок, а снимок стареет молча: компилятор
// поменяет форму вывода, C#-тест останется зелёным на вчерашнем JSON, и
// проверка превратится в украшение. Здесь исходники пересобираются заново и
// сверяются с тем, что вложено.
func TestОбразцыПродолженияСвежие(t *testing.T) {
	root := repoRoot(t)
	cs, err := os.ReadFile(filepath.Join(root,
		"unity/Packages/com.lvn.engine/Tests/Editor/ResumeAfterEditTests.cs"))
	if err != nil {
		t.Fatalf("не читается тест продолжения: %v", err)
	}

	for _, f := range []struct{ konst, file string }{
		{"Before", "before"},
		{"After", "after"},
	} {
		src, err := os.ReadFile(filepath.Join(root, "qa/fixtures/resume", f.file+".lvns"))
		if err != nil {
			t.Fatalf("не читается исходник %s.lvns: %v", f.file, err)
		}
		doc, err := lvns.Convert(string(src))
		if err != nil {
			t.Fatalf("%s.lvns не компилируется: %v", f.file, err)
		}
		fresh, err := json.Marshal(doc)
		if err != nil {
			t.Fatalf("%s: вывод компилятора не сериализуется: %v", f.file, err)
		}

		embedded := constOf(t, string(cs), f.konst)
		if !sameJSON(t, embedded, string(fresh)) {
			t.Errorf("образец %s разошёлся с компилятором.\n"+
				"C#-тест играет вчерашний JSON, и его зелёный цвет ничего не значит.\n"+
				"Перегенерировать вложенные образцы из qa/fixtures/resume/%s.lvns.\n"+
				"  вложено: %s\n  свежее:  %s", f.konst, f.file, cut(embedded), cut(string(fresh)))
		}
	}
}

var constRe = regexp.MustCompile(`(?m)^\s*private const string (\w+)\s*=\s*"((?:[^"\\]|\\.)*)";`)

func constOf(t *testing.T, src, name string) string {
	t.Helper()
	for _, m := range constRe.FindAllStringSubmatch(src, -1) {
		if m[1] == name {
			return strings.NewReplacer(`\"`, `"`, `\\`, `\`).Replace(m[2])
		}
	}
	t.Fatalf("в тесте нет константы %s — образец потерян", name)
	return ""
}

// Сравнение по СМЫСЛУ, а не по байтам: порядок ключей в JSON не значим, и
// придираться к нему значило бы краснеть на безобидной смене сериализатора.
func sameJSON(t *testing.T, a, b string) bool {
	t.Helper()
	var x, y any
	if json.Unmarshal([]byte(a), &x) != nil || json.Unmarshal([]byte(b), &y) != nil {
		return false
	}
	ax, _ := json.Marshal(x)
	by, _ := json.Marshal(y)
	return string(ax) == string(by)
}

func cut(s string) string {
	if len(s) > 120 {
		return s[:120] + "…"
	}
	return s
}
