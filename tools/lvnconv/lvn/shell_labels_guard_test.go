package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// Надписи движка не пишутся на одном языке насильно.
//
// Русские слова были вписаны в код оболочки: подписи игровой панели, весь экран
// настроек, центр загрузок, склонение «глава/главы/глав». Дом для слов
// (LvnWords) при этом существовал и был описан ровно как «откуда берётся ЛЮБАЯ
// подпись» — мимо него ходили 200+ строк. Любая другая новелла получала русский
// интерфейс, и обойти его автор не мог ничем.
//
// Страж смотрит только на то, что становится НАДПИСЬЮ. Диагностика на русском —
// законна и нарочна: её читает разработчик, а не игрок.
func TestShellLabelsGoThroughTheWordBook(t *testing.T) {
	scanned := 0
	root := repoRoot(t)
	// Строка попадает на экран: её отдают в конструктор надписи или кладут в text.
	makers := regexp.MustCompile(`\btext\s*[:=]|Label\(|Button\(|ModalButton|GameButton|` +
		`SectionTitle|SectionHeader|RowEx|InfoCell|Pill\(|FlashText|Hint\(|VolumeRow|` +
		`SwitchRow|RangeRow|SaveRow|Progress\(|Status\(|Alert|Confirm|Title\s*=|` +
		`Message\s*=|AddUnique|Enqueue\(`)
	// Диагностика — не надпись.
	diag := regexp.MustCompile(`Debug\.|LvnLog|Tooltip|nameof|Trace\(|Warning|Error|Watch\(`)
	cyrillic := regexp.MustCompile(`"[^"]*[а-яА-ЯёЁ][^"]*"`)

	var hard []string
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell", "com.lvn.engine.services"} {
		dir := filepath.Join(root, "unity", "Packages", pkg, "Runtime")
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			scanned++
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			for i, line := range strings.Split(string(b), "\n") {
				code := line
				if c := strings.Index(code, "//"); c >= 0 {
					code = code[:c]
				}
				if !cyrillic.MatchString(code) || diag.MatchString(code) || !makers.MatchString(code) {
					continue
				}
				rel, _ := filepath.Rel(root, path)
				hard = append(hard, fmt.Sprintf("%s:%d: %s", rel, i+1, strings.TrimSpace(line)))
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}
	atLeast(t, scanned, 60, "просмотренных файлов")

	if len(hard) > 0 {
		t.Fatalf("надписи вписаны в код по-русски:\n  %s\n\nБерите слово из LvnWords.Of(ключ, английское умолчание)"+
			" и кладите русское в ui.words манифеста — иначе новелла на другом языке получит эти слова насильно.",
			strings.Join(hard, "\n  "))
	}
}
