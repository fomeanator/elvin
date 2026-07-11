package main

// Admin API — the panel's backoffice: the manifest (titles/chapters/bundles),
// users (accounts + wallets + grants), orders (IAP/spend history across all
// wallets) and cloud saves. Every route sits behind the -admin-token bearer,
// same as the existing asset upload; no token — no admin surface at all.

import (
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
)

type AdminService struct {
	content string
	token   string
	auth    *AuthService
	wallet  *WalletService
	// writeMu serialises snapshot+write pairs so two parallel panel saves
	// can't lose a history revision (same-millisecond .bak collision).
	writeMu sync.Mutex
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
	mux.HandleFunc("/v1/admin/config/", s.handleConfig)
	mux.HandleFunc("/v1/admin/manifest/publish", s.handlePublish)
	mux.HandleFunc("/v1/admin/history", s.handleHistory)
	mux.HandleFunc("/v1/admin/rollback", s.handleRollback)
	mux.HandleFunc("/v1/admin/files", s.handleFiles)
}

// ── editorial history: every panel write snapshots the previous version ────

// historyEligible: the editable text-ish content whose past versions are
// worth keeping. Binary art is excluded (bulky, and reuploading is cheap).
// Doubles as the path guard for history/rollback — reject traversal HERE so
// every caller (list, fetch, restore-write) inherits the check.
func historyEligible(rel string) bool {
	if strings.Contains(rel, "..") || strings.ContainsAny(rel, "\\") {
		return false
	}
	if rel == "manifest.json" || adminConfigs[rel] {
		return true
	}
	return strings.HasPrefix(rel, "scripts/") &&
		(strings.HasSuffix(rel, ".lvn") || strings.HasSuffix(rel, ".lvns"))
}

// history timestamps are pure decimal millis — anything else is an attack.
var reHistoryTS = regexp.MustCompile(`^[0-9]{10,16}$`)

// snapshotHistory copies the CURRENT file into content/.history/<rel>/<ms>.bak
// before it gets overwritten; keeps the newest 50 per file.
func snapshotHistory(content, rel string) {
	if !historyEligible(rel) {
		return
	}
	src := filepath.Join(content, rel)
	data, err := os.ReadFile(src)
	if err != nil {
		return // nothing to snapshot (first write)
	}
	dir := filepath.Join(content, ".history", rel)
	if os.MkdirAll(dir, 0o755) != nil {
		return
	}
	name := fmt.Sprintf("%d.bak", time.Now().UnixMilli())
	_ = atomicWrite(filepath.Join(dir, name), data, 0o644)
	entries, _ := os.ReadDir(dir)
	if len(entries) > 50 {
		names := make([]string, 0, len(entries))
		for _, e := range entries {
			names = append(names, e.Name())
		}
		sort.Strings(names)
		for _, n := range names[:len(names)-50] {
			_ = os.Remove(filepath.Join(dir, n))
		}
	}
}

// GET /v1/admin/history?file=<rel> — the file's saved versions, newest first.
func (s *AdminService) handleHistory(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	rel := r.URL.Query().Get("file")
	if !historyEligible(rel) {
		http.Error(w, "no history for this file", http.StatusNotFound)
		return
	}
	if ts := r.URL.Query().Get("ts"); ts != "" { // fetch one version's body
		if !reHistoryTS.MatchString(ts) {
			http.Error(w, "bad ts", http.StatusBadRequest)
			return
		}
		data, err := os.ReadFile(filepath.Join(s.content, ".history", rel, ts+".bak"))
		if err != nil {
			http.Error(w, "not found", http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(data)
		return
	}
	entries, _ := os.ReadDir(filepath.Join(s.content, ".history", rel))
	type row struct {
		TS   string `json:"ts"`
		Size int64  `json:"size"`
	}
	var out []row
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".bak") {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		out = append(out, row{TS: strings.TrimSuffix(e.Name(), ".bak"), Size: info.Size()})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].TS > out[j].TS })
	writeJSON(w, http.StatusOK, map[string]any{"file": rel, "versions": out})
}

// POST /v1/admin/rollback {file, ts} — restore a saved version (the current
// one is snapshotted first, so a rollback is itself undoable).
func (s *AdminService) handleRollback(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	var req struct{ File, TS string }
	if json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req) != nil ||
		!historyEligible(req.File) || !reHistoryTS.MatchString(req.TS) {
		http.Error(w, "file and ts required", http.StatusBadRequest)
		return
	}
	data, err := os.ReadFile(filepath.Join(s.content, ".history", req.File, req.TS+".bak"))
	if err != nil {
		http.Error(w, "version not found", http.StatusNotFound)
		return
	}
	s.writeMu.Lock()
	snapshotHistory(s.content, req.File)
	err = atomicWrite(filepath.Join(s.content, req.File), data, 0o644)
	s.writeMu.Unlock()
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, http.StatusOK, map[string]bool{"restored": true})
}

// ── the manifest draft: edit safely, publish deliberately ──────────────────

// POST /v1/admin/manifest/publish — the draft becomes the live manifest (the
// previous live version is snapshotted). No draft → 404.
func (s *AdminService) handlePublish(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	draft := filepath.Join(s.content, "manifest.draft.json")
	data, err := os.ReadFile(draft)
	if err != nil {
		http.Error(w, "no draft to publish", http.StatusNotFound)
		return
	}
	s.writeMu.Lock()
	snapshotHistory(s.content, "manifest.json")
	err = atomicWrite(filepath.Join(s.content, "manifest.json"), data, 0o644)
	s.writeMu.Unlock()
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	_ = os.Remove(draft)
	writeJSON(w, http.StatusOK, map[string]bool{"published": true})
}

// ── content file browser (the panel's Assets tab) ──────────────────────────

// GET /v1/admin/files?dir=<rel> — list a content directory (dirs first).
func (s *AdminService) handleFiles(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	rel := filepath.Clean("/" + r.URL.Query().Get("dir"))[1:] // no escapes
	// Case-folded: macOS/Windows filesystems are case-insensitive, so
	// dir=State would otherwise open the very folder the blacklist names.
	low := strings.ToLower(rel)
	if strings.HasPrefix(low, ".history") || strings.HasPrefix(low, "services") || strings.HasPrefix(low, "state") {
		http.Error(w, "not browsable", http.StatusForbidden)
		return
	}
	entries, err := os.ReadDir(filepath.Join(s.content, rel))
	if err != nil {
		http.Error(w, "no such directory", http.StatusNotFound)
		return
	}
	type row struct {
		Name     string `json:"name"`
		Dir      bool   `json:"dir"`
		Size     int64  `json:"size"`
		Modified string `json:"modified"`
	}
	var out []row
	for _, e := range entries {
		name := e.Name()
		if strings.HasPrefix(name, ".") || (rel == "" && (name == "services" || name == "state")) {
			continue
		}
		info, err := e.Info()
		if err != nil {
			continue
		}
		out = append(out, row{Name: name, Dir: e.IsDir(), Size: info.Size(),
			Modified: info.ModTime().UTC().Format(time.RFC3339)})
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Dir != out[j].Dir {
			return out[i].Dir
		}
		return out[i].Name < out[j].Name
	})
	writeJSON(w, http.StatusOK, map[string]any{"dir": rel, "files": out})
}

// The economy configs the panel edits directly. Whitelisted by name — the
// admin surface must never become a generic file writer.
var adminConfigs = map[string]bool{
	"iap-catalog.json":   true, // packs (sku → grant + presentation)
	"ads.json":           true, // rewarded placements (currency/amount/daily_cap)
	"daily-rewards.json": true, // streak rewards, day by day
}

// GET/PUT /v1/admin/config/<name> — validated JSON, atomic write. Services
// hot-reload these by mtime, so the panel edit takes effect immediately.
func (s *AdminService) handleConfig(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	name := strings.TrimPrefix(r.URL.Path, "/v1/admin/config/")
	if !adminConfigs[name] {
		http.Error(w, "unknown config (iap-catalog.json | ads.json | daily-rewards.json)", http.StatusNotFound)
		return
	}
	path := filepath.Join(s.content, name)
	switch r.Method {
	case http.MethodGet:
		data, err := os.ReadFile(path)
		if err != nil {
			data = []byte("{}")
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(data)
	case http.MethodPut:
		var doc json.RawMessage
		if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<20)).Decode(&doc); err != nil {
			http.Error(w, "invalid JSON: "+err.Error(), http.StatusBadRequest)
			return
		}
		var pretty any
		_ = json.Unmarshal(doc, &pretty)
		data, _ := json.MarshalIndent(pretty, "", "  ")
		s.writeMu.Lock()
		snapshotHistory(s.content, name)
		err := atomicWrite(path, data, 0o644)
		s.writeMu.Unlock()
		if err != nil {
			http.Error(w, "write failed: "+err.Error(), http.StatusInternalServerError)
			return
		}
		writeJSON(w, http.StatusOK, map[string]bool{"saved": true})
	default:
		http.Error(w, "GET or PUT", http.StatusMethodNotAllowed)
	}
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
// write, previous version snapshotted to history). With ?draft=1 both verbs
// address manifest.draft.json instead — a safe scratchpad the clients never
// see until /v1/admin/manifest/publish promotes it. DELETE ?draft=1 discards
// the draft. Publishing (or a direct PUT) IS the deploy: the content version
// moves and every running client picks the change up within its sync interval.
func (s *AdminService) handleManifest(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	draft := r.URL.Query().Get("draft") == "1"
	name := "manifest.json"
	if draft {
		name = "manifest.draft.json"
	}
	path := filepath.Join(s.content, name)
	switch r.Method {
	case http.MethodGet:
		data, err := os.ReadFile(path)
		if err != nil && draft { // no draft yet → start from the live manifest
			data, err = os.ReadFile(filepath.Join(s.content, "manifest.json"))
		}
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
		s.writeMu.Lock()
		if !draft {
			snapshotHistory(s.content, "manifest.json")
		}
		err := atomicWrite(path, data, 0o644)
		s.writeMu.Unlock()
		if err != nil {
			http.Error(w, "write failed: "+err.Error(), http.StatusInternalServerError)
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"saved": true, "draft": draft})
	case http.MethodDelete:
		if !draft {
			http.Error(w, "only the draft can be deleted", http.StatusBadRequest)
			return
		}
		_ = os.Remove(path)
		writeJSON(w, http.StatusOK, map[string]bool{"discarded": true})
	default:
		http.Error(w, "GET, PUT or DELETE", http.StatusMethodNotAllowed)
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
	// A typo'd user id must be a loud 400, not a silent no-op "ok" the
	// support person walks away from believing the grant landed.
	if !reUserFile.MatchString(req.UserID) {
		http.Error(w, "malformed user_id", http.StatusBadRequest)
		return
	}
	if req.Reason == "" {
		req.Reason = "admin:grant"
	}
	var gerr error
	if req.Amount > 0 {
		gerr = s.wallet.Grant(req.UserID, req.Currency, req.Amount, req.Reason)
	} else {
		// negative grant = clawback (refund abuse etc.) — floor at zero
		gerr = s.wallet.Clawback(req.UserID, req.Currency, -req.Amount, req.Reason)
	}
	if gerr != nil {
		// The support person must know the grant did NOT land — a cheerful
		// "ok" over a failed write is exactly the lie this endpoint fights.
		http.Error(w, "grant failed: "+gerr.Error(), http.StatusInternalServerError)
		return
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

// reSaveKey mirrors stateFile's filename alphabet (letters, digits, -_.) so any
// key handleSaves can list is addressable by the detail endpoint.
var reSaveKey = regexp.MustCompile(`^[a-zA-Z0-9_.-]+$`)

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
	// The key alphabet must match stateFile's sanitiser (main.go), which ALLOWS
	// '.' — a "<uid>__<title>" key with a dotted title is listed by handleSaves,
	// so it must be viewable/deletable here too. ".." is still rejected outright.
	if key == "" || strings.Contains(key, "..") || !reSaveKey.MatchString(key) {
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
