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
	Name       string `json:"name,omitempty"` // display name set from the auth screen
	// Linked platform identities (provider → stable subject id) — the
	// cross-device recovery path: sign in with Google/Apple on a new phone
	// and the SAME account comes back, wallet and saves included.
	Providers map[string]string `json:"providers,omitempty"`
}

type AuthService struct {
	mu     sync.Mutex
	path   string
	users  map[string]*authUser // userID → record
	byDev  map[string]string    // device hash → userID
	byProv map[string]string    // "provider:subject" → userID

	// AuthDev accepts the "dev" provider with any token (test builds).
	AuthDev bool
	// Optional audience pins — when set, a Google id_token's aud / an Apple
	// identity token's aud must match (production hardening).
	GoogleClientID string
	AppleBundleID  string

	// verifyProvider validates a platform token and returns its stable
	// subject. Swappable in tests; defaults to the real Google/Apple checks.
	verifyProvider func(provider, token string) (subject string, err error)
}

func NewAuthService(dir string) (*AuthService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	s := &AuthService{
		path:   filepath.Join(dir, "users.json"),
		users:  map[string]*authUser{},
		byDev:  map[string]string{},
		byProv: map[string]string{},
	}
	s.verifyProvider = s.verifyProviderReal
	if data, err := os.ReadFile(s.path); err == nil {
		_ = json.Unmarshal(data, &s.users)
		for id, u := range s.users {
			s.byDev[u.DeviceHash] = id
			for p, sub := range u.Providers {
				s.byProv[p+":"+sub] = id
			}
		}
	}
	return s, nil
}

func (s *AuthService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/auth/register", s.handleRegister)
	mux.HandleFunc("/v1/auth/me", s.handleMe)
	mux.HandleFunc("/v1/auth/profile", s.handleProfile)
	mux.HandleFunc("/v1/auth/link", s.handleLink)
	mux.HandleFunc("/v1/auth/login", s.handleLogin)
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

// SnapshotUsers returns a deep-enough copy of the user table for read-only
// admin views (no hashes — nothing session-sensitive leaves the service).
func (s *AuthService) SnapshotUsers() map[string]authUser {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make(map[string]authUser, len(s.users))
	for id, u := range s.users {
		cp := authUser{Created: u.Created, Name: u.Name}
		if len(u.Providers) > 0 {
			cp.Providers = make(map[string]string, len(u.Providers))
			for k, v := range u.Providers {
				cp.Providers[k] = v
			}
		}
		out[id] = cp
	}
	return out
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
	created, name := s.users[userID].Created, s.users[userID].Name
	s.mu.Unlock()
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]string{"user_id": userID, "created": created, "name": name})
}

// handleLink attaches a verified platform identity (Google/Apple) to the
// CURRENT device account — after this, signing in with that identity on any
// device recovers this account. 409 when the identity already belongs to a
// different account (the client offers "switch to that account?" via login).
func (s *AuthService) handleLink(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	userID := s.UserFromRequest(r)
	if userID == "" {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var req struct {
		Provider string `json:"provider"` // google | apple | dev (-auth-dev)
		Token    string `json:"token"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 64<<10)).Decode(&req); err != nil ||
		req.Provider == "" || req.Token == "" {
		http.Error(w, "provider and token required", http.StatusBadRequest)
		return
	}
	subject, err := s.verifyProvider(req.Provider, req.Token)
	if err != nil {
		http.Error(w, "token verification failed: "+err.Error(), http.StatusUnauthorized)
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	key := req.Provider + ":" + subject
	if owner, taken := s.byProv[key]; taken && owner != userID {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusConflict)
		_ = json.NewEncoder(w).Encode(map[string]string{
			"error": "already_linked", "user_id": owner,
		})
		return
	}
	u := s.users[userID]
	if u.Providers == nil {
		u.Providers = map[string]string{}
	}
	u.Providers[req.Provider] = subject
	s.byProv[key] = userID
	s.persistLocked()
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]string{"user_id": userID, "provider": req.Provider})
}

// handleLogin signs in WITH a platform identity: a known identity returns its
// account (fresh session token — the cross-device recovery), an unknown one
// creates a new account on the spot (the standard first "Sign in with …").
func (s *AuthService) handleLogin(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	var req struct {
		Provider string `json:"provider"`
		Token    string `json:"token"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 64<<10)).Decode(&req); err != nil ||
		req.Provider == "" || req.Token == "" {
		http.Error(w, "provider and token required", http.StatusBadRequest)
		return
	}
	subject, err := s.verifyProvider(req.Provider, req.Token)
	if err != nil {
		http.Error(w, "token verification failed: "+err.Error(), http.StatusUnauthorized)
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	key := req.Provider + ":" + subject
	userID, known := s.byProv[key]
	if !known {
		userID = "u_" + newSecret()[:16]
		s.users[userID] = &authUser{
			Created:   time.Now().UTC().Format(time.RFC3339),
			Providers: map[string]string{req.Provider: subject},
		}
		s.byProv[key] = userID
	}
	secret := newSecret()
	s.users[userID].TokenHash = hashHex(secret)
	s.persistLocked()

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]string{
		"user_id": userID,
		"token":   userID + "." + secret,
		"name":    s.users[userID].Name,
	})
}

// handleProfile sets the display name shown by the auth screen and anywhere
// else the account surfaces (leaderboards already carry their own name field).
func (s *AuthService) handleProfile(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	userID := s.UserFromRequest(r)
	if userID == "" {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var req struct {
		Name string `json:"name"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req); err != nil {
		http.Error(w, "bad json", http.StatusBadRequest)
		return
	}
	name := strings.TrimSpace(req.Name)
	if len(name) > 64 {
		name = name[:64]
	}
	s.mu.Lock()
	s.users[userID].Name = name
	s.persistLocked()
	s.mu.Unlock()
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]string{"user_id": userID, "name": name})
}
