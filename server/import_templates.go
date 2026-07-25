package main

// Import-Template CRUD — GET /v1/admin/import-templates (list),
// GET/PUT/DELETE /v1/admin/import-templates/<name>.
//
// A Template captures a novel's authoring conventions (narrator/protagonist
// role labels, scene-marker pattern, wardrobe layout, speaker aliases…) so
// the articy importer isn't hardcoded to any one project's vocabulary — see
// tools/lvnconv/importer/template.go. Before this file the only way to add
// or edit one was hand-editing a JSON file on the server's disk; this lets
// the panel's import-mapper screen do it interactively and versions every
// edit through the SAME editorial-history machinery as manifest.json
// (snapshotHistory/atomicWrite/s.writeMu — see admin.go), not a bespoke one.
//
// Deliberately on *AdminService (not *server, unlike import-bundle/detect-
// roles/stage-extract): those three read-only/write-once import endpoints
// use the simpler s.adminToken+bearerOK pattern because a bundle import has
// no "previous version" to roll back to. A Template is a small, hand-edited,
// long-lived config an author iterates on — it deserves the same rollback
// safety net as manifest.json, which AdminService already provides.

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
)

func (s *AdminService) templatesDir() string {
	return filepath.Join(s.content, "import-templates")
}

// GET /v1/admin/import-templates — every available template name. The
// built-in default ("cold") is always listed first even when no cold.json
// file exists on disk (ResolveTemplate falls back to DefaultTemplate()).
func (s *AdminService) handleImportTemplates(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	if r.Method != http.MethodGet {
		http.Error(w, "GET only", http.StatusMethodNotAllowed)
		return
	}
	names := map[string]bool{"cold": true}
	entries, _ := os.ReadDir(s.templatesDir())
	for _, e := range entries {
		if !e.IsDir() && strings.HasSuffix(e.Name(), ".json") {
			names[strings.TrimSuffix(e.Name(), ".json")] = true
		}
	}
	out := make([]string, 0, len(names))
	for n := range names {
		out = append(out, n)
	}
	sort.Strings(out)
	writeJSON(w, http.StatusOK, map[string]any{"templates": out})
}

// GET/PUT/DELETE /v1/admin/import-templates/<name>.
func (s *AdminService) handleImportTemplateDetail(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	name := strings.TrimPrefix(r.URL.Path, "/v1/admin/import-templates/")
	if name == "" || !validID(name) {
		http.Error(w, "template name must match [A-Za-z0-9_-]+", http.StatusBadRequest)
		return
	}
	isDefault := name == "cold" || name == "default"
	path := filepath.Join(s.templatesDir(), name+".json")
	rel := "import-templates/" + name + ".json"

	switch r.Method {
	case http.MethodGet:
		data, err := os.ReadFile(path)
		if err != nil {
			if !isDefault {
				http.Error(w, "no such template", http.StatusNotFound)
				return
			}
			data, _ = json.MarshalIndent(importer.DefaultTemplate(), "", "  ")
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(data)

	case http.MethodPut:
		var doc json.RawMessage
		if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 1<<20)).Decode(&doc); err != nil {
			http.Error(w, "invalid JSON: "+err.Error(), http.StatusBadRequest)
			return
		}
		// Validate WITHOUT persisting the resolved form — a partial overlay
		// (e.g. only speaker_aliases) must stay partial on disk so it keeps
		// inheriting DefaultTemplate() for every field it doesn't state.
		if _, err := importer.ParseTemplateJSON(doc); err != nil {
			http.Error(w, "invalid template: "+err.Error(), http.StatusBadRequest)
			return
		}
		var pretty any
		_ = json.Unmarshal(doc, &pretty)
		data, _ := json.MarshalIndent(pretty, "", "  ")
		if err := os.MkdirAll(s.templatesDir(), 0o755); err != nil {
			http.Error(w, "mkdir: "+err.Error(), http.StatusInternalServerError)
			return
		}
		s.writeMu.Lock()
		snapshotHistory(s.content, rel)
		err := atomicWrite(path, data, 0o644)
		s.writeMu.Unlock()
		if err != nil {
			http.Error(w, "write failed: "+err.Error(), http.StatusInternalServerError)
			return
		}
		writeJSON(w, http.StatusOK, map[string]bool{"saved": true})

	case http.MethodDelete:
		if isDefault {
			http.Error(w, "the built-in default isn't file-backed", http.StatusBadRequest)
			return
		}
		_ = os.Remove(path)
		writeJSON(w, http.StatusOK, map[string]bool{"deleted": true})

	default:
		http.Error(w, "GET, PUT or DELETE", http.StatusMethodNotAllowed)
	}
}
