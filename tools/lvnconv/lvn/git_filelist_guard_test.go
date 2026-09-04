package lvn

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// СПИСОК ФАЙЛОВ ИЗ GIT БЕРЁТСЯ С quotePath=false — ИНАЧЕ КИРИЛЛИЦА ЛОМАЕТ ГЕЙТ.
//
// По умолчанию git ЭКРАНИРУЕТ не-ASCII имена восьмеричными последовательностями:
//
//	"examples/\321\203\320\272\321\203\321\201.lvns"
//
// Инструмент получает путь, которого на диске нет, честно отвечает «НЕ РАЗОБРАН»
// и выходит единицей — а вызывающий объявляет это своей находкой. Сообщение,
// врущее про причину, дороже самой поломки.
//
// Репозиторий это уже проходил: qa/run-all.sh носит тот же флаг с пометкой
// «поймано первым же файлом с кириллицей в имени». Но два места остались без
// него — гейт обхода в CI и его местная половина, — и нашлось это укусом:
// подложенный мёртвый блок в файле с кириллическим именем дал не «мёртвый
// блок», а «не разобран», под заголовком «найден недостижимый контент».
//
// Контент здесь кириллический сплошь, так что это был вопрос времени.
func TestGitFileListsSurviveNonAsciiNames(t *testing.T) {
	root := repoRoot(t)
	dirs := []string{
		filepath.Join(root, ".github", "workflows"),
		filepath.Join(root, "qa"),
	}

	seen, bad := 0, 0
	for _, dir := range dirs {
		entries, err := os.ReadDir(dir)
		if err != nil {
			continue // каталога может не быть в урезанной проверке
		}
		for _, e := range entries {
			if e.IsDir() {
				continue
			}
			name := e.Name()
			if !strings.HasSuffix(name, ".yml") && !strings.HasSuffix(name, ".sh") {
				continue
			}
			raw, err := os.ReadFile(filepath.Join(dir, name))
			if err != nil {
				t.Fatalf("%s: %v", name, err)
			}
			for _, ln := range strings.Split(string(raw), "\n") {
				if strings.HasPrefix(strings.TrimSpace(ln), "#") {
					continue // объяснение грабли — не сама грабля
				}
				// Якорь — «ls-files», а НЕ «git ls-files»: после починки между
				// ними встаёт сам флаг, и подстрока распадается. Первая
				// редакция стража искала целиком и охватила ноль вызовов;
				// поймал это порог охвата, а не глаз.
				if !strings.Contains(ln, "ls-files") {
					continue
				}
				seen++
				if !strings.Contains(ln, "core.quotePath=false") {
					bad++
					t.Errorf("%s: список файлов из git без core.quotePath=false — "+
						"кириллическое имя приедет экранированным, инструмент не найдёт "+
						"файл, а вызывающий объявит это СВОЕЙ находкой:\n    %s",
						name, strings.TrimSpace(ln))
				}
			}
		}
	}

	// Порог на ПРОСМОТРЕННОЕ: ноль найденных вызовов означал бы, что признак
	// разъехался с кодом, а не что всё в порядке.
	atLeast(t, seen, 2, "вызовов git ls-files в сценариях и workflow")
}
