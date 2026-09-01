package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

// ОГОВОРКА — ЭТО ДЫРКА В СТРАЖЕ, И ЕЁ ШИРИНУ НАДО МЕРИТЬ.
//
// Живой случай 01.09: в оговорки следа «выбор языка решён по полю» попало
// слово `foreach` — чтобы пропустить того, кто перебирает список для прогрева.
// Мутация показала, что след после этого МОЛЧИТ и на настоящем обходе:
// оговорки ищутся в окне ±4 строки, а цикл в коде интерфейса стоит через
// строку от чего угодно.
//
// Хорошая оговорка называет МЕСТО (имя файла-дома) или НАМЕРЕНИЕ (НАРОЧНО).
// Плохая называет конструкцию, встречающуюся везде, — и тогда след,
// формально зелёный, не проверяет ничего.
//
// Мерить долю заглушённых попаданий БЕСПОЛЕЗНО: у доведённого до конца
// переезда заглушено ровно всё — остаются только сам дом (глушится по имени
// файла) и подписи НАРОЧНО. Это признак здоровья, а не болезни.
//
// Опасна ТРЕТЬЯ причина. Если попадание заглушено не местом и не намерением, а
// каким-то словом из оговорки, — значит оговорка глушит вслепую, и никто не
// знает, что ещё она проглотила. Такие и считаем.
func TestSkipsAreNarrow(t *testing.T) {
	// Порог только уменьшается. Каждое «слепое» глушение — это место, про
	// которое НИКТО не сказал ни «здесь дом», ни «здесь намеренно».
	const blindBudget = 0

	root := repoRoot(t)
	scanned := 0
	type row struct {
		what  string
		hit   int
		blind []string
	}
	var rows []row
	for _, p := range bypassProbes {
		hit := 0
		var blind []string
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
				rel, _ := filepath.Rel(root, path)
				lines := strings.Split(string(mustRead(t, path)), "\n")
				for i, line := range lines {
					code := line
					if c := strings.Index(code, "//"); c >= 0 {
						code = code[:c]
					}
					if !p.re.MatchString(code) {
						continue
					}
					hit++
					ctx := line
					for j := i - 4; j <= i+4 && !tightSkips[p.what]; j++ {
						if j >= 0 && j < len(lines) && j != i {
							ctx += "\n" + lines[j]
						}
					}
					if p.skip == nil {
						continue
					}
					if p.skip.MatchString(rel) {
						continue // заглушено МЕСТОМ: это сам дом
					}
					hitText := p.skip.FindString(ctx)
					if hitText == "" {
						continue // не заглушено вовсе
					}
					// Заглушено НАМЕРЕНИЕМ или ИМЕНЕМ ДОМА — законно.
					if strings.Contains(hitText, "НАРОЧНО") ||
						strings.Contains(hitText, "Lvn") ||
						strings.Contains(hitText, ".cs") {
						continue
					}
					// Обобщённое слово в оговорке допустимо ТОЛЬКО у следа,
					// который смотрит свою строку: в окне ±4 оно глушит вслепую.
					if tightSkips[p.what] {
						continue
					}
					blind = append(blind, fmt.Sprintf("%s:%d («%s»)", filepath.ToSlash(rel), i+1, hitText))
				}
				return nil
			})
			if err != nil {
				t.Fatal(err)
			}
		}
		rows = append(rows, row{p.what, hit, blind})
	}
	atLeast(t, scanned, 60, "просмотренных файлов")
	atLeast(t, len(rows), 10, "следов в таблице")

	var wide []string
	for _, r := range rows {
		if len(r.blind) > blindBudget {
			wide = append(wide, fmt.Sprintf("«%s»: заглушено вслепую %d — %s",
				r.what, len(r.blind), strings.Join(r.blind, ", ")))
		}
	}
	sort.Strings(wide)
	if len(wide) > 0 {
		t.Errorf("оговорки глушат вслепую (%d следов):\n  %s\n\n"+
			"Заглушено не именем файла-дома и не подписью НАРОЧНО, а словом из оговорки.\n"+
			"Значит про это место никто не сказал ни «здесь дом», ни «здесь намеренно», —\n"+
			"и неизвестно, что ещё та же оговорка проглотила. Назовите место или намерение.",
			len(wide), strings.Join(wide, "\n  "))
	}
}
