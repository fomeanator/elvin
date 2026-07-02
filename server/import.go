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
	if !bearerOK(r, s.adminToken) {
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
		if !s.importDirAllowed(body.Dir) {
			http.Error(w, "dir must live under the configured import-root", http.StatusForbidden)
			return
		}
		if body.ID != "" && !validID(body.ID) {
			http.Error(w, "id must match [A-Za-z0-9_-]+", http.StatusBadRequest)
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
		if id := q.Get("id"); id != "" && !validID(id) {
			http.Error(w, "id must match [A-Za-z0-9_-]+", http.StatusBadRequest)
			return
		}
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

// importDirAllowed gates the JSON {dir} import mode. When -import-root is unset
// (default) any host path is accepted — the endpoint is already admin-gated and
// this mode exists to import a project already sitting on the server. When set,
// the requested dir must resolve inside that root, so a leaked/guessed admin
// token still can't turn the importer into an arbitrary-directory reader.
func (s *server) importDirAllowed(dir string) bool {
	if s.importRoot == "" {
		return true
	}
	root := filepath.Clean(s.importRoot)
	d := filepath.Clean(dir)
	return d == root || strings.HasPrefix(d, root+string(os.PathSeparator))
}

// archive extraction limits — defence against zip/rar bombs. A single upload
// can't exceed these no matter how it's compressed.
const (
	maxArchiveEntries = 50000     // entry-count cap (inode/FD exhaustion)
	maxArchiveEntry   = 512 << 20 // 512 MiB per uncompressed entry
	maxArchiveTotal   = 4 << 30   // 4 GiB total uncompressed across the archive
)

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
	if len(zr.File) > maxArchiveEntries {
		return fmt.Errorf("archive has too many entries (%d > %d)", len(zr.File), maxArchiveEntries)
	}
	var total int64
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
		// Cap per-entry (bomb) and running total — never trust the header's size.
		n, err := writeCapped(out, rc, maxArchiveEntry)
		rc.Close()
		if err != nil {
			return err
		}
		total += n
		if total > maxArchiveTotal {
			return fmt.Errorf("archive exceeds %d bytes uncompressed", maxArchiveTotal)
		}
	}
	return nil
}

// writeCapped streams src to a new file at path, failing if it would exceed max
// bytes. Reads one byte past the limit to detect overflow rather than trusting
// any declared size.
func writeCapped(path string, src io.Reader, max int64) (int64, error) {
	f, err := os.Create(path)
	if err != nil {
		return 0, err
	}
	n, err := io.Copy(f, io.LimitReader(src, max+1))
	cerr := f.Close()
	if err != nil {
		os.Remove(path)
		return n, err
	}
	if n > max {
		os.Remove(path)
		return n, fmt.Errorf("entry %s exceeds %d bytes", filepath.Base(path), max)
	}
	if cerr != nil {
		os.Remove(path)
		return n, cerr
	}
	return n, nil
}

// unrarInto extracts a .rar archive into dst. RAR is proprietary and absent from
// the Go stdlib, so this uses the pure-Go rardecode reader — same path-sanitising
// and dir-first handling as unzipInto.
func unrarInto(dst string, data []byte) error {
	rr, err := rardecode.NewReader(bytes.NewReader(data), "")
	if err != nil {
		return err
	}
	var total int64
	entries := 0
	for {
		hdr, err := rr.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			return err
		}
		entries++
		if entries > maxArchiveEntries {
			return fmt.Errorf("archive has too many entries (> %d)", maxArchiveEntries)
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
		n, err := writeCapped(out, rr, maxArchiveEntry)
		if err != nil {
			return err
		}
		total += n
		if total > maxArchiveTotal {
			return fmt.Errorf("archive exceeds %d bytes uncompressed", maxArchiveTotal)
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
