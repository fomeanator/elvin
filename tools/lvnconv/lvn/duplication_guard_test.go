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

// ОДНО ПОНЯТИЕ — ОДИН ДОМ. Страж против копипасты в рантайме движка.
//
// Ревизия 21.08 нашла пятнадцать мест, где тело метода повторялось дословно в
// разных файлах: разбор цвета жил в пяти домах, разбор чисел в трёх, скругление
// углов было скопировано в тринадцать экранов, а описание кости — в два.
// Опасна не сама копия, а то, что она РАСХОДИТСЯ молча: ровно так проценты в
// координатах понимались деревом `ui` и терялись у актёров, а `actor scale=`
// оказался объявленным, но нигде не применённым.
//
// Дубли сведены до нуля. Этот тест держит ноль: он не даёт коду вернуться в
// прежнее состояние тихо, по одной копии за раз.
//
// Порог намеренно грубый (тело от 90 значащих символов): короткие геттеры и
// однострочные обёртки совпадают у всех и ничего не говорят.

var dupSig = regexp.MustCompile(`(?m)(?:private|internal|public|protected)[\w\s<>\[\],\?\.]*?\s(\w+)\s*\([^)]*\)\s*\{`)
var dupComment = regexp.MustCompile(`//.*`)
var dupSpace = regexp.MustCompile(`\s+`)

// Пакеты, за которыми следим: рантайм движка, оболочки и сервисов.
var dupRoots = []string{
	filepath.Join("unity", "Packages", "com.lvn.engine", "Runtime"),
	filepath.Join("unity", "Packages", "com.lvn.engine.shell", "Runtime"),
	filepath.Join("unity", "Packages", "com.lvn.engine.services", "Runtime"),
}

type dupSite struct{ file, method string }

func TestNoDuplicatedMethodBodies(t *testing.T) {
	root := capsRepoRoot()
	bodies := map[string][]dupSite{}

	for _, rel := range dupRoots {
		dir := filepath.Join(root, rel)
		if _, err := os.Stat(dir); err != nil {
			continue // пакет не выложен рядом — тесту нечего проверять
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			data, rerr := os.ReadFile(path)
			if rerr != nil {
				return nil
			}
			src := string(data)
			for _, m := range dupSig.FindAllStringSubmatchIndex(src, -1) {
				name := src[m[2]:m[3]]
				body, ok := braceBody(src, m[1]-1)
				if !ok {
					continue
				}
				norm := dupSpace.ReplaceAllString(dupComment.ReplaceAllString(body, ""), " ")
				norm = strings.TrimSpace(norm)
				if len(norm) < 90 {
					continue
				}
				bodies[norm] = append(bodies[norm], dupSite{filepath.Base(path), name})
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", rel, err)
		}
	}

	var offenders []string
	for _, sites := range bodies {
		files := map[string]bool{}
		for _, s := range sites {
			files[s.file] = true
		}
		if len(files) < 2 {
			continue
		}
		var parts []string
		for _, s := range sites {
			parts = append(parts, fmt.Sprintf("%s:%s", s.file, s.method))
		}
		sort.Strings(parts)
		offenders = append(offenders, strings.Join(parts, " | "))
	}
	sort.Strings(offenders)

	for _, o := range offenders {
		t.Errorf("одно тело метода в разных файлах — %s\n"+
			"    Копия расходится молча: правку вносят в один дом и забывают про второй.\n"+
			"    Вынесите общее в один дом (примеры: UiColor — цвет, LvnNum — числа,\n"+
			"    LvnUrl — адреса, LvnChrome — огранка, AssetMemory — память арта,\n"+
			"    LvnOverlayScreen — жизненный цикл экрана).", o)
	}
}

// braceBody возвращает тело от открывающей скобки на позиции i.
func braceBody(src string, i int) (string, bool) {
	if i < 0 || i >= len(src) || src[i] != '{' {
		return "", false
	}
	depth := 0
	for j := i; j < len(src); j++ {
		switch src[j] {
		case '{':
			depth++
		case '}':
			depth--
			if depth == 0 {
				return src[i : j+1], true
			}
		}
	}
	return "", false
}
