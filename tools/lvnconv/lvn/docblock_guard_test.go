package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"testing"
)

// СТРАЖИ ОПИСАНИЙ — комментарий, который врёт, дороже отсутствующего.
//
// Скан 02.09 нашёл тринадцать `///`-описаний, стоящих ни над чем: методы
// переехали при разрезании файлов на partial-части, описания остались на
// старом месте — перед `}` класса, перед разделителем секции, перед чужим
// описанием. Хуже пустых — прилипшие к ЧУЖОМУ объявлению: FontPath был
// описан как «скругление карточек», RemoveAll — как «момент кроссфейда».
// Компилятор о таком молчит, а читатель верит описанию больше, чем коду.
//
// Второй скан нашёл абзац, скопированный вместе с подпиской: LvnPicture.Pin
// подписывал AttachToPanelEvent дважды, и второй Hold на миг ронял счётчик
// держателя в ноль — открывая то самое окно, против которого правка заведена.
// Одинаковый многострочный комментарий дважды в файле — это почти всегда дубль
// кода под ним.

// docblockRepoRoot — корень репозитория: тот, где лежит unity/Packages.
func docblockRepoRoot(t *testing.T) string {
	t.Helper()
	dir, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 8; i++ {
		if st, err := os.Stat(filepath.Join(dir, "unity", "Packages")); err == nil && st.IsDir() {
			return dir
		}
		dir = filepath.Dir(dir)
	}
	t.Fatal("не нашёл корень репозитория (unity/Packages) выше " + dir)
	return ""
}

// runtimeSources — все .cs под unity/Packages/*/Runtime.
func runtimeSources(t *testing.T) []string {
	t.Helper()
	root := docblockRepoRoot(t)
	pkgs, err := filepath.Glob(filepath.Join(root, "unity", "Packages", "*", "Runtime"))
	if err != nil {
		t.Fatal(err)
	}
	var out []string
	for _, p := range pkgs {
		_ = filepath.Walk(p, func(path string, info os.FileInfo, err error) error {
			if err == nil && !info.IsDir() && strings.HasSuffix(path, ".cs") {
				out = append(out, path)
			}
			return nil
		})
	}
	if len(out) < 100 {
		t.Fatalf("Runtime-файлов подозрительно мало: %d", len(out))
	}
	return out
}

func isDocLine(s string) bool   { return strings.HasPrefix(s, "///") }
func isProseLine(s string) bool { return strings.HasPrefix(s, "//") && !strings.HasPrefix(s, "///") }

// isSkippable — то, что стоит между описанием и объявлением законно: пустые
// строки, атрибуты на своей строке, директивы препроцессора.
func isSkippable(s string) bool {
	if s == "" || strings.HasPrefix(s, "#") {
		return true
	}
	return strings.HasPrefix(s, "[") && strings.HasSuffix(s, "]")
}

// orphanDocblocks — описания, за которыми не стоит объявление: перед `}`,
// перед разделителем `// ──`, перед другим описанием, в конце файла, или
// перед прозаическим `//`-абзацем, за которым идёт `}`/описание/конец.
func orphanDocblocks(src string) []string {
	lines := strings.Split(src, "\n")
	var hits []string
	n := len(lines)
	for i := 0; i < n; {
		if !isDocLine(strings.TrimSpace(lines[i])) {
			i++
			continue
		}
		start := i
		for i < n && isDocLine(strings.TrimSpace(lines[i])) {
			i++
		}
		j := i
		for j < n && isSkippable(strings.TrimSpace(lines[j])) {
			j++
		}
		why := ""
		switch {
		case j >= n:
			why = "конец файла"
		case strings.HasPrefix(strings.TrimSpace(lines[j]), "}"):
			why = "перед }"
		case strings.HasPrefix(strings.TrimSpace(lines[j]), "// ──"):
			why = "перед разделителем секции"
		case isDocLine(strings.TrimSpace(lines[j])):
			why = "перед другим описанием"
		case isProseLine(strings.TrimSpace(lines[j])):
			k := j
			for k < n && (isProseLine(strings.TrimSpace(lines[k])) || strings.TrimSpace(lines[k]) == "") {
				k++
			}
			switch {
			case k >= n:
				why = "абзац комментария, потом конец файла"
			case strings.HasPrefix(strings.TrimSpace(lines[k]), "}"):
				why = "абзац комментария, потом }"
			case isDocLine(strings.TrimSpace(lines[k])):
				why = "абзац комментария, потом другое описание"
			}
		}
		if why != "" {
			hits = append(hits, strings.TrimSpace(lines[start])+"  ← строка "+strconv.Itoa(start+1)+": "+why)
		}
	}
	return hits
}

func TestDocblocksDescribeSomething(t *testing.T) {
	var bad []string
	for _, path := range runtimeSources(t) {
		src := string(mustRead(t, path))
		for _, h := range orphanDocblocks(src) {
			bad = append(bad, filepath.Base(path)+": "+h)
		}
	}
	if len(bad) > 0 {
		t.Fatalf("описания без предмета (метод переехал, описание осталось):\n  %s\n"+
			"верните описание к его объявлению или удалите — читатель верит ему больше, чем коду",
			strings.Join(bad, "\n  "))
	}
}

// commentParagraphs — абзацы комментария из трёх и более строк, приведённые
// к одной строке (без слэшей и лишних пробелов); мелкие (<120 знаков) не в счёт.
func commentParagraphs(src string) map[string][]int {
	lines := strings.Split(src, "\n")
	out := map[string][]int{}
	n := len(lines)
	for i := 0; i < n; {
		if !strings.HasPrefix(strings.TrimSpace(lines[i]), "//") {
			i++
			continue
		}
		start := i
		var parts []string
		for i < n && strings.HasPrefix(strings.TrimSpace(lines[i]), "//") {
			parts = append(parts, strings.TrimSpace(strings.TrimLeft(strings.TrimSpace(lines[i]), "/")))
			i++
		}
		if i-start < 3 {
			continue
		}
		text := strings.Join(strings.Fields(strings.Join(parts, " ")), " ")
		if len(text) < 120 {
			continue
		}
		out[text] = append(out[text], start+1)
	}
	return out
}

func TestCommentParagraphsAreNotDuplicated(t *testing.T) {
	var bad []string
	for _, path := range runtimeSources(t) {
		for text, at := range commentParagraphs(string(mustRead(t, path))) {
			if len(at) > 1 {
				short := text
				if len([]rune(short)) > 70 {
					short = string([]rune(short)[:70]) + "…"
				}
				bad = append(bad, filepath.Base(path)+": строки "+joinInts(at)+" — «"+short+"»")
			}
		}
	}
	if len(bad) > 0 {
		t.Fatalf("абзац комментария стоит в файле дважды — а под ним обычно дважды стоит и код:\n  %s",
			strings.Join(bad, "\n  "))
	}
}

func joinInts(xs []int) string {
	var s []string
	for _, x := range xs {
		s = append(s, strconv.Itoa(x))
	}
	return strings.Join(s, ", ")
}

// Папку в адресе узнают ОДНИМ способом — без учёта регистра. Урок
// «/Art/Hero.PNG» был усвоен у LargeStoryArt/HasFolder и не дошёл до соседних
// проверок в том же файле: /UI/ считался артом истории, /Pixel/ уменьшался.
func TestFolderChecksIgnoreCase(t *testing.T) {
	root := docblockRepoRoot(t)
	path := filepath.Join(root, "unity", "Packages", "com.lvn.engine", "Runtime", "Content", "DownloadPolicy.cs")
	src := stripComments(string(mustRead(t, path)))
	if m := regexp.MustCompile(`\burl\.Contains\("/[A-Za-z]+/"\)`).FindAllString(src, -1); len(m) > 0 {
		t.Fatalf("DownloadPolicy сверяет папку с учётом регистра: %v — спрашивайте HasFolder", m)
	}
	body, ok := ruleBody(src, "private static bool HasFolder(string url, string folder)")
	if !ok {
		t.Fatal("не нашёл HasFolder в DownloadPolicy")
	}
	if !strings.Contains(body, "OrdinalIgnoreCase") {
		t.Fatal("HasFolder обязан сравнивать без учёта регистра (OrdinalIgnoreCase)")
	}
}
