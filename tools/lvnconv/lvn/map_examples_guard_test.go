package lvn

import (
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// «КАК ЗВАТЬ» — ТОЖЕ УТВЕРЖДЕНИЕ О КОДЕ.
//
// Прямая сверка карты читает ПЕРВУЮ колонку: имя дома обязано существовать.
// Последнюю — «как звать» — не читал никто, а именно её копируют. Проверено
// мутацией: выдуманный `LvnWardrobe.ПридуманныйСпособ(...)` проходил молча.
//
// Цена не в опечатке. Переименованный способ оставляет карту показывающей
// старое имя; читатель зовёт его, не находит и заключает, что дом не умеет
// нужного, — и пишет своё. Ровно так заводится второй дом рядом с первым,
// то есть причина почти каждой находки этих суток.
func TestMapExamplesNameRealMembers(t *testing.T) {
	root := repoRoot(t)
	canon := string(mustRead(t, filepath.Join(root, "docs", "where-things-live.md")))

	// Члены по классам: имя класса берём из КОДА (файл держит несколько), а
	// принадлежность — по ближайшему объявлению выше.
	// СТРУКТУРА — ТОЖЕ ДОМ. Считать домом только `class` значило бы не видеть
	// `Placement` (расстановка фигуры — структура), и карта, называющая её
	// члены, объявлялась бы врущей. Хуже: имя `Placement` в движке ЗАНЯТО
	// дважды — второй раз рекламным местом в сервисах, — и страж находил
	// чужого однофамильца, у которого нужного члена, конечно, нет.
	classRe := regexp.MustCompile(`(?:public|internal|private|protected)\s+(?:sealed\s+|static\s+|partial\s+|abstract\s+|readonly\s+|ref\s+)*(?:class|struct|record)\s+(\w+)`)
	member := regexp.MustCompile(`(?:public|internal|private|protected)\s+(?:static\s+|readonly\s+|const\s+|async\s+|override\s+|virtual\s+|sealed\s+|event\s+)*[\w<>\[\],.?]+\s+(\w+)\s*(?:<[^>]*>)?\s*[\(\{;=]`)
	tupleMember := regexp.MustCompile(`(?:public|internal|private|protected)\s+(?:static\s+)?\([^)]*\)\s+(\w+)\s*[\(\{]`)
	members := map[string]map[string]bool{}
	// Части имён файлов: карта законно зовёт партиал («BrowseHub.Feed»,
	// «ContentLoader.Fetch») — это не член, а половина дома.
	fileParts := map[string]bool{"cs": true}
	scanned := 0
	for _, rel := range storageRoots {
		_ = filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			scanned++
			for _, part := range strings.Split(strings.TrimSuffix(filepath.Base(path), ".cs"), ".") {
				fileParts[part] = true
			}
			src := string(mustRead(t, path))
			// ЧЛЕНЫ ФАЙЛА ПРИНАДЛЕЖАТ ДОМУ ФАЙЛА. Привязывать член к
			// БЛИЖАЙШЕМУ объявлению класса нельзя: вложенный приватный тип
			// становится «ближайшим», и настоящие члены дома съезжают на него —
			// так страж объявил выдуманными LvnAppear.Parse, LvnBackend.Ok и
			// LvnExpression.CallFunc. Точная привязка требует счёта скобок;
			// для сверки карты хватает файла: карта говорит «у этого дома есть
			// такое имя», а дом — это файл.
			var names []string
			for _, loc := range classRe.FindAllStringSubmatchIndex(src, -1) {
				names = append(names, src[loc[2]:loc[3]])
			}
			all := map[string]bool{}
			for _, n := range names {
				all[n] = true
			}
			locs := member.FindAllStringSubmatchIndex(src, -1)
			locs = append(locs, tupleMember.FindAllStringSubmatchIndex(src, -1)...)
			for _, loc := range locs {
				all[src[loc[2]:loc[3]]] = true
			}
			for _, n := range names {
				if members[n] == nil {
					members[n] = map[string]bool{}
				}
				for k := range all {
					members[n][k] = true
				}
			}
			return nil
		})
	}
	atLeast(t, scanned, 60, "просмотренных файлов")
	atLeast(t, len(members), 40, "классов с разобранными членами")

	// Примеры вида `Дом.Способ(` или `Дом.Свойство` в любой клетке строки.
	// ЛАТИНИЦА НАРОЧНО: члены C# пишутся ею, а `\w` в RE2 кириллицу не берёт.
	// Мутация русским именем прошла бы молча — не потому, что страж плох, а
	// потому, что такого имени в коде не бывает; проверять надо тем, что
	// бывает.
	// Звёздочка в карте — ПОДСТАНОВКА («LvnMenuStage.Guard*» значит
	// GuardPeriodSeconds и GuardPatienceSeconds), а не имя. Судить о ней
	// нечем, и делать вид, что судим, нельзя.
	example := regexp.MustCompile("`([A-Z]\\w+)\\.(\\w+)(\\*?)")
	checked := 0
	var wrong []string
	seen := map[string]bool{}
	for _, m := range example.FindAllStringSubmatch(canon, -1) {
		cls, mem := m[1], m[2]
		if m[3] == "*" {
			continue
		}
		if members[cls] == nil || seen[cls+"."+mem] || fileParts[mem] {
			continue // дом не наш (или не класс) — судить не о чем
		}
		seen[cls+"."+mem] = true
		checked++
		if !members[cls][mem] {
			wrong = append(wrong, cls+"."+mem)
		}
	}
	atLeast(t, checked, 40, "проверенных примеров")
	sort.Strings(wrong)
	if len(wrong) > 0 {
		t.Errorf("карта зовёт то, чего у дома нет (%d):\n  %s\n\n"+
			"Колонку «как звать» копируют. Переименованный способ оставляет карту показывающей\n"+
			"старое имя; читатель не находит его и решает, что дом не умеет нужного, — и пишет\n"+
			"своё. Так заводится второй дом рядом с первым.",
			len(wrong), strings.Join(wrong, ", "))
	}
}

// ПУТЬ МАНИФЕСТА В КАРТЕ ОБЯЗАН СУЩЕСТВОВАТЬ.
//
// Третья колонка карты называет АВТОРСКИЕ поля — то, что человек напишет в
// манифесте, прочитав строку. Устаревшее имя тут стоит дороже устаревшего имени
// метода: разработчик не найдёт метод и полезет в код, а автор напишет поле,
// увидит, что ничего не изменилось, и решит, что не работает ВОЗМОЖНОСТЬ.
//
// Так и было 02.09: карта обещала `ui.dialogue.panel_sprite` (на деле
// `panel_image`), `ui.dialogue.cps` (на деле `chars_per_second`) и
// `ui.art_quality` — последнее вообще не поле манифеста, а настройка игрока.
//
// Сторожим только пути с приставкой `ui.` и без звёздочки: третья колонка
// мешает поля манифеста с полями операций скрипта и тегами диагностики, и
// судить обо всём подряд значило бы кусать верное.
func TestMapManifestPathsExist(t *testing.T) {
	root := repoRoot(t)
	canon := string(mustRead(t, filepath.Join(root, "docs", "where-things-live.md")))

	var schema map[string]map[string]string
	if err := json.Unmarshal(manifestFieldsJSON, &schema); err != nil {
		t.Fatalf("схема не читается: %v", err)
	}
	leaves := map[string]bool{}
	for _, f := range schema {
		for k := range f {
			leaves[k] = true
		}
	}
	sawSources(t, len(leaves), 100, "полей схемы")

	path := regexp.MustCompile("`(ui\\.[\\w\\.]+)`")
	seen, checked := map[string]bool{}, 0
	var lost []string
	for _, m := range path.FindAllStringSubmatch(canon, -1) {
		p := m[1]
		if seen[p] {
			continue
		}
		seen[p] = true
		checked++
		parts := strings.Split(p, ".")
		if !leaves[parts[len(parts)-1]] {
			lost = append(lost, p)
		}
	}
	sawSources(t, checked, 20, "путей манифеста в карте")
	sort.Strings(lost)
	if len(lost) > 0 {
		t.Errorf("карта называет поля манифеста, которых нет (%d):\n  %s\n\n"+
			"Автор напишет такое поле, увидит, что ничего не изменилось, и решит, "+
			"что не работает возможность. Сверьтесь со схемой (lvn/manifest-fields.json).",
			len(lost), strings.Join(lost, "\n  "))
	}
}

// ЗАКРЫТЫЙ РАЙОН КАРТЫ НЕ ОСЫПАЕТСЯ.
//
// Полупустой район карты хуже пустого: читатель находит два дома из десяти,
// делает вывод «остального тут нет» — и заводит второй дом рядом с живым. В
// `UI/World` (канвас-путь и 3D — там, где продукт РИСУЕТ) на карте стояло два
// класса из десяти: не было ни `LvnFade` с решением Ильи про вход фейдом, ни
// `LvnGlass`, чей докблок прямо отвечает на регулярное «диалог надо переносить
// на канвас».
//
// Механизмом считаем класс с публичным способом: чистые записи данных
// (`Lvn3DSet`, `LvnBox`) на карте не нужны — карта отвечает «как это делается»,
// а не «из чего состоит».
// Рядом, но НЕ то же самое: `TestEveryLivedInHomeIsOnTheMap` сверяет
// СТАТИЧЕСКИЕ дома с двумя и более читателями по всему движку. Классов-
// экземпляров он не видит, а район состоит в основном из них.
func TestWorldDistrictIsFullyMapped(t *testing.T) {
	root := repoRoot(t)
	canon := string(mustRead(t, filepath.Join(root, "docs", "where-things-live.md")))
	// Закрытые районы. Оболочка и Content ещё не закрыты — их числа записаны в
	// хронике; храповик добавляет район сюда, когда тот дочитан до конца.
	dirs := []string{
		"unity/Packages/com.lvn.engine/Runtime/UI",
		"unity/Packages/com.lvn.engine/Runtime/UI/World",
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine/Runtime/Content",
	}
	class := regexp.MustCompile(`\bpublic\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*class\s+(\w+)`)
	method := regexp.MustCompile(`public\s+[\w<>\[\]\.]+\s+\w+\s*\(`)

	seen := 0
	var lost []string
	for _, dir := range dirs {
		entries, err := os.ReadDir(filepath.Join(root, dir))
		if err != nil {
			t.Fatal(err)
		}
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
				continue
			}
			src := string(mustRead(t, filepath.Join(root, dir, e.Name())))
			for _, m := range class.FindAllStringSubmatchIndex(src, -1) {
				name := src[m[2]:m[3]]
				// Тело класса по скобкам, а не «столько-то знаков вперёд»:
				// короткая запись данных (`DownloadCenter.Entry` — четыре
				// поля) иначе прихватывает способы СОСЕДА и выдаёт себя за
				// механизм. Обрезать по следующему слову `class` тоже нельзя:
				// у `WorldStage` вложенная запись `Slot` объявлена раньше
				// первого способа, и обрезка съела бы весь дом.
				body := classBody(src, m[1])
				if !method.MatchString(body) {
					continue // запись данных, а не механизм
				}
				seen++
				// Имя целиком, а не приставкой: иначе строка про
				// `Lvn3DSetEnv` засчитывалась бы за `Lvn3DSet`, а
				// переименование в карте прошло бы незамеченным.
				if !regexp.MustCompile("`" + regexp.QuoteMeta(name) + "(?:`|\\.|/|\\s)").MatchString(canon) {
					lost = append(lost, name)
				}
			}
		}
	}
	sawSources(t, seen, 60, "механизмов закрытых районов")
	sort.Strings(lost)
	if len(lost) > 0 {
		t.Errorf("механизмы закрытых районов вне карты (%d): %s\n\n"+
			"Полупустой район хуже пустого: читатель делает вывод «остального тут "+
			"нет» и заводит второй дом рядом с живым.", len(lost), strings.Join(lost, ", "))
	}
}

// Тело класса от объявления до парной закрывающей скобки. `from` — позиция
// сразу за именем; открывающая скобка ищется вперёд (между ними может стоять
// список предков).
func classBody(src string, from int) string {
	open := strings.IndexByte(src[from:], '{')
	if open < 0 {
		return ""
	}
	i := from + open + 1
	depth := 1
	for j := i; j < len(src); j++ {
		switch src[j] {
		case '{':
			depth++
		case '}':
			depth--
			if depth == 0 {
				return src[i:j]
			}
		}
	}
	return src[i:]
}

// ДОКУМЕНТ НЕ ССЫЛАЕТСЯ НА СТОРОЖА, КОТОРОГО НЕТ.
//
// Хроника и карта называют сторожей поимённо — и это не украшение: читатель по
// этому имени решает, ЗАКРЫТ ли урок проверкой или держится на внимательности.
// Ссылка на несуществующего сторожа отвечает «закрыт» там, где закрыто ничего.
//
// Так и было: разбор про сокрытие экранов ссылался на сторожа, которого я в тот
// же день заменил другим, а разбор про словарь согласия — на имя, под которым
// сторож так и не родился (родился под русским). Оба урока при этом оставались
// закрытыми — просто документ показывал не туда.
//
// Считаем существующими и тестовые ПОМОЩНИКИ (`TestStage`, `TestAssets`): карта
// называет их наравне со сторожами, и они такие же настоящие.
func TestDocsNameOnlyLivingGuards(t *testing.T) {
	root := repoRoot(t)
	have := map[string]bool{}

	// Go-стражи.
	for _, dir := range []string{"tools", "server"} {
		_ = filepath.Walk(filepath.Join(root, dir), func(p string, i os.FileInfo, err error) error {
			if err != nil || i.IsDir() || !strings.HasSuffix(p, "_test.go") {
				return err
			}
			for _, m := range regexp.MustCompile(`func (Test\w+)\(`).FindAllStringSubmatch(string(mustRead(t, p)), -1) {
				have[m[1]] = true
			}
			return nil
		})
	}
	// Unity-тесты и их помощники.
	_ = filepath.Walk(filepath.Join(root, "unity/Packages"), func(p string, i os.FileInfo, err error) error {
		if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") || !strings.Contains(p, "/Tests/") {
			return err
		}
		src := string(mustRead(t, p))
		for _, re := range []*regexp.Regexp{
			regexp.MustCompile(`(?:public|internal)\s+(?:static\s+)?(?:IEnumerator|void)\s+(\w+)\s*\(`),
			regexp.MustCompile(`class\s+(\w+)`),
		} {
			for _, m := range re.FindAllStringSubmatch(src, -1) {
				have[m[1]] = true
			}
		}
		return nil
	})
	sawSources(t, len(have), 400, "имён тестов и помощников")

	named := regexp.MustCompile("`(Test[A-Za-z0-9_]+)`")
	docs, err := filepath.Glob(filepath.Join(root, "docs", "*.md"))
	if err != nil {
		t.Fatal(err)
	}
	seen := 0
	var ghosts []string
	for _, d := range docs {
		for _, m := range named.FindAllStringSubmatch(string(mustRead(t, d)), -1) {
			seen++
			if !have[m[1]] {
				ghosts = append(ghosts, filepath.Base(d)+": "+m[1])
			}
		}
	}
	sawSources(t, seen, 40, "ссылок на сторожей в доках")
	sort.Strings(ghosts)
	if len(ghosts) > 0 {
		t.Errorf("документы ссылаются на сторожей, которых нет (%d):\n  %s\n\n"+
			"По имени сторожа читатель решает, закрыт ли урок проверкой. Ссылка "+
			"на несуществующего отвечает «закрыт» там, где закрыто ничего.",
			len(ghosts), strings.Join(ghosts, "\n  "))
	}
}
