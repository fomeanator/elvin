package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

// У ОТЛОЖЕННОГО РЕШЕНИЯ ЕСТЬ ВЕС, И ЕГО ПЛАТИТ ИГРОК.
//
// `LvnSkin` — облик из картинок: рамка карточки, плашки, разделитель, чип.
// Написан, задокументирован и НЕ ПОДКЛЮЧЁН: у него ноль зовущих. Само по себе
// это не беда — спящий класс стоит внимания читающего, не больше.
//
// Беда в том, что папка `Resources` попадает в сборку ЦЕЛИКОМ, без отсеивания
// неиспользуемого. Значит текстуры, до которых можно добраться только через
// спящий дом, лежат в каждом APK уже сегодня — и будут лежать, пока решение
// откладывается.
//
// Летопись (роль 105) это предсказала и оставила два честных решения: включить
// облик или убрать набор из `Resources` до того дня. Нечестно только третье —
// считать, что ждать бесплатно. Абзац в летописи не мешает считать именно так,
// поэтому счёт стоит здесь: он попадается на глаза при каждом прогоне.
//
// Страж гаснет САМ, как только одна из половин изменится: у дома появится
// зовущий или файлы уедут из `Resources`.
func TestDormantSkinCostIsTracked(t *testing.T) {
	const budgetKB = 658 // 01.09: семь текстур, доступных только через спящий LvnSkin

	root := repoRoot(t)
	res := filepath.Join(root, "unity", "Packages", "com.lvn.engine", "Runtime", "Resources", "ui")
	if _, err := os.Stat(res); err != nil {
		return // набор уехал — решение принято, считать нечего
	}

	// Весь код пакетов одним куском: ищем, кто упоминает имя файла.
	var sources []string
	for _, rel := range storageRoots {
		_ = filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err == nil && !info.IsDir() && strings.HasSuffix(path, ".cs") {
				sources = append(sources, path)
			}
			return nil
		})
	}
	atLeast(t, len(sources), 60, "просмотренных файлов")

	entries, err := os.ReadDir(res)
	if err != nil {
		t.Fatal(err)
	}
	total := 0
	var orphans []string
	for _, e := range entries {
		if e.IsDir() || strings.HasSuffix(e.Name(), ".meta") {
			continue
		}
		stem := strings.TrimSuffix(e.Name(), filepath.Ext(e.Name()))
		outside := false
		for _, p := range sources {
			if strings.HasSuffix(p, "LvnSkin.cs") {
				continue
			}
			if strings.Contains(string(mustRead(t, p)), stem) {
				outside = true
				break
			}
		}
		if outside {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		total += int(info.Size())
		orphans = append(orphans, fmt.Sprintf("%s (%d КБ)", e.Name(), info.Size()/1024))
	}
	sort.Strings(orphans)
	if total/1024 > budgetKB {
		t.Errorf("вес спящего облика вырос: %d КБ при пороге %d\n  %s\n\n"+
			"Эти файлы едут в каждом APK, а добраться до них можно только через LvnSkin,\n"+
			"у которого ноль зовущих. Решений два и оба честные: подключить облик или убрать\n"+
			"набор из Resources. Третье — добавить сюда ещё файлов — не решение.",
			total/1024, budgetKB, strings.Join(orphans, "\n  "))
	}
}
