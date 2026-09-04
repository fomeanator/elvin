package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

func writeContent(t *testing.T, root, rel, body string) {
	t.Helper()
	p := filepath.Join(root, filepath.FromSlash(rel))
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		t.Fatalf("каталог %s: %v", rel, err)
	}
	if err := os.WriteFile(p, []byte(body), 0o644); err != nil {
		t.Fatalf("файл %s: %v", rel, err)
	}
}

func changesOf(t *testing.T, s *server, since string) contentDelta {
	t.Helper()
	u := "/v1/content/changes"
	if since != "" {
		u += "?since=" + since
	}
	w := httptest.NewRecorder()
	s.handleContentChanges(w, httptest.NewRequest(http.MethodGet, u, nil))
	if w.Code != http.StatusOK {
		t.Fatalf("код %d вместо 200", w.Code)
	}
	var d contentDelta
	if err := json.Unmarshal(w.Body.Bytes(), &d); err != nil {
		t.Fatalf("ответ не разбирается: %v (%s)", err, w.Body.String())
	}
	return d
}

// ПРАВКА ОДНОЙ РЕПЛИКИ НЕ ДОЛЖНА СТОИТЬ ВСЕГО КАТАЛОГА.
//
// Замер на живом проекте 04.09: карта версий 282 КБ, манифест 435 КБ. Клиент
// забирал 717 КБ, чтобы применить изменение в сотню байт, — и тем чаще, чем
// чаще мы опрашиваем. Здесь проверяется, что сервер умеет назвать ИМЕННО
// изменившееся.
func TestРазницаНазываетТолькоИзменившееся(t *testing.T) {
	root := t.TempDir()
	writeContent(t, root, "scripts/ch1.lvn", `{"scene":"a","script":[]}`)
	writeContent(t, root, "scripts/ch2.lvn", `{"scene":"b","script":[]}`)
	writeContent(t, root, "bg/room.jpg", "картинка")
	s := &server{content: root}

	// Первый заход: клиент ещё ничего не видел.
	first := changesOf(t, s, "")
	if !first.Full {
		t.Fatalf("первому заходу не сказали «забирай всё» — он решит, что у него уже всё есть")
	}
	base := first.Version

	// Ничего не менялось.
	same := changesOf(t, s, base)
	if same.Full || len(same.Changed) != 0 || len(same.Removed) != 0 {
		t.Errorf("на неизменившемся контенте обещана работа: full=%v changed=%v removed=%v",
			same.Full, same.Changed, same.Removed)
	}

	// Автор правит ОДНУ главу.
	s.verCache = nil // иначе ответит кэш прошлого опроса
	writeContent(t, root, "scripts/ch1.lvn", `{"scene":"a","script":[{"op":"say","text":"новая реплика"}]}`)

	d := changesOf(t, s, base)
	if d.Full {
		t.Fatalf("сервер не смог назвать разницу и попросил забрать всё — ради этого всё и делалось")
	}
	if len(d.Changed) != 1 {
		t.Errorf("названо не то: changed=%v (ожидалась одна правленая глава)", d.Changed)
	}
	if h, ok := d.Changed["scripts/ch1.lvn"]; !ok || h == "" {
		t.Errorf("правленая глава без нового хеша: %v — клиенту придётся идти за картой версий целиком", d.Changed)
	}
	if len(d.Removed) != 0 {
		t.Errorf("ничего не удаляли, а сервер называет удалённое: %v", d.Removed)
	}
}

// УДАЛЁННОЕ НАЗЫВАЕТСЯ ОТДЕЛЬНО. Клиенту мало знать, что файл больше не тот, —
// без «его больше нет» он оставит его в кэше навсегда.
func TestУдалённыеФайлыНазваныОтдельно(t *testing.T) {
	root := t.TempDir()
	writeContent(t, root, "bg/room.jpg", "картинка")
	writeContent(t, root, "bg/hall.jpg", "другая")
	s := &server{content: root}
	base := changesOf(t, s, "").Version

	s.verCache = nil
	if err := os.Remove(filepath.Join(root, "bg", "hall.jpg")); err != nil {
		t.Fatalf("не удалился файл: %v", err)
	}

	d := changesOf(t, s, base)
	if len(d.Removed) != 1 || d.Removed[0] != "bg/hall.jpg" {
		t.Errorf("удалённый файл не назван: removed=%v changed=%v", d.Removed, d.Changed)
	}
}

// ВЕРСИЯ ВЫПАЛА ИЗ КОЛЬЦА — ЧЕСТНОЕ «ЗАБИРАЙ ВСЁ», А НЕ ВЫДУМАННАЯ РАЗНИЦА.
// Клиент, проспавший неделю, должен получить полный ответ, а не тишину, из
// которой он заключит, что у него всё свежее.
func TestНеизвестнаяВерсияПроситЗабратьВсё(t *testing.T) {
	root := t.TempDir()
	writeContent(t, root, "bg/room.jpg", "картинка")
	s := &server{content: root}
	changesOf(t, s, "") // наполнили кольцо

	d := changesOf(t, s, "0000000000000000000000000000000000000000000000000000000000000000")
	if !d.Full {
		t.Errorf("на незнакомую версию выдумана разница вместо честного «забирай всё»")
	}
}

// Кольцо ограничено сверху и не копит повторы: опрос идёт раз в две секунды,
// а контент меняется раз в часы — без этого память росла бы от одного лишь
// наблюдения за тишиной.
func TestКольцоОграниченоИНеКопитПовторы(t *testing.T) {
	var r deltaRing
	for i := 0; i < 5; i++ {
		r.remember("одна-и-та-же", map[string]string{"a": "1"})
	}
	if len(r.items) != 1 {
		t.Errorf("повторы копятся: в кольце %d снимков вместо одного", len(r.items))
	}
	for i := 0; i < deltaRingSize*3; i++ {
		r.remember(string(rune('a'+i%26))+string(rune('0'+i/26)), map[string]string{"a": "x"})
	}
	if len(r.items) > deltaRingSize {
		t.Errorf("кольцо не ограничено: %d снимков при потолке %d", len(r.items), deltaRingSize)
	}
}

// Снимок должен быть КОПИЕЙ: карта версий живёт в кэше сервера и переписывается
// на месте, а кольцо обязано помнить прошлое, а не ссылаться на настоящее.
func TestКольцоХранитКопиюАНеСсылку(t *testing.T) {
	var r deltaRing
	live := map[string]string{"a": "1"}
	r.remember("h1", live)
	live["a"] = "2"
	got, ok := r.find("h1")
	if !ok {
		t.Fatal("снимок потерян")
	}
	if got["a"] != "1" {
		t.Errorf("кольцо хранит ссылку на живую карту: помнит %q вместо «1» — "+
			"разница будет считаться от настоящего к настоящему и всегда выйдет пустой", got["a"])
	}
}
