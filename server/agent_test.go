package main

// agent_test.go — стражи «Коннекта».
//
// Две вещи здесь ломаются молча, и обе учат ИИ работать неправильно:
// встроенная документация, отставшая от howto/, и файл, из которого выпал
// адрес или ключ. Ни то, ни другое не видно глазами — файл на 1700 строк никто
// не перечитывает.

import (
	"bytes"
	"encoding/json"
	"flag"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

var updateBundle = flag.Bool("update", false, "пересобрать server/agent-bundle.md из howto/")

// Встроенная копия обязана совпадать с документацией репозитория. Расхождение
// не проявляется никак: сервер отдаёт вчерашний справочник, ИИ пишет по нему
// синтаксис, которого больше нет, и виноватым выглядит движок.
func TestAgentBundleIsUpToDate(t *testing.T) {
	root := filepath.Join("..")
	if _, err := os.Stat(filepath.Join(root, "howto")); err != nil {
		t.Skip("howto/ недоступен (сборка вне репозитория)")
	}
	want, err := BuildAgentBundle(root)
	if err != nil {
		t.Fatalf("сборка: %v", err)
	}
	path := filepath.Join(".", "agent-bundle.md")
	if *updateBundle {
		if err := os.WriteFile(path, []byte(want), 0o644); err != nil {
			t.Fatal(err)
		}
		t.Log("agent-bundle.md пересобран, закоммить его")
		return
	}
	got, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("встроенный файл не читается: %v", err)
	}
	if string(got) != want {
		t.Errorf("agent-bundle.md отстал от howto/ на %d байт разницы — пересобери:\n"+
			"  go test ./server -run TestAgentBundleIsUpToDate -update",
			len(want)-len(got))
	}
}

// Файл бесполезен без адреса и ключа, и опасен, если про ключ не сказано.
func TestAgentBundleCarriesLiveCredentialsAndAWarning(t *testing.T) {
	srv := &server{content: t.TempDir(), adminToken: "секрет-токен"}
	req := httptest.NewRequest(http.MethodGet, "/v1/admin/agent-bundle", nil)
	req.Host = "studio.example"
	req.Header.Set("Authorization", "Bearer секрет-токен")
	req.Header.Set("X-Forwarded-Proto", "https")
	rec := httptest.NewRecorder()
	srv.handleAgentBundle(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("код %d", rec.Code)
	}
	body := rec.Body.String()
	for _, want := range []string{
		"https://studio.example",  // адрес именно этого сервера, а не зашитый
		"секрет-токен",            // без ключа ИИ ничего не опубликует
		"Внимание, это секрет",    // и обязан знать, что держит в руках
		"/v1/admin/agent/publish", // куда публиковать
		"Шпаргалка",               // и собственно язык
	} {
		if !strings.Contains(body, want) {
			t.Errorf("в файле нет %q — он неработоспособен", want)
		}
	}
	if got := rec.Header().Get("Cache-Control"); got != "no-store" {
		t.Errorf("файл с ключом кэшируется (%q)", got)
	}
	if len(strings.Split(body, "\n")) < 1000 {
		t.Errorf("файл на %d строк — документация не вклеилась", len(strings.Split(body, "\n")))
	}
}

func TestAgentBundleNeedsTheToken(t *testing.T) {
	srv := &server{content: t.TempDir(), adminToken: "t"}
	rec := httptest.NewRecorder()
	srv.handleAgentBundle(rec, httptest.NewRequest(http.MethodGet, "/v1/admin/agent-bundle", nil))
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("файл с ключом отдан без ключа: %d", rec.Code)
	}
}

// ── публикация ───────────────────────────────────────────────────────────────

func publishSrv(t *testing.T) *server {
	t.Helper()
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "manifest.json"),
		[]byte(`{"titles":[{"id":"other","name":"Чужая","seasons":[{"chapters":[]}]}]}`), 0o644); err != nil {
		t.Fatal(err)
	}
	return &server{content: dir, adminToken: "t"}
}

func publish(t *testing.T, s *server, body map[string]any) (int, map[string]any) {
	t.Helper()
	raw, _ := json.Marshal(body)
	req := httptest.NewRequest(http.MethodPost, "/v1/admin/agent/publish", bytes.NewReader(raw))
	req.Header.Set("Authorization", "Bearer t")
	rec := httptest.NewRecorder()
	s.handleAgentPublish(rec, req)
	var out map[string]any
	_ = json.Unmarshal(rec.Body.Bytes(), &out)
	return rec.Code, out
}

// Главное: ИИ шлёт ИСХОДНИК и получает играбельную главу. Ни тулчейна, ни
// второго запроса, ни ручной правки манифеста.
func TestPublishCompilesRegistersAndPlays(t *testing.T) {
	s := publishSrv(t)
	code, out := publish(t, s, map[string]any{
		"id": "dragons", "name": "Драконы", "chapter": 1,
		"lvns": "scene dragons\nТы просыпаешься в пещере.\n- Встать -> up\n\n:up\nПора.\n-> __end\n",
	})
	if code != http.StatusOK || out["ok"] != true {
		t.Fatalf("публикация не прошла: %d %v", code, out)
	}
	if w, _ := out["warnings"].([]any); len(w) != 0 {
		t.Errorf("чистый исходник дал предупреждения: %v", w)
	}
	for _, rel := range []string{"scripts/dragons-ch01.lvn", "scripts/dragons-ch01.lvns"} {
		if _, err := os.Stat(filepath.Join(s.content, filepath.FromSlash(rel))); err != nil {
			t.Errorf("нет %s: %v", rel, err)
		}
	}
	// Исходник кладётся рядом не для красоты: это то, что откроет IDE и что
	// будет править ребёнок после ИИ.
	src, _ := os.ReadFile(filepath.Join(s.content, "scripts", "dragons-ch01.lvns"))
	if !strings.Contains(string(src), "scene dragons") {
		t.Errorf("исходник не сохранён дословно: %q", string(src))
	}

	m := readManifest(t, s)
	titles := m["titles"].([]any)
	if len(titles) != 2 {
		t.Fatalf("титулов %d — чужой титул потерян или не добавлен свой", len(titles))
	}
	ch := chaptersOf(t, m, "dragons")
	if len(ch) != 1 {
		t.Fatalf("глав %d", len(ch))
	}
	if ch[0].(map[string]any)["script_url"] != "/content/scripts/dragons-ch01.lvn" {
		t.Errorf("ссылка на скрипт неверна: %v", ch[0])
	}
}

// Повторная публикация — обычное дело: ИИ правит и шлёт снова. Глава должна
// ЗАМЕНЯТЬСЯ, а не удваиваться.
func TestRepublishReplacesTheChapter(t *testing.T) {
	s := publishSrv(t)
	src := "scene d\nПривет.\n-> __end\n"
	for i := 0; i < 3; i++ {
		if code, out := publish(t, s, map[string]any{"id": "d", "chapter": 1, "lvns": src}); code != 200 {
			t.Fatalf("публикация %d: %d %v", i, code, out)
		}
	}
	if ch := chaptersOf(t, readManifest(t, s), "d"); len(ch) != 1 {
		t.Fatalf("после трёх публикаций глав %d, ожидалась 1", len(ch))
	}
}

// Битый исходник не должен ни записаться, ни снести уже работающую главу.
func TestBrokenSourceLeavesThePreviousVersionAlone(t *testing.T) {
	s := publishSrv(t)
	good := "scene d\nХорошая версия.\n-> __end\n"
	if code, _ := publish(t, s, map[string]any{"id": "d", "chapter": 1, "lvns": good}); code != 200 {
		t.Fatal("первая публикация не прошла")
	}
	code, out := publish(t, s, map[string]any{"id": "d", "chapter": 1, "lvns": "scene d\n- Опция без цели\n"})
	if code == http.StatusOK {
		t.Fatalf("битый исходник опубликовался: %v", out)
	}
	src, _ := os.ReadFile(filepath.Join(s.content, "scripts", "d-ch01.lvns"))
	if !strings.Contains(string(src), "Хорошая версия") {
		t.Errorf("неудачная публикация затёрла рабочую главу: %q", string(src))
	}
}

// Ошибка компиляции обязана нести номер строки: без него ИИ правит наугад.
func TestCompileErrorNamesTheLine(t *testing.T) {
	s := publishSrv(t)
	code, out := publish(t, s, map[string]any{"id": "d", "lvns": "scene d\ngoto нет цели тут\n"})
	if code != http.StatusBadRequest {
		t.Fatalf("код %d, ожидался 400: %v", code, out)
	}
	if e, _ := out["error"].(string); !strings.Contains(e, "line") {
		t.Errorf("в ошибке нет номера строки: %q", e)
	}
}

func TestPublishRejectsBadIDAndEmptySource(t *testing.T) {
	s := publishSrv(t)
	if code, _ := publish(t, s, map[string]any{"id": "../escape", "lvns": "scene d\n-> __end\n"}); code != 400 {
		t.Errorf("id с обходом каталога принят: %d", code)
	}
	if code, _ := publish(t, s, map[string]any{"id": "d", "lvns": "   "}); code != 400 {
		t.Errorf("пустой исходник принят: %d", code)
	}
}

func readManifest(t *testing.T, s *server) map[string]any {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join(s.content, "manifest.json"))
	if err != nil {
		t.Fatal(err)
	}
	var m map[string]any
	if err := json.Unmarshal(raw, &m); err != nil {
		t.Fatalf("манифест перестал быть валидным JSON: %v", err)
	}
	return m
}

func chaptersOf(t *testing.T, m map[string]any, id string) []any {
	t.Helper()
	for _, x := range m["titles"].([]any) {
		tm := x.(map[string]any)
		if tm["id"] != id {
			continue
		}
		seasons, _ := tm["seasons"].([]any)
		if len(seasons) == 0 {
			t.Fatalf("у титула %s нет сезонов", id)
		}
		ch, _ := seasons[0].(map[string]any)["chapters"].([]any)
		return ch
	}
	t.Fatalf("титул %s не найден", id)
	return nil
}
