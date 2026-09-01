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

// ПОЧТИ-ДВОЙНИК: ДВА ТЕЛА, КОТОРЫЕ ЗАВТРА РАЗОЙДУТСЯ.
//
// Точную копию ловит глаз и любой поиск. Опаснее почти-копия: два тела,
// совпадающие построчно, кроме одной-двух строк. Она выглядит как две разные
// работы, а на деле это одна работа и один довод — и когда в первую вносят
// правку, вторая молча остаётся прежней.
//
// 01.09 такой обход, пущенный по четырём папкам подряд, нашёл в КАЖДОЙ:
//   - сетевой загрузчик — звук и картинка по девятнадцать строк дословно;
//   - цепочка загрузчиков — три копии обхода по восемь строк;
//   - счётчик полос — мой собственный код того же дня;
//   - хранилище состояния — два чтения по пять строк;
//   - ожидание операции — различалось одной строкой «что делать при отмене»;
//   - показ картинки — вместе со сторожем устаревания, самым опасным местом
//     для копии: забудешь его в третьем присвоении — карточка покажет чужую
//     обложку, и только на медленной сети.
//
// Отсюда и страж: свойство, которое пришлось искать шесть раз подряд, надо
// держать, а не вспоминать. Бюджет только вниз.
func TestNoNearTwins(t *testing.T) {
	const budget = 0 // только вниз

	// Оправдания поимённо: пара, которая ПОХОЖА, но одна работа в двух видах
	// её не делает.
	known := map[string]string{}

	type body struct {
		file, name string
		lines      []string
	}
	var bodies []body

	root := repoRoot(t)
	// Подпись бывает и в одну строку, и в несколько, и со скобкой на той же
	// строке. Первая версия знала только один вид и прочла 847 тел вместо
	// полутора тысяч — порог пустоты это и поймал, чем себя оправдал.
	decl := regexp.MustCompile(`(?m)^\s{8}(?:private|internal|public|protected)[^\n(=]*\s(\w+)\s*\([^)]*\)[^{;=]*\{`)
	for _, dir := range []string{
		"unity/Packages/com.lvn.engine/Runtime",
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine.services/Runtime",
	} {
		_ = filepath.Walk(filepath.Join(root, dir), func(p string, i os.FileInfo, err error) error {
			if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			src := stripComments(string(mustRead(t, p)))
			for _, m := range decl.FindAllStringSubmatchIndex(src, -1) {
				start := m[1] - 1 // скобка уже захвачена выражением
				depth, end := 0, start
				for j := start; j < len(src) && j < start+12000; j++ {
					if src[j] == '{' {
						depth++
					} else if src[j] == '}' {
						depth--
						if depth == 0 {
							end = j
							break
						}
					}
				}
				var code []string
				for _, l := range strings.Split(src[start:end], "\n") {
					l = strings.TrimSpace(l)
					if l != "" {
						code = append(code, strings.Join(strings.Fields(l), " "))
					}
				}
				if len(code) >= 6 && len(code) <= 70 {
					bodies = append(bodies, body{filepath.Base(p), src[m[2]:m[3]], code})
				}
			}
			return nil
		})
	}
	sawSources(t, len(bodies), 1200, "тел способов")

	sets := make([]map[string]int, len(bodies))
	for i, b := range bodies {
		s := map[string]int{}
		for _, l := range b.lines {
			s[l]++
		}
		sets[i] = s
	}

	var twins []string
	seen := map[string]bool{}
	for a := 0; a < len(bodies); a++ {
		for b := a + 1; b < len(bodies); b++ {
			la, lb := len(bodies[a].lines), len(bodies[b].lines)
			if la-lb > 5 || lb-la > 5 {
				continue
			}
			shared := 0
			for l, n := range sets[a] {
				if m, ok := sets[b][l]; ok {
					if m < n {
						shared += m
					} else {
						shared += n
					}
				}
			}
			// ДВЕ ПЛАНКИ, А НЕ ОДНА. Просто опустить порог с 0.85 до 0.80
			// не вышло: на телах в шесть строк «пять общих» ничего не
			// значит — четыре из них займёт обёртка try/catch, и страж
			// начинает ругаться на идиому языка, а не на копию работы.
			//
			// Поэтому пара считается двойником, если она ЛИБО очень похожа
			// (0.85 и выше), ЛИБО похожа и помногу (0.80 при восьми общих
			// строках). Вторая планка и открыла кошелёк: начисление и трата
			// держали девять общих строк из одиннадцати — в коде про деньги
			// такая копия опаснее прочих.
			small := la
			if lb < small {
				small = lb
			}
			ratio := float64(shared) / float64(small)
			if ratio < 0.85 && !(ratio >= 0.80 && shared >= 8) {
				continue
			}
			key := bodies[a].file + "::" + bodies[a].name + " ↔ " + bodies[b].file + "::" + bodies[b].name
			if seen[key] {
				continue
			}
			seen[key] = true
			if _, ok := known[key]; !ok {
				twins = append(twins, fmt.Sprintf("%s  (%d/%d строк общие)", key, shared, small))
			}
		}
	}

	sort.Strings(twins)
	if len(twins) > budget {
		t.Errorf("почти-двойников стало %d при бюджете %d:\n  %s\n\n"+
			"Два тела, совпадающие построчно кроме одной-двух строк, — это одна "+
			"работа и один довод. Правку внесут в первое, второе молча останется "+
			"прежним. Сведите или назовите пару в known с причиной.",
			len(twins), budget, strings.Join(twins, "\n  "))
	}
}
