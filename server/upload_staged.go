package main

import (
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strings"
)

// Staged uploads decouple "get the bytes onto the server" from "run the
// import": a bundle import is a multi-hundred-MB multipart POST, and on a
// slow/flaky uplink a single giant request has to restart from zero on every
// drop. Instead the panel PUTs each file in chunks to a stable id (derived
// from name+size, so re-picking the same files after a reload resumes rather
// than reuploading); once a file is fully staged, its path plugs straight
// into the EXISTING JSON {dir}-style import-bundle/import-articy fields — no
// change needed there, staged files just happen to already live on disk.

const maxStagedUpload = 4 << 30 // 4 GiB — matches the archive-extraction ceiling elsewhere

// stagedUploadIDRe mirrors idRe but allows dots (original extension carried
// in the id, e.g. "cold_13_08_25.rar-33554432", is nice for debugging on disk).
var stagedUploadIDRe = regexp.MustCompile(`^[A-Za-z0-9_.-]{1,200}$`)

func (s *server) stagingDir() string {
	return filepath.Join(filepath.Dir(strings.TrimRight(s.content, string(filepath.Separator))), "uploads")
}

// handleStagedUpload serves PUT (append a chunk, optionally resuming via
// Content-Range) and GET/HEAD (report bytes received so far) under
// /v1/admin/staged-upload/<id>.
func (s *server) handleStagedUpload(w http.ResponseWriter, r *http.Request) {
	if s.adminToken == "" {
		http.Error(w, "admin disabled", http.StatusForbidden)
		return
	}
	if !bearerOK(r, s.adminToken) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	id := strings.TrimPrefix(r.URL.Path, "/v1/admin/staged-upload/")
	if id == "" || !stagedUploadIDRe.MatchString(id) || strings.Contains(id, "..") {
		http.Error(w, "bad id", http.StatusBadRequest)
		return
	}
	dir := s.stagingDir()
	if err := os.MkdirAll(dir, 0o755); err != nil {
		http.Error(w, "staging dir: "+err.Error(), http.StatusInternalServerError)
		return
	}
	path := filepath.Join(dir, id)

	switch r.Method {
	case http.MethodGet, http.MethodHead:
		info, err := os.Stat(path)
		if err != nil {
			writeJSON(w, http.StatusOK, map[string]any{"offset": 0})
			return
		}
		// path included so a client whose upload finished on an earlier chunk
		// (or was already fully staged from a prior run) can pick it up
		// straight from this GET without a further PUT — mirrors the PUT
		// response shape.
		writeJSON(w, http.StatusOK, map[string]any{"offset": info.Size(), "path": path})

	case http.MethodPut:
		start := int64(0)
		total := int64(-1)
		if cr := r.Header.Get("Content-Range"); cr != "" {
			var end int64
			if _, err := fmt.Sscanf(cr, "bytes %d-%d/%d", &start, &end, &total); err != nil {
				http.Error(w, "bad Content-Range", http.StatusBadRequest)
				return
			}
		}
		if total > maxStagedUpload || start > maxStagedUpload {
			http.Error(w, "upload exceeds the size ceiling", http.StatusRequestEntityTooLarge)
			return
		}

		info, statErr := os.Stat(path)
		cur := int64(0)
		if statErr == nil {
			cur = info.Size()
		}
		if start != cur {
			// Out of sync (a dropped ack, a stale resume) — tell the client
			// where the file actually is instead of silently corrupting it.
			w.WriteHeader(http.StatusConflict)
			writeJSON(w, http.StatusConflict, map[string]any{"offset": cur})
			return
		}

		f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY, 0o644)
		if err != nil {
			http.Error(w, "open: "+err.Error(), http.StatusInternalServerError)
			return
		}
		defer f.Close()
		if _, err := f.Seek(start, io.SeekStart); err != nil {
			http.Error(w, "seek: "+err.Error(), http.StatusInternalServerError)
			return
		}
		n, err := io.Copy(f, io.LimitReader(r.Body, maxStagedUpload-start+1))
		if err != nil {
			http.Error(w, "write: "+err.Error(), http.StatusInternalServerError)
			return
		}
		newSize := start + n
		writeJSON(w, http.StatusOK, map[string]any{"offset": newSize, "path": path})

	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}
