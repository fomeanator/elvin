package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Цвет из строки разбирается ровно в одном месте — UiColor.
//
// Дом уже был, но половина жильцов ходила мимо: в ОДНОМ файле `rim_color` шёл
// через UiColor, а соседний `glow_color` — напрямую через Unity. Разница
// невидима в коде и видна автору: hex без решётки один эффект красил, другой
// молча пропускал. Страж не даёт разбору расселиться снова.
func TestColorParsingLivesInOneHome(t *testing.T) {
	scanned := 0
	root := repoRoot(t)
	home := filepath.Join("Runtime", "UI", "UiColor.cs")
	var strays []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			scanned++
			if strings.HasSuffix(path, home) {
				return nil
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			for i, line := range strings.Split(string(b), "\n") {
				if strings.Contains(line, "TryParseHtmlString") {
					rel, _ := filepath.Rel(root, path)
					strays = append(strays, fmt.Sprintf("%s:%d", rel, i+1))
				}
			}
			return nil
		})
	if err != nil {
		t.Fatalf("обход пакетов: %v", err)
	}
	atLeast(t, scanned, 60, "просмотренных файлов")

	if len(strays) > 0 {
		t.Fatalf("цвет разбирают мимо UiColor:\n  %s\n\nЗовите UiColor.Parse/TryParse/FromCmd:"+
			" иначе соседние поля одной команды начнут понимать разное написание.",
			strings.Join(strays, "\n  "))
	}
}
