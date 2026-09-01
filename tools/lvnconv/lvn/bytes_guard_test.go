package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// Размер игроку показывает один дом.
//
// Перевод байтов в подпись стоял восемью экземплярами и по трём правилам: центр
// загрузок считал по-умному (до ста мегабайт — с десятыми), настройки и ворота
// главы рубили целыми через сдвиг с Max(1,…), чтобы четыреста килобайт не стали
// «0 МБ». Один объём получал разные подписи на соседних экранах, а игрок по ним
// решает, хватит ли трафика.
//
// Признак работы «показать размер» — сдвиг на 20 (или деление на 1048576) рядом
// с подписью единицы. В логах это норма: там читает разработчик, и ему нужны
// круглые мегабайты, а не «0,4».
func TestByteSizeHasOneHome(t *testing.T) {
	scanned := 0
	root := repoRoot(t)

	allowed := map[string]string{
		"LvnBytes.cs": "сам дом",
	}

	shiftRe := regexp.MustCompile(`>> 20|/ 1048576|/ \(1024 \* 1024\)`)
	unitRe := regexp.MustCompile(`unit\.mb|unit\.gb|"MB"|"МБ"|"GB"|"ГБ"`)
	logRe := regexp.MustCompile(`Debug\.Log|LogWarning|LogError|Trace\(`)
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
			base := filepath.Base(path)
			if _, ok := allowed[base]; ok {
				return nil
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			lines := strings.Split(string(b), "\n")
			for i, ln := range lines {
				if !shiftRe.MatchString(ln) || logRe.MatchString(ln) {
					continue
				}
				window := ln
				if i+1 < len(lines) {
					window += "\n" + lines[i+1]
				}
				if unitRe.MatchString(window) {
					found = append(found, fmt.Sprintf("%s:%d", base, i+1))
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	sort.Strings(found)
	atLeast(t, scanned, 60, "просмотренных файлов")

	if len(found) > 0 {
		t.Fatalf("размер переводят в подпись мимо дома: %s\n"+
			"возьмите LvnBytes.Short/Approx — там записано, почему мелкое с десятыми, "+
			"крупное целыми, а нуля не бывает у непустого файла",
			strings.Join(found, ", "))
	}
}
