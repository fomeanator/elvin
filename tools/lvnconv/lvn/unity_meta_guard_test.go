package lvn

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// У каждого импортируемого файла Unity есть свой .meta.
//
// Без .meta Unity выдаёт файлу НОВЫЙ идентификатор на каждой машине, и ссылки на
// него — компонент на префабе, поле в сцене, ссылка из другого ассета — молча
// теряются у того, кто склонировал репозиторий следующим. Заметно это не сразу
// и не тому, кто файл добавил.
//
// Папки `Samples~` исключены намеренно: тильда в имени говорит Unity не
// импортировать их, поэтому .meta им не нужны.
func TestEveryImportedFileHasMeta(t *testing.T) {
	root := repoRoot(t)
	var naked []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil {
				return err
			}
			if info.IsDir() {
				if strings.Contains(info.Name(), "~") {
					return filepath.SkipDir
				}
				return nil
			}
			if !strings.HasSuffix(path, ".cs") && !strings.HasSuffix(path, ".asmdef") {
				return nil
			}
			if _, err := os.Stat(path + ".meta"); os.IsNotExist(err) {
				rel, _ := filepath.Rel(root, path)
				naked = append(naked, filepath.ToSlash(rel))
			}
			return nil
		})
	if err != nil {
		t.Fatalf("обход пакетов: %v", err)
	}
	if len(naked) > 0 {
		t.Fatalf("файлы без .meta:\n  %s\n\nUnity выдаст им новый идентификатор на каждой машине,"+
			" и ссылки на них потеряются у следующего, кто склонирует репозиторий.",
			strings.Join(naked, "\n  "))
	}
}
