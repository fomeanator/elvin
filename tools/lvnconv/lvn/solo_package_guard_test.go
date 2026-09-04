package lvn

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// ГРАНИЦА, ДЕЛАЮЩАЯ ЯДРО САМОСТОЯТЕЛЬНЫМ ПАКЕТОМ.
//
// Довод о встраиваемости звучит так: команда с готовой игрой ставит ОДИН пакет
// com.lvn.engine и получает язык, плеер и постановку — без хаба, магазина и
// учёток. Проверен он установкой (qa/solo-package-check.sh): пустой проект,
// один пакет, глава с развилкой играется до конца.
//
// Та проверка поднимает настоящий редактор и потому идёт по требованию, а не
// каждым прогоном. Здесь стоит дешёвая половина: правило, нарушение которого
// сделало бы её красной, ловится за миллисекунды.
//
// Почему это не ловилось раньше: сборки тестов САМОГО ядра ссылаются на
// Lvn.Engine.Shell и Lvn.Engine.Services, то есть все 2090 зелёных тестов идут
// в проекте, где оболочка уже стоит. Зелёный там ничего не говорит об одиночке.
func TestCorePackageStandsAlone(t *testing.T) {
	root := repoRoot(t)
	core := filepath.Join(root, "unity", "Packages", "com.lvn.engine")

	// 1. Манифест ядра не смеет зависеть от наших же пакетов: такая строка
	//    превратила бы «один пакет» в «один пакет и всё, что он притащит».
	var pkg struct {
		Dependencies map[string]string `json:"dependencies"`
	}
	raw, err := os.ReadFile(filepath.Join(core, "package.json"))
	if err != nil {
		t.Fatalf("манифест ядра не прочитан: %v", err)
	}
	if err := json.Unmarshal(raw, &pkg); err != nil {
		t.Fatalf("манифест ядра не разобран: %v", err)
	}
	atLeast(t, len(pkg.Dependencies), 5, "зависимостей в манифесте ядра")
	for dep := range pkg.Dependencies {
		if strings.HasPrefix(dep, "com.lvn.") {
			t.Errorf("ядро объявило зависимость от нашего пакета %q — "+
				"«один пакет» перестало быть правдой", dep)
		}
	}

	// 2. Рантайм ядра не смеет обращаться к пространствам оболочки и сервисов.
	//    Сборки это и так запрещают, но ошибка вылезет только у того, кто
	//    поставил пакет ОДИН, — то есть у постороннего, а не у нас.
	forbidden := []string{"Lvn.UI.Screens", "Lvn.Services"}
	scanned := 0
	err = filepath.Walk(filepath.Join(core, "Runtime"), func(p string, fi os.FileInfo, err error) error {
		if err != nil || fi.IsDir() || !strings.HasSuffix(p, ".cs") {
			return err
		}
		scanned++
		body, err := os.ReadFile(p)
		if err != nil {
			return err
		}
		for _, ns := range forbidden {
			if strings.Contains(string(body), ns) {
				rel, _ := filepath.Rel(root, p)
				t.Errorf("%s тянется в %s — ядро перестало стоять в одиночку", rel, ns)
			}
		}
		return nil
	})
	if err != nil {
		t.Fatalf("обход рантайма ядра: %v", err)
	}
	// Порог ставится на ПРОСМОТРЕННОЕ: ноль нарушений при нуле прочитанных
	// файлов означал бы промах обхода, а не порядок в пакете.
	sawSources(t, scanned, 200, "файлов рантайма ядра")
}
