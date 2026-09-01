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

// СТРАЖ, КОТОРЫЙ НИЧЕГО НЕ НАШЁЛ, ОБЯЗАН ОТЛИЧАТЬСЯ ОТ СТРАЖА, КОТОРОМУ НЕЧЕГО
// БЫЛО ИСКАТЬ.
//
// Почти все проверки здесь устроены одинаково: обойти исходники, собрать
// нарушения, потребовать ноль. У такой формы есть тихий отказ — обход, который
// не нашёл НИ ОДНОГО ФАЙЛА. Переименовали папку, переехал пакет, сместился
// якорь — и страж зеленеет, не проверив ничего. Отличить «правило соблюдено» от
// «я ничего не смотрел» по цвету невозможно.
//
// Сегодня это было проверено живьём дважды: страж адресов оставался зелёным при
// подменённом адресе (под корневой маршрут «/» подходило всё), а до него
// скрапер схемы молча терял семнадцать классов из сорока пяти.
//
// Отсюда правило: обходишь дерево — объяви порог (atLeast). Бюджет ниже —
// долг: столько стражей ещё живут без порога. Он только уменьшается.
func TestGuardsCountWhatTheyScan(t *testing.T) {
	const budget = 12 // 01.09: столько обходчиков ещё без порога; только вниз (было 39)

	dir := "."
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("не читается каталог стражей: %v", err)
	}
	// ИМЕНА СТРАЖЕЙ БЫВАЮТ РУССКИМИ. В Go `\w` — это [0-9A-Za-z_], кириллица
	// в него НЕ входит, и первая версия этого стража молча не видела тринадцати
	// проверок с русскими именами. То есть страж пустоты сам оказался пуст
	// ровно тем способом, который ищет.
	fn := regexp.MustCompile(`(?m)^func (Test[\pL\pN_]+)\(t \*testing\.T\) \{`)
	// ПОРОГ БЫВАЕТ ЗАПИСАН ПО-РАЗНОМУ. Кроме atLeast и «меньше N» встречается
	// «ничего не нашли — Fatal» (`if files == 0 { t.Fatal(...) }`). Это более
	// слабый порог (доказывает «хотя бы один», а не «столько-то»), но пустоту
	// он ловит, а значит долгом не является. Первая версия стража его не знала
	// и завышала долг.
	floor := regexp.MustCompile(`atLeast\(|(?:len\(\w+\)|scanned|count|files)\s*<\s*\d+|\b\w+\s*==\s*0\s*\{\s*\n\s*t\.Fatal`)

	// ПОРОГ БЫВАЕТ У ПОМОЩНИКА. Пять стражей оболочки берут каталог у
	// shellRuntimeDir, и порог пустоты стоит там — один раз, а не пятью
	// копиями. Считаем такой вызов защитой, но только пока помощник и вправду
	// проверяет: пропадёт atLeast у него — вернутся в должники все пятеро.
	helpers := map[string]bool{}
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), "_test.go") {
			continue
		}
		src := string(mustRead(t, filepath.Join(dir, e.Name())))
		for _, m := range regexp.MustCompile(`(?s)func (\w+)\(t \*testing\.T\) string \{(.*?)\n\}`).FindAllStringSubmatch(src, -1) {
			if strings.Contains(m[2], "atLeast(") {
				helpers[m[1]] = true
			}
		}
	}

	var without []string
	total := 0
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), "_test.go") {
			continue
		}
		src := string(mustRead(t, filepath.Join(dir, e.Name())))
		locs := fn.FindAllStringSubmatchIndex(src, -1)
		for i, loc := range locs {
			end := len(src)
			if i+1 < len(locs) {
				end = locs[i+1][0]
			}
			body := src[loc[1]:end]
			if !strings.Contains(body, "filepath.Walk") && !strings.Contains(body, "os.ReadDir") {
				continue
			}
			total++
			guarded := floor.MatchString(body)
			for h := range helpers {
				if strings.Contains(body, h+"(t)") {
					guarded = true
				}
			}
			if !guarded {
				without = append(without, fmt.Sprintf("%s [%s]", src[loc[2]:loc[3]], e.Name()))
			}
		}
	}

	// Порог у самого мета-стража: разбор сломается — и он тоже позеленеет ни о чём.
	atLeast(t, total, 60, "стражей, обходящих дерево")

	if len(without) > budget {
		sort.Strings(without)
		t.Errorf("стражей без порога пустоты стало %d при бюджете %d:\n  %s\n\n"+
			"Обходишь дерево — считай просмотренное и объяви atLeast(t, scanned, N, …). "+
			"Иначе переименованная папка превращает стража в вечнозелёного.",
			len(without), budget, strings.Join(without, "\n  "))
	}
	if len(without) < budget {
		t.Logf("обходчиков без порога стало %d (бюджет %d) — опустите бюджет в этом файле",
			len(without), budget)
	}
}
