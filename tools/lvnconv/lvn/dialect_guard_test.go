package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// ДВА КОМПИЛЯТОРА ЯЗЫКА ГОВОРЯТ НА ОДНОМ ДИАЛЕКТЕ.
//
// `.lvns` компилируют двое: Go-транскодер (CLI, сервер, браузерный wasm) и его
// C#-порт в редакторе (студия, сохранение из Unity). Go — источник правды, и
// золотой корпус сверяет их ВЫВОД на готовых образцах. Но словарь команд корпус
// не покрывает: команда, добавленная в Go и не добавленная в порт, не сломает ни
// один образец — она просто не встретится.
//
// Цена молчания известна по самому порту: неизвестное слово в начале строки
// уходит в НАРРАЦИЮ и печатается игроку. Автор, пишущий в студии, получил бы
// свою команду репликой — тот самый тихий отказ, ради которого стоят все эти
// стражи.
//
// Проверка мягкая и потому честная: команду из Go порт обязан либо ПОДДЕРЖАТЬ,
// либо явно назвать неподдерживаемой (`UnsupportedSourceOps` — там она валит
// компиляцию с объяснением, а не молчит). Третьего не дано.
func TestCSharpPortKnowsEveryLvnsOp(t *testing.T) {
	root := repoRoot(t)
	src, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(
		"unity/Packages/com.lvn.engine/Editor/LvnsCompiler.cs")))
	if err != nil {
		t.Skipf("C#-порт не найден в этой раскладке: %v", err)
	}
	text := string(src)

	block := func(marker string) map[string]bool {
		out := map[string]bool{}
		i := strings.Index(text, marker)
		if i < 0 {
			return out
		}
		tail := text[i:]
		if j := strings.Index(tail, "};"); j > 0 {
			tail = tail[:j]
		}
		for _, m := range regexp.MustCompile(`"([a-z_0-9]+)"`).FindAllStringSubmatch(tail, -1) {
			out[m[1]] = true
		}
		return out
	}
	known := block("KnownOps = new HashSet<string>")
	unsupported := block("UnsupportedSourceOps = new Dictionary<string, string>")
	if len(known) == 0 {
		t.Fatal("не удалось прочитать словарь C#-порта — разбор сломался, а не порт")
	}

	var missing []string
	for op := range lvns.KnownOps {
		if !known[op] && !unsupported[op] {
			missing = append(missing, op)
		}
	}
	sort.Strings(missing)

	if len(missing) > 0 {
		t.Fatalf("Go знает команды, о которых C#-порт не слышал (%d):\n  %s\n\n"+
			"В порту неизвестное слово в начале строки уходит в НАРРАЦИЮ и печатается игроку. "+
			"Либо поддержите команду в LvnsCompiler.cs, либо впишите её в UnsupportedSourceOps "+
			"с причиной — тогда она честно свалит компиляцию вместо того, чтобы стать репликой.",
			len(missing), strings.Join(missing, "\n  "))
	}
}
