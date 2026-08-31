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

// КАРТА ДОМОВ ОПИСЫВАЕТ КОД, А НЕ ПАМЯТЬ О НЁМ.
//
// `docs/where-things-live.md` — не обзорная статья, а канон: по нему решают,
// куда класть новое правило, и по нему же ищут, где живёт старое. Пока она ни с
// чем не сверялась, отставать ей ничто не мешало — и она отставала: строка про
// размеры файлов не знала ключа, который код читает уже давно, а строка про
// шрифты не знала, что путь остался ровно один.
//
// Врущая карта хуже отсутствующей: по отсутствующей идут читать код, по врущей —
// уверенно делают не то.
//
// Страж держит ровно одно: КАЖДЫЙ НАЗВАННЫЙ В КАРТЕ ДОМ СУЩЕСТВУЕТ. Обратное
// (каждый класс движка описан) он не требует и требовать не должен — домом
// становится не всякий класс, и карта из трёхсот строк перестала бы читаться.
func TestEveryHomeInTheMapExists(t *testing.T) {
	root := repoRoot(t)
	mapPath := filepath.Join(root, "docs", "where-things-live.md")
	b, err := os.ReadFile(mapPath)
	if err != nil {
		t.Fatalf("карта домов не найдена: %v", err)
	}

	// Собираем всё, что вообще есть в исходниках движка: имена файлов и имена
	// типов. Дом может называться и тем и другим — `LvnChrome` лежит в
	// LvnChrome.cs, а `ContentLoader.SpriteCache` — вложенный тип.
	known := map[string]bool{}
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell", "com.lvn.engine.services"} {
		dir := filepath.Join(root, "unity", "Packages", pkg)
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			base := strings.TrimSuffix(filepath.Base(path), ".cs")
			// Партиалы: «VnStage.Actors.cs» объявляет и «VnStage», и тему
			// «Actors» — карта называет дом любой из этих частей
			// («ContentLoader.SpriteCache» — память под картинки).
			known[base] = true
			for _, part := range strings.Split(base, ".") {
				known[part] = true
			}
			src, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			for _, m := range typeDecl.FindAllStringSubmatch(string(src), -1) {
				known[m[1]] = true
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	// Только таблицы ДОМОВ: ниже по документу такой же таблицей описаны
	// глаголы записной книжки (`Put`, `Flush`), а они не дома и в коде их
	// ищут не по имени класса.
	text := string(b)
	if i := strings.Index(text, "## Что игра помнит между запусками"); i > 0 {
		text = text[:i]
	}
	// Первая колонка таблицы: | `Имя` | … — и только она.
	rowHome := regexp.MustCompile("(?m)^\\|\\s*`([^`]+)`\\s*\\|")
	var missing []string
	seen := map[string]bool{}
	for _, m := range rowHome.FindAllStringSubmatch(text, -1) {
		name := strings.TrimSpace(m[1])
		// «LvnRedress (ILvnRedress)» — дом и его интерфейс в одной клетке;
		// «LvnNum / LvnBool» — два дома одной темы; «ContentLoader.SpriteCache»
		// — вложенный. Проверяем каждое имя по отдельности.
		for _, part := range splitHomeNames(name) {
			if part == "" || seen[part] {
				continue
			}
			seen[part] = true
			if !knownHome(known, part) {
				missing = append(missing, part)
			}
		}
	}

	// ПОРОГ, ЧТОБЫ СТРАЖ НЕ ПРОВЕРИЛ ПУСТОТУ. Сместись якорь разбора таблицы
	// (переименуют заголовок, сменят разметку) — множество имён окажется
	// пустым, и страж радостно позеленеет, не проверив ничего. Сегодня это уже
	// случилось в соседнем месте: скрапер схемы молча терял семнадцать классов
	// из сорока пяти, и обе сверявшиеся стороны врали одинаково.
	if len(seen) < 80 {
		t.Fatalf("в карте домов разобрано всего %d имён — якорь таблицы промахнулся, "+
			"и страж проверил бы пустоту", len(seen))
	}
	if len(known) < 200 {
		t.Fatalf("в коде найдено всего %d имён типов — обход исходников промахнулся, "+
			"и любой дом объявился бы несуществующим", len(known))
	}

	if len(missing) > 0 {
		sort.Strings(missing)
		t.Fatalf("карта домов называет то, чего в коде нет: %s\n"+
			"дом переименовали или убрали, а строку в docs/where-things-live.md — нет. "+
			"Канон, которому нельзя верить, дороже отсутствующего: по нему уверенно делают не то",
			strings.Join(missing, ", "))
	}
}

var typeDecl = regexp.MustCompile(`(?m)^\s*(?:public|internal|private|protected)?\s*(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|ref\s+)*(?:class|struct|interface|enum)\s+(\w+)`)

// splitHomeNames разбирает клетку первой колонки на отдельные имена: скобки,
// косая черта и точка внутри имени вложенного типа.
func splitHomeNames(cell string) []string {
	cell = strings.NewReplacer("(", " ", ")", " ", "/", " ", ",", " ").Replace(cell)
	var out []string
	for _, f := range strings.Fields(cell) {
		if f = strings.TrimSpace(f); f != "" {
			out = append(out, f)
		}
	}
	return out
}

// knownHome — существует ли то, что названо в карте.
//
// Имя с точкой значит одно из двух, и оба законны: ВЛОЖЕННЫЙ ТИП
// (`ContentLoader.SpriteCache` — тип по последней части) и МЕХАНИЗМ ДОМА
// (`VnStage.ShowsBackdrop` — метод у известного типа). Раньше проверялась
// только последняя часть, поэтому вторая запись объявлялась несуществующей, и
// карта не могла назвать механизм иначе как целым классом — хотя ровно так её
// и пишут: «дом отвечает за это, и вот его глагол».
func knownHome(known map[string]bool, name string) bool {
	if known[name] {
		return true
	}
	i := strings.LastIndex(name, ".")
	if i <= 0 || i+1 >= len(name) {
		return false
	}
	return known[name[i+1:]] || known[name[:strings.Index(name, ".")]]
}

// Пустая проверка формы: карта обязана оставаться таблицей, иначе страж выше
// молча перестанет что-либо находить и будет зелёным ни о чём.
func TestHomesMapIsStillATable(t *testing.T) {
	root := repoRoot(t)
	b, err := os.ReadFile(filepath.Join(root, "docs", "where-things-live.md"))
	if err != nil {
		t.Fatalf("карта домов не найдена: %v", err)
	}
	rows := regexp.MustCompile("(?m)^\\|\\s*`([^`]+)`\\s*\\|").FindAllString(string(b), -1)
	if len(rows) < 40 {
		t.Fatal(fmt.Sprintf("в карте домов разобрано %d строк — таблица сломалась "+
			"или разъехалась, и страж существования домов проверяет пустоту", len(rows)))
	}
}
