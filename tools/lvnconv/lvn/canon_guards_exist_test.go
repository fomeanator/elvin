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

// СТРАЖ, НАЗВАННЫЙ В КАНОНЕ, СУЩЕСТВУЕТ.
//
// У карты домов такой страж есть давно: каждый названный в ней дом обязан
// найтись в коде. У КАНОНА РОЛЕЙ его не было, а он тоже называет вещи по
// именам — и чаще всего именно стражей: «правило держит такой-то тест».
//
// Ссылка на стража ценнее ссылки на дом. По ней читатель идёт узнать, ЧТО
// именно держится и на каком уровне строгости; врущая ссылка отправляет его
// искать несуществующий файл, и следующий вывод — «канон устарел, читать его
// незачем». Так канон и умирает: не одним махом, а первой мёртвой ссылкой.
//
// Историю страж не трогает: канон ролей — летопись, и упоминание удалённого
// КЛАССА в ней законно («вторая реализация удалена»). А вот страж, на которого
// канон ссылается как на живой, обязан быть живым.
func TestGuardsNamedInCanonExist(t *testing.T) {
	root := repoRoot(t)
	docs := []string{
		filepath.Join(root, "docs", "missing-roles.md"),
		filepath.Join(root, "docs", "where-things-live.md"),
	}
	named := regexp.MustCompile(`[a-z0-9_]+_guard_test\.go`)

	seen := map[string]string{} // имя файла → в каком каноне назван
	for _, doc := range docs {
		b, err := os.ReadFile(doc)
		if err != nil {
			t.Fatalf("канон не найден (%s): %v", filepath.Base(doc), err)
		}
		for _, m := range named.FindAllString(string(b), -1) {
			if _, ok := seen[m]; !ok {
				seen[m] = filepath.Base(doc)
			}
		}
	}
	if len(seen) == 0 {
		t.Fatal("в канонах не названо ни одного стража — разбор имён сломался")
	}

	var missing []string
	for name, doc := range seen {
		if _, err := os.Stat(filepath.Join(root, "tools", "lvnconv", "lvn", name)); err != nil {
			missing = append(missing, fmt.Sprintf("%s (назван в %s)", name, doc))
		}
	}
	if len(missing) > 0 {
		sort.Strings(missing)
		t.Errorf("канон ссылается на стражей, которых нет:\n  %s\n"+
			"  Переименовали или удалили стража — поправьте и канон.\n"+
			"  Мёртвая ссылка учит не верить всему остальному тексту.",
			strings.Join(missing, "\n  "))
	}
}
