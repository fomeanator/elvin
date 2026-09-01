package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ПАРА ОДИНАКОВЫХ ОТСТУПОВ ИДЁТ ЧЕРЕЗ ДОМ ВОЗДУХА.
//
// Отдельным стражем, а не следом обхода, по единственной причине: следы
// читают файл ПОСТРОЧНО, а пара живёт в двух строках подряд. Проверка
// мутацией это и вскрыла — след ловил только однострочные пары, то есть ровно
// те написания, которых почти не было. Пятьдесят четыре блока по четыре
// строки он бы пропустил.
//
// Ловится именно ПАРА С ОДИНАКОВЫМ ЗНАЧЕНИЕМ: «слева столько же, сколько
// справа» — это одно решение, записанное дважды, и правится оно по одной
// строке («поправил три, забыл четвёртую»). Разные значения по осям законны:
// у крестика воздух слева и справа правда разный. Подпись НАРОЧНО снимает
// вопрос там, где асимметрия намеренная.
func TestPaddingPairsGoThroughAir(t *testing.T) {
	root := repoRoot(t)
	assign := regexp.MustCompile(`^\s*([\w.\[\]_]*?)\.?style\.(padding|margin)(Left|Right|Top|Bottom)\s*=\s*(.+?);\s*$`)

	scanned, pairs := 0, 0
	var found []string
	for _, rel := range storageRoots {
		err := filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			slash := filepath.ToSlash(path)
			if strings.Contains(slash, "/Tests/") || strings.HasSuffix(slash, "LvnAir.cs") {
				return nil
			}
			scanned++
			relPath, _ := filepath.Rel(root, path)
			lines := strings.Split(string(mustRead(t, path)), "\n")
			// ключ = получатель+вид+сторона, значение = выражение и строка
			type slot struct {
				expr string
				line int
			}
			for i := 0; i < len(lines); i++ {
				seen := map[string]slot{}
				j := i
				for ; j < len(lines); j++ {
					m := assign.FindStringSubmatch(lines[j])
					if m == nil {
						break
					}
					seen[m[1]+"|"+m[2]+"|"+m[3]] = slot{m[4], j + 1}
				}
				if j == i {
					continue
				}
				ctx := strings.Join(lines[i:j], "\n")
				if strings.Contains(ctx, "НАРОЧНО") {
					i = j
					continue
				}
				for key, a := range seen {
					parts := strings.Split(key, "|")
					var other string
					switch parts[2] {
					case "Left":
						other = "Right"
					case "Top":
						other = "Bottom"
					default:
						continue // пару считаем один раз, от левой/верхней стороны
					}
					b, ok := seen[parts[0]+"|"+parts[1]+"|"+other]
					if !ok || b.expr != a.expr {
						continue
					}
					pairs++
					found = append(found, fmt.Sprintf("%s:%d — %s%s и %s%s равны, а записаны порознь",
						filepath.ToSlash(relPath), a.line, parts[1], parts[2], parts[1], other))
				}
				i = j
			}
			return nil
		})
		if err != nil {
			t.Fatal(err)
		}
	}
	atLeast(t, scanned, 60, "просмотренных файлов")
	if len(found) > 0 {
		t.Errorf("пары одинаковых отступов мимо LvnAir (%d):\n  %s\n\n"+
			"LvnAir.Pad/PadX/PadY: ось называется словом, и одно решение стоит одной строкой.\n"+
			"Если стороны различаются НАМЕРЕННО — подпишите НАРОЧНО рядом.",
			len(found), strings.Join(found, "\n  "))
	}
}
