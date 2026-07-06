package main

// Admin API — the panel's backoffice: the manifest (titles/chapters/bundles),
// users (accounts + wallets + grants), orders (IAP/spend history across all
// wallets) and cloud saves. Every route sits behind the -admin-token bearer,
// same as the existing asset upload; no token — no admin surface at all.

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

type AdminService struct {
	content string
	token   string
	auth    *AuthService
	wallet  *WalletService
}

func NewAdminService(content, token string, auth *AuthService, wallet *WalletService) *AdminService {
	return &AdminService{content: content, token: token, auth: auth, wallet: wallet}
}

func (s *AdminService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/admin/manifest", s.handleManifest)
	mux.HandleFunc("/v1/admin/users", s.handleUsers)
	mux.HandleFunc("/v1/admin/users/", s.handleUserDetail)
	mux.HandleFunc("/v1/admin/grant", s.handleGrant)
	mux.HandleFunc("/v1/admin/orders", s.handleOrders)
	mux.HandleFunc("/v1/admin/saves", s.handleSaves)
	mux.HandleFunc("/v1/admin/saves/", s.handleSaveDetail)
}

func (s *AdminService) ok(w http.ResponseWriter, r *http.Request) bool {
	if s.token == "" || !bearerOK(r, s.token) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return false
	}
	return true
}

// ── manifest ────────────────────────────────────────────────────────────────

// GET returns the live manifest; PUT replaces it (validated JSON, atomic
// write). The content-version index recomputes on the fly, so every running
// client picks the change up within its sync interval — the panel edit IS the
// deploy.
func (s *AdminService) handleManifest(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	path := filepath.Join(s.content, "manifest.json")
	switch r.Method {
	case http.MethodGet:
		data, err := os.ReadFile(path)
		if err != nil {
			http.Error(w, "no manifest", http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(data)
	case http.MethodPut:
		var doc json.RawMessage
		if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 32<<20)).Decode(&doc); err != nil {
			http.Error(w, "invalid JSON: "+err.Error(), http.StatusBadRequest)
			return
		}
		// pretty-print so the file stays hand-editable next to the panel
		var pretty any
		_ = json.Unmarshal(doc, &pretty)
		data, _ := json.MarshalIndent(pretty, "", "  ")
		if err := atomicWrite(path, data, 0o644); err != nil {
			http.Error(w, "write failed: "+err.Error(), http.StatusInternalServerError)
			return
		}
		writeJSON(w, http.StatusOK, map[string]bool{"saved": true})
	default:
		http.Error(w, "GET or PUT", http.StatusMethodNotAllowed)
	}
}

// ── users ───────────────────────────────────────────────────────────────────

func (s *AdminService) handleUsers(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	users := s.auth.SnapshotUsers()
	type row struct {
		UserID    string            `json:"user_id"`
		Created   string            `json:"created"`
		Name      string            `json:"name,omitempty"`
		Providers map[string]string `json:"providers,omitempty"`
		Balances  map[string]int64  `json:"balances,omitempty"`
	}
	out := make([]row, 0, len(users))
	for id, u := range users {
		doc := s.wallet.AdminLoad(id)
		out = append(out, row{
			UserID: id, Created: u.Created, Name: u.Name,
			Providers: u.Providers, Balances: doc.Balances,
		})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Created > out[j].Created })
	writeJSON(w, http.StatusOK, map[string]any{"users": out})
}

func (s *AdminService) handleUserDetail(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	id := strings.TrimPrefix(r.URL.Path, "/v1/admin/users/")
	if id == "" || !reUserFile.MatchString(id) {
		http.Error(w, "bad user id", http.StatusBadRequest)
		return
	}
	users := s.auth.SnapshotUsers()
	u, ok := users[id]
	if !ok {
		http.Error(w, "unknown user", http.StatusNotFound)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"user_id": id, "created": u.Created, "name": u.Name,
		"providers": u.Providers,
		"wallet":    s.wallet.AdminLoad(id),
	})
}

// POST {user_id, currency, amount, reason} — a support/ops grant, audited in
// the wallet history like every other credit.
func (s *AdminService) handleGrant(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	var req struct {
		UserID   string `json:"user_id"`
		Currency string `json:"currency"`
		Amount   int64  `json:"amount"`
		Reason   string `json:"reason"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req); err != nil ||
		req.UserID == "" || req.Currency == "" || req.Amount == 0 {
		http.Error(w, "user_id, currency and amount required", http.StatusBadRequest)
		return
	}
	if req.Reason == "" {
		req.Reason = "admin:grant"
	}
	if req.Amount > 0 {
		s.wallet.Grant(req.UserID, req.Currency, req.Amount, req.Reason)
	} else {
		// negative grant = clawback (refund abuse etc.) — floor at zero
		s.wallet.Clawback(req.UserID, req.Currency, -req.Amount, req.Reason)
	}
	writeJSON(w, http.StatusOK, map[string]any{"ok": true, "wallet": s.wallet.AdminLoad(req.UserID)})
}

// ── orders ──────────────────────────────────────────────────────────────────

// The purchase ledger across every wallet: IAP grants and sku spends (shop /
// wardrobe buys), newest first.
func (s *AdminService) handleOrders(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	type order struct {
		UserID string `json:"user_id"`
		walletEntry
	}
	var out []order
	for _, id := range s.wallet.AllUserIDs() {
		doc := s.wallet.AdminLoad(id)
		for _, e := range doc.History {
			if e.Type == "iap" || (e.Type == "spend" && e.SKU != "") {
				out = append(out, order{UserID: id, walletEntry: e})
			}
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].TS > out[j].TS })
	if len(out) > 500 {
		out = out[:500]
	}
	writeJSON(w, http.StatusOK, map[string]any{"orders": out})
}

// ── saves (cloud state blobs) ───────────────────────────────────────────────

func (s *AdminService) handleSaves(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	dir := filepath.Join(s.content, "state")
	entries, _ := os.ReadDir(dir)
	type row struct {
		Key      string `json:"key"`
		Size     int64  `json:"size"`
		Modified string `json:"modified"`
	}
	var out []row
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		out = append(out, row{
			Key:      strings.TrimSuffix(e.Name(), ".json"),
			Size:     info.Size(),
			Modified: info.ModTime().UTC().Format(time.RFC3339),
		})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Modified > out[j].Modified })
	writeJSON(w, http.StatusOK, map[string]any{"saves": out})
}

func (s *AdminService) handleSaveDetail(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	key := strings.TrimPrefix(r.URL.Path, "/v1/admin/saves/")
	if key == "" || !reUserFile.MatchString(strings.ReplaceAll(key, "__", "")) {
		http.Error(w, "bad save key", http.StatusBadRequest)
		return
	}
	path := filepath.Join(s.content, "state", key+".json")
	switch r.Method {
	case http.MethodGet:
		data, err := os.ReadFile(path)
		if err != nil {
			http.Error(w, "not found", http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(data)
	case http.MethodDelete:
		if err := os.Remove(path); err != nil {
			http.Error(w, "not found", http.StatusNotFound)
			return
		}
		_ = os.Remove(path + ".key") // the TOFU sidecar goes with it
		writeJSON(w, http.StatusOK, map[string]bool{"deleted": true})
	default:
		http.Error(w, "GET or DELETE", http.StatusMethodNotAllowed)
	}
}
