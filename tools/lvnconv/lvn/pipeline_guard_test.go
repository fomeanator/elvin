package lvn

import (
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// СТРАЖИ ТРАКТА — что генерируется, что копируется, что разложено по темам.
//
// Общее у них одно: файл, который кто-то однажды собрал и больше не
// пересобирал. Отставший генерируемый файл и отставшая вшитая копия выглядят
// как справка и врут с уверенным видом.

func TestПодсказкиНеОтстаютОтГрамматики(t *testing.T) {
	root := repoRoot(t)
	jsonRaw, err := os.ReadFile(filepath.Join(root, "tools", "lvn-lang", "src", "grammar.json"))
	if err != nil {
		t.Fatal(err)
	}
	jsRaw, err := os.ReadFile(filepath.Join(root, "tools", "lvn-lang", "src", "grammar.js"))
	if err != nil {
		t.Fatal(err)
	}
	names := func(src, key, open, close string) map[string]bool {
		m := regexp.MustCompile(`(?s)` + regexp.QuoteMeta(key) + open + `(.*?)\n` + close).FindStringSubmatch(src)
		if m == nil {
			t.Fatalf("не нашёл список %q — поправь якорь сторожа", key)
		}
		out := map[string]bool{}
		for _, w := range regexp.MustCompile(`"([a-z_0-9]+)"`).FindAllStringSubmatch(m[1], -1) {
			out[w[1]] = true
		}
		return out
	}
	truth := names(string(jsonRaw), `"ops":`, ` \[`, `  \],`)
	shown := names(string(jsRaw), `export const OPS =`, ` \[`, `\];`)
	for w := range truth {
		if !shown[w] {
			t.Fatalf("команду %q грамматика знает, а подсказки нет — забыли `npm run gen` "+
				"в tools/lvn-lang после правки grammar.json", w)
		}
	}
	for w := range shown {
		if !truth[w] {
			t.Fatalf("подсказки предлагают %q, чего в grammar.json нет — "+
				"grammar.js правили руками, а он генерируемый", w)
		}
	}
}

// Компилятор .lvns разложен по темам — и режут его РАЗБОРОМ, а не подсчётом.
//
// Файл был крупнейшим в репозитории (1703 строки) и неучтённым нарушением
// собственного правила канона. Две попытки разрезать его подсчётом фигурных
// скобок развалили сборку: в компиляторе полно строковых литералов со
// скобками — он про них и написан. Сторож держит разложение и заодно ловит
// возврат к «одному файлу на всё».

func TestКомпиляторРазложенПоТемам(t *testing.T) {
	dir := filepath.Join(repoRoot(t), "unity", "Packages", "com.lvn.engine", "Editor")
	for _, f := range []string{"LvnsCompiler.Expand.cs", "LvnsCompiler.Anim.cs"} {
		if _, err := os.Stat(filepath.Join(dir, f)); err != nil {
			t.Fatalf("%s пропал — темы компилятора снова съехались в один файл", f)
		}
	}
	// Развороты живут в .Expand: в корне их быть не должно.
	core, err := os.ReadFile(filepath.Join(dir, "LvnsCompiler.cs"))
	if err != nil {
		t.Fatal(err)
	}
	src := stripCommentsAndStrings(string(core))
	for _, gone := range []string{"static string ExpandLoops(", "static string ExpandCalls(", "static JObject BuildAnimCmd("} {
		if strings.Contains(src, gone) {
			t.Fatalf("LvnsCompiler.cs: %q вернулся в корень — у него свой файл", gone)
		}
	}
	// Правило канона, из-за которого всё и делалось.
	lines := strings.Count(string(core), "\n") + 1
	if lines > 1400 {
		t.Fatalf("LvnsCompiler.cs снова разросся (%d строк): файл в тысячу строк — "+
			"это несколько классов, которые забыли разделить", lines)
	}
}

// «Что это за адрес» решает дом, а не место вызова.
//
// Различать приходилось в семи местах, и написано это было тремя разными
// способами. Один из трёх считал ЛОКАЛЬНЫЙ адрес относительным — приписывал к
// нему базу и кодировал, — а за file:// стоит чтение с диска, где «%20»
// означает файл, которого нет.

func TestКопияГрамматикиВРасширенииНеФорк(t *testing.T) {
	root := repoRoot(t)
	for _, name := range []string{"grammar.js", "grammar.json"} {
		src, err := os.ReadFile(filepath.Join(root, "tools", "lvn-lang", "src", name))
		if err != nil {
			t.Fatal(err)
		}
		vendored, err := os.ReadFile(filepath.Join(root, "tools", "vscode-lvn", "lib", "lvn-lang", name))
		if err != nil {
			t.Fatalf("вшитая копия %s пропала: %v", name, err)
		}
		if string(src) != string(vendored) {
			t.Fatalf("tools/vscode-lvn/lib/lvn-lang/%s разошлась с правдой.\n"+
				"Перегенерируйте обе одним шагом:\n"+
				"  (cd tools/lvn-lang && npm run gen)\n"+
				"Пока копия своя, расширение подсказывает другой язык.", name)
		}
	}
}

// Встроенные функции выражений сверяются С ДВИЖКОМ, а не только между собой.
//
// ExprFuncs был пришпилен к Go-компилятору и к веб-плееру, но НЕ к C#
// вычислителю — то есть к тому, кто их и исполняет. Ровно та расстановка, при
// которой две стороны согласны, а третья тихо уезжает: так уже протухли
// золотые эталоны.

// Схема манифеста СНЯТА с DTO и не отстаёт.
//
// Правда о полях живёт в `LvnUiConfig.cs`: Newtonsoft молча пропускает
// незнакомое имя, поэтому `titel_color` не даёт ни ошибки, ни строчки — цвет
// просто остаётся умолчанием. Переписать схему на Go значило бы завести
// очередное зеркало; она снимается генератором, а этот страж требует, чтобы
// снимок был свежим — как у сгенерированной grammar.js.
func TestСхемаМанифестаНеОтстаётОтDTO(t *testing.T) {
	root := repoRoot(t)
	// Оба исходника, как и генератор: облик описан в LvnUiConfig, каталог — в
	// LvnManifest, а для игрока это один файл.
	fresh := ManifestSchema{}
	for _, name := range []string{"LvnUiConfig.cs", "LvnManifest.cs"} {
		raw, err := os.ReadFile(filepath.Join(root, "unity", "Packages", "com.lvn.engine",
			"Runtime", "Content", name))
		if err != nil {
			t.Fatal(err)
		}
		for cls, fields := range ScrapeManifestSchema(string(raw)) {
			if _, clash := fresh[cls]; clash {
				t.Fatalf("класс %s объявлен в обоих исходниках — снимок стал бы неоднозначным", cls)
			}
			fresh[cls] = fields
		}
	}
	if len(fresh) < 30 {
		t.Fatalf("снялось всего %d классов — разбор промахнулся, поправь ScrapeManifestSchema", len(fresh))
	}
	stored := ManifestSchema{}
	blob, err := os.ReadFile(filepath.Join(root, "tools", "lvnconv", "lvn", "manifest-fields.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(blob, &stored); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(fresh, stored) {
		var missing, extra []string
		for cls, fields := range fresh {
			for f := range fields {
				if stored[cls][f] == "" {
					missing = append(missing, cls+"."+f)
				}
			}
		}
		for cls, fields := range stored {
			for f := range fields {
				if fresh[cls][f] == "" {
					extra = append(extra, cls+"."+f)
				}
			}
		}
		sort.Strings(missing)
		sort.Strings(extra)
		t.Fatalf("схема манифеста отстала от DTO.\n"+
			"  нет в снимке: %v\n  лишнее в снимке: %v\n"+
			"Перегенерируйте: (cd tools/lvnconv && go run ./cmd/lvn-genschema)\n"+
			"Пока снимок отстал, новое поле манифеста будет объявляться несуществующим.",
			missing, extra)
	}
}

// Каждый дом, названный в каноне, существует.
//
// Канон домов (`docs/where-things-live.md`) — первое, что читает и человек, и
// агент, прежде чем решить, куда класть работу. Он уже врал сегодня трижды:
// про охват стража, про имя переименованного файла и про свечение темы. Ссылка
// на дом, которого нет, хуже отсутствия строки: по ней пойдут искать.
//
// Обратную сторону (дом есть, а в каноне не назван) проверять нечем: не всякий
// класс — дом, и решает это человек.
func TestДомаКанонаСуществуют(t *testing.T) {
	root := repoRoot(t)
	canon, err := os.ReadFile(filepath.Join(root, "docs", "where-things-live.md"))
	if err != nil {
		t.Fatal(err)
	}
	named := map[string]bool{}
	for _, m := range regexp.MustCompile(`(?m)^\| `+"`"+`([A-Za-z][\w.]*)`+"`").FindAllStringSubmatch(string(canon), -1) {
		named[m[1]] = true
	}
	if len(named) < 80 {
		t.Fatalf("в каноне названо всего %d домов — похоже, разбор таблицы промахнулся", len(named))
	}
	// Ищем по всем местам, где дом может жить: рантайм и редактор движка,
	// оболочка, сервисы, стражи, сервер, веб-плеер и тестовая оснастка.
	var haystack strings.Builder
	for _, pat := range []string{
		"unity/Packages/*/Runtime", "unity/Packages/*/Editor", "unity/Packages/*/Tests",
		"tools/lvnconv/lvn", "tools/lvnconv/internal", "server", "panel/public/play",
	} {
		dirs, _ := filepath.Glob(filepath.Join(root, filepath.FromSlash(pat)))
		for _, d := range dirs {
			_ = filepath.Walk(d, func(p string, info os.FileInfo, err error) error {
				if err != nil || info.IsDir() {
					return nil
				}
				switch strings.ToLower(filepath.Ext(p)) {
				case ".cs", ".go", ".js":
					b, err := os.ReadFile(p)
					if err == nil {
						haystack.Write(b)
					}
				}
				return nil
			})
		}
	}
	hay := haystack.String()
	var ghosts []string
	for n := range named {
		// Составное имя («LvnPlayer.TraceKey») ищем по первой части: дом это
		// тип, остальное — его дверь.
		if !strings.Contains(hay, strings.Split(n, ".")[0]) {
			ghosts = append(ghosts, n)
		}
	}
	sort.Strings(ghosts)
	if len(ghosts) > 0 {
		t.Fatalf("канон называет дома, которых в коде нет: %v\n"+
			"Переименовали или удалили — поправьте канон: по такой строке пойдут искать.", ghosts)
	}
}
