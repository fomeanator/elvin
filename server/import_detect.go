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
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
)

// detectScratchRoot is where stage-extract unpacks archives for preview. It
// deliberately lives under -import-root when one is configured: detect-roles
// gates its dir through importDirAllowed, and the previous MkdirTemp("")
// placement only passed that gate by the ACCIDENT of the prod deploy setting
// TMPDIR under the import root — on any other deployment the mapper 403'd.
func (s *server) detectScratchRoot() string {
	if s.importRoot != "" {
		return filepath.Join(s.importRoot, ".detect-cache")
	}
	return filepath.Join(os.TempDir(), "lvn-detect-cache")
}

// sweepDetectScratch removes preview extractions older than maxAge. Each
// entry is a fully-unpacked articy project (100+ MB) and nothing else ever
// deletes them — before this sweep existed, every "Настроить роли" click
// leaked one until the disk filled.
func sweepDetectScratch(root string, maxAge time.Duration) {
	entries, err := os.ReadDir(root)
	if err != nil {
		return
	}
	cutoff := time.Now().Add(-maxAge)
	for _, e := range entries {
		info, err := e.Info()
		if err != nil {
			continue
		}
		if info.ModTime().Before(cutoff) {
			_ = os.RemoveAll(filepath.Join(root, e.Name()))
		}
	}
}

func (s *server) handleStageExtract(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	if !onlyMethod(w, r, http.MethodPost) {
		return
	}
	var body struct{ Path string }
	if err := json.NewDecoder(io.LimitReader(r.Body, bodySmall)).Decode(&body); err != nil {
		http.Error(w, "bad json: "+err.Error(), http.StatusBadRequest)
		return
	}
	if body.Path == "" || !s.importDirAllowed(body.Path) {
		http.Error(w, "path must live under the configured -import-root", http.StatusForbidden)
		return
	}
	root := s.detectScratchRoot()
	sweepDetectScratch(root, 6*time.Hour)
	// Key the scratch dir by (path, size, mtime) so re-opening the mapper on
	// the SAME staged archive reuses the extraction instead of unpacking a
	// 100MB+ archive again — and a re-staged (changed) archive gets a fresh key.
	key := body.Path
	if fi, err := os.Stat(body.Path); err == nil {
		key = fmt.Sprintf("%s|%d|%d", body.Path, fi.Size(), fi.ModTime().UnixNano())
	}
	sum := sha256.Sum256([]byte(key))
	scratch := filepath.Join(root, fmt.Sprintf("x%x", sum[:8]))
	if dir, err := findCachedProject(scratch); err == nil {
		_ = os.Chtimes(scratch, time.Now(), time.Now()) // keep warm entries alive
		writeJSON(w, http.StatusOK, map[string]string{"dir": dir})
		return
	}
	_ = os.RemoveAll(scratch) // half-extracted leftovers from a crashed attempt
	if err := os.MkdirAll(scratch, 0o755); err != nil {
		http.Error(w, "stage: "+err.Error(), http.StatusInternalServerError)
		return
	}
	dir, err := importer.ExtractArticyProject(body.Path, scratch)
	if err != nil {
		_ = os.RemoveAll(scratch)
		http.Error(w, "extract: "+err.Error(), http.StatusUnprocessableEntity)
		return
	}
	log.Printf("stage-extract: %s -> %s", body.Path, dir)
	writeJSON(w, http.StatusOK, map[string]string{"dir": dir})
}

// findCachedProject re-locates the articy project root inside an existing
// scratch extraction (same lookup ExtractArticyProject performs after
// unpacking). An empty/missing dir errors, which the caller treats as a miss.
func findCachedProject(scratch string) (string, error) {
	if fi, err := os.Stat(scratch); err != nil || !fi.IsDir() {
		return "", fmt.Errorf("no cache")
	}
	entries, err := os.ReadDir(scratch)
	if err != nil || len(entries) == 0 {
		return "", fmt.Errorf("empty cache")
	}
	return importer.ExtractArticyProject(scratch, scratch) // dir passthrough + project lookup
}

func (s *server) handleDetectRoles(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	if !onlyMethod(w, r, http.MethodPost) {
		return
	}
	var body struct {
		Dir      string
		Template string
		Draft    json.RawMessage
		// Vars is the staged -vars.xlsx of the SAME bundle, when the author
		// picked one. Without it the preview and the real import disagree —
		// see applyXlsxToPreview.
		Vars string
	}
	if err := json.NewDecoder(io.LimitReader(r.Body, bodyDoc)).Decode(&body); err != nil {
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
	var xlsx xlsxPreview
	if body.Vars != "" {
		if !s.importDirAllowed(body.Vars) {
			http.Error(w, "vars must live under the configured -import-root", http.StatusForbidden)
			return
		}
		xlsx = applyXlsxToPreview(tpl, body.Vars)
	}
	rep, err := importer.DetectRoles(body.Dir, tpl)
	if err != nil {
		http.Error(w, "detect: "+err.Error(), http.StatusUnprocessableEntity)
		return
	}
	writeJSON(w, http.StatusOK, detectResponse{DetectReport: rep, xlsxPreview: xlsx})
}

// detectResponse is DetectRoles' report plus the bits of the bundle the
// importer reads from the SPREADSHEET, which the preview otherwise cannot
// know about. The embedded pointer promotes every DetectReport field, so the
// wire shape stays a superset — old panel builds keep working.
type detectResponse struct {
	*importer.DetectReport
	xlsxPreview
}

// xlsxPreview is what the -vars.xlsx contributes to a preview that the articy
// project alone doesn't carry.
type xlsxPreview struct {
	// XlsxProtagonist is the roster tech-name RunBundle injects into the
	// staging cast for the protagonist label (bundle.go's ExtraCast). Its
	// presence means "the import WILL have art for her" — DetectRoles only
	// sees the articy roster and would warn that she stays invisible.
	XlsxProtagonist string `json:"xlsx_protagonist,omitempty"`
	// XlsxEmotionColors counts the legend entries folded into this preview,
	// so the author can tell an honest emotion_color_misses list from one
	// computed without the spreadsheet.
	XlsxEmotionColors int    `json:"xlsx_emotion_colors,omitempty"`
	XlsxError         string `json:"xlsx_error,omitempty"`
}

// applyXlsxToPreview folds the spreadsheet into the preview the same way
// RunBundle folds it into the real import (bundle.go): the cell-colour legend
// is authoritative over the template's, and the roster's protagonist row is
// what gives her art the articy project has no entity for.
//
// The legend is applied for real — it goes onto the template DetectRoles
// probes with, so emotion_color_misses stops listing colours the import
// resolves. The protagonist can only be REPORTED (DetectRoles builds its cast
// from adpd.Cast and takes no injection), so it rides back as xlsx_protagonist
// and the mapper reworks its warning around it. A parse failure is surfaced,
// never swallowed: a preview quietly computed without the sheet is exactly the
// discrepancy this closes.
func applyXlsxToPreview(tpl *importer.Template, path string) xlsxPreview {
	var out xlsxPreview
	xd, err := importer.ParseVarsXlsx(path)
	if err != nil {
		out.XlsxError = err.Error()
		return out
	}
	if len(xd.EmotionColors) > 0 {
		if tpl.EmotionColors == nil {
			tpl.EmotionColors = map[string]string{}
		}
		// Same normalization RunBundle applies: the legend's labels line up
		// with the character-art axis values only in lower case.
		for hex, emo := range xd.EmotionColors {
			tpl.EmotionColors[strings.ToUpper(hex)] = strings.ToLower(emo)
		}
		out.XlsxEmotionColors = len(xd.EmotionColors)
	}
	if xd.Protagonist != nil {
		out.XlsxProtagonist = strings.TrimSpace(xd.Protagonist.TechName)
	}
	return out
}
