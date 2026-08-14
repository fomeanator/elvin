package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Отзыв без контекста бесполезен: «тут баг» через неделю невозможно ни
// воспроизвести, ни отнести к версии. Контекст обязан сохраниться целиком, а
// кадр — достроиться сервером по скрипту.
func TestFeedbackKeepsContextAndRebuildsFrame(t *testing.T) {
	content := t.TempDir()
	script := `{"scene":"ch1","script":[
		{"op":"label","id":"начало"},
		{"op":"bg","sprite_url":"/content/bg/двор.png"},
		{"op":"say","who":"Аня","text":"Здесь всё сломалось."}
	]}`
	if err := os.MkdirAll(filepath.Join(content, "cold"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(content, "cold", "ch1.lvn"), []byte(script), 0o644); err != nil {
		t.Fatal(err)
	}
	manifest := `{"titles":[{"id":"cold","seasons":[{"chapters":[
		{"id":"ch1","script_url":"/content/cold/ch1.lvn"}]}]}]}`
	if err := os.WriteFile(filepath.Join(content, "manifest.json"), []byte(manifest), 0o644); err != nil {
		t.Fatal(err)
	}
	auth, err := NewAuthService(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	svc, err := NewFeedbackService(t.TempDir(), auth, "t",
		newChapterIndex(filepath.Join(content, "manifest.json")))
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	svc.Routes(mux)

	body := `{"text":"героиня пропала после выбора","kind":"bug","build":"1.4.2",
		"title":"cold","chapter":"ch1","at":2,"device":"Pixel 7","log":"NullReference"}`
	req := httptest.NewRequest(http.MethodPost, "/v1/feedback", bytes.NewReader([]byte(body)))
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("отправка: код %d — %s", rec.Code, rec.Body.String())
	}

	req = httptest.NewRequest(http.MethodGet, "/v1/admin/feedback?days=2", nil)
	req.Header.Set("Authorization", "Bearer t")
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	var out struct {
		Feedback []feedbackEntry `json:"feedback"`
		ByBuild  map[string]int  `json:"by_build"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &out); err != nil {
		t.Fatal(err)
	}
	if len(out.Feedback) != 1 {
		t.Fatalf("ожидалась одна запись: %+v", out.Feedback)
	}
	f := out.Feedback[0]
	if f.Build != "1.4.2" || f.Chapter != "ch1" || f.At != 2 || f.Device != "Pixel 7" {
		t.Errorf("контекст потерян: %+v", f)
	}
	// Кадр достроен сервером: клиент реплику и фон не присылал.
	if !strings.Contains(f.Line, "сломалось") || f.BG == "" {
		t.Errorf("кадр не восстановлен: line=%q bg=%q", f.Line, f.BG)
	}
	if f.Label != "начало" {
		t.Errorf("метка не найдена: %q", f.Label)
	}
	// Разбивка по сборкам — первый вопрос к любому отзыву.
	if out.ByBuild["1.4.2"] != 1 {
		t.Errorf("разбивка по сборкам: %+v", out.ByBuild)
	}
}

// Пустой отзыв не запись, а промах по кнопке.
func TestFeedbackRejectsEmpty(t *testing.T) {
	auth, _ := NewAuthService(t.TempDir())
	svc, err := NewFeedbackService(t.TempDir(), auth, "t", nil)
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	svc.Routes(mux)
	req := httptest.NewRequest(http.MethodPost, "/v1/feedback", bytes.NewReader([]byte(`{"text":"   "}`)))
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Errorf("код %d, ожидался 400", rec.Code)
	}
}
