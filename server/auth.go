package main

// Auth service — anonymous device accounts, the mobile-game way:
// the client generates a random device_id ONCE and keeps it secret;
// /v1/auth/register is idempotent on it (same device → same user, a fresh
// token each time, the old one revoked). Tokens are "userID.secret" with only
// the secret's sha256 stored, so a leaked users.json doesn't leak sessions.
//
// A clean seam: NewAuthService(dir) + Routes(mux). Splitting it into its own
// process later means moving this file and pointing the gateway at it.

import (
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

type authUser struct {
	DeviceHash string `json:"device_hash"` // sha256 of the client's device_id
	TokenHash  string `json:"token_hash"`  // sha256 of the current session secret
	Created    string `json:"created"`
}

type AuthService struct {
	mu    sync.Mutex
	path  string
	users map[string]*authUser // userID → record
	byDev map[string]string    // device hash → userID
}

func NewAuthService(dir string) (*AuthService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	s := &AuthService{
		path:  filepath.Join(dir, "users.json"),
		users: map[string]*authUser{},
		byDev: map[string]string{},
	}
	if data, err := os.ReadFile(s.path); err == nil {
		_ = json.Unmarshal(data, &s.users)
		for id, u := range s.users {
			s.byDev[u.DeviceHash] = id
		}
	}
	return s, nil
}

func (s *AuthService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/auth/register", s.handleRegister)
	mux.HandleFunc("/v1/auth/me", s.handleMe)
}

func (s *AuthService) persistLocked() {
	data, _ := json.MarshalIndent(s.users, "", "  ")
	tmp := s.path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err == nil {
		_ = os.Rename(tmp, s.path)
	}
}

func hashHex(v string) string {
	h := sha256.Sum256([]byte(v))
	return hex.EncodeToString(h[:])
}

func newSecret() string {
	b := make([]byte, 24)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// UserFromRequest resolves the Bearer "userID.secret" token; empty when the
// request is anonymous or the token is stale.
func (s *AuthService) UserFromRequest(r *http.Request) string {
	tok := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
	dot := strings.IndexByte(tok, '.')
	if dot <= 0 {
		return ""
	}
	userID, secret := tok[:dot], tok[dot+1:]
	s.mu.Lock()
	defer s.mu.Unlock()
	u, ok := s.users[userID]
	if !ok {
		return ""
	}
	if subtle.ConstantTimeCompare([]byte(u.TokenHash), []byte(hashHex(secret))) != 1 {
		return ""
	}
	return userID
}

func (s *AuthService) handleRegister(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	var req struct {
		DeviceID string `json:"device_id"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req); err != nil || len(req.DeviceID) < 16 {
		http.Error(w, "device_id (>=16 chars) required", http.StatusBadRequest)
		return
	}
	devHash := hashHex(req.DeviceID)

	s.mu.Lock()
	defer s.mu.Unlock()
	userID, known := s.byDev[devHash]
	if !known {
		userID = "u_" + newSecret()[:16]
		s.users[userID] = &authUser{
			DeviceHash: devHash,
			Created:    time.Now().UTC().Format(time.RFC3339),
		}
		s.byDev[devHash] = userID
	}
	// Fresh session secret on every register; the previous one stops working
	// (a re-register is how a reinstalled client with the same device_id
	// recovers its account).
	secret := newSecret()
	s.users[userID].TokenHash = hashHex(secret)
	s.persistLocked()

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]string{
		"user_id": userID,
		"token":   userID + "." + secret,
	})
}

func (s *AuthService) handleMe(w http.ResponseWriter, r *http.Request) {
	userID := s.UserFromRequest(r)
	if userID == "" {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	s.mu.Lock()
	created := s.users[userID].Created
	s.mu.Unlock()
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]string{"user_id": userID, "created": created})
}
