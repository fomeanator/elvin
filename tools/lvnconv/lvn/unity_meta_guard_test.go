package lvn

import (
	"os"
	"os/exec"
	"path/filepath"
	"sort"
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
	scanned := 0
	root := repoRoot(t)
	var naked []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil {
				return err
			}
			scanned++
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
	// Порог пустоты: обход, не нашедший ни одного файла, зеленеет ни о чём.
	atLeast(t, scanned, 300, "просмотренных файлов")

}

// СТРАЖ СМОТРЕЛ НА ДИСК, А КЛОНИРУЮТ ИЗ GIT.
//
// Проверка выше спрашивает файловую систему — и молчит, если .meta на диске
// ЕСТЬ, но в репозиторий не добавлен. А именно так и выходит чаще всего: Unity
// создаёт .meta сама, уже после того как автор сделал `git add` по именам, и у
// него на машине всё в порядке ровно до тех пор, пока кто-то не склонирует.
//
// Пойман этим замером живьём: `ReplayClassTests.cs` уехал в main без своего
// .meta (06.09), и первая проверка была при этом зелёной.
func TestКаждыйMetaЛежитВРепозитории(t *testing.T) {
	root := repoRoot(t)
	out, err := exec.Command("git", "-C", root, "ls-files", "--", "unity/Packages").Output()
	if err != nil {
		t.Skipf("git недоступен — проверить нечем: %v", err)
	}
	tracked := map[string]bool{}
	for _, line := range strings.Split(string(out), "\n") {
		if line != "" {
			tracked[line] = true
		}
	}
	if len(tracked) == 0 {
		t.Skip("git не назвал ни одного файла пакетов — проверять нечего")
	}

	var lost []string
	for path := range tracked {
		if !strings.HasSuffix(path, ".cs") && !strings.HasSuffix(path, ".asmdef") {
			continue
		}
		if strings.Contains(path, "~/") {
			continue // Samples~ Unity не импортирует
		}
		if !tracked[path+".meta"] {
			lost = append(lost, path)
		}
	}
	sort.Strings(lost)
	if len(lost) > 0 {
		t.Fatalf("в репозитории есть файл, а его .meta нет:\n  %s\n\nНа машине автора .meta"+
			" лежит на диске, и проверка «у каждого файла есть .meta» зелена. У того, кто"+
			" склонирует следующим, Unity выдаст файлу новый идентификатор.",
			strings.Join(lost, "\n  "))
	}
}
