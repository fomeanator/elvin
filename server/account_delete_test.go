package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

// «Удалить аккаунт» обязан стереть ВСЁ и пережить перезапуск: воскресший из
// базы или из файла сейва «удалённый» игрок — это уже не баг, а нарушение
// обещания стора.
func TestAccountDeleteErasesEverything(t *testing.T) {
	dir := t.TempDir()
	db, err := openStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	auth, err := NewAuthServiceDB(dir, db)
	if err != nil {
		t.Fatal(err)
	}

	// Регистрация обычным путём — как клиент.
	mux := http.NewServeMux()
	auth.Routes(mux)
	reg := httptest.NewRequest("POST", "/v1/auth/register",
		bytes.NewBufferString(`{"device_id":"device-0123456789abcdef"}`))
	rw := httptest.NewRecorder()
	mux.ServeHTTP(rw, reg)
	var regResp struct {
		UserID string `json:"user_id"`
		Token  string `json:"token"`
	}
	if err := json.Unmarshal(rw.Body.Bytes(), &regResp); err != nil || regResp.Token == "" {
		t.Fatalf("register: %v %s", err, rw.Body.String())
	}
	uid := regResp.UserID

	// Персональные файлы: кошелёк и два сейв-блоба (простой и составной).
	walletDir := filepath.Join(dir, "wallet")
	_ = os.MkdirAll(walletDir, 0o755)
	walletFile := filepath.Join(walletDir, uid+".json")
	_ = os.WriteFile(walletFile, []byte(`{"balances":{"gems":5}}`), 0o600)
	srv := &server{content: dir, state: map[string]stateEntry{
		uid:              {body: []byte(`{}`)},
		uid + "__global": {body: []byte(`{}`)},
	}}
	stateDir := filepath.Join(dir, "state")
	_ = os.MkdirAll(stateDir, 0o755)
	for _, name := range []string{uid + ".json", uid + "__global.json"} {
		_ = os.WriteFile(filepath.Join(stateDir, name), []byte(`{}`), 0o600)
	}

	eraser := &accountEraser{auth: auth, userFileDirs: []string{walletDir}, srv: srv}
	eraser.Routes(mux)

	// Без подтверждения — отказ, ничего не тронуто.
	req := httptest.NewRequest("POST", "/v1/account/delete", bytes.NewBufferString(`{}`))
	req.Header.Set("Authorization", "Bearer "+regResp.Token)
	rw = httptest.NewRecorder()
	mux.ServeHTTP(rw, req)
	if rw.Code != http.StatusBadRequest {
		t.Fatalf("без confirm ждали 400, получили %d", rw.Code)
	}
	if auth.UserFromRequest(req) == "" {
		t.Fatal("аккаунт пропал без подтверждения")
	}

	// С подтверждением — всё стёрто.
	req = httptest.NewRequest("POST", "/v1/account/delete",
		bytes.NewBufferString(`{"confirm":"DELETE"}`))
	req.Header.Set("Authorization", "Bearer "+regResp.Token)
	rw = httptest.NewRecorder()
	mux.ServeHTTP(rw, req)
	if rw.Code != http.StatusOK {
		t.Fatalf("delete: %d %s", rw.Code, rw.Body.String())
	}
	if auth.UserFromRequest(req) != "" {
		t.Error("токен всё ещё работает после удаления")
	}
	if _, err := os.Stat(walletFile); err == nil {
		t.Error("кошелёк не удалён")
	}
	for _, name := range []string{uid + ".json", uid + "__global.json"} {
		if _, err := os.Stat(filepath.Join(stateDir, name)); err == nil {
			t.Errorf("сейв %s не удалён", name)
		}
	}
	if _, ok := srv.state[uid]; ok {
		t.Error("сейв остался в памяти")
	}

	// Перезапуск: игрок не воскресает из базы.
	db.Close()
	db2, err := openStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	defer db2.Close()
	again, err := NewAuthServiceDB(dir, db2)
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := again.users[uid]; ok {
		t.Error("после перезапуска удалённый игрок вернулся из базы")
	}
}
