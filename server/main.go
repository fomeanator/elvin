// LVN server template — a minimal, dependency-free content + state backend.
//
// It is deliberately small: enough to serve a game's content manifest, its
// .lvn scripts and assets, and per-player saves, with an optional token-gated
// admin upload that mirrors the lvnconv pipeline (compile a .lvn, PUT it, the
// client picks it up). Swap the in-memory store for a database when you grow.
//
//	go run . -content ./content -addr :8000 -admin-token secret
//
// Routes:
//
//	GET  /healthz                       liveness
//	GET  /v1/content/manifest           the content manifest (content/manifest.json)
//	GET  /content/<path>                static .lvn / art / audio
//	GET  /v1/state?user=<id>            player save (JSON; 404 if none)
//	PUT  /v1/state?user=<id>            store player save (body = JSON)
//	PUT  /v1/admin/assets/<path>        upload an asset/script (admin token)
package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
)

func main() {
	addr := flag.String("addr", ":8000", "listen address")
	contentDir := flag.String("content", "./content", "content directory (manifest.json + assets)")
	adminToken := flag.String("admin-token", "", "bearer token for /v1/admin/* (empty disables admin)")
	templateDir := flag.String("template", "./sandbox", "Unity project template used by /v1/export")
	flag.Parse()

	if err := os.MkdirAll(*contentDir, 0o755); err != nil {
		log.Fatalf("content dir: %v", err)
	}
	srv := &server{content: *contentDir, adminToken: *adminToken, templateDir: *templateDir, state: map[string][]byte{}}

	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	})
	mux.HandleFunc("/v1/content/manifest", srv.handleManifest)
	// Content-version index (path -> sha256), computed live so cache-busting
	// works the moment a file changes. Registered before the static prefix so
	// the exact path wins.
	mux.HandleFunc("/content/asset-versions.json", srv.handleAssetVersions)
	mux.HandleFunc("/v1/content/version", srv.handleVersion)
	mux.Handle("/content/", srv.contentHandler(*contentDir))
	mux.HandleFunc("/v1/state", srv.handleState)
	mux.HandleFunc("/v1/admin/assets/", srv.handleAdminAsset)
	mux.HandleFunc("/v1/admin/import-articy", srv.handleImportArticy)
	mux.HandleFunc("/v1/export", srv.handleExport)

	// Serve the authoring panel (the lvns playground + reference + save-to-app)
	// at /panel; also kept at / for convenience.
	webDir := "./website"
	if _, err := os.Stat(webDir); os.IsNotExist(err) {
		webDir = "server/website"
	}
	site := http.FileServer(http.Dir(webDir))
	mux.Handle("/panel/", http.StripPrefix("/panel/", site))
	mux.HandleFunc("/panel", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/panel/", http.StatusFound)
	})
	mux.Handle("/", site)

	log.Printf("LVN server on %s, content=%s, admin=%v", *addr, *contentDir, *adminToken != "")
	log.Fatal(http.ListenAndServe(*addr, withLog(mux)))
}

type server struct {
	content     string
	adminToken  string
	templateDir string
	mu          sync.RWMutex
	state       map[string][]byte // user id -> raw save JSON
}

func (s *server) handleManifest(w http.ResponseWriter, r *http.Request) {
	data, err := os.ReadFile(filepath.Join(s.content, "manifest.json"))
	if err != nil {
		// A fresh install has no manifest yet — return an empty one, not a 500.
		writeJSON(w, http.StatusOK, map[string]any{"titles": []any{}})
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store") // the manifest is the live index
	w.Write(data)
}

// contentHandler serves static content with cache rules that match the engine's
// cache-busting design: .lvn scripts are live (no-store), every other asset is
// versioned (immutable, long-lived) — a changed asset gets a new ?v= and so a
// new URL, so it never serves stale.
func (s *server) contentHandler(dir string) http.Handler {
	fs := http.StripPrefix("/content/", http.FileServer(http.Dir(dir)))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.HasSuffix(strings.ToLower(r.URL.Path), ".lvn") {
			w.Header().Set("Cache-Control", "no-store")
		} else {
			w.Header().Set("Cache-Control", "public, max-age=604800, immutable")
		}
		fs.ServeHTTP(w, r)
	})
}

// computeVersions returns {content-relative-path: sha256} for every served
// file. includeManifest folds manifest.json into the map (used by the version
// endpoint so manifest edits register), otherwise it's left out (the asset
// index is for art/scripts; the manifest is fetched fresh, never versioned).
func (s *server) computeVersions(includeManifest bool) map[string]string {
	out := map[string]string{}
	_ = filepath.Walk(s.content, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return nil
		}
		rel, rerr := filepath.Rel(s.content, path)
		if rerr != nil {
			return nil
		}
		rel = filepath.ToSlash(rel)
		if rel == "asset-versions.json" || (rel == "manifest.json" && !includeManifest) {
			return nil
		}
		data, derr := os.ReadFile(path)
		if derr != nil {
			return nil
		}
		sum := sha256.Sum256(data)
		out[rel] = hex.EncodeToString(sum[:])
		return nil
	})
	return out
}

// handleAssetVersions returns {content-relative-path: sha256} for every served
// asset/script. The client folds these hashes into its disk cache key and the
// ?v= query, so re-uploaded content auto-invalidates.
func (s *server) handleAssetVersions(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	json.NewEncoder(w).Encode(s.computeVersions(false))
}

// handleVersion returns a single content version hash that changes whenever ANY
// served file (manifest, scripts, assets) changes — the cheap poll the client
// uses to detect "something changed" before pulling the delta. Supports ETag /
// If-None-Match so an unchanged poll is a zero-body 304.
func versionHash(versions map[string]string) string {
	keys := make([]string, 0, len(versions))
	for k := range versions {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	h := sha256.New()
	for _, k := range keys {
		h.Write([]byte(k))
		h.Write([]byte{0})
		h.Write([]byte(versions[k]))
		h.Write([]byte{0})
	}
	return hex.EncodeToString(h.Sum(nil))
}

func (s *server) handleVersion(w http.ResponseWriter, r *http.Request) {
	sum := versionHash(s.computeVersions(true))
	etag := `"` + sum + `"`
	w.Header().Set("ETag", etag)
	w.Header().Set("Cache-Control", "no-store")
	if r.Header.Get("If-None-Match") == etag {
		w.WriteHeader(http.StatusNotModified)
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"version": sum})
}

func (s *server) handleState(w http.ResponseWriter, r *http.Request) {
	user := r.URL.Query().Get("user")
	if user == "" {
		http.Error(w, "user query param required", http.StatusBadRequest)
		return
	}
	switch r.Method {
	case http.MethodGet:
		s.mu.RLock()
		data, ok := s.state[user]
		s.mu.RUnlock()
		if !ok {
			// Not in memory — try the on-disk mirror (survives a server restart).
			if b, err := os.ReadFile(s.stateFile(user)); err == nil {
				data, ok = b, true
				s.mu.Lock()
				s.state[user] = b
				s.mu.Unlock()
			}
		}
		if !ok {
			http.Error(w, "no save", http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.Write(data)
	case http.MethodPut:
		body, err := io.ReadAll(io.LimitReader(r.Body, 1<<20))
		if err != nil {
			http.Error(w, "read body", http.StatusBadRequest)
			return
		}
		if !json.Valid(body) {
			http.Error(w, "body must be JSON", http.StatusBadRequest)
			return
		}
		s.mu.Lock()
		s.state[user] = body
		s.mu.Unlock()
		// Persist to disk so saves survive a restart (best-effort — the in-memory
		// copy already answers this session).
		if err := s.writeStateFile(user, body); err != nil {
			fmt.Fprintf(os.Stderr, "state: persist %s: %v\n", user, err)
		}
		writeJSON(w, http.StatusOK, map[string]bool{"saved": true})
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

// stateFile is the on-disk mirror path for a user's save, under <content>/state/.
// The user key (may carry a "<uid>__<title>" composite) is sanitised into a safe
// filename.
func (s *server) stateFile(user string) string {
	safe := make([]rune, 0, len(user))
	for _, r := range user {
		switch {
		case r == '-' || r == '_' || r == '.' ||
			(r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9'):
			safe = append(safe, r)
		default:
			safe = append(safe, '_')
		}
	}
	return filepath.Join(s.content, "state", string(safe)+".json")
}

func (s *server) writeStateFile(user string, body []byte) error {
	p := s.stateFile(user)
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		return err
	}
	return os.WriteFile(p, body, 0o644)
}

func (s *server) handleAdminAsset(w http.ResponseWriter, r *http.Request) {
	if s.adminToken == "" {
		http.Error(w, "admin disabled", http.StatusForbidden)
		return
	}
	if r.Header.Get("Authorization") != "Bearer "+s.adminToken {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if r.Method != http.MethodPut {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	rel := strings.TrimPrefix(r.URL.Path, "/v1/admin/assets/")
	if rel == "" || strings.Contains(rel, "..") {
		http.Error(w, "bad path", http.StatusBadRequest)
		return
	}
	dst := filepath.Join(s.content, filepath.Clean(rel))
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	body, err := io.ReadAll(io.LimitReader(r.Body, 64<<20))
	if err != nil {
		http.Error(w, "read body", http.StatusBadRequest)
		return
	}
	if err := atomicWrite(dst, body, 0o644); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"path": rel, "bytes": len(body)})
}

// atomicWrite writes via a temp file in the same directory then renames, so a
// concurrent reader (e.g. computeVersions hashing for cache-busting) never sees
// a half-written or zero-byte file. Rename is atomic on the same filesystem.
func atomicWrite(dst string, body []byte, perm os.FileMode) error {
	tmp, err := os.CreateTemp(filepath.Dir(dst), ".tmp-*")
	if err != nil {
		return err
	}
	tmpName := tmp.Name()
	if _, err := tmp.Write(body); err != nil {
		tmp.Close()
		os.Remove(tmpName)
		return err
	}
	if err := tmp.Close(); err != nil {
		os.Remove(tmpName)
		return err
	}
	if err := os.Chmod(tmpName, perm); err != nil {
		os.Remove(tmpName)
		return err
	}
	if err := os.Rename(tmpName, dst); err != nil {
		os.Remove(tmpName)
		return err
	}
	return nil
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	json.NewEncoder(w).Encode(v)
}

func withLog(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		log.Printf("%s %s", r.Method, r.URL.Path)
		next.ServeHTTP(w, r)
	})
}
