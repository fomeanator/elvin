package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// Молчание об ошибке — решение, и оно обязано быть ПОДПИСАНО.
//
// Пустой catch honest чаще, чем кажется: удалить недокачанный файл, оборвать
// запрос, разбудить ожидание — если уборка не удалась, делать с этим нечего.
// Беда в том, что по коду не отличить осознанное молчание от забытого, а
// разница огромная: первое решение, второе потерянная ошибка. Дом для подписи
// кодом есть (`LvnQuiet.Try`), но живых мест вчетверо больше, чем его вызовов,
// и почти все они подписаны словами — комментарием рядом.
//
// Поэтому страж требует не конкретной формы, а ПОДПИСИ в любом виде: либо
// `LvnQuiet.Try`, либо объяснение в той же строке или на следующей. Требовать
// один лишь `LvnQuiet` значило бы переписать три десятка честных мест ради
// формы; требовать хоть чего-нибудь — значит закрыть единственный настоящий
// случай, «catch {} и ни слова».
func TestSilentCatchIsSigned(t *testing.T) {
	scanned := 0
	root := repoRoot(t)

	empty := regexp.MustCompile(`catch\s*(\([^)]*\)\s*)?\{\s*\}`)
	var found []string

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
			lines := strings.Split(string(b), "\n")
			for i, ln := range lines {
				if !empty.MatchString(ln) {
					continue
				}
				tail := ln[strings.LastIndex(ln, "catch"):]
				next := ""
				if i+1 < len(lines) {
					next = lines[i+1]
				}
				signed := strings.Contains(tail, "//") || strings.Contains(ln, "/*") ||
					strings.Contains(next, "//")
				if !signed {
					found = append(found, fmt.Sprintf("%s:%d", filepath.Base(path), i+1))
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	atLeast(t, scanned, 60, "просмотренных файлов")

	if len(found) > 0 {
		t.Fatalf("молчание об ошибке без подписи: %s\n"+
			"скажите ОДНОЙ строкой, почему здесь нечего делать, — или заверните "+
			"в LvnQuiet.Try: иначе через месяц не отличить решение от забытого catch",
			strings.Join(found, ", "))
	}
}
