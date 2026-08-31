package lvn

import (
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// ЭТАЛОН НЕ ПРОТУХАЕТ МОЛЧА.
//
// Золотые тесты редакторного компилятора сверяют его вывод с сохранённым
// `.lvn`, который когда-то посчитал Go. Но САМ эталон никто не пересчитывал: Go
// научился класть `who_id` (id говорящего, когда экранное имя и id спрайта
// расходятся), эталоны остались без него — и анти-дрейфовый гейт четыре
// фикстуры подряд светил зелёным на устаревшей правде. Расхождение, ради
// поимки которого гейт и стоит, он же и прятал.
//
// Здесь эталон пересчитывается ЗДЕСЬ И СЕЙЧАС тем же транскодером, что и в
// проде, и сверяется с сохранённым. Красный тест значит одно: перегенерируйте
// фикстуры (`lvnconv convert -i <name>.lvns -o <name>.lvn`).
func TestЗолотыеЭталоныСвежие(t *testing.T) {
	dir := filepath.Join(repoRoot(t), "unity", "Packages", "com.lvn.engine",
		"Tests", "Editor", "Fixtures")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	checked := 0
	for _, e := range entries {
		name := e.Name()
		if e.IsDir() || !strings.HasSuffix(name, ".lvns.txt") {
			continue
		}
		base := strings.TrimSuffix(name, ".lvns.txt")
		goldenRaw, err := os.ReadFile(filepath.Join(dir, base+".lvn.txt"))
		if err != nil {
			t.Fatalf("%s: исходник есть, эталона нет — фикстура собрана наполовину", base)
		}
		// Через ConvertFile: он же разворачивает включения от каталога
		// фикстуры — тем же путём, каким собирает главы CLI.
		fresh, err := lvns.ConvertFile(filepath.Join(dir, name))
		if err != nil {
			t.Fatalf("%s: транскодер не собрал фикстуру: %v", base, err)
		}
		freshJSON, err := json.Marshal(fresh)
		if err != nil {
			t.Fatal(err)
		}
		var a, b interface{}
		if err := json.Unmarshal(freshJSON, &a); err != nil {
			t.Fatal(err)
		}
		if err := json.Unmarshal(goldenRaw, &b); err != nil {
			t.Fatalf("%s: сохранённый эталон — не JSON: %v", base, err)
		}
		if !reflect.DeepEqual(a, b) {
			t.Fatalf("%s: сохранённый эталон РАСХОДИТСЯ с тем, что транскодер даёт сейчас.\n"+
				"Эталон протух — перегенерируйте его:\n"+
				"  lvnconv convert -i %s.lvns -o %s.lvn\n"+
				"Пока он протухший, золотой тест редакторного компилятора светит "+
				"зелёным на неправде.", base, base, base)
		}
		checked++
	}
	if checked < 10 {
		t.Fatalf("проверено всего %d фикстур — похоже, каталог не тот", checked)
	}
	t.Logf("свежих эталонов: %d", checked)
}
