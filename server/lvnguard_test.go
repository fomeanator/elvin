package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// putAsset PUTs a body to /v1/admin/assets/<rel> against a throwaway server
// and returns the response recorder plus the decoded JSON body.
func putAsset(t *testing.T, s *server, rel, body string) (*httptest.ResponseRecorder, map[string]any) {
	t.Helper()
	req := httptest.NewRequest("PUT", "/v1/admin/assets/"+rel, strings.NewReader(body))
	req.Header.Set("Authorization", "Bearer t")
	rec := httptest.NewRecorder()
	s.handleAdminAsset(rec, req)
	var out map[string]any
	_ = json.Unmarshal(rec.Body.Bytes(), &out)
	return rec, out
}

func guardServer(t *testing.T) *server {
	t.Helper()
	return &server{content: t.TempDir(), adminToken: "t"}
}

func strList(v any) []string {
	raw, _ := v.([]any)
	out := make([]string, 0, len(raw))
	for _, r := range raw {
		if s, ok := r.(string); ok {
			out = append(out, s)
		}
	}
	return out
}

const validLvn = `{"scene":"s","script":[
 {"op":"say","text":"hello"},
 {"op":"goto","label":"end"},
 {"op":"label","id":"end"},
 {"op":"say","text":"bye"}
]}`

func TestAssetPutValidLvnIsWritten(t *testing.T) {
	s := guardServer(t)
	rec, out := putAsset(t, s, "scripts/ok.lvn", validLvn)
	if rec.Code != http.StatusOK {
		t.Fatalf("want 200, got %d: %s", rec.Code, rec.Body.String())
	}
	if _, err := os.Stat(filepath.Join(s.content, "scripts/ok.lvn")); err != nil {
		t.Fatalf("valid script was not written: %v", err)
	}
	if w := strList(out["warnings"]); len(w) != 0 {
		t.Fatalf("clean script should carry no warnings, got %v", w)
	}
}

// The core promise: a script the runtime cannot play is refused, and the
// refusal leaves NOTHING behind — no file, no history entry. A partial write
// here would be worse than no gate at all.
func TestAssetPutDanglingGotoRejectedAndNotWritten(t *testing.T) {
	s := guardServer(t)
	// Seed a good version first, so we can prove the reject doesn't clobber it
	// and doesn't push a snapshot into .history either.
	if rec, _ := putAsset(t, s, "scripts/ch.lvn", validLvn); rec.Code != http.StatusOK {
		t.Fatalf("seed write failed: %d", rec.Code)
	}
	const broken = `{"scene":"s","script":[{"op":"goto","label":"nowhere"}]}`
	rec, out := putAsset(t, s, "scripts/ch.lvn", broken)
	if rec.Code != http.StatusUnprocessableEntity {
		t.Fatalf("want 422, got %d: %s", rec.Code, rec.Body.String())
	}
	errs := strList(out["errors"])
	if len(errs) == 0 || !strings.Contains(strings.Join(errs, "|"), "undefined label") {
		t.Fatalf("want a dangling-jump error in the body, got %v", errs)
	}

	onDisk, err := os.ReadFile(filepath.Join(s.content, "scripts/ch.lvn"))
	if err != nil {
		t.Fatalf("previous version disappeared: %v", err)
	}
	if string(onDisk) != validLvn {
		t.Fatalf("rejected body reached the disk:\n%s", onDisk)
	}
	// .history must not have grown: snapshotHistory runs only past the gate.
	hist, _ := os.ReadDir(filepath.Join(s.content, ".history", "scripts/ch.lvn"))
	if len(hist) != 0 {
		t.Fatalf("rejected save polluted .history with %d entry(ies)", len(hist))
	}
}

func TestAssetPutRejectsNonDocumentShapes(t *testing.T) {
	cases := map[string]string{
		"bare op array":  `[{"op":"say","text":"hi"}]`,
		"no script key":  `{"scene":"s"}`,
		"script not arr": `{"script":{"op":"say"}}`,
		"not json":       `oh hi`,
		"duplicate label": `{"script":[{"op":"label","id":"a"},{"op":"label","id":"a"},
		 {"op":"say","text":"x"}]}`,
	}
	for name, body := range cases {
		t.Run(name, func(t *testing.T) {
			s := guardServer(t)
			rec, out := putAsset(t, s, "scripts/x.lvn", body)
			if rec.Code != http.StatusUnprocessableEntity {
				t.Fatalf("want 422, got %d: %s", rec.Code, rec.Body.String())
			}
			if len(strList(out["errors"])) == 0 {
				t.Fatalf("422 with no errors listed: %s", rec.Body.String())
			}
			if _, err := os.Stat(filepath.Join(s.content, "scripts/x.lvn")); !os.IsNotExist(err) {
				t.Fatalf("rejected body was written to disk")
			}
		})
	}
}

// A host op (LvnOps.Register / `ext`) is legal content. It must WRITE, and it
// must be reported — blocking it would break every embedding game, staying
// silent would defeat the "the compiler owns unknown ops" decision.
//
// Операция взята ЗАВЕДОМО ЧУЖАЯ. Раньше здесь стоял `leaderboard_submit`, но с
// 28.08 движок объявляет свои сервисные операции сам (встроенная грамматика в
// валидаторе), и первопартийная операция больше не шумит — ради этого правка и
// делалась. Проверять на ней «предупреждение о неизвестной команде» значит
// закреплять как раз то, что чинили.
func TestAssetPutUnknownOpWarnsButWrites(t *testing.T) {
	s := guardServer(t)
	const hostOp = `{"scene":"s","script":[{"op":"my_minigame","board":"quiz","score":10},
	 {"op":"say","text":"done"}]}`
	rec, out := putAsset(t, s, "scripts/host.lvn", hostOp)
	if rec.Code != http.StatusOK {
		t.Fatalf("want 200 for a host op, got %d: %s", rec.Code, rec.Body.String())
	}
	if _, err := os.Stat(filepath.Join(s.content, "scripts/host.lvn")); err != nil {
		t.Fatalf("host-op script was not written: %v", err)
	}
	warns := strings.Join(strList(out["warnings"]), "|")
	if !strings.Contains(warns, "my_minigame") {
		t.Fatalf("want the unknown op reported as a warning, got %q", warns)
	}
}

// With an ext-grammar sidecar the same host op stops being noise: declared and
// well-formed → silent; declared but missing a required field → a real error.
func TestAssetPutExtGrammarIsHonoured(t *testing.T) {
	s := guardServer(t)
	grammar := `{"name":"quiz","ops":{"leaderboard_submit":{
	  "fields":["board","score"],"required":["board"]}}}`
	if err := os.MkdirAll(filepath.Join(s.content, "scripts"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(s.content, "ext-grammar.json"), []byte(grammar), 0o644); err != nil {
		t.Fatal(err)
	}

	const declared = `{"scene":"s","script":[{"op":"leaderboard_submit","board":"quiz","score":10},
	 {"op":"say","text":"done"}]}`
	rec, out := putAsset(t, s, "scripts/host.lvn", declared)
	if rec.Code != http.StatusOK {
		t.Fatalf("declared host op should pass, got %d: %s", rec.Code, rec.Body.String())
	}
	if w := strings.Join(strList(out["warnings"]), "|"); strings.Contains(w, "unknown op") {
		t.Fatalf("declared host op still reported unknown: %q", w)
	}

	const missingRequired = `{"scene":"s","script":[{"op":"leaderboard_submit","score":10},
	 {"op":"say","text":"done"}]}`
	rec, out = putAsset(t, s, "scripts/host2.lvn", missingRequired)
	if rec.Code != http.StatusUnprocessableEntity {
		t.Fatalf("want 422 when a declared required field is missing, got %d", rec.Code)
	}
	if e := strings.Join(strList(out["errors"]), "|"); !strings.Contains(e, "requires field") {
		t.Fatalf("want the ext-grammar requirement in the errors, got %q", e)
	}
}

// The gate must be invisible to everything that isn't a compiled script:
// images, config JSON, and the .lvns editable source (which an author saves
// half-written all the time).
func TestAssetPutNonLvnPathsAreUntouched(t *testing.T) {
	s := guardServer(t)
	for _, tc := range []struct{ rel, body string }{
		{"sprites/a.png", "\x89PNG not really"},
		{"config/economy.json", `{"anything":true}`},
		{"scripts/draft.lvns", "Аня: неоконченная реплика ->"},
		{"scripts/x-vars.json", `{"game":{}}`},
	} {
		rec, _ := putAsset(t, s, tc.rel, tc.body)
		if rec.Code != http.StatusOK {
			t.Fatalf("%s: want 200, got %d: %s", tc.rel, rec.Code, rec.Body.String())
		}
		got, err := os.ReadFile(filepath.Join(s.content, filepath.FromSlash(tc.rel)))
		if err != nil || string(got) != tc.body {
			t.Fatalf("%s: body not written verbatim (%v)", tc.rel, err)
		}
	}
}

// Regression fence: every script this repo ships must survive the gate. If a
// future validator rule starts flagging real content, this fails here instead
// of locking authors out of saving on a live server.
func TestShippedScriptsPassTheGate(t *testing.T) {
	dir := filepath.Join("content", "scripts")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Skipf("no shipped content to check: %v", err)
	}
	s := &server{content: "content"}
	checked := 0
	for _, e := range entries {
		if !isLvnPath(e.Name()) {
			continue
		}
		data, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			t.Fatal(err)
		}
		if f := s.checkLvn("scripts/"+e.Name(), data); f.blocked() {
			t.Errorf("%s would be REJECTED on save: %s", e.Name(), strings.Join(f.Errors, "; "))
		}
		checked++
	}
	if checked == 0 {
		t.Skip("no .lvn files under content/scripts")
	}
	t.Logf("%d shipped scripts pass the write gate", checked)
}

// Ссылка есть, файла нет — компилятору не видно (он не знает, что на диске), а
// игроку видно сразу: пустой экран вместо фона. Предупреждение, а не отказ:
// «сначала текст, потом картинки» — обычный порядок работы.
func TestGateReportsMissingAssets(t *testing.T) {
	dir := t.TempDir()
	content := filepath.Join(dir, "content")
	if err := os.MkdirAll(filepath.Join(content, "bg"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(content, "bg", "есть.jpg"), []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}
	s := &server{content: content}

	doc := []byte(`{"scene":"t","script":[
		{"op":"bg","sprite_url":"/content/bg/есть.jpg"},
		{"op":"bg","sprite_url":"/content/bg/нет.jpg"},
		{"op":"audio","url":"/content/audio/нет.ogg","channel":"music","action":"play"},
		{"op":"actor","id":"a","sprite_url":"https://example.test/внешний.png"},
		{"op":"obj","id":"o","sprite_url":"/content/art/{стихия}.png"}
	]}`)
	f := s.checkLvn("scripts/t.lvn", doc)

	if f.blocked() {
		t.Fatalf("отсутствие файла не должно блокировать запись: %v", f.Errors)
	}
	var missing []string
	for _, w := range f.Warnings {
		if strings.Contains(w, "файла нет") {
			missing = append(missing, w)
		}
	}
	if len(missing) != 2 {
		t.Fatalf("ожидались ровно две пропажи (фон и звук), получено %d: %v", len(missing), missing)
	}
	for _, w := range missing {
		if strings.Contains(w, "есть.jpg") {
			t.Error("существующий файл попал в пропажи")
		}
		if strings.Contains(w, "example.test") {
			t.Error("внешний адрес — не наша забота, проверять его нечем")
		}
		if strings.Contains(w, "{стихия}") {
			t.Error("шаблон с подстановкой знает только игра — проверять его нельзя")
		}
	}
}

// ИМЯ ФАЙЛА СВЕРЯЕТСЯ С ДИСКОМ, А НЕ С УДАЧЕЙ ФАЙЛОВОЙ СИСТЕМЫ.
//
// Контент собирают на маке (и на Windows), а раздаёт его Linux. Там, где у
// автора `Room.png` спокойно открывает `room.png`, у игрока приходит 404 и
// пустой экран. Пока гейт спрашивал существование через os.Stat, он молчал
// ровно на той машине, где автор ещё мог всё исправить, и говорил только на
// проде — то есть после выкладки. Замерено стендом qa/asset-case-check.sh:
// одна и та же публикация даёт предупреждение на регистрозависимом томе и
// не даёт на обычном.
//
// Тот же счёт форме юникода: «ё» одним знаком и «е» с надстрочным — разные
// имена для Linux и одно для macOS.
func TestGateCatchesNameMismatchOnAnyFilesystem(t *testing.T) {
	dir := t.TempDir()
	content := filepath.Join(dir, "content")
	if err := os.MkdirAll(filepath.Join(content, "bg"), 0o755); err != nil {
		t.Fatal(err)
	}
	// На диске — точные имена: строчное латинское и «ёлка» одним знаком (NFC).
	for _, name := range []string{"room.png", "\u0451\u043b\u043a\u0430.png"} {
		if err := os.WriteFile(filepath.Join(content, "bg", name), []byte("x"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	// Прощает ли файловая система стенда регистр — от этого зависит не наличие
	// предупреждения, а его текст: где файл открывается, гейт обязан назвать
	// настоящее имя; где нет — сказать, что файла нет.
	_, err := os.Stat(filepath.Join(content, "bg", "Room.png"))
	caseBlind := err == nil

	s := &server{content: content}
	doc := []byte(`{"scene":"t","script":[
		{"op":"bg","sprite_url":"/content/bg/Room.png"},
		{"op":"bg","sprite_url":"/content/bg/\u0435\u0308\u043b\u043a\u0430.png"}
	]}`)
	f := s.checkLvn("scripts/t.lvn", doc)
	if f.blocked() {
		t.Fatalf("расхождение имени не блокирует запись (арт зальют завтра): %v", f.Errors)
	}
	if len(f.Warnings) != 2 {
		t.Fatalf("ожидались два предупреждения (регистр и форма юникода), получено %d: %v",
			len(f.Warnings), f.Warnings)
	}
	for _, w := range f.Warnings {
		said := strings.Contains(w, "имя файла разошлось") || strings.Contains(w, "файла нет")
		if !said {
			t.Errorf("предупреждение не про имя: %s", w)
		}
		if caseBlind && !strings.Contains(w, "имя файла разошлось") {
			t.Errorf("на регистронезависимом томе гейт обязан назвать настоящее имя, а сказал: %s", w)
		}
	}
	if caseBlind {
		// Настоящее имя — половина пользы: без него автор ищет опечатку глазами.
		if !strings.Contains(strings.Join(f.Warnings, " "), "/content/bg/room.png") {
			t.Errorf("гейт не назвал имя, под которым файл лежит на диске: %v", f.Warnings)
		}
	}
	t.Logf("регистронезависимая файловая система: %v; предупреждения: %v", caseBlind, f.Warnings)
}

// ТОЧНОЕ ИМЯ НЕ ДОЛЖНО ПУГАТЬ АВТОРА. Посегментная сверка проходит по
// каталогам, и кириллица с вложенностью — обычный для нас случай: ложное
// «имя разошлось» на здоровом контенте стоило бы дороже пропущенного.
func TestGateStaysQuietOnExactNames(t *testing.T) {
	dir := t.TempDir()
	deep := filepath.Join(dir, "content", "sprites", "\u0413\u0435\u0440\u043e\u0438\u043d\u044f")
	if err := os.MkdirAll(deep, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(deep, "\u0438\u0434\u043b\u0435.png"), []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}
	s := &server{content: filepath.Join(dir, "content")}
	doc := []byte(`{"scene":"t","script":[
		{"op":"actor","id":"a","sprite_url":"/content/sprites/\u0413\u0435\u0440\u043e\u0438\u043d\u044f/\u0438\u0434\u043b\u0435.png"}
	]}`)
	if f := s.checkLvn("scripts/t.lvn", doc); len(f.Warnings) != 0 || f.blocked() {
		t.Fatalf("точная ссылка вызвала жалобу: errors=%v warnings=%v", f.Errors, f.Warnings)
	}
}
