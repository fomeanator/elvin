package main

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestLoginLimiterBlocksAfterRepeatedFailures(t *testing.T) {
	now := time.Date(2026, 9, 3, 12, 0, 0, 0, time.UTC)
	l := newFailLimiter(10, 10*time.Minute)
	l.now = func() time.Time { return now }

	for i := 0; i < 9; i++ {
		l.fail("a")
	}
	if l.wait("a") != 0 {
		t.Fatalf("девять промахов ещё не повод ждать")
	}
	l.fail("a")
	if w := l.wait("a"); w <= 0 || w > 10*time.Minute {
		t.Fatalf("после десяти промахов ждём ~10 минут, а не %v", w)
	}
	if l.wait("b") != 0 {
		t.Errorf("чужие промахи заперли другой источник")
	}

	now = now.Add(10*time.Minute + time.Second)
	if l.wait("a") != 0 {
		t.Errorf("окно уехало, а источник всё ещё заперт")
	}

	for i := 0; i < 10; i++ {
		l.fail("a")
	}
	l.clear("a")
	if l.wait("a") != 0 {
		t.Errorf("удачный вход не простил промахи")
	}

	var none *failLimiter
	none.fail("x")
	none.clear("x")
	if none.wait("x") != 0 {
		t.Errorf("пустой ограничитель должен пропускать")
	}
}

func TestLoginPeerTrustsRealIPOnlyFromLoopback(t *testing.T) {
	r := httptest.NewRequest(http.MethodPost, "/v1/admin/session/login", nil)
	r.Header.Set("X-Real-IP", "203.0.113.7")
	r.RemoteAddr = "127.0.0.1:5000"
	if got := loginPeer(r); got != "203.0.113.7" {
		t.Errorf("за nginx источник — X-Real-IP, а не петля: %s", got)
	}
	r.RemoteAddr = "198.51.100.2:5000"
	if got := loginPeer(r); got != "198.51.100.2" {
		t.Errorf("снаружи заголовку верить нельзя: %s", got)
	}
}

func TestHandleLoginAnswers429AfterRepeatedFailures(t *testing.T) {
	users, err := NewAdminUsers(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	if err := users.SetUser("ilya", "correct-horse-battery", RoleOwner); err != nil {
		t.Fatal(err)
	}
	svc := &AdminService{users: users, logins: newFailLimiter(3, time.Minute)}
	try := func(password string) *httptest.ResponseRecorder {
		body := `{"login":"ilya","password":"` + password + `"}`
		req := httptest.NewRequest(http.MethodPost, "/v1/admin/session/login", strings.NewReader(body))
		req.RemoteAddr = "198.51.100.2:5000"
		rec := httptest.NewRecorder()
		svc.handleLogin(rec, req)
		return rec
	}
	for i := 0; i < 3; i++ {
		if rec := try("wrong"); rec.Code != http.StatusUnauthorized {
			t.Fatalf("промах %d: %d, ждали 401", i+1, rec.Code)
		}
	}
	rec := try("correct-horse-battery")
	if rec.Code != http.StatusTooManyRequests {
		t.Fatalf("после трёх промахов даже верный пароль ждёт: %d", rec.Code)
	}
	if rec.Header().Get("Retry-After") == "" {
		t.Errorf("429 без Retry-After — клиенту нечем понять, сколько ждать")
	}
	// Другой источник входит как ни в чём не бывало.
	req := httptest.NewRequest(http.MethodPost, "/v1/admin/session/login",
		strings.NewReader(`{"login":"ilya","password":"correct-horse-battery"}`))
	req.RemoteAddr = "203.0.113.9:5000"
	other := httptest.NewRecorder()
	svc.handleLogin(other, req)
	if other.Code != http.StatusOK {
		t.Errorf("чужие промахи заперли другой адрес: %d", other.Code)
	}
}
