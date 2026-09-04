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
	// Порог охвата: пустые списки сверяются друг с другом безупречно.
	atLeast(t, len(truth), 25, "команд в грамматике")
	atLeast(t, len(shown), 25, "команд в подсказках")
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

// СПИСОК, КОТОРЫЙ НАДО ПОМНИТЬ ПОПОЛНЯТЬ, — ЭТО НЕ СТРАЖ.
//
// Сверялись ДВА файла из четырёх: `analyze.js` и `index.js` лежали в той же
// вшитой копии и не проверялись ничем. Совпадали они по случайности — генератор
// копирует каталог целиком, — то есть держались на дисциплине, а не на правиле.
// Замер 04.09: все четыре совпадали, и именно поэтому пропажу проверки никто
// бы не заметил до первой правки мимо `npm run gen`.
//
// Теперь сверяется ВСЁ, у чего есть пара в исходниках. Добавили файл в пакет —
// он под присмотром с первой минуты, а не с той, когда о нём вспомнят.
func TestКопияГрамматикиВРасширенииНеФорк(t *testing.T) {
	root := repoRoot(t)
	srcDir := filepath.Join(root, "tools", "lvn-lang", "src")
	libDir := filepath.Join(root, "tools", "vscode-lvn", "lib", "lvn-lang")

	entries, err := os.ReadDir(libDir)
	if err != nil {
		t.Fatalf("вшитая копия пропала целиком: %v", err)
	}
	checked := 0
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		srcPath := filepath.Join(srcDir, name)
		if _, err := os.Stat(srcPath); err != nil {
			// У манифеста пакета пары в src/ нет по устройству — он лежит
			// уровнем выше. Сверять нечего, и придираться не за что.
			continue
		}
		src, err := os.ReadFile(srcPath)
		if err != nil {
			t.Fatalf("исходник %s не читается: %v", name, err)
		}
		vendored, err := os.ReadFile(filepath.Join(libDir, name))
		if err != nil {
			t.Fatalf("вшитая копия %s не читается: %v", name, err)
		}
		checked++
		if string(src) != string(vendored) {
			t.Errorf("tools/vscode-lvn/lib/lvn-lang/%s разошлась с правдой.\n"+
				"Перегенерируйте обе одним шагом:\n"+
				"  (cd tools/lvn-lang && npm run gen)\n"+
				"Пока копия своя, расширение подсказывает другой язык.", name)
		}
	}
	if checked < 2 {
		t.Fatalf("сверено %d файлов — страж потерял предмет охраны", checked)
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
	for _, name := range ManifestSchemaSources {
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
