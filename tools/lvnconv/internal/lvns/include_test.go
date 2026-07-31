package lvns

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func writeFiles(t *testing.T, files map[string]string) string {
	t.Helper()
	dir := t.TempDir()
	for name, body := range files {
		p := filepath.Join(dir, name)
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(p, []byte(body), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	return dir
}

func ops(d *Doc) []string {
	out := make([]string, 0, len(d.Script))
	for _, c := range d.Script {
		if s, ok := c["op"].(string); ok {
			out = append(out, s)
		}
	}
	return out
}

// The point of the feature: a chapter pulls shared mechanics from one file
// instead of every chapter carrying its own copy that drifts.
func TestIncludeBringsInSharedMechanics(t *testing.T) {
	dir := writeFiles(t, map[string]string{
		"mech.lvns": "elements = []\nrecipes = {}\nrecipes = put(recipes, \"fire+water\", \"steam\")\n",
		"ch.lvns":   "scene ch\ninclude \"mech.lvns\"\nelements = push(elements, \"fire\")\nGot {len(elements)}.\n-> __end\n",
	})
	doc, err := ConvertFile(filepath.Join(dir, "ch.lvns"))
	if err != nil {
		t.Fatal(err)
	}
	got := ops(doc)
	sets := 0
	for _, o := range got {
		if o == "set" {
			sets++
		}
	}
	if sets != 4 { // 3 from mech + 1 from the chapter
		t.Fatalf("ops = %v, want 4 sets (3 included + 1 local)", got)
	}
}

// A def preset declared in the shared file must work at the call site: that is
// the whole reason to have a shared file at all.
func TestIncludedPresetExpandsInTheIncludingFile(t *testing.T) {
	dir := writeFiles(t, map[string]string{
		"mech.lvns": "def enter actor hero left\n",
		"ch.lvns":   "scene ch\ninclude \"mech.lvns\"\nenter\n-> __end\n",
	})
	doc, err := ConvertFile(filepath.Join(dir, "ch.lvns"))
	if err != nil {
		t.Fatal(err)
	}
	for _, o := range ops(doc) {
		if o == "actor" {
			return
		}
	}
	t.Fatalf("the preset did not expand: %v", ops(doc))
}

// An error inside an included file must name THAT file and THAT line. Without
// the remap the author is sent hunting for "line 412" in a 60-line chapter.
func TestErrorInsideAnIncludeNamesTheRealFileAndLine(t *testing.T) {
	dir := writeFiles(t, map[string]string{
		// A preset name the compiler rejects — a real error, three lines in.
		"mech.lvns": "// ok\n// ok\ndef = 1\n",
		"ch.lvns":   "scene ch\ninclude \"mech.lvns\"\n-> __end\n",
	})
	_, err := ConvertFile(filepath.Join(dir, "ch.lvns"))
	if err == nil {
		t.Fatal("a broken included file compiled")
	}
	if !strings.Contains(err.Error(), "mech.lvns:3") {
		t.Errorf("error = %q, want it to point at mech.lvns line 3, not a line of the joined source", err)
	}
	if !strings.Contains(err.Error(), "mech.lvns:") {
		t.Errorf("error = %q, want it to name mech.lvns and the line inside it", err)
	}
}

// A cycle must be a readable diagnosis, not a stack overflow.
func TestIncludeCycleIsReportedWithTheChain(t *testing.T) {
	dir := writeFiles(t, map[string]string{
		"a.lvns": "scene a\ninclude \"b.lvns\"\n-> __end\n",
		"b.lvns": "include \"a.lvns\"\n",
	})
	_, err := ConvertFile(filepath.Join(dir, "a.lvns"))
	if err == nil {
		t.Fatal("a cycle compiled — expected an error")
	}
	msg := err.Error()
	if !strings.Contains(msg, "cycle") || !strings.Contains(msg, "a.lvns -> b.lvns -> a.lvns") {
		t.Errorf("error = %q, want the full chain starting at the root", msg)
	}
}

func TestSelfIncludeIsACycle(t *testing.T) {
	dir := writeFiles(t, map[string]string{
		"s.lvns": "scene s\ninclude \"s.lvns\"\n-> __end\n",
	})
	if _, err := ConvertFile(filepath.Join(dir, "s.lvns")); err == nil {
		t.Fatal("a file including itself compiled")
	}
}

// The diamond A→B, A→C, B→D, C→D is the NORMAL shape once there is a shared
// mechanics file, so a second include must be skipped rather than duplicated:
// text-substituting twice would emit duplicate labels, which the validator
// rejects.
func TestRepeatedIncludeIsIdempotent(t *testing.T) {
	dir := writeFiles(t, map[string]string{
		"d.lvns": "scene d\ninclude \"x.lvns\"\ninclude \"y.lvns\"\nDone.\n-> __end\n",
		"x.lvns": "include \"z.lvns\"\n",
		"y.lvns": "include \"z.lvns\"\n",
		"z.lvns": "shared_flag = 1\n",
	})
	doc, err := ConvertFile(filepath.Join(dir, "d.lvns"))
	if err != nil {
		t.Fatal(err)
	}
	sets := 0
	for _, o := range ops(doc) {
		if o == "set" {
			sets++
		}
	}
	if sets != 1 {
		t.Errorf("shared file emitted %d sets, want exactly 1 — a second include must be skipped", sets)
	}
}

func TestMissingIncludeNamesThePathItLookedFor(t *testing.T) {
	dir := writeFiles(t, map[string]string{
		"c.lvns": "scene c\ninclude \"nope.lvns\"\n-> __end\n",
	})
	_, err := ConvertFile(filepath.Join(dir, "c.lvns"))
	if err == nil {
		t.Fatal("a missing include compiled")
	}
	if !strings.Contains(err.Error(), "nope.lvns") {
		t.Errorf("error = %q, want the path it searched for", err)
	}
}

// Paths resolve against the INCLUDING file, so the same chapter compiles the
// same way from any working directory.
func TestPathIsRelativeToTheIncludingFile(t *testing.T) {
	dir := writeFiles(t, map[string]string{
		"lib/mech.lvns": "include \"deep.lvns\"\n",
		"lib/deep.lvns": "deep_flag = 1\n",
		"story/ch.lvns": "scene ch\ninclude \"../lib/mech.lvns\"\n-> __end\n",
	})
	if _, err := ConvertFile(filepath.Join(dir, "story", "ch.lvns")); err != nil {
		t.Fatalf("nested relative include failed: %v", err)
	}
}

// Convert() has no file to resolve against. Silence here would be the worst
// outcome: the line would fall through to narration and PRINT ITSELF to the
// player — the one step in docs/adding-an-op.md with no guard of its own.
func TestStrayIncludeWithoutAFileIsAnError(t *testing.T) {
	_, err := Convert("scene x\ninclude \"mech.lvns\"\n-> __end\n")
	if err == nil {
		t.Fatal("include outside a file context compiled — it would have become a dialogue line")
	}
	if !strings.Contains(err.Error(), "include") {
		t.Errorf("error = %q, want it to explain that include needs a file", err)
	}
}

// Строки САМОГО файла после include тоже сдвинуты длиной подстановки. Первая
// версия перекладывала только подключённые файлы, и ошибка на строке 10 главы
// приезжала как "line 66" — то есть автора отправляли не туда.
func TestErrorAfterAnIncludeKeepsTheRootFileLineNumber(t *testing.T) {
	dir := writeFiles(t, map[string]string{
		"mech.lvns": strings.Repeat("// наполнитель\n", 50),
		"ch.lvns":   "scene ch\ninclude \"mech.lvns\"\ndef = 1\n-> __end\n",
	})
	_, err := ConvertFile(filepath.Join(dir, "ch.lvns"))
	if err == nil {
		t.Fatal("сломанная строка скомпилировалась")
	}
	if !strings.Contains(err.Error(), "line 3:") {
		t.Errorf("ошибка = %q, ожидалось «line 3» — настоящая строка главы, а не строка склейки", err)
	}
}

// ── include в браузере: файлов на диске нет, буферы редактора есть ──────────
//
// Веб-IDE компилирует через wasm, где файловой системы нет вовсе. Пока
// компилятор умел только диск, любая глава с include давала в студии
// «подключение работает только при компиляции файла» — то есть многофайловую
// игру в редакторе было не написать, хотя язык это умеет.

func TestConvertFilesResolvesIncludeFromEditorBuffers(t *testing.T) {
	doc, err := ConvertFiles("scene ch\ninclude \"mech.lvns\"\nЕсть {золото}.\n-> __end\n", "ch.lvns",
		map[string]string{"mech.lvns": "золото = 5\n"})
	if err != nil {
		t.Fatalf("не скомпилировалось: %v", err)
	}
	sets := 0
	for _, c := range doc.Script {
		if c["op"] == "set" {
			sets++
		}
	}
	if sets != 1 {
		t.Fatalf("подключённый файл не вклеился: %v", ops(doc))
	}
}

// Отсутствующий файл должен подсказывать, ЧТО есть рядом: в браузере автор не
// может «посмотреть каталог», и путь на диске ему ни о чём не скажет.
func TestConvertFilesMissingIncludeListsWhatIsAvailable(t *testing.T) {
	_, err := ConvertFiles("scene ch\ninclude \"нет.lvns\"\n-> __end\n", "ch.lvns",
		map[string]string{"mech.lvns": "x = 1\n", "voices.lvns": "y = 2\n"})
	if err == nil {
		t.Fatal("отсутствующий файл скомпилировался")
	}
	for _, want := range []string{"нет.lvns", "mech.lvns", "voices.lvns"} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("в ошибке нет %q: %v", want, err)
		}
	}
}

func TestConvertFilesKeepsCycleAndIdempotenceRules(t *testing.T) {
	// Цикл через САМ открытый файл: глава подключает механики, механики —
	// главу. Без имени корня это разворачивалось второй копией молча.
	if _, err := ConvertFiles("scene a\ninclude \"b.lvns\"\n-> __end\n", "a.lvns",
		map[string]string{"b.lvns": "include \"a.lvns\"\n", "a.lvns": "scene a\n"}); err == nil {
		t.Error("цикл через открытый файл не поймали")
	}
	// Ромб: два файла подключают одну механику — дубля меток быть не должно.
	doc, err := ConvertFiles("scene d\ninclude \"x.lvns\"\ninclude \"y.lvns\"\n-> __end\n", "d.lvns",
		map[string]string{"x.lvns": "include \"z.lvns\"\n", "y.lvns": "include \"z.lvns\"\n", "z.lvns": "флаг = 1\n"})
	if err != nil {
		t.Fatalf("ромб не собрался: %v", err)
	}
	sets := 0
	for _, c := range doc.Script {
		if c["op"] == "set" {
			sets++
		}
	}
	if sets != 1 {
		t.Errorf("общий файл вклеился %d раз, ожидался 1", sets)
	}
}

// Номер строки обязан указывать в НАСТОЯЩИЙ файл: автор видит в редакторе
// именно его, а не склейку.
func TestConvertFilesErrorPointsAtTheRealFileAndLine(t *testing.T) {
	_, err := ConvertFiles("scene ch\ninclude \"mech.lvns\"\n-> __end\n", "ch.lvns",
		map[string]string{"mech.lvns": "// ok\n// ok\ndef = 1\n"})
	if err == nil {
		t.Fatal("битый подключённый файл скомпилировался")
	}
	if !strings.Contains(err.Error(), "mech.lvns:3") {
		t.Errorf("ошибка = %q, ожидалось mech.lvns:3", err)
	}
}

// Пакетный путь "@scope/pkg/file.lvns" резолвится через lvns_packages/,
// который ищется ВВЕРХ по дереву от подключающего файла: глава может лежать
// в глубине проекта (content/game/ch.lvns), а vendor — в его корне.
func TestIncludePackagePathResolvesViaVendorDir(t *testing.T) {
	dir := writeFiles(t, map[string]string{
		"lvns_packages/@t/lib/lib.lvns":   "lib_gold = 7\ninclude \"extra.lvns\"\n",
		"lvns_packages/@t/lib/extra.lvns": "lib_extra = 1\n",
		"content/game/ch.lvns":            "scene ch\ninclude \"@t/lib/lib.lvns\"\nGot {lib_gold}.\n-> __end\n",
	})
	doc, err := ConvertFile(filepath.Join(dir, "content/game/ch.lvns"))
	if err != nil {
		t.Fatal(err)
	}
	sets := 0
	for _, o := range ops(doc) {
		if o == "set" {
			sets++
		}
	}
	// lib_gold + lib_extra: сам пакет и его ВНУТРЕННИЙ относительный include.
	if sets != 2 {
		t.Fatalf("ops = %v, want 2 sets from the package", ops(doc))
	}
}

// Ненайденный пакет обязан назвать место, где искали, — lvns_packages/…,
// а не молча превратить директиву в реплику.
func TestMissingPackageIncludeNamesTheVendorDir(t *testing.T) {
	dir := writeFiles(t, map[string]string{
		"ch.lvns": "scene ch\ninclude \"@t/ghost/lib.lvns\"\n-> __end\n",
	})
	_, err := ConvertFile(filepath.Join(dir, "ch.lvns"))
	if err == nil {
		t.Fatal("include несуществующего пакета скомпилировался")
	}
	if !strings.Contains(err.Error(), "lvns_packages") {
		t.Errorf("ошибка = %q, ожидал упоминание lvns_packages", err)
	}
}
