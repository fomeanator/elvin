package main

// Bundle import endpoint — POST /v1/admin/import-bundle.
//
// The admin's one-shot "import a whole novel" flow: five multipart file parts —
//
//	articy       — the articy:draft project archive (.rar/.zip)
//	backgrounds  — backgrounds .zip
//	heroine      — the main-heroine wardrobe .zip
//	characters   — the character-art .zip
//	vars         — the variables .xlsx
//
// plus optional text fields id/name/subtitle. Each file part streams straight to
// a temp file (uploads are hundreds of MB — never buffered in memory), then
// importer.RunBundle extracts, imports the flow and layers the real art + vars on
// top. WriteToContentDir publishes it (scripts, art, manifest title+sprites) so
// the game/IDE see it on the next poll.

import (
	"encoding/json"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
)

// bundleFields maps a multipart field name → where its path lands in BundleInputs.
var bundleFileFields = map[string]func(*importer.BundleInputs, string){
	"articy":      func(b *importer.BundleInputs, p string) { b.ArticyArchive = p },
	"backgrounds": func(b *importer.BundleInputs, p string) { b.BackgroundsZip = p },
	"heroine":     func(b *importer.BundleInputs, p string) { b.HeroineZip = p },
	"characters":  func(b *importer.BundleInputs, p string) { b.CharactersZip = p },
	"vars":        func(b *importer.BundleInputs, p string) { b.VarsXlsx = p },
}

// resolveImportTemplate picks the novel-authoring Template for a bundle import:
// an empty name → the built-in default ("cold"); any other name → a
// <name>.json under <content>/import-templates (drop-in, no code change). Callers
// treat a resolve error as a 400 (a named-but-missing template is user error).
func (s *server) resolveImportTemplate(name string) (*importer.Template, error) {
	return importer.ResolveTemplate(name, filepath.Join(s.content, "import-templates"))
}

func (s *server) handleImportBundle(w http.ResponseWriter, r *http.Request) {
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
	// Local mode: the five files already sit on the server host — pass their PATHS
	// as JSON and skip the upload entirely (a ~1GB bundle over multipart is the slow
	// part). Gated by -import-root exactly like the single-file {dir} import.
	if strings.HasPrefix(r.Header.Get("Content-Type"), "application/json") {
		s.importBundleLocal(w, r)
		return
	}
	if !strings.HasPrefix(r.Header.Get("Content-Type"), "multipart/form-data") {
		http.Error(w, "send multipart/form-data (file parts) or application/json (host paths)", http.StatusUnsupportedMediaType)
		return
	}

	// Staging dir for the uploaded archives + extraction; wiped when we're done
	// (the real art has already been copied into the content root by then).
	stage, err := os.MkdirTemp("", "lvn-bundle-*")
	if err != nil {
		http.Error(w, "stage: "+err.Error(), http.StatusInternalServerError)
		return
	}
	defer os.RemoveAll(stage)

	var in importer.BundleInputs
	opt := importer.Options{Start: -1, AutoStage: true}
	templateName := "" // resolved to opt.Template after the parts are read

	mr, err := r.MultipartReader()
	if err != nil {
		http.Error(w, "multipart: "+err.Error(), http.StatusBadRequest)
		return
	}
	for {
		part, err := mr.NextPart()
		if err == io.EOF {
			break
		}
		if err != nil {
			http.Error(w, "part: "+err.Error(), http.StatusBadRequest)
			return
		}
		field := part.FormName()
		set, isFile := bundleFileFields[field]
		if !isFile {
			// small text field (id/name/subtitle) — read a bounded amount
			buf, _ := io.ReadAll(io.LimitReader(part, 1<<16))
			val := strings.TrimSpace(string(buf))
			switch field {
			case "id":
				if val != "" && !validID(val) {
					http.Error(w, "id must match [A-Za-z0-9_-]+", http.StatusBadRequest)
					return
				}
				opt.ID = val
			case "name":
				opt.Name = val
			case "subtitle":
				opt.Subtitle = val
			case "template":
				templateName = val
			}
			part.Close()
			continue
		}
		if part.FileName() == "" {
			part.Close()
			continue // empty optional file input
		}
		// Preserve the extension so extractArchive dispatches (.rar/.zip/.xlsx).
		ext := filepath.Ext(part.FileName())
		dst := filepath.Join(stage, field+ext)
		f, err := os.Create(dst)
		if err != nil {
			part.Close()
			http.Error(w, "save "+field+": "+err.Error(), http.StatusInternalServerError)
			return
		}
		_, cerr := io.Copy(f, part)
		f.Close()
		part.Close()
		if cerr != nil {
			http.Error(w, "save "+field+": "+cerr.Error(), http.StatusInternalServerError)
			return
		}
		set(&in, dst)
	}

	if in.ArticyArchive == "" {
		http.Error(w, "the 'articy' file part is required", http.StatusBadRequest)
		return
	}
	tpl, err := s.resolveImportTemplate(templateName)
	if err != nil {
		http.Error(w, "template: "+err.Error(), http.StatusBadRequest)
		return
	}
	opt.Template = tpl

	s.runBundleAndRespond(w, in, stage, opt)
}

// importBundleLocal handles the JSON path mode: {articy, backgrounds, heroine,
// characters, vars, id, name, subtitle} as host file paths. No upload, no copy —
// RunBundle reads them where they are. Each path is gated by -import-root.
func (s *server) importBundleLocal(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Articy, Backgrounds, Heroine, Characters, Vars string
		ID, Name, Subtitle                             string
		Template                                       string
	}
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<16)).Decode(&body); err != nil {
		http.Error(w, "bad json: "+err.Error(), http.StatusBadRequest)
		return
	}
	if body.Articy == "" {
		http.Error(w, "articy path is required", http.StatusBadRequest)
		return
	}
	if body.ID != "" && !validID(body.ID) {
		http.Error(w, "id must match [A-Za-z0-9_-]+", http.StatusBadRequest)
		return
	}
	in := importer.BundleInputs{
		ArticyArchive:  body.Articy,
		BackgroundsZip: body.Backgrounds,
		HeroineZip:     body.Heroine,
		CharactersZip:  body.Characters,
		VarsXlsx:       body.Vars,
	}
	for _, p := range []string{in.ArticyArchive, in.BackgroundsZip, in.HeroineZip, in.CharactersZip, in.VarsXlsx} {
		if p != "" && !s.importDirAllowed(p) {
			http.Error(w, "path must live under the configured -import-root: "+p, http.StatusForbidden)
			return
		}
	}
	tpl, err := s.resolveImportTemplate(body.Template)
	if err != nil {
		http.Error(w, "template: "+err.Error(), http.StatusBadRequest)
		return
	}
	stage, err := os.MkdirTemp("", "lvn-bundle-*")
	if err != nil {
		http.Error(w, "stage: "+err.Error(), http.StatusInternalServerError)
		return
	}
	defer os.RemoveAll(stage)
	opt := importer.Options{Start: -1, AutoStage: true, ID: body.ID, Name: body.Name, Subtitle: body.Subtitle, Template: tpl}
	s.runBundleAndRespond(w, in, stage, opt)
}

// runBundleAndRespond runs the bundle import and writes the JSON result — shared
// by the upload and local-path modes.
func (s *server) runBundleAndRespond(w http.ResponseWriter, in importer.BundleInputs, stage string, opt importer.Options) {
	res, err := importer.RunBundle(in, s.content, stage, opt)
	if err != nil {
		http.Error(w, "import: "+err.Error(), http.StatusUnprocessableEntity)
		return
	}
	if err := importer.WriteToContentDir(s.content, res); err != nil {
		http.Error(w, "write: "+err.Error(), http.StatusInternalServerError)
		return
	}
	// res.Scripts holds BOTH files of every chapter (.lvn + .lvns) — count
	// actual chapters, or the response reads "50" for a 25-chapter novel
	// (a live half-hour of debugging chased that phantom doubling).
	chapters := 0
	for _, s := range res.Scripts {
		if strings.HasSuffix(s.Rel, ".lvn") {
			chapters++
		}
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"id":         res.Title.ID,
		"name":       res.Title.Name,
		"script_url": "/content/" + res.ScriptRel,
		"chapters":   chapters,
		"sprites":    len(res.Sprites),
		"art_files":  len(res.Art),
		"bg_missing": len(res.MissingBg),
		"ops":        res.Stats,
		"warnings":   res.Warnings, // genuinely-incomplete source data (missing art, etc.)
	})
}
