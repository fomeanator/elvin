package main

// Точки входа для учётных записей панели: вход, выход, кто я, управление
// людьми. Отдельным файлом от хранилища — чтобы правила доступа читались
// подряд и не терялись среди работы с паролями.

import (
	"encoding/json"
	"net/http"
	"strconv"
	"strings"
)

// AdminUsersRoutes вешает обработчики. Путь /v1/admin/session* НАМЕРЕННО
// открыт: чтобы войти, надо иметь возможность постучаться.
func (s *AdminService) AdminUsersRoutes(mux *http.ServeMux) {
	if s.users == nil {
		return
	}
	mux.HandleFunc("/v1/admin/session/login", s.handleLogin)
	mux.HandleFunc("/v1/admin/session/logout", s.handleLogout)
	mux.HandleFunc("/v1/admin/session/me", s.handleMe)
	mux.HandleFunc("/v1/admin/people", s.handlePeople)
}

func (s *AdminService) handleLogin(w http.ResponseWriter, r *http.Request) {
	if !onlyMethod(w, r, http.MethodPost) {
		return
	}
	var body struct {
		Login    string `json:"login"`
		Password string `json:"password"`
	}
	if json.NewDecoder(http.MaxBytesReader(w, r.Body, bodyTiny)).Decode(&body) != nil {
		http.Error(w, "bad request", http.StatusBadRequest)
		return
	}
	// Источник исчерпал промахи — пароль даже не проверяем: проверка стоит
	// десятую секунды ядра, и именно её подбор и покупал бы.
	peer := loginPeer(r)
	if wait := s.logins.wait(peer); wait > 0 {
		w.Header().Set("Retry-After", strconv.Itoa(int(wait.Seconds())+1))
		http.Error(w, "слишком много неудачных попыток входа — подождите", http.StatusTooManyRequests)
		return
	}
	secret, role, err := s.users.Login(body.Login, body.Password)
	if err != nil {
		s.logins.fail(peer)
		// Одна и та же формулировка на неверный логин и неверный пароль:
		// разные ответы подсказали бы, какие учётки существуют.
		http.Error(w, "неверный логин или пароль", http.StatusUnauthorized)
		return
	}
	s.logins.clear(peer)
	http.SetCookie(w, &http.Cookie{
		Name:     "lvn_admin",
		Value:    secret,
		Path:     "/",
		HttpOnly: true, // скрипту на странице секрет недоступен
		Secure:   r.TLS != nil || strings.HasPrefix(r.Header.Get("X-Forwarded-Proto"), "https"),
		SameSite: http.SameSiteLaxMode,
		MaxAge:   int(s.users.TTL.Seconds()),
	})
	writeJSON(w, http.StatusOK, map[string]any{"ok": true, "role": role, "session": secret})
}

func (s *AdminService) handleLogout(w http.ResponseWriter, r *http.Request) {
	s.users.Logout(r)
	http.SetCookie(w, &http.Cookie{Name: "lvn_admin", Value: "", Path: "/", MaxAge: -1})
	writeJSON(w, http.StatusOK, map[string]any{"ok": true})
}

func (s *AdminService) handleMe(w http.ResponseWriter, r *http.Request) {
	if sess := s.users.Session(r); sess != nil {
		writeJSON(w, http.StatusOK, map[string]any{"login": sess.Login, "role": sess.Role})
		return
	}
	// Старый токен — это «ключ машины»: инструменты сборки ходят им, и по
	// нему у запроса нет человека, только полные права.
	if s.token != "" && bearerOK(r, s.token) {
		writeJSON(w, http.StatusOK, map[string]any{"login": "", "role": RoleOwner, "token": true})
		return
	}
	http.Error(w, "unauthorized", http.StatusUnauthorized)
}

// handlePeople — список, заведение и удаление учёток. Только владелец: право
// раздавать доступ и есть главное право.
func (s *AdminService) handlePeople(w http.ResponseWriter, r *http.Request) {
	if !s.okRole(w, r, RoleOwner) {
		return
	}
	switch r.Method {
	case http.MethodGet:
		writeJSON(w, http.StatusOK, map[string]any{"people": s.users.List()})
	case http.MethodPost:
		var body struct {
			Login    string `json:"login"`
			Password string `json:"password"`
			Role     string `json:"role"`
		}
		if json.NewDecoder(http.MaxBytesReader(w, r.Body, bodyTiny)).Decode(&body) != nil {
			http.Error(w, "bad request", http.StatusBadRequest)
			return
		}
		if body.Role == "" {
			body.Role = RoleEditor
		}
		if err := s.users.SetUser(body.Login, body.Password, body.Role); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"ok": true})
	case http.MethodDelete:
		login := r.URL.Query().Get("login")
		if err := s.users.RemoveUser(login); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"ok": true})
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

// okRole — проверка прав для защищённых действий.
//
// Порядок важен: сначала СЕССИЯ человека, потом токен машины. Так в логах
// у действия появляется имя, когда оно есть, и не появляется выдуманное,
// когда действие совершил скрипт сборки.
func (s *AdminService) okRole(w http.ResponseWriter, r *http.Request, need string) bool {
	return adminAllowedRole(w, r, s.token, need)
}

// roleAllows: владелец может всё, редактор — всё кроме управления людьми,
// смотрящий — только чтение.
func roleAllows(has, need string) bool {
	rank := map[string]int{RoleViewer: 1, RoleEditor: 2, RoleOwner: 3}
	return rank[has] >= rank[need]
}
