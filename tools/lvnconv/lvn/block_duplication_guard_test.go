package lvn

import (
	"crypto/md5"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ПОВТОР ВНУТРИ МЕТОДА — тоже повтор.
//
// Соседний страж (duplication_guard_test.go) держит целые тела методов. Но
// копия редко бывает целым методом: чаще это КУСОК, вставленный в середину
// чужого. Ревизия 31.08 нашла шесть таких — и каждый оказался правилом,
// записанным дважды:
//
//   - часы анимационного канала (петля, качание, конец, равномерная скорость
//     вдоль сплайна) — у плоской фигуры и у трёхмерной;
//   - «предмет можно тащить» — у обычной фигуры и у костяной;
//   - обнуление счёта пакетной загрузки, шесть полей — в конце пакета и в
//     конце одиночной загрузки;
//   - «следующая глава» — у загрузчика и у оболочки;
//   - «на чём остановились» — у метки прогресса и у облачного свёртка, причём
//     РАЗНЫМ порядком поиска: на новелле с частично сменившимися id они
//     возвращали разные главы;
//   - лист вкладки хаба — в профиле и в лавке.
//
// Ни один из них не падает. Они расходятся молча — и находит это игрок.
//
// Порог — восемь значащих строк подряд. Шесть уже дают случайные совпадения
// (хвост отступов одного блока плюс начало другого), семь чисты, восьмая —
// запас. Пустые строки, комментарии, скобки и `using` не в счёт.
func TestНетПовторовБлоковМеждуФайлами(t *testing.T) {
	const window = 8
	root := capsRepoRoot()

	space := regexp.MustCompile(`\s+`)
	noise := map[string]bool{
		"{": true, "}": true, "});": true, "};": true, ")": true,
		"else": true, "return;": true, "break;": true, "continue;": true,
	}
	skip := func(t string) bool {
		return t == "" || strings.HasPrefix(t, "//") || strings.HasPrefix(t, "using ") ||
			strings.HasPrefix(t, "namespace") || strings.HasPrefix(t, "#") || noise[t]
	}

	type site struct {
		file string
		line int
	}
	blocks := map[string][]site{}

	for _, rel := range dupRoots {
		dir := filepath.Join(root, rel)
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			data, rerr := os.ReadFile(path)
			if rerr != nil {
				return nil
			}
			type row struct {
				n int
				s string
			}
			var code []row
			for i, ln := range strings.Split(string(data), "\n") {
				s := space.ReplaceAllString(strings.TrimSpace(ln), " ")
				if skip(s) {
					continue
				}
				code = append(code, row{i + 1, s})
			}
			base := filepath.Base(path)
			for i := 0; i+window <= len(code); i++ {
				var sb strings.Builder
				for _, r := range code[i : i+window] {
					sb.WriteString(r.s)
					sb.WriteByte('\n')
				}
				sum := md5.Sum([]byte(sb.String()))
				key := hex.EncodeToString(sum[:])
				blocks[key] = append(blocks[key], site{base, code[i].n})
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", rel, err)
		}
	}

	// Один и тот же блок в РАЗНЫХ файлах. Повтор внутри одного файла оставляем
	// его хозяину: там копия видна глазами и часто это развёрнутый цикл.
	reported := map[string]bool{}
	var found []string
	for _, sites := range blocks {
		files := map[string]int{}
		for _, s := range sites {
			if _, ok := files[s.file]; !ok {
				files[s.file] = s.line
			}
		}
		if len(files) < 2 {
			continue
		}
		names := make([]string, 0, len(files))
		for f := range files {
			names = append(names, f)
		}
		sort.Strings(names)
		key := strings.Join(names, "|")
		if reported[key] {
			continue // тот же набор файлов уже назван — окна перекрываются
		}
		reported[key] = true
		var where []string
		for _, f := range names {
			where = append(where, fmt.Sprintf("%s:%d", f, files[f]))
		}
		found = append(found, strings.Join(where, " ↔ "))
	}

	if len(found) > 0 {
		sort.Strings(found)
		t.Errorf("восемь строк подряд повторяются в разных файлах — значит правило записано дважды:\n  %s\n"+
			"  Дай ему дом: имя метода вместо копии. Копии не падают, они РАСХОДЯТСЯ —\n"+
			"  и находит это игрок, а не тест.",
			strings.Join(found, "\n  "))
	}
}
