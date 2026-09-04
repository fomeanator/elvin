package importer

import (
	"os"
	"path/filepath"
	"testing"
)

// РАЗРЕШЁННЫЙ КОНФЛИКТ НЕ ВОСКРЕСАЕТ СЛЕДУЮЩИМ ИМПОРТОМ.
//
// Парковка проверена соседними тестами: правка руками плюс другой upstream дают
// конфликт, и ничего не затирается. Но это ПОЛОВИНА обещания. Вторая — то, ради
// чего разрешение вообще существует: автор один раз выбрал сторону, и следующий
// импорт с ТЕМ ЖЕ upstream обязан молчать.
//
// Комментарий в cmd_conflicts.go называет способ сломаться дословно: «stamp the
// winner into the import baseline, without which the next import re-opens the
// conflict that was just closed». Ни один тест этого не трогал — ни здесь, ни
// на стороне сервера.
//
// Цена ошибки не в данных, а в доверии: конфликт, всплывающий после каждого
// импорта, перестают разбирать и начинают закрывать не глядя.
func TestResolvedConflictStaysClosed(t *testing.T) {
	for _, choice := range []string{ChoiceMine, ChoiceIncoming} {
		t.Run(choice, func(t *testing.T) {
			dir := t.TempDir()
			const rel = "scripts/a.lvn"
			dst := filepath.Join(dir, rel)

			// 1. Первый импорт — файл наш, база записана.
			if _, err := WriteToContentDir(dir, oneScript("t", rel, `{"script":["upstream-1"]}`)); err != nil {
				t.Fatal(err)
			}
			// 2. Человек правит файл руками.
			if err := os.WriteFile(dst, []byte(`{"script":["ПРАВКА РУКАМИ"]}`), 0o644); err != nil {
				t.Fatal(err)
			}
			// 3. Второй импорт с ДРУГИМ upstream — обязан припарковать, не затереть.
			rep, err := WriteToContentDir(dir, oneScript("t", rel, `{"script":["upstream-2"]}`))
			if err != nil {
				t.Fatal(err)
			}
			if got := statusOf(rep, rel); got != StatusConflict {
				t.Fatalf("статус %q, ждали conflict — парковка не сработала, мерить нечего", got)
			}
			if got := readFile(t, dst); got != `{"script":["ПРАВКА РУКАМИ"]}` {
				t.Fatalf("правку руками затёрли: %s", got)
			}
			found, err := ScanConflicts(dir)
			if err != nil {
				t.Fatal(err)
			}
			if len(found) != 1 {
				t.Fatalf("обход нашёл %d конфликтов, ждали 1", len(found))
			}

			// 4. Автор выбирает сторону.
			res, err := ResolveConflict(dir, rel, choice, ResolveOptions{
				Write: func(r string, data []byte) error {
					return os.WriteFile(filepath.Join(dir, r), data, 0o644)
				},
			})
			if err != nil {
				t.Fatalf("разрешение %q: %v", choice, err)
			}
			if len(res.Baselines) == 0 {
				t.Errorf("база не обновлена (%s) — следующий импорт откроет тот же конфликт", res.Note)
			}
			left, err := ScanConflicts(dir)
			if err != nil {
				t.Fatal(err)
			}
			if len(left) != 0 {
				t.Fatalf("после разрешения осталось %d конфликтов", len(left))
			}

			want := `{"script":["ПРАВКА РУКАМИ"]}`
			if choice == ChoiceIncoming {
				want = `{"script":["upstream-2"]}`
			}
			if got := readFile(t, dst); got != want {
				t.Fatalf("на диске %s, ждали %s", got, want)
			}

			// 5. ГЛАВНОЕ: тот же upstream снова — конфликта быть НЕ ДОЛЖНО.
			rep2, err := WriteToContentDir(dir, oneScript("t", rel, `{"script":["upstream-2"]}`))
			if err != nil {
				t.Fatal(err)
			}
			if len(rep2.Conflicts) != 0 {
				t.Errorf("КОНФЛИКТ ВОСКРЕС: %v — победитель не стал базой, и автор будет "+
					"закрывать один и тот же спор после каждого импорта", rep2.Conflicts)
			}
			if got := readFile(t, dst); got != want {
				t.Errorf("повторный импорт изменил файл: %s, ждали %s", got, want)
			}
		})
	}
}
