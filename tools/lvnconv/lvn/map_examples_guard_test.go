package lvn

import (
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
