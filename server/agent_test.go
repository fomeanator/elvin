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
	"fmt"
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

// Игра больше чем на одну главу всегда выносит общие механики в отдельный файл
// — ради этого include и существует, и файл «Коннекта» ему учит. Публикация,
// компилирующая текст без файлового контекста, упиралась в это на второй главе
// автора: «подключение работает только при компиляции файла».
func TestPublishResolvesIncludeAgainstTheStudioScripts(t *testing.T) {
	s := publishSrv(t)
	if _, err := publishRaw(t, s, "mech", 1, "общее = 7\n"); err != nil {
		t.Fatalf("общий файл не опубликовался: %v", err)
	}
	// Публикуется как mech-ch01.lvns — глава подключает именно его.
	code, out := publish(t, s, map[string]any{
		"id": "game", "chapter": 1,
		"lvns": "scene game\ninclude \"mech-ch01.lvns\"\nВсего {общее}.\n-> __end\n",
	})
	if code != http.StatusOK {
		t.Fatalf("глава с include не опубликовалась: %d %v", code, out)
	}
	raw, err := os.ReadFile(filepath.Join(s.content, "scripts", "game-ch01.lvn"))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(raw), `"общее"`) {
		t.Errorf("подключённый файл не вклеился в результат: %s", raw)
	}
}

// Временный файл компиляции живёт в scripts/ — то есть внутри раздаваемого
// дерева. Скачать его не должно быть возможно даже в это окно.
func TestPublishTempSourceIsNotServable(t *testing.T) {
	dir := t.TempDir()
	srv := &server{content: dir}
	if err := os.MkdirAll(filepath.Join(dir, "scripts"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "scripts", ".publish-x.lvns"), []byte("secret"), 0o644); err != nil {
		t.Fatal(err)
	}
	rec := httptest.NewRecorder()
	srv.contentHandler(dir).ServeHTTP(rec,
		httptest.NewRequest(http.MethodGet, "/content/scripts/.publish-x.lvns", nil))
	if rec.Code != http.StatusNotFound {
		t.Errorf("исходник, из которого идёт компиляция, отдан наружу: %d", rec.Code)
	}
}

func publishRaw(t *testing.T, s *server, id string, ch int, lvns string) (map[string]any, error) {
	t.Helper()
	code, out := publish(t, s, map[string]any{"id": id, "chapter": ch, "lvns": lvns})
	if code != http.StatusOK {
		return out, fmt.Errorf("код %d: %v", code, out)
	}
	return out, nil
}

// Общий файл публикуется под СВОИМ именем — иначе главы его не найдут: include
// ищет "механики.lvns", а публикация глав именует всё как <id>-chNN.lvns.
func TestSharedFilePublishesUnderItsOwnName(t *testing.T) {
	s := publishSrv(t)
	code, out := publish(t, s, map[string]any{"path": "mech.lvns", "lvns": "общее = 7\n"})
	if code != http.StatusOK || out["kind"] != "shared" {
		t.Fatalf("общий файл не принят: %d %v", code, out)
	}
	if _, err := os.Stat(filepath.Join(s.content, "scripts", "mech.lvns")); err != nil {
		t.Fatalf("файл не под своим именем: %v", err)
	}
	// В манифест библиотека попадать не должна: это не глава.
	if len(readManifest(t, s)["titles"].([]any)) != 1 {
		t.Error("общий файл зарегистрирован как игра")
	}
	// И теперь глава может его подключить.
	if code, out := publish(t, s, map[string]any{"id": "g", "chapter": 1,
		"lvns": "scene g\ninclude \"mech.lvns\"\nВсего {общее}.\n-> __end\n"}); code != 200 {
		t.Fatalf("глава не увидела общий файл: %d %v", code, out)
	}
}

func TestSharedPathCannotEscapeScripts(t *testing.T) {
	s := publishSrv(t)
	for _, bad := range []string{"../../etc/x.lvns", "sub/dir.lvns", "x.lvn", "x.sh"} {
		if code, _ := publish(t, s, map[string]any{"path": bad, "lvns": "x = 1\n"}); code != 400 {
			t.Errorf("путь %q принят (код %d)", bad, code)
		}
	}
}

// Правка общего файла обязана пересобрать главы, которые его подключают.
// Иначе студия говорит «сохранено», а на телефоне прежняя игра: играется
// СКОМПИЛИРОВАННЫЙ .lvn, и он остался вчерашним.
func TestSavingASharedFileRebuildsTheChaptersThatIncludeIt(t *testing.T) {
	s := publishSrv(t)
	if _, err := publishRaw(t, s, "", 0, ""); err == nil {
		_ = err // publishRaw не годится для общего файла — публикуем напрямую
	}
	if code, _ := publish(t, s, map[string]any{"path": "mech.lvns", "lvns": "сила = 1\n"}); code != 200 {
		t.Fatal("общий файл не опубликовался")
	}
	if code, _ := publish(t, s, map[string]any{"id": "g", "chapter": 1,
		"lvns": "scene g\ninclude \"mech.lvns\"\nСила {сила}.\n-> __end\n"}); code != 200 {
		t.Fatal("глава не опубликовалась")
	}
	before, _ := os.ReadFile(filepath.Join(s.content, "scripts", "g-ch01.lvn"))
	if !strings.Contains(string(before), "1") {
		t.Fatalf("в скомпилированной главе нет значения из механик: %s", before)
	}

	// Меняем ОБЩИЙ файл — глава должна пересобраться сама.
	code, out := publish(t, s, map[string]any{"path": "mech.lvns", "lvns": "сила = 42\n"})
	if code != 200 {
		t.Fatalf("повторная публикация общего файла: %d %v", code, out)
	}
	rebuilt, _ := out["rebuilt"].([]any)
	if len(rebuilt) != 1 || rebuilt[0] != "scripts/g-ch01.lvn" {
		t.Errorf("пересобрано %v, ожидалась ровно глава g-ch01", out["rebuilt"])
	}
	after, _ := os.ReadFile(filepath.Join(s.content, "scripts", "g-ch01.lvn"))
	if !strings.Contains(string(after), "42") {
		t.Errorf("глава осталась на старом значении — правка механик не доехала до игры:\n%s", after)
	}
}

// Цепочка: глава → механики → таблицы. Пересборка «только прямых зависимостей»
// тихо оставила бы главу на старом коде.
func TestRebuildFollowsTheIncludeChain(t *testing.T) {
	s := publishSrv(t)
	publish(t, s, map[string]any{"path": "tables.lvns", "lvns": "базовая = 5\n"})
	publish(t, s, map[string]any{"path": "mech.lvns", "lvns": "include \"tables.lvns\"\nсила = базовая\n"})
	publish(t, s, map[string]any{"id": "g", "chapter": 1,
		"lvns": "scene g\ninclude \"mech.lvns\"\nСила {сила}.\n-> __end\n"})

	code, out := publish(t, s, map[string]any{"path": "tables.lvns", "lvns": "базовая = 99\n"})
	if code != 200 {
		t.Fatalf("%d %v", code, out)
	}
	rebuilt, _ := out["rebuilt"].([]any)
	if len(rebuilt) != 1 || rebuilt[0] != "scripts/g-ch01.lvn" {
		t.Errorf("через цепочку пересобрано %v, ожидалась глава g-ch01", out["rebuilt"])
	}
	after, _ := os.ReadFile(filepath.Join(s.content, "scripts", "g-ch01.lvn"))
	if !strings.Contains(string(after), "99") {
		t.Errorf("глава не увидела правку через цепочку:\n%s", after)
	}
}

// Опечатка в общем файле НЕ должна обрушить уже работающие главы: на диске
// остаётся последняя рабочая версия, а имя и ошибка приходят в ответе.
func TestABrokenSharedFileLeavesWorkingChaptersOnDisk(t *testing.T) {
	s := publishSrv(t)
	publish(t, s, map[string]any{"path": "mech.lvns", "lvns": "сила = 1\n"})
	publish(t, s, map[string]any{"id": "g", "chapter": 1,
		"lvns": "scene g\ninclude \"mech.lvns\"\nСила {сила}.\n-> __end\n"})
	good, _ := os.ReadFile(filepath.Join(s.content, "scripts", "g-ch01.lvn"))

	_, out := publish(t, s, map[string]any{"path": "mech.lvns", "lvns": "- опция без цели\n"})
	failed, _ := out["failed"].(map[string]any)
	if len(failed) == 0 {
		t.Errorf("битый общий файл не дал ни одной ошибки: %v", out)
	}
	now, _ := os.ReadFile(filepath.Join(s.content, "scripts", "g-ch01.lvn"))
	if string(now) != string(good) {
		t.Errorf("рабочая глава перезаписана из-за опечатки в общем файле")
	}
}

// Пакетный файл публикуется полным @-путём, ложится в scripts/lvns_packages/,
// глава подключает его тем же путём, а правка пакета пересобирает главу —
// цепочка целиком, как у плоских общих файлов.
func TestPublishPackageFileAndRebuildDependents(t *testing.T) {
	s := publishSrv(t)
	// файл пакета
	code, out := publish(t, s, map[string]any{
		"path": "@t/duel/duel.lvns",
		"lvns": "func duel_ping() { return 1 }\n",
	})
	if code != 200 {
		t.Fatalf("публикация пакета: %d %v", code, out)
	}
	if out["path"] != "scripts/lvns_packages/@t/duel/duel.lvns" {
		t.Fatalf("path = %v", out["path"])
	}
	if _, err := os.Stat(filepath.Join(s.content, "scripts/lvns_packages/@t/duel/duel.lvns")); err != nil {
		t.Fatal(err)
	}
	// глава, подключающая пакет @-путём
	code, out = publish(t, s, map[string]any{
		"id": "kd", "chapter": 1,
		"lvns": "scene kd\ninclude \"@t/duel/duel.lvns\"\nPing {duel_ping()}.\n-> __end\n",
	})
	if code != 200 {
		t.Fatalf("публикация главы с пакетом: %d %v", code, out)
	}
	// правка пакета пересобирает главу
	code, out = publish(t, s, map[string]any{
		"path": "@t/duel/duel.lvns",
		"lvns": "func duel_ping() { return 2 }\n",
	})
	if code != 200 {
		t.Fatalf("переиздание пакета: %d %v", code, out)
	}
	rebuilt, _ := out["rebuilt"].([]any)
	found := false
	for _, r := range rebuilt {
		if r == "scripts/kd-ch01.lvn" {
			found = true
		}
	}
	if !found {
		t.Fatalf("глава не пересобралась после правки пакета: rebuilt=%v failed=%v", out["rebuilt"], out["failed"])
	}
}

// Пути мимо формата — от годного не отличить, отказ без записи.
func TestPublishPackagePathValidation(t *testing.T) {
	s := publishSrv(t)
	for _, bad := range []string{
		"@t/../escape/x.lvns", "@T/Upper/x.lvns", "@t/duel/x.txt", "@t/x.lvns", "@t//x.lvns",
	} {
		if code, _ := publish(t, s, map[string]any{"path": bad, "lvns": "x = 1\n"}); code != 400 {
			t.Fatalf("путь %q принят с кодом %d", bad, code)
		}
	}
}
