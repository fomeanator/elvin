package main

// «Удалить аккаунт» — стор-требование (Google Play / App Store): игрок стирает
// свои данные сам, из приложения, без письма в поддержку. Авторизованный POST
// сносит учётку, кошелёк, сейвы и привязки платформ. Явное подтверждение в
// теле — чтобы случайный вызов (ретрай, любопытный прокси) не стёр игрока.

import (
	"encoding/json"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
)

type accountEraser struct {
	auth *AuthService
	// Пер-юзерные файлы сервисов: <dir>/<uid>.json. Кошелёк здесь обязателен,
	// остальные — лучшая практика (данных там немного, но они тоже персональные).
	userFileDirs []string
	srv          *server
}

func (e *accountEraser) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/account/delete", e.handleDelete)
}

func (e *accountEraser) handleDelete(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	uid := e.auth.UserFromRequest(r)
	if uid == "" {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var req struct {
		Confirm string `json:"confirm"`
	}
	_ = json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req)
	if req.Confirm != "DELETE" {
		http.Error(w, `confirm:"DELETE" required`, http.StatusBadRequest)
		return
	}
	if err := e.auth.DeleteUser(uid); err != nil {
		http.Error(w, "delete failed", http.StatusInternalServerError)
		return
	}
	for _, dir := range e.userFileDirs {
		_ = os.Remove(filepath.Join(dir, uid+".json"))
	}
	if e.srv != nil {
		e.srv.deleteStatesOf(uid)
	}
	log.Printf("[account] игрок %s удалил аккаунт", uid)
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// deleteStatesOf стирает все сейв-блобы игрока: и простой ключ <uid>, и
// составные <uid>__<title> (пер-новелльные статы, __global). Память и диск
// одним махом — иначе перезаход тем же device_id воскресил бы «удалённый»
// прогресс из файла.
func (s *server) deleteStatesOf(uid string) {
	s.mu.Lock()
	for key := range s.state {
		if key == uid || strings.HasPrefix(key, uid+"__") {
			delete(s.state, key)
		}
	}
	s.mu.Unlock()
	dir := filepath.Join(s.content, "state")
	entries, err := os.ReadDir(dir)
	if err != nil {
		return
	}
	// Имя файла — санированный ключ; uid сам из безопасных символов, так что
	// префикс совпадает буквально.
	for _, ent := range entries {
		name := ent.Name()
		if name == uid+".json" || strings.HasPrefix(name, uid+"__") {
			_ = os.Remove(filepath.Join(dir, name))
		}
	}
}
