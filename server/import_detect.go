package main

// Pre-import preview endpoints — POST /v1/admin/stage-extract and
// POST /v1/admin/detect-roles. Together they let the panel's import-mapper
// screen show an author exactly what a Template does and doesn't already
// handle for a project BEFORE committing to a full import: stage-extract
// unpacks a staged articy archive once (reused by every subsequent
// detect-roles call and by the eventual import-bundle call — extractArchive
// treats an already-extracted directory as a no-op, so the archive is never
// re-unpacked), detect-roles runs importer.DetectRoles against it.

import (
	"encoding/json"
	"io"
	"net/http"
	"os"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
)

func (s *server) handleStageExtract(w http.ResponseWriter, r *http.Request) {
	if s.adminToken == "" {
		http.Error(w, "admin disabled", http.StatusForbidden)
		return
	}
	if !bearerOK(r, s.adminToken) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	var body struct{ Path string }
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<16)).Decode(&body); err != nil {
		http.Error(w, "bad json: "+err.Error(), http.StatusBadRequest)
		return
	}
	if body.Path == "" || !s.importDirAllowed(body.Path) {
		http.Error(w, "path must live under the configured -import-root", http.StatusForbidden)
		return
	}
	scratch, err := os.MkdirTemp("", "lvn-detect-*")
	if err != nil {
		http.Error(w, "stage: "+err.Error(), http.StatusInternalServerError)
		return
	}
	dir, err := importer.ExtractArticyProject(body.Path, scratch)
	if err != nil {
		http.Error(w, "extract: "+err.Error(), http.StatusUnprocessableEntity)
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"dir": dir})
}

func (s *server) handleDetectRoles(w http.ResponseWriter, r *http.Request) {
	if s.adminToken == "" {
		http.Error(w, "admin disabled", http.StatusForbidden)
		return
	}
	if !bearerOK(r, s.adminToken) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	var body struct {
		Dir      string
		Template string
		Draft    json.RawMessage
	}
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<20)).Decode(&body); err != nil {
		http.Error(w, "bad json: "+err.Error(), http.StatusBadRequest)
		return
	}
	if body.Dir == "" {
		http.Error(w, "dir is required", http.StatusBadRequest)
		return
	}
	if !s.importDirAllowed(body.Dir) {
		http.Error(w, "dir must live under the configured -import-root", http.StatusForbidden)
		return
	}
	var tpl *importer.Template
	if len(body.Draft) > 0 {
		t, err := importer.ParseTemplateJSON(body.Draft)
		if err != nil {
			http.Error(w, "draft template: "+err.Error(), http.StatusBadRequest)
			return
		}
		tpl = t
	} else {
		t, err := s.resolveImportTemplate(body.Template)
		if err != nil {
			http.Error(w, "template: "+err.Error(), http.StatusBadRequest)
			return
		}
		tpl = t
	}
	rep, err := importer.DetectRoles(body.Dir, tpl)
	if err != nil {
		http.Error(w, "detect: "+err.Error(), http.StatusUnprocessableEntity)
		return
	}
	writeJSON(w, http.StatusOK, rep)
}
