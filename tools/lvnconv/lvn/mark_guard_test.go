package lvn

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// ПАСПОРТИСТ выдаёт метки один, и это не вкусовщина.
//
// Метка запуска стояла двумя экземплярами — 16 знаков в аналитике и 12 у
// отправщика логов, — и событие «сбой» нельзя было свести с логом этого сбоя.
// Постоянная метка выдавалась по трём рецептам, из которых только один
// (идентификатор игрока) имел второй дом; остальные теряли вместе с prefs
// учётку игрока и его покупки.
//
// Страж ловит возврат к ручной выдаче: Guid, превращённый в строку без
// разделителей, — это всегда метка, а метки живут у Lvn.LvnMark.
func TestМеткиВыдаётПаспортист(t *testing.T) {
	scanned := 0
	root := repoRoot(t)
	pkgs := filepath.Join(root, "unity", "Packages")

	var bad []string
	err := filepath.Walk(pkgs, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
			return nil
		}
		scanned++
		// Сам дом и его тесты — единственные, кому метку порождать положено.
		if strings.HasSuffix(path, "LvnMark.cs") || strings.Contains(path, "/Tests/") {
			return nil
		}
		b, err := os.ReadFile(path)
		if err != nil {
			return nil
		}
		for i, line := range strings.Split(string(b), "\n") {
			if strings.Contains(line, `Guid.NewGuid().ToString("N")`) {
				rel, _ := filepath.Rel(root, path)
				bad = append(bad, rel+":"+itoa(i+1)+" "+strings.TrimSpace(line))
			}
		}
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	atLeast(t, scanned, 60, "просмотренных файлов")

	if len(bad) > 0 {
		t.Errorf("метка выдана мимо паспортиста (%d):\n  %s\n\n"+
			"Метку на запуск берут у Lvn.LvnMark.Run — она ОДНА, иначе событие\n"+
			"аналитики и строку лога того же запуска не сшить.\n"+
			"Постоянную — у LvnMark.Steady(имя): два дома, потому что потеря prefs\n"+
			"иначе отнимает у игрока учётку вместе с кошельком.\n"+
			"Разовую — у LvnMark.Once().",
			len(bad), strings.Join(bad, "\n  "))
	}
}
