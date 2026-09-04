package main

// Публикация, которая ничего не изменила, ничего и не стоит.
//
// Одну и ту же главу тем же текстом публикуют чаще, чем кажется: клиент не
// дождался ответа и повторил, панель выкладывает всё разом, конвейер
// переиздаёт после сборки. Раньше каждый такой вызов двигал rev в манифесте, а
// rev входит в общую версию контента — ту самую, по которой играющий клиент
// решает, что мир изменился, идёт за каталогом (в живой студии это 436 КБ),
// перечитывает открытую главу мимо кэша и пересобирает фигуры на сцене. За
// новости, которых нет.
//
// Здесь проверяется обе стороны правила: тишина, когда менять нечего, и
// движение, когда есть.

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

func manifestRev(t *testing.T, s *server) int {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join(s.content, "manifest.json"))
	if err != nil {
		t.Fatal(err)
	}
	var m map[string]any
	if err := json.Unmarshal(raw, &m); err != nil {
		t.Fatal(err)
	}
	rev, _ := m["rev"].(float64)
	return int(rev)
}

const republishLvns = "scene proba\n\nРеплика.\n-> __end\n"

func TestRepublishOfTheSameChapterLeavesTheCatalogAlone(t *testing.T) {
	s := publishSrv(t)
	body := map[string]any{"id": "proba", "name": "Проба", "chapter": 1, "lvns": republishLvns}

	if code, _ := publish(t, s, body); code != 200 {
		t.Fatalf("первая публикация не прошла: %d", code)
	}
	revAfterFirst := manifestRev(t, s)
	before, err := os.ReadFile(filepath.Join(s.content, "manifest.json"))
	if err != nil {
		t.Fatal(err)
	}
	scriptPath := filepath.Join(s.content, "scripts", "proba-ch01.lvn")
	st1, err := os.Stat(scriptPath)
	if err != nil {
		t.Fatal(err)
	}
	// Снимки, оставленные ПЕРВОЙ публикацией, законны: она каталог изменила.
	// Считаем прибавку от холостых повторов.
	histDir := filepath.Join(s.content, ".history", "manifest.json")
	histBefore := 0
	if entries, err := os.ReadDir(histDir); err == nil {
		histBefore = len(entries)
	}

	for i := 0; i < 3; i++ {
		if code, _ := publish(t, s, body); code != 200 {
			t.Fatalf("повтор %d не прошёл: %d", i+1, code)
		}
	}

	if got := manifestRev(t, s); got != revAfterFirst {
		t.Errorf("три холостых переиздания сдвинули rev %d → %d: у играющих сменилась версия контента",
			revAfterFirst, got)
	}
	after, err := os.ReadFile(filepath.Join(s.content, "manifest.json"))
	if err != nil {
		t.Fatal(err)
	}
	if string(before) != string(after) {
		t.Error("манифест переписан публикацией, ничего в нём не изменившей")
	}
	// Скрипт с теми же байтами тоже не переписывается: иначе история студии
	// копит одинаковые редакции, а «откатить на предыдущую» возвращает ту же.
	st2, err := os.Stat(scriptPath)
	if err != nil {
		t.Fatal(err)
	}
	if !st1.ModTime().Equal(st2.ModTime()) {
		t.Error("скрипт переписан теми же байтами — в историю уехала пустая редакция")
	}
	if entries, err := os.ReadDir(histDir); err == nil && len(entries) > histBefore {
		t.Errorf("холостые переиздания добавили %d снимк(ов) в историю каталога",
			len(entries)-histBefore)
	}
}

// Обратная сторона: то, что игрок обязан увидеть, каталог менять ДОЛЖНО.
func TestRealChangesStillMoveTheCatalog(t *testing.T) {
	s := publishSrv(t)
	base := map[string]any{"id": "proba", "name": "Проба", "chapter": 1, "lvns": republishLvns}
	if code, _ := publish(t, s, base); code != 200 {
		t.Fatal("первая публикация не прошла")
	}

	cases := []struct {
		name string
		body map[string]any
	}{
		{"новая глава", map[string]any{"id": "proba", "name": "Проба", "chapter": 2, "lvns": "scene p2\n\nВторая.\n-> __end\n"}},
		{"переименование", map[string]any{"id": "proba", "name": "Проба другая", "chapter": 1, "lvns": republishLvns}},
		{"обложка главы", map[string]any{"id": "proba", "name": "Проба другая", "chapter": 1, "lvns": republishLvns, "bg_url": "/content/bg/room.jpg"}},
	}
	for _, c := range cases {
		was := manifestRev(t, s)
		if code, _ := publish(t, s, c.body); code != 200 {
			t.Fatalf("%s: публикация не прошла", c.name)
		}
		if got := manifestRev(t, s); got == was {
			t.Errorf("%s: каталог не изменился (rev остался %d) — игрок этого не узнает", c.name, was)
		}
	}
}

// Правка текста главы меняет скрипт — и НЕ трогает каталог: клиент умеет
// «каталог тот же — за ним не ходим», и это единственный случай, когда он
// действительно срабатывает.
func TestEditedLineChangesTheScriptOnly(t *testing.T) {
	s := publishSrv(t)
	body := map[string]any{"id": "proba", "name": "Проба", "chapter": 1, "lvns": republishLvns}
	if code, _ := publish(t, s, body); code != 200 {
		t.Fatal("первая публикация не прошла")
	}
	before, _ := os.ReadFile(filepath.Join(s.content, "manifest.json"))
	script, _ := os.ReadFile(filepath.Join(s.content, "scripts", "proba-ch01.lvn"))

	body["lvns"] = "scene proba\n\nРеплика, поправленная.\n-> __end\n"
	if code, _ := publish(t, s, body); code != 200 {
		t.Fatal("правка не опубликовалась")
	}

	after, _ := os.ReadFile(filepath.Join(s.content, "manifest.json"))
	if string(before) != string(after) {
		t.Error("правка реплики переписала каталог — игрок качает его вместе со скриптом")
	}
	newScript, _ := os.ReadFile(filepath.Join(s.content, "scripts", "proba-ch01.lvn"))
	if string(script) == string(newScript) {
		t.Error("скрипт не изменился — правка не доехала")
	}
}

// Та же цена приходит и с другой стороны: панель сохраняет каталог целиком.
// «Открыл, ничего не тронул, сохранил» — обычное движение редактора, и оно не
// должно стоить играющим перезагрузки каталога.
func TestSavingAnUnchangedManifestIsNotAWrite(t *testing.T) {
	s := publishSrv(t)
	if code, _ := publish(t, s, map[string]any{
		"id": "proba", "name": "Проба", "chapter": 1, "lvns": republishLvns,
	}); code != 200 {
		t.Fatal("подготовка не прошла")
	}
	path := filepath.Join(s.content, "manifest.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	was := manifestRev(t, s)

	// То же тело обратно — как его вернула бы панель после «Сохранить».
	body, code, msg := s.manifestRevGate(raw)
	if code != 0 {
		t.Fatalf("сохранение своей же копии отклонено: %d %s", code, msg)
	}
	if body != nil {
		t.Errorf("гейт приготовил запись для каталога, который не изменился (%d Б)", len(body))
	}
	if got := manifestRev(t, s); got != was {
		t.Errorf("rev сдвинулся %d → %d без единой правки", was, got)
	}

	// А настоящая правка обязана пройти и подвинуть rev.
	var m map[string]any
	if err := json.Unmarshal(raw, &m); err != nil {
		t.Fatal(err)
	}
	m["titles"] = append(m["titles"].([]any), map[string]any{"id": "ещё", "name": "Ещё"})
	edited, _ := json.Marshal(m)
	body, code, msg = s.manifestRevGate(edited)
	if code != 0 || body == nil {
		t.Fatalf("правка каталога не прошла: %d %s", code, msg)
	}
	var out map[string]any
	if err := json.Unmarshal(body, &out); err != nil {
		t.Fatal(err)
	}
	if rev, _ := out["rev"].(float64); int(rev) != was+1 {
		t.Errorf("после правки rev %v, ожидался %d", out["rev"], was+1)
	}
}
