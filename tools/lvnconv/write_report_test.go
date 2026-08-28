package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// ЧТО ЗАПИСАЛИ — ТО И ПРОВЕРИЛИ.
//
// Импорт через студию прогонял результат через валидатор
// (server.checkImportedScripts), а тот же импорт из командной строки — нет.
// Один файл проверялся или не проверялся в зависимости от пути, каким его
// получили, и локальный автор узнавал о находках только в игре.
//
// Цена известна поимённо: в главах Cold тридцать мест, где гардероб замкнут в
// петлю без выхода. Импортёр эту болезнь давно лечит, но контент собирали до
// починки — и молчаливый CLI не сказал ни слова ни тогда, ни при каждой
// следующей сборке.
func TestWrittenLvnIsReportedOn(t *testing.T) {
	dir := t.TempDir()
	out := filepath.Join(dir, "chapter.lvn")

	// Ловушка: возврат на метку без единого пути наружу.
	const trap = `{"scene":"t","script":[
	 {"op":"label","id":"loop"},
	 {"op":"say","text":"крутимся"},
	 {"op":"goto","label":"loop"},
	 {"op":"say","text":"сюда не попасть"}
	]}`

	stderr := captureStderr(t, func() { writeOut(out, []byte(trap)) })

	if _, err := os.Stat(out); err != nil {
		t.Fatalf("файл обязан быть записан несмотря на находки: %v", err)
	}
	if !strings.Contains(stderr, "has no way out") {
		t.Fatalf("о ловушке должно быть сказано при записи, получено: %q", stderr)
	}
	if !strings.Contains(stderr, "written "+out) {
		t.Fatalf("итоговая строка должна назвать файл: %q", stderr)
	}
}

// Чистая глава молчит: диагностика, которая говорит всегда, не диагностика.
func TestCleanLvnWritesQuietly(t *testing.T) {
	dir := t.TempDir()
	out := filepath.Join(dir, "clean.lvn")
	const clean = `{"scene":"t","script":[{"op":"say","text":"привет"}]}`

	stderr := captureStderr(t, func() { writeOut(out, []byte(clean)) })

	if strings.TrimSpace(stderr) != "" {
		t.Fatalf("чистая глава не должна ничего печатать, получено: %q", stderr)
	}
}

// Не-.lvn (каталог строк, отчёт) через проверку не гоняем.
func TestNonLvnOutputIsNotValidated(t *testing.T) {
	dir := t.TempDir()
	out := filepath.Join(dir, "strings.json")

	stderr := captureStderr(t, func() { writeOut(out, []byte(`{"a":"b"}`)) })

	if strings.TrimSpace(stderr) != "" {
		t.Fatalf("посторонний файл не должен проверяться как глава: %q", stderr)
	}
}

func captureStderr(t *testing.T, fn func()) string {
	t.Helper()
	prev := os.Stderr
	r, w, err := os.Pipe()
	if err != nil {
		t.Fatal(err)
	}
	os.Stderr = w
	done := make(chan string, 1)
	go func() {
		var sb strings.Builder
		buf := make([]byte, 4096)
		for {
			n, err := r.Read(buf)
			if n > 0 {
				sb.Write(buf[:n])
			}
			if err != nil {
				break
			}
		}
		done <- sb.String()
	}()
	fn()
	w.Close()
	os.Stderr = prev
	return <-done
}
