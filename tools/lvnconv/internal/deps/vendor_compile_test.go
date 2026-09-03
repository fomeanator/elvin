package deps_test

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/deps"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// ПАКЕТ ПОД ЖИВЫМ ПРОЕКТОМ — ЦЕПОЧКА ЦЕЛИКОМ.
//
// Соседние тесты проверяют пакетную систему В ОДИНОЧКУ: замок, хэши, отказ от
// подвижных ссылок, обход каталога. Всё это про сам deps. Но обещание звучит
// иначе — «обновить зависимость под живым проектом», и между deps и проектом
// лежит компилятор: он резолвит "@scope/pkg/file.lvns" из vendor-каталога.
// Здесь проверяется именно стык.
func TestПакетПодЖивымПроектом(t *testing.T) {
	root := t.TempDir()
	proj := filepath.Join(root, "proj")
	pkg := filepath.Join(root, "pkg")
	mkdir(t, proj)
	mkdir(t, pkg)

	writePkg(t, pkg, "1.0.0", "версия ОДИН")
	write(t, filepath.Join(proj, "main.lvns"),
		"scene probe\ninclude \"@probe/greet/greet.lvns\"\nNarrator: Проект на месте.\n")

	if err := deps.Add(proj, "@probe/greet", "file:../pkg"); err != nil {
		t.Fatalf("зависимость не добавилась: %v", err)
	}

	if got := compile(t, proj); !strings.Contains(got, "версия ОДИН") {
		t.Fatalf("проект не увидел содержимое пакета:\n%s", got)
	}

	// СКАЧИВАНИЕ — ТУЛИНГ, НЕ ЯЗЫК. Компилятор обязан собираться из vendor и
	// никогда не ходить за исходником пакета: иначе сборка на машине без сети
	// (или без соседнего каталога) молча разойдётся с той, что была у автора.
	if err := os.RemoveAll(pkg); err != nil {
		t.Fatalf("не удалось убрать исходник пакета: %v", err)
	}
	if got := compile(t, proj); !strings.Contains(got, "версия ОДИН") {
		t.Errorf("компилятор полез за исходником пакета вместо vendor-каталога.\n"+
			"Сборка перестала быть воспроизводимой: убрали соседний каталог — и вот:\n%s", got)
	}

	// Автор пакета выпустил новую версию. Обновление обязано доехать ДО текста,
	// а не только до замка: замок без содержимого — бухгалтерия без товара.
	mkdir(t, pkg)
	writePkg(t, pkg, "2.0.0", "версия ДВА")
	if err := deps.Sync(proj, true); err != nil {
		t.Fatalf("обновление не прошло: %v", err)
	}
	if got := compile(t, proj); !strings.Contains(got, "версия ДВА") {
		t.Errorf("обновление не доехало до скомпилированного проекта:\n%s", got)
	}
}

// Замок обязан называть vendor полностью: по нему восстанавливают сборку.
func TestЗамокНазываетКаждыйФайлПакета(t *testing.T) {
	root := t.TempDir()
	proj, pkg := filepath.Join(root, "proj"), filepath.Join(root, "pkg")
	mkdir(t, proj)
	mkdir(t, pkg)
	writePkg(t, pkg, "1.0.0", "неважно")
	if err := deps.Add(proj, "@probe/greet", "file:../pkg"); err != nil {
		t.Fatalf("зависимость не добавилась: %v", err)
	}
	lock, err := os.ReadFile(filepath.Join(proj, deps.LockName))
	if err != nil {
		t.Fatalf("замок не написан: %v", err)
	}
	for _, want := range []string{"greet.lvns", deps.ManifestName} {
		if !strings.Contains(string(lock), want) {
			t.Errorf("замок не называет %q — восстановить vendor по нему нельзя", want)
		}
	}
}

func compile(t *testing.T, proj string) string {
	t.Helper()
	doc, err := lvns.ConvertFile(filepath.Join(proj, "main.lvns"))
	if err != nil {
		t.Fatalf("проект не компилируется: %v", err)
	}
	var sb strings.Builder
	for _, op := range doc.Script {
		if s, ok := op["text"].(string); ok {
			sb.WriteString(s)
			sb.WriteByte('\n')
		}
	}
	return sb.String()
}

func writePkg(t *testing.T, dir, version, line string) {
	t.Helper()
	write(t, filepath.Join(dir, deps.ManifestName),
		`{"name":"@probe/greet","version":"`+version+`","license":"MIT","min_engine":"0","exports":["greet.lvns"]}`)
	write(t, filepath.Join(dir, "greet.lvns"), ":greet\nGuide: "+line+".\n")
}

func mkdir(t *testing.T, d string) {
	t.Helper()
	if err := os.MkdirAll(d, 0o755); err != nil {
		t.Fatalf("не создан %s: %v", d, err)
	}
}

func write(t *testing.T, p, s string) {
	t.Helper()
	if err := os.WriteFile(p, []byte(s), 0o644); err != nil {
		t.Fatalf("не записан %s: %v", p, err)
	}
}
