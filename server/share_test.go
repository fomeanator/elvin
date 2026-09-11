package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

// Завести игрока обычным путём — как это делает клиент.
func signUp(t *testing.T, auth *AuthService) string {
	t.Helper()
	mux := http.NewServeMux()
	auth.Routes(mux)
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, httptest.NewRequest("POST", "/v1/auth/register",
		strings.NewReader(`{"device_id":"share-device-0123456789"}`)))
	var out struct{ Token string }
	if err := json.Unmarshal(rec.Body.Bytes(), &out); err != nil || out.Token == "" {
		t.Fatalf("регистрация не удалась: %s", rec.Body.String())
	}
	return out.Token
}

// ПЕРЕДАЧА ПРОХОЖДЕНИЯ (TR-17): круг «отдал — открыл», срок жизни и предел
// на игрока. Ссылку открывают из чата, БЕЗ учётки: требовать вход раньше, чем
// человек увидел присланное, — верный способ его потерять, и этот вход не
// должен вернуться незаметной правкой.
func TestShareRoundTrip(t *testing.T) {
	db := testStore(t)
	dir := t.TempDir()
	auth, _ := NewAuthService(dir)
	auth.db = db
	svc := NewShareService(db, auth)
	mux := http.NewServeMux()
	svc.Routes(mux)

	token := signUp(t, auth)

	// Отдать снимок.
	rec := httptest.NewRecorder()
	req := httptest.NewRequest("POST", "/v1/share",
		strings.NewReader(`{"title":"agency","note":"акт 1","body":{"chapter":3,"vars":{"name":"Вика"}}}`))
	req.Header.Set("Authorization", "Bearer "+token)
	mux.ServeHTTP(rec, req)
	if rec.Code != 200 {
		t.Fatalf("снимок не принят: %d %s", rec.Code, rec.Body.String())
	}
	var made struct{ Code string }
	if err := json.Unmarshal(rec.Body.Bytes(), &made); err != nil || made.Code == "" {
		t.Fatalf("кода нет: %s", rec.Body.String())
	}
	if strings.ContainsAny(made.Code, "01OIl") {
		t.Errorf("в коде похожие знаки — его набирают руками: %q", made.Code)
	}

	// Открыть по коду БЕЗ учётки.
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, httptest.NewRequest("GET", "/v1/share/"+made.Code, nil))
	if rec.Code != 200 {
		t.Fatalf("ссылка не открылась без входа: %d %s", rec.Code, rec.Body.String())
	}
	var got struct {
		Title string          `json:"title"`
		Note  string          `json:"note"`
		Body  json.RawMessage `json:"body"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("ответ не разобран: %s", rec.Body.String())
	}
	if got.Title != "agency" || got.Note != "акт 1" {
		t.Errorf("подпись потерялась: %+v", got)
	}
	if !strings.Contains(string(got.Body), `"chapter":3`) {
		t.Errorf("снимок вернулся не тем: %s", got.Body)
	}

	// Чужого кода не существует — и это не 500.
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, httptest.NewRequest("GET", "/v1/share/ZZZZZZZZ", nil))
	if rec.Code != 404 {
		t.Errorf("несуществующий код ответил %d", rec.Code)
	}
}

// СНИМОК ПРОТУХАЕТ. Месяц — это срок ссылки в чате; вечное хранение чужих
// снимков превращает сервер в склад, который никто не разбирает.
func TestShareExpires(t *testing.T) {
	db := testStore(t)
	dir := t.TempDir()
	auth, _ := NewAuthService(dir)
	auth.db = db
	svc := NewShareService(db, auth)
	mux := http.NewServeMux()
	svc.Routes(mux)
	token := signUp(t, auth)

	rec := httptest.NewRecorder()
	req := httptest.NewRequest("POST", "/v1/share", strings.NewReader(`{"title":"t","body":{"a":1}}`))
	req.Header.Set("Authorization", "Bearer "+token)
	mux.ServeHTTP(rec, req)
	var made struct{ Code string }
	json.Unmarshal(rec.Body.Bytes(), &made)

	// Время ушло вперёд на срок жизни с запасом.
	svc.now = func() time.Time { return time.Now().Add(shareLifetime + time.Hour) }

	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, httptest.NewRequest("GET", "/v1/share/"+made.Code, nil))
	if rec.Code != 404 {
		t.Errorf("просроченная ссылка всё ещё открывается: %d", rec.Code)
	}
}

// НЕЛЬЗЯ ОТДАВАТЬ БЕЗ УЧЁТКИ: иначе сервер превращается в открытый файлохост.
func TestShareNeedsAnAccountToGive(t *testing.T) {
	db := testStore(t)
	dir := t.TempDir()
	auth, _ := NewAuthService(dir)
	auth.db = db
	mux := http.NewServeMux()
	NewShareService(db, auth).Routes(mux)

	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, httptest.NewRequest("POST", "/v1/share", strings.NewReader(`{"body":{"a":1}}`)))
	if rec.Code != 401 {
		t.Errorf("снимок принят без входа: %d", rec.Code)
	}
}
