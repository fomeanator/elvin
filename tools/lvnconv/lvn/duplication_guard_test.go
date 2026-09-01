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

// Пакеты, за которыми следим.
//
// СЛЕДИЛИ ЗА ТРЕМЯ РАНТАЙМАМИ — и мимо стража оставались редакторские
// инструменты (компилятор .lvns, импортёр, сборка APK) и два отдельных пакета.
// Это тоже код движка, и через компилятор проходит ВСЁ, что пишет автор: копия
// правила там стоит дороже, а не дешевле. Пусто там сегодня — тем более незачем
// оставлять дыру в охране.
var dupRoots = []string{
	filepath.Join("unity", "Packages", "com.lvn.engine", "Runtime"),
	filepath.Join("unity", "Packages", "com.lvn.engine", "Editor"),
	filepath.Join("unity", "Packages", "com.lvn.engine.shell", "Runtime"),
	filepath.Join("unity", "Packages", "com.lvn.engine.services", "Runtime"),
	filepath.Join("unity", "Packages", "com.lvn.engine.spine", "Runtime"),
	filepath.Join("unity", "Packages", "com.lvn.engine.addressables", "Runtime"),
}

type dupSite struct{ file, method string }

func TestNoDuplicatedMethodBodies(t *testing.T) {
	scanned := 0
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
			scanned++
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

	// Порог пустоты: обход, не нашедший ни одного файла, зеленеет ни о чём.
	atLeast(t, scanned, 150, "просмотренных файлов")

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

// ПОЧТИ-ДУБЛЬ. Вторая половина стража, и она ловит то, чего не видит первая.
//
// `DownscaleIfOversized` жил третьей копией в загрузчике контента и отличался
// одной строкой (не отпускал копию пикселей — «финализирует вызывающий»). Тела
// не совпали дословно, и проверка выше промолчала, а копия оставалась копией:
// правку в одной пришлось бы повторять в трёх местах.
//
// Признак: ОДИНАКОВОЕ ИМЯ плюс ПОХОЖЕЕ тело. Одного имени мало — у разных
// классов законно бывают свои OnPointerDown и FlushAsync, и они ничего общего
// не имеют. Похожесть считаем по доле совпавших строк.
type dupBody struct {
	file  string
	lines map[string]bool
	size  int
}

// similarity — доля строк меньшего тела, встречающихся в большем.
func similarity(a, b dupBody) float64 {
	small, big := a, b
	if small.size > big.size {
		small, big = big, small
	}
	if small.size == 0 {
		return 0
	}
	hit := 0
	for l := range small.lines {
		if big.lines[l] {
			hit++
		}
	}
	return float64(hit) / float64(len(small.lines))
}

func bodyLines(body string) (map[string]bool, int) {
	out := map[string]bool{}
	n := 0
	for _, l := range strings.Split(body, "\n") {
		l = strings.TrimSpace(dupComment.ReplaceAllString(l, ""))
		if l == "" || l == "{" || l == "}" {
			continue
		}
		out[l] = true
		n++
	}
	return out, n
}

func TestNoNearDuplicateMethods(t *testing.T) {
	root := capsRepoRoot()
	byName := map[string][]dupBody{}

	for _, rel := range dupRoots {
		dir := filepath.Join(root, rel)
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		_ = filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
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
				lines, n := bodyLines(body)
				if n < 6 {
					continue // короткие обёртки совпадают случайно
				}
				byName[name] = append(byName[name], dupBody{filepath.Base(path), lines, n})
			}
			return nil
		})
	}

	// Порог на ОХВАТ, а не на группу: `byName` — методы, сгруппированные по
	// имени, и два одноимённых это норма. Считаем все тела.
	total := 0
	for _, bodies := range byName {
		total += len(bodies)
	}
	atLeast(t, total, 300, "тел методов")

	var offenders []string
	for name, bodies := range byName {
		for i := 0; i < len(bodies); i++ {
			for j := i + 1; j < len(bodies); j++ {
				if bodies[i].file == bodies[j].file {
					continue
				}
				if sim := similarity(bodies[i], bodies[j]); sim >= 0.75 {
					offenders = append(offenders, fmt.Sprintf("%s — %s и %s совпадают на %.0f%%",
						name, bodies[i].file, bodies[j].file, sim*100))
				}
			}
		}
	}
	sort.Strings(offenders)

	for _, o := range offenders {
		t.Errorf("почти-копия: %s\n"+
			"    Тела разошлись на строку-другую, поэтому дословная проверка молчит,\n"+
			"    но копия остаётся копией: правку придётся вносить дважды.\n"+
			"    Сведите в общий дом, оставив различие параметром.", o)
	}
}
