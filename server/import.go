package main

import (
	"archive/zip"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"

	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/importer"
	"github.com/nwaples/rardecode"
)

// handleImportArticy is the server side of the IDE's one-click "Import articy"
// button. It takes an extracted .adpd project, runs the full pipeline (compile →
// auto-stage → resolve+matte art → manifest title) and writes the result straight
// into the content root, so the title is live for the game and IDE on their next
// manifest poll. No restart, no manual asset uploads.
//
// Two ways to hand it the project (both admin-gated):
//
//	POST /v1/admin/import-articy            (Content-Type: application/json)
//	     {"dir":"/abs/path","id":"soviet","name":"…","start":-1,"max":0}
//	     — import a project directory already on the server host (fast; no upload).
//
//	POST /v1/admin/import-articy?id=…&name=…   (multipart/form-data)
//	     either a single "zip" file part (a zipped .adpd folder), or many file
//	     parts whose names are the project-relative paths (a browser folder pick).
//	     — reconstructed into a temp dir, then imported.
func (s *server) handleImportArticy(w http.ResponseWriter, r *http.Request) {
	if s.adminToken == "" {
		http.Error(w, "admin disabled", http.StatusForbidden)
		return
	}
	if r.Header.Get("Authorization") != "Bearer "+s.adminToken {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	opt := importer.Options{Start: -1, AutoStage: true}
	var projectDir string
	var cleanup func()
	defer func() {
		if cleanup != nil {
			cleanup()
		}
	}()

	ct := r.Header.Get("Content-Type")
	switch {
	case strings.HasPrefix(ct, "application/json"):
		var body struct {
			Dir, ID, Name, Subtitle string
			Start, Max              int
			Localize                bool
		}
		if err := json.NewDecoder(io.LimitReader(r.Body, 1<<20)).Decode(&body); err != nil {
			http.Error(w, "bad json: "+err.Error(), http.StatusBadRequest)
			return
		}
		if body.Dir == "" {
			http.Error(w, "dir is required", http.StatusBadRequest)
			return
		}
		projectDir = body.Dir
		opt.ID, opt.Name, opt.Subtitle = body.ID, body.Name, body.Subtitle
		if body.Start != 0 {
			opt.Start = body.Start
		}
		opt.Max = body.Max
		opt.Localize = body.Localize

	case strings.HasPrefix(ct, "multipart/form-data"):
		dir, clean, err := reconstructUpload(r)
		if err != nil {
			http.Error(w, "upload: "+err.Error(), http.StatusBadRequest)
			return
		}
		projectDir, cleanup = dir, clean
		q := r.URL.Query()
		opt.ID, opt.Name, opt.Subtitle = q.Get("id"), q.Get("name"), q.Get("subtitle")
		opt.Localize = q.Get("localize") == "1" || q.Get("localize") == "true"

	default:
		http.Error(w, "send application/json {dir} or multipart/form-data", http.StatusUnsupportedMediaType)
		return
	}

	res, err := importer.Run(projectDir, opt)
	if err != nil {
		http.Error(w, "import: "+err.Error(), http.StatusUnprocessableEntity)
		return
	}
	if err := importer.WriteToContentDir(s.content, res); err != nil {
		http.Error(w, "write: "+err.Error(), http.StatusInternalServerError)
		return
	}

	writeJSON(w, http.StatusOK, map[string]any{
		"id":         res.Title.ID,
		"name":       res.Title.Name,
		"script_url": "/content/" + res.ScriptRel,
		"cover_url":  res.Title.CoverURL,
		"ops":        res.Stats,
		"art_files":  len(res.Art),
		"bg_missing": len(res.MissingBg),
		"lang":       res.Lang,
		"strings":    len(res.Catalog),
	})
}

// reconstructUpload rebuilds an uploaded project into a temp dir. It accepts a
// single zipped folder (part name "zip") or many parts whose filenames are the
// project-relative paths (a browser directory pick). Returns the dir and a
// cleanup func.
func reconstructUpload(r *http.Request) (string, func(), error) {
	tmp, err := os.MkdirTemp("", "lvn-import-*")
	if err != nil {
		return "", nil, err
	}
	clean := func() { os.RemoveAll(tmp) }

	mr, err := r.MultipartReader()
	if err != nil {
		clean()
		return "", nil, err
	}
	wrote := 0
	for {
		part, err := mr.NextPart()
		if err == io.EOF {
			break
		}
		if err != nil {
			clean()
			return "", nil, err
		}
		name := part.FileName()
		if name == "" { // a plain form field (id/name/…) — skip
			part.Close()
			continue
		}
		data, err := io.ReadAll(io.LimitReader(part, 256<<20))
		part.Close()
		if err != nil {
			clean()
			return "", nil, err
		}
		lower := strings.ToLower(name)
		switch {
		case part.FormName() == "zip" || strings.HasSuffix(lower, ".zip"):
			if err := unzipInto(tmp, data); err != nil {
				clean()
				return "", nil, err
			}
			wrote++
			continue
		case part.FormName() == "rar" || strings.HasSuffix(lower, ".rar"):
			if err := unrarInto(tmp, data); err != nil {
				clean()
				return "", nil, err
			}
			wrote++
			continue
		}
		// A path-bearing part: write it at its relative location (sanitised).
		rel := filepath.Clean("/" + filepath.ToSlash(name))[1:]
		if rel == "" || strings.Contains(rel, "..") {
			continue
		}
		dst := filepath.Join(tmp, rel)
		if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
			clean()
			return "", nil, err
		}
		if err := os.WriteFile(dst, data, 0o644); err != nil {
			clean()
			return "", nil, err
		}
		wrote++
	}
	if wrote == 0 {
		clean()
		return "", nil, fmt.Errorf("no files in upload")
	}
	return findProjectRoot(tmp), clean, nil
}

func unzipInto(dst string, data []byte) error {
	zr, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		return err
	}
	for _, f := range zr.File {
		rel := filepath.Clean("/" + filepath.ToSlash(f.Name))[1:]
		if rel == "" || strings.Contains(rel, "..") {
			continue
		}
		out := filepath.Join(dst, rel)
		if f.FileInfo().IsDir() {
			os.MkdirAll(out, 0o755)
			continue
		}
		if err := os.MkdirAll(filepath.Dir(out), 0o755); err != nil {
			return err
		}
		rc, err := f.Open()
		if err != nil {
			return err
		}
		b, err := io.ReadAll(rc)
		rc.Close()
		if err != nil {
			return err
		}
		if err := os.WriteFile(out, b, 0o644); err != nil {
			return err
		}
	}
	return nil
}

// unrarInto extracts a .rar archive into dst. RAR is proprietary and absent from
// the Go stdlib, so this uses the pure-Go rardecode reader — same path-sanitising
// and dir-first handling as unzipInto.
func unrarInto(dst string, data []byte) error {
	rr, err := rardecode.NewReader(bytes.NewReader(data), "")
	if err != nil {
		return err
	}
	for {
		hdr, err := rr.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			return err
		}
		rel := filepath.Clean("/" + filepath.ToSlash(hdr.Name))[1:]
		if rel == "" || strings.Contains(rel, "..") {
			continue
		}
		out := filepath.Join(dst, rel)
		if hdr.IsDir {
			os.MkdirAll(out, 0o755)
			continue
		}
		if err := os.MkdirAll(filepath.Dir(out), 0o755); err != nil {
			return err
		}
		f, err := os.Create(out)
		if err != nil {
			return err
		}
		_, err = io.Copy(f, io.LimitReader(rr, 512<<20))
		f.Close()
		if err != nil {
			return err
		}
	}
	return nil
}

// findProjectRoot descends through single-child wrapper directories (e.g. a zip
// that contains one top folder) so importer.Run sees the real project root.
func findProjectRoot(dir string) string {
	for i := 0; i < 4; i++ {
		entries, err := os.ReadDir(dir)
		if err != nil {
			return dir
		}
		var sub []os.DirEntry
		for _, e := range entries {
			if !strings.HasPrefix(e.Name(), ".") {
				sub = append(sub, e)
			}
		}
		if len(sub) == 1 && sub[0].IsDir() {
			dir = filepath.Join(dir, sub[0].Name())
			continue
		}
		break
	}
	return dir
}
