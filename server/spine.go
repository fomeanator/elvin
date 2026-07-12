package main

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
)

// POST /v1/admin/spine — register a Spine character from its editor export:
// multipart form with `id` (+ optional `name`, `auto`, `scale`) and the three
// export files (fields json / atlas / texture). Files land under
// content/spine/<id>/ and the entity is spliced into manifest.sprites as
//
//	{ "kind": "spine", "spine": { json, atlas, texture, scale, auto } }
//
// — after which `actor id=<id>` plays it (the runtime needs the optional
// spine-unity integration installed). The ELVIN IDE's Characters panel is
// the intended caller.
func (s *server) handleAdminSpine(w http.ResponseWriter, r *http.Request) {
	if s.adminToken == "" {
		http.Error(w, "admin api disabled (start with -admin-token)", http.StatusForbidden)
		return
	}
	if !bearerOK(r, s.adminToken) {
		http.Error(w, "admin token required", http.StatusUnauthorized)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	if err := r.ParseMultipartForm(64 << 20); err != nil {
		http.Error(w, "multipart form expected: "+err.Error(), http.StatusBadRequest)
		return
	}

	id := sanitizeSpineID(r.FormValue("id"))
	if id == "" {
		http.Error(w, "id required ([a-z0-9-_])", http.StatusBadRequest)
		return
	}
	name := strings.TrimSpace(r.FormValue("name"))
	auto := strings.TrimSpace(r.FormValue("auto"))
	scale := strings.TrimSpace(r.FormValue("scale"))

	dir := filepath.Join(s.content, "spine", id)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}

	// Save each export file under a stable name; the atlas page image keeps
	// its ORIGINAL filename — the atlas text references it by that name.
	save := func(field, forcedName string) (string, error) {
		f, hdr, err := r.FormFile(field)
		if err != nil {
			return "", fmt.Errorf("file field %q missing", field)
		}
		defer f.Close()
		fname := forcedName
		if fname == "" {
			fname = filepath.Base(hdr.Filename)
		}
		dst, err := os.Create(filepath.Join(dir, fname))
		if err != nil {
			return "", err
		}
		defer dst.Close()
		if _, err := io.Copy(dst, f); err != nil {
			return "", err
		}
		return "/content/spine/" + id + "/" + fname, nil
	}

	jsonURL, err := save("json", id+".json")
	if err == nil {
		var probe struct {
			Skeleton struct {
				Spine string `json:"spine"`
			} `json:"skeleton"`
		}
		raw, _ := os.ReadFile(filepath.Join(dir, id+".json"))
		if jerr := json.Unmarshal(raw, &probe); jerr != nil || probe.Skeleton.Spine == "" {
			err = fmt.Errorf("json doesn't look like a Spine skeleton export")
		}
	}
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	atlasURL, err := save("atlas", id+".atlas.txt")
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	texURL, err := save("texture", "") // keep the original name — the atlas references it
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}

	entity := map[string]any{
		"kind": "spine",
		"spine": map[string]any{
			"json":    jsonURL,
			"atlas":   atlasURL,
			"texture": texURL,
			"scale":   1.0,
		},
	}
	if name != "" {
		entity["name"] = name
	}
	sp := entity["spine"].(map[string]any)
	if auto != "" {
		sp["auto"] = auto
	}
	if scale != "" {
		var f float64
		if _, perr := fmt.Sscanf(scale, "%g", &f); perr == nil && f > 0 {
			sp["scale"] = f
		}
	}

	if err := importer.MergeSpritesIntoManifest(
		filepath.Join(s.content, "manifest.json"), map[string]any{id: entity}); err != nil {
		http.Error(w, "manifest: "+err.Error(), http.StatusInternalServerError)
		return
	}

	writeJSON(w, http.StatusOK, map[string]any{
		"id": id, "json": jsonURL, "atlas": atlasURL, "texture": texURL,
	})
}

var spineIDRe = regexp.MustCompile(`[^a-z0-9\-_]+`)

func sanitizeSpineID(s string) string {
	return spineIDRe.ReplaceAllString(strings.ToLower(strings.TrimSpace(s)), "")
}
