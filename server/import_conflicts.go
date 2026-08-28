package main

// import_conflicts.go — the admin surface for finishing a re-import.
//
// The three-way merge (importer/baseline.go) refuses to overwrite a file that
// the author edited AND the exporter regenerated differently: the author's file
// stays, the new version is parked beside it as `<file>.incoming`, and the path
// is reported. That is deliberately only half a workflow — until something can
// COMMIT one of the two versions, every conflict is permanent, the parked files
// pile up in content/, and the next import re-raises all of them.
//
// This is the other half:
//
//	GET  /v1/admin/import-conflicts           what is parked, and the diff
//	POST /v1/admin/import-conflicts/resolve   commit "mine" or "incoming"
//
// Three properties are non-negotiable, and each one is a line in resolve():
//
//   - Reversible. A resolution is an ordinary editorial write: it takes the
//     same s.writeMu, snapshots the previous bytes into .history and lands via
//     atomicWrite, so the panel's rollback undoes it like any other save. That
//     is also why choosing "mine" rewrites the file with its own bytes instead
//     of just deleting the parked version — one cheap write buys an undo point
//     and a baseline whose size/mtime match the file exactly.
//   - Validated. Installing an incoming .lvn goes through the SAME gate as a
//     Studio save (lvnguard.go): a structurally broken chapter cannot be
//     resolved into content/, which is served to players with no-store.
//   - Final. The chosen bytes are stamped into the import baseline, so the next
//     import compares against the decision instead of re-raising the conflict.
//     The baseline format lives in the importer package and is updated through
//     its helper — duplicating that JSON here would rot the day it changes.

import (
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
)

// conflictAPI borrows exactly two things: the content root + .lvn gate from the
// main server, and the editorial write lock from the admin service (the same
// mutex every other panel write takes — a resolution racing a manifest save
// must not lose a .history revision).
type conflictAPI struct {
	srv   *server
	admin *AdminService
}

// fallbackWriteMu covers the case where no AdminService was wired in (tests,
// or a server built without the panel API). Serialising against nothing is
// still better than not serialising at all.
var fallbackWriteMu sync.Mutex

func (a *conflictAPI) writeLock() *sync.Mutex {
	if a.admin != nil {
		return &a.admin.writeMu
	}
	return &fallbackWriteMu
}

// routesImportConflicts registers the pair. Called from main.go with the admin
// service so both share one write lock.
func (s *server) routesImportConflicts(mux *http.ServeMux, admin *AdminService) {
	api := &conflictAPI{srv: s, admin: admin}
	mux.HandleFunc("/v1/admin/import-conflicts", api.handleList)
	mux.HandleFunc("/v1/admin/import-conflicts/resolve", api.handleResolve)
}

// diff budgets: a listing shows enough to recognise the change, a single
// conflict shows enough to decide on it. Both are capped because these bodies
// go to a browser.
const (
	listDiffLines = 400
	oneDiffLines  = 4000
	maxDiffLines  = 20000
)

func (a *conflictAPI) authed(w http.ResponseWriter, r *http.Request) bool {
	return adminAllowed(w, r, a.srv.adminToken)
}

// conflictRow is one listed conflict: the importer's metadata plus the rendered
// diff (text) or the reason there isn't one (binary art, missing side).
type conflictRow struct {
	importer.Conflict
	Diff     string `json:"diff,omitempty"`
	DiffNote string `json:"diff_note,omitempty"`
	// Undoable says whether resolving this path leaves a .history entry.
	// Binary art is excluded from editorial history by design (admin.go), so
	// for art the answer is no and the caller should say so before overwriting.
	Undoable bool `json:"undoable"`
}

// GET /v1/admin/import-conflicts[?rel=<path>][&diff=0][&max_lines=N]
//
// Lists every parked version under the content root, newest decision first is
// meaningless here so it is sorted by path. `rel` narrows to one conflict (404
// if it isn't parked) and raises the diff budget; `diff=0` skips diffing
// entirely, for a cheap "is anything waiting?" poll.
func (a *conflictAPI) handleList(w http.ResponseWriter, r *http.Request) {
	if !a.authed(w, r) {
		return
	}
	if !onlyMethod(w, r, http.MethodGet) {
		return
	}
	conflicts, err := importer.ScanConflicts(a.srv.content)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	q := r.URL.Query()
	one := strings.TrimSuffix(q.Get("rel"), importer.IncomingSuffix)
	budget := listDiffLines
	if one != "" {
		budget = oneDiffLines
		filtered := conflicts[:0]
		for _, c := range conflicts {
			if c.Rel == one {
				filtered = append(filtered, c)
			}
		}
		conflicts = filtered
		if len(conflicts) == 0 {
			http.Error(w, "no conflict parked for this path", http.StatusNotFound)
			return
		}
	}
	// n <= 0 keeps the default on purpose: UnifiedDiff reads 0 as "unlimited",
	// so honouring ?max_lines=0 would answer a request to shrink the body with
	// an unbounded one. Use diff=0 to drop diffs entirely.
	budget = qtyParam(r, "max_lines", budget, maxDiffLines)
	wantDiff := q.Get("diff") != "0"

	rows := make([]conflictRow, 0, len(conflicts))
	for _, c := range conflicts {
		row := conflictRow{Conflict: c, Undoable: historyEligible(c.Rel)}
		if wantDiff {
			row.Diff, row.DiffNote = importer.ConflictDiff(a.srv.content, c, budget)
		}
		rows = append(rows, row)
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"count":     len(rows),
		"conflicts": rows,
	})
}

// POST /v1/admin/import-conflicts/resolve
//
//	{"rel":"scripts/mystory/ch1.lvn","choice":"mine"|"incoming"[,"title":"mystory"]}
//
// 200 → resolved (bytes written, baselines updated, any .lvn warnings)
// 400 → bad JSON / bad path / bad choice
// 404 → nothing parked at that path
// 422 → the chosen version is not a valid .lvn (nothing on disk moved)
// 500 → the write itself failed
//
// `title` is an optional hint for the one case the baseline cannot be inferred
// from: a conflict on a path no import ever tracked, in a content root holding
// several titles. The response says when it was needed.
func (a *conflictAPI) handleResolve(w http.ResponseWriter, r *http.Request) {
	if !a.authed(w, r) {
		return
	}
	if !onlyMethod(w, r, http.MethodPost) {
		return
	}
	var req struct {
		Rel    string `json:"rel"`
		Choice string `json:"choice"`
		Title  string `json:"title"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, bodySmall)).Decode(&req); err != nil {
		http.Error(w, "invalid JSON: "+err.Error(), http.StatusBadRequest)
		return
	}
	// Defence in depth. importer.ResolveConflict validates the path too (and is
	// the check that actually protects the CLI), but a write endpoint that can
	// be reached with a leaked admin token earns its own explicit reject —
	// same posture as handleAdminAsset.
	rel := strings.TrimSuffix(req.Rel, importer.IncomingSuffix)
	if rel == "" || strings.Contains(rel, "..") || strings.HasPrefix(rel, "/") ||
		strings.ContainsAny(rel, `\`) {
		http.Error(w, "bad path", http.StatusBadRequest)
		return
	}
	if req.Title != "" && !validID(req.Title) {
		http.Error(w, "bad title id", http.StatusBadRequest)
		return
	}

	// The WHOLE resolution runs under the editorial write lock, not just its
	// final write: it reads the author's file, validates it and only then puts
	// bytes back, and a Studio save landing in that window would be silently
	// undone by the rewrite. Taking the lock here (and NOT again inside Write —
	// sync.Mutex does not recurse) makes read → validate → write → unpark →
	// baseline one critical section against every other admin write.
	mu := a.writeLock()
	mu.Lock()
	defer mu.Unlock()

	var warnings []string
	res, err := importer.ResolveConflict(a.srv.content, rel, req.Choice, importer.ResolveOptions{
		Title: req.Title,
		// THE gate, before anything moves: a rejected resolution leaves the
		// author's file, the parked version and the baseline exactly as they
		// were. This runs for "mine" as well — resolving a conflict is the
		// moment those bytes are blessed as the shipped version, and blessing
		// a chapter the runtime cannot parse is the failure lvnguard exists to
		// stop, whichever side it came from.
		Validate: func(rel string, data []byte) error {
			if !isLvnPath(rel) {
				return nil
			}
			f := a.srv.checkLvn(rel, data)
			warnings = f.Warnings
			if f.blocked() {
				return errors.New(strings.Join(f.Errors, "; "))
			}
			return nil
		},
		// The editorial write path, unchanged: a history snapshot then an
		// atomic rename — already inside the lock taken above.
		Write: func(rel string, data []byte) error {
			dst := filepath.Join(a.srv.content, filepath.FromSlash(rel))
			if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
				return err
			}
			snapshotHistory(a.srv.content, rel)
			return atomicWrite(dst, data, 0o644)
		},
	})
	if err != nil {
		a.failResolve(w, rel, req.Choice, warnings, err)
		return
	}
	log.Printf("import conflict resolved: %s → %s (%d bytes, baselines %v)",
		res.Rel, res.Choice, res.Bytes, res.Baselines)
	writeJSON(w, http.StatusOK, map[string]any{
		"rel": res.Rel, "choice": res.Choice, "bytes": res.Bytes,
		"baselines": res.Baselines, "note": res.Note,
		"warnings": orEmpty(warnings),
		"undoable": historyEligible(res.Rel),
		"resolved": true,
	})
}

// failResolve maps the importer's sentinels onto HTTP. A validation failure
// carries the parse errors back so the panel can show the author WHY the
// version it offered cannot ship.
func (a *conflictAPI) failResolve(w http.ResponseWriter, rel, choice string, warnings []string, err error) {
	switch {
	case errors.Is(err, importer.ErrInvalidContent):
		log.Printf("import conflict resolve rejected %s (%s): %v", rel, choice, err)
		writeJSON(w, http.StatusUnprocessableEntity, map[string]any{
			"rel": rel, "choice": choice, "rejected": true,
			"errors":   []string{strings.TrimPrefix(err.Error(), importer.ErrInvalidContent.Error()+": ")},
			"warnings": orEmpty(warnings),
		})
	case errors.Is(err, importer.ErrNoConflict):
		http.Error(w, err.Error(), http.StatusNotFound)
	case errors.Is(err, importer.ErrBadPath), errors.Is(err, importer.ErrBadChoice),
		errors.Is(err, importer.ErrMineMissing):
		http.Error(w, err.Error(), http.StatusBadRequest)
	default:
		log.Printf("import conflict resolve failed %s (%s): %v", rel, choice, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
	}
}
