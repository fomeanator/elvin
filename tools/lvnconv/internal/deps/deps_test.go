package deps

import (
	"archive/tar"
	"bytes"
	"compress/gzip"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

// makeTarball собирает codeload-подобный tar.gz: pax-заголовок с коммитом,
// корневой каталог repo-ref/, дальше файлы пакета.
func makeTarball(t *testing.T, commit string, files map[string]string) []byte {
	t.Helper()
	var buf bytes.Buffer
	gz := gzip.NewWriter(&buf)
	tw := tar.NewWriter(gz)
	if commit != "" {
		if err := tw.WriteHeader(&tar.Header{
			Name: "pax_global_header", Typeflag: tar.TypeXGlobalHeader,
			PAXRecords: map[string]string{"comment": commit},
		}); err != nil {
			t.Fatal(err)
		}
	}
	for name, body := range files {
		if err := tw.WriteHeader(&tar.Header{
			Name: "repo-v1/" + name, Typeflag: tar.TypeReg, Mode: 0o644, Size: int64(len(body)),
		}); err != nil {
			t.Fatal(err)
		}
		if _, err := tw.Write([]byte(body)); err != nil {
			t.Fatal(err)
		}
	}
	tw.Close()
	gz.Close()
	return buf.Bytes()
}

func writeManifest(t *testing.T, dir string, m Manifest) {
	t.Helper()
	raw, _ := json.MarshalIndent(m, "", "  ")
	if err := os.WriteFile(filepath.Join(dir, ManifestName), append(raw, '\n'), 0o644); err != nil {
		t.Fatal(err)
	}
}

// pkgFiles — минимальный валидный пакет @t/lib.
func pkgFiles() map[string]string {
	return map[string]string{
		"lvns.package.json": `{"name":"@t/lib","version":"1.0.0","exports":["lib.lvns"]}`,
		"lib.lvns":          "func lib_ping() { return 1 }\n",
		"assets/icon.png":   "PNGDATA",
	}
}

func TestGithubSyncLockAndOffline(t *testing.T) {
	tb := makeTarball(t, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", pkgFiles())
	hits := 0
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits++
		w.Write(tb)
	}))
	defer srv.Close()
	old := CodeloadBase
	CodeloadBase = srv.URL
	defer func() { CodeloadBase = old }()

	root := t.TempDir()
	writeManifest(t, root, Manifest{
		Name: "@t/game", ContentDir: "content",
		Dependencies: map[string]string{"@t/lib": "github:t/lib@v1.0.0"},
	})
	if err := Sync(root, false); err != nil {
		t.Fatal(err)
	}
	// vendor на месте
	if _, err := os.Stat(filepath.Join(root, VendorDir, "@t/lib/lib.lvns")); err != nil {
		t.Fatalf("vendor: %v", err)
	}
	// ассеты скопированы в контент
	if _, err := os.Stat(filepath.Join(root, "content/packages/lib/icon.png")); err != nil {
		t.Fatalf("assets: %v", err)
	}
	lock1, err := os.ReadFile(filepath.Join(root, LockName))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(lock1), "deadbeef") {
		t.Fatalf("в lock нет коммита: %s", lock1)
	}
	// Оффлайн: повторный sync не ходит в сеть и не меняет lock байт-в-байт.
	srv.Close()
	if err := Sync(root, false); err != nil {
		t.Fatalf("offline sync: %v", err)
	}
	lock2, _ := os.ReadFile(filepath.Join(root, LockName))
	if !bytes.Equal(lock1, lock2) {
		t.Fatalf("lock не детерминирован:\n%s\n---\n%s", lock1, lock2)
	}
	if hits != 1 {
		t.Fatalf("ожидал 1 сетевой запрос, было %d", hits)
	}
}

func TestGithubHashMismatchIsFatal(t *testing.T) {
	tb := makeTarball(t, "", pkgFiles())
	body := tb
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Write(body)
	}))
	defer srv.Close()
	old := CodeloadBase
	CodeloadBase = srv.URL
	defer func() { CodeloadBase = old }()

	root := t.TempDir()
	writeManifest(t, root, Manifest{Dependencies: map[string]string{"@t/lib": "github:t/lib@v1.0.0"}})
	if err := Sync(root, false); err != nil {
		t.Fatal(err)
	}
	// «Тег передвинули»: содержимое сменилось, vendor стёрт (иначе оффлайн-путь) —
	// sync обязан упасть на несовпадении SHA-256 с lock.
	files := pkgFiles()
	files["lib.lvns"] = "// evil\n"
	body = makeTarball(t, "", files)
	os.RemoveAll(filepath.Join(root, VendorDir))
	err := Sync(root, false)
	if err == nil || !strings.Contains(err.Error(), "SHA-256") {
		t.Fatalf("ожидал ошибку хэша, получил: %v", err)
	}
	// deps update — явное согласие на новое содержимое.
	if err := Sync(root, true); err != nil {
		t.Fatalf("update: %v", err)
	}
}

func TestMutableRefRejected(t *testing.T) {
	root := t.TempDir()
	writeManifest(t, root, Manifest{Dependencies: map[string]string{"@t/lib": "github:t/lib@main"}})
	err := Sync(root, false)
	if err == nil || !strings.Contains(err.Error(), "ветк") {
		t.Fatalf("ветка main должна быть запрещена, получил: %v", err)
	}
}

func TestTarballTraversalRejected(t *testing.T) {
	var buf bytes.Buffer
	gz := gzip.NewWriter(&buf)
	tw := tar.NewWriter(gz)
	tw.WriteHeader(&tar.Header{Name: "repo-v1/../../evil.lvns", Typeflag: tar.TypeReg, Size: 4, Mode: 0o644})
	tw.Write([]byte("evil"))
	tw.Close()
	gz.Close()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Write(buf.Bytes())
	}))
	defer srv.Close()
	old := CodeloadBase
	CodeloadBase = srv.URL
	defer func() { CodeloadBase = old }()

	root := t.TempDir()
	writeManifest(t, root, Manifest{Dependencies: map[string]string{"@t/lib": "github:t/lib@v1.0.0"}})
	err := Sync(root, false)
	if err == nil || !strings.Contains(err.Error(), "подозрительный путь") {
		t.Fatalf("ожидал отказ по traversal, получил: %v", err)
	}
}

func TestFileRefAndCycle(t *testing.T) {
	root := t.TempDir()
	// @t/a (file:) зависит от @t/b (file:), b — от a: цикл.
	for _, p := range []string{"vendor-src/a", "vendor-src/b"} {
		if err := os.MkdirAll(filepath.Join(root, p), 0o755); err != nil {
			t.Fatal(err)
		}
	}
	writeManifest(t, filepath.Join(root, "vendor-src/a"), Manifest{
		Name: "@t/a", Version: "0.1.0",
		Dependencies: map[string]string{"@t/b": "file:vendor-src/b"},
	})
	os.WriteFile(filepath.Join(root, "vendor-src/a/a.lvns"), []byte("// a\n"), 0o644)
	writeManifest(t, filepath.Join(root, "vendor-src/b"), Manifest{
		Name: "@t/b", Version: "0.1.0",
		Dependencies: map[string]string{"@t/a": "file:vendor-src/a"},
	})
	os.WriteFile(filepath.Join(root, "vendor-src/b/b.lvns"), []byte("// b\n"), 0o644)

	writeManifest(t, root, Manifest{Dependencies: map[string]string{"@t/a": "file:vendor-src/a"}})
	err := Sync(root, false)
	if err == nil || !strings.Contains(err.Error(), "цикл") {
		t.Fatalf("ожидал цикл, получил: %v", err)
	}

	// Разрываем цикл — sync проходит, оба пакета вендорятся.
	writeManifest(t, filepath.Join(root, "vendor-src/b"), Manifest{Name: "@t/b", Version: "0.1.0"})
	if err := Sync(root, false); err != nil {
		t.Fatal(err)
	}
	for _, f := range []string{"@t/a/a.lvns", "@t/b/b.lvns"} {
		if _, err := os.Stat(filepath.Join(root, VendorDir, f)); err != nil {
			t.Fatalf("vendor %s: %v", f, err)
		}
	}
}

func TestNameMismatchRejected(t *testing.T) {
	root := t.TempDir()
	src := filepath.Join(root, "impostor")
	os.MkdirAll(src, 0o755)
	writeManifest(t, src, Manifest{Name: "@t/other"})
	writeManifest(t, root, Manifest{Dependencies: map[string]string{"@t/lib": "file:impostor"}})
	err := Sync(root, false)
	if err == nil || !strings.Contains(err.Error(), "совпад") {
		t.Fatalf("ожидал отказ по имени, получил: %v", err)
	}
}

// КРУГ ЗАМКА: записали — прочитали — то же самое, и БАЙТ В БАЙТ при повторе.
//
// Половины писались порознь и не проверялись ни одна. У записи при этом
// записано правило словами — «пишет детерминированно», — и держалось оно
// ничем. Цена недетерминизма тихая и обидная: замок в каждом коммите выглядит
// изменённым, дифф шумит, а на слиянии двух веток получается конфликт там, где
// содержимое одинаковое.
func TestLockRoundTripAndDeterminism(t *testing.T) {
	dir := t.TempDir()
	l := &Lock{Packages: map[string]LockEntry{
		"@lvn/ui":    {Ref: "github:lvn/ui@v1", Commit: "abc", Files: map[string]string{"b.lvns": "2", "a.lvns": "1"}},
		"@lvn/audio": {Ref: "file:../audio", Files: map[string]string{"x.lvns": "9"}},
	}}
	if err := writeLock(dir, l); err != nil {
		t.Fatal(err)
	}
	first, err := os.ReadFile(filepath.Join(dir, LockName))
	if err != nil {
		t.Fatal(err)
	}

	back, err := readLock(dir)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(l.Packages, back.Packages) {
		t.Errorf("круг не сошёлся:\n  было  %#v\n  стало %#v", l.Packages, back.Packages)
	}

	// Второй заход по ПРОЧИТАННОМУ: порядок ключей в карте Go случаен от
	// запуска к запуску, и если бы запись от него зависела, файл менялся бы
	// сам собой.
	if err := writeLock(dir, back); err != nil {
		t.Fatal(err)
	}
	second, _ := os.ReadFile(filepath.Join(dir, LockName))
	if string(first) != string(second) {
		t.Errorf("повтор записи дал другой файл — замок недетерминирован:\n  %s\n  %s", first, second)
	}
	if len(first) == 0 || first[len(first)-1] != '\n' {
		t.Error("файл без перевода строки в конце — дифф будет вечно трогать последнюю строку")
	}
}

// Замка ещё нет — это НЕ ошибка: проект без него законен, и первый же Sync
// его напишет. Отличать «нет файла» от «файл битый» обязано чтение, иначе
// новый проект не собрать вовсе.
func TestMissingLockIsEmptyNotAnError(t *testing.T) {
	l, err := readLock(t.TempDir())
	if err != nil {
		t.Fatalf("отсутствие замка объявлено ошибкой: %v", err)
	}
	if l == nil || l.Packages == nil {
		t.Fatal("пустой замок обязан быть пригодным к записи, а не nil")
	}
	if len(l.Packages) != 0 {
		t.Errorf("в пустом замке что-то есть: %v", l.Packages)
	}
}
