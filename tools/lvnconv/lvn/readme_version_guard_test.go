package lvn

import (
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"testing"
)

// ВЕРСИЯ В ЛИЦЕ ПРОЕКТА ОБЯЗАНА СОВПАДАТЬ С НАСТОЯЩЕЙ.
//
// Репозиторий публичный, и README — первое, что читают. Замер 04.09: там
// стояло `v0.9`, тогда как пакет объявлял 0.11.0, а последний тег был
// v0.11.0. Оценивающий делает вывод о зрелости по трём сигналам — версия,
// журнал, дата релиза, — и все три занижали.
//
// Расходится это молча: версию пакета поднимают при выпуске, строку в README
// правят руками, и второе однажды забывают. Здесь они связаны.
func TestВерсияВReadmeСовпадаетСПакетом(t *testing.T) {
	root := repoRoot(t)

	raw, err := os.ReadFile(filepath.Join(root, "unity/Packages/com.lvn.engine/package.json"))
	if err != nil {
		t.Fatalf("не читается манифест пакета: %v", err)
	}
	var pkg struct {
		Version string `json:"version"`
	}
	if err := json.Unmarshal(raw, &pkg); err != nil || pkg.Version == "" {
		t.Fatalf("в манифесте пакета нет версии: %v", err)
	}

	readme, err := os.ReadFile(filepath.Join(root, "README.md"))
	if err != nil {
		t.Fatalf("не читается README: %v", err)
	}
	m := regexp.MustCompile("(?m)^\\*\\*Status:\\*\\* `v([0-9][^`]*)`").FindSubmatch(readme)
	if m == nil {
		t.Fatal("в README нет строки статуса с версией — страж потерял предмет охраны")
	}
	if got := string(m[1]); got != pkg.Version {
		t.Errorf("README объявляет v%s, пакет — %s.\n"+
			"Первое, что читают о проекте, занижает его возраст: версию пакета "+
			"поднимают при выпуске, а строку в README правят руками.", got, pkg.Version)
	}
}
