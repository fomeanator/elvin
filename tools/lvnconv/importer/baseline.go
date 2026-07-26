package importer

// Re-import without clobbering hand edits.
//
// The authoring loop is a three-way merge and nobody called it that: articy is
// upstream, the import generates files, and then a human edits some of them
// inside the engine (a line fixed in Studio, a background swapped for the HD
// one). A second import used to overwrite everything, and the only reason it
// was survivable is that nobody looked afterwards.
//
// A three-way merge needs a BASE — what the previous import produced. That is
// the whole of this file: after each import we record a hash per written file,
// and the next import compares three states.
//
//	base == disk, base != incoming  → the human did not touch it   → update
//	base == disk, base == incoming  → nothing changed anywhere      → skip
//	base != disk, base == incoming  → only the human changed it     → keep theirs
//	base != disk, base != incoming  → both changed it               → CONFLICT
//
// A conflict is never resolved automatically. The incoming version is written
// beside the file as `<name>.incoming` and reported, so the author can see both
// and choose. Silently picking a side is the failure this exists to prevent.
//
// A file with no baseline entry that already exists on disk is treated as a
// hand edit too: we did not put it there, so we do not own it.

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"sort"
	"time"
)

// baselineDir is where per-title baselines live inside the content root.
// It is bookkeeping, not content: the server's static handler blocks this
// prefix explicitly (a leading dot does NOT hide anything from a FileServer —
// verified the hard way when this directory came back 200 over HTTP).
const baselineDir = ".lvn-import"

type baselineEntry struct {
	SHA   string `json:"sha"`
	Size  int64  `json:"size"`
	MTime int64  `json:"mtime"` // unix nanos, the git-index trick: skip re-hashing
}

// ImportBaseline records what the previous import wrote for one title.
type ImportBaseline struct {
	Title      string                   `json:"title"`
	ImportedAt string                   `json:"imported_at"`
	Files      map[string]baselineEntry `json:"files"`
}

// FileStatus is what happened to one file during a write.
type FileStatus string

const (
	StatusNew       FileStatus = "new"        // did not exist
	StatusUpdated   FileStatus = "updated"    // regenerated over an untouched file
	StatusUnchanged FileStatus = "unchanged"  // byte-identical, not rewritten
	StatusKeptLocal FileStatus = "kept_local" // hand-edited, upstream unchanged — left alone
	StatusConflict  FileStatus = "conflict"   // hand-edited AND regenerated differently
)

// FileOutcome is one line of the write report.
type FileOutcome struct {
	Rel      string     `json:"rel"`
	Status   FileStatus `json:"status"`
	Incoming string     `json:"incoming,omitempty"` // where the rejected version was parked
}

// WriteReport is what an import did to the content root. Conflicts are the
// part a caller must surface: they mean the author's work and the new export
// disagree, and nothing was overwritten.
type WriteReport struct {
	Files     []FileOutcome `json:"files"`
	Conflicts []string      `json:"conflicts,omitempty"`
}

// Count returns how many files landed in each status, for a one-line summary.
func (r *WriteReport) Count(s FileStatus) int {
	n := 0
	for _, f := range r.Files {
		if f.Status == s {
			n++
		}
	}
	return n
}

func baselinePath(contentDir, titleID string) string {
	name := titleID
	if name == "" {
		name = "_untitled"
	}
	return filepath.Join(contentDir, baselineDir, name+".json")
}

// loadBaseline reads the previous import's record. A missing or unreadable file
// is not an error: the first import simply has no base, and every file it finds
// on disk is then treated as somebody else's.
func loadBaseline(contentDir, titleID string) *ImportBaseline {
	b := &ImportBaseline{Title: titleID, Files: map[string]baselineEntry{}}
	data, err := os.ReadFile(baselinePath(contentDir, titleID))
	if err != nil {
		return b
	}
	var got ImportBaseline
	if json.Unmarshal(data, &got) != nil || got.Files == nil {
		return b
	}
	got.Title = titleID
	return &got
}

func (b *ImportBaseline) save(contentDir string) error {
	b.ImportedAt = time.Now().UTC().Format(time.RFC3339)
	p := baselinePath(contentDir, b.Title)
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		return err
	}
	data, err := json.MarshalIndent(b, "", "  ")
	if err != nil {
		return err
	}
	return atomicWrite(p, data, 0o644)
}

func sha256Hex(data []byte) string {
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:])
}

// diskMatchesBaseline reports whether the file on disk is still what the last
// import wrote. Size+mtime are checked first and the content is hashed only
// when they moved — a full re-hash of an art directory would otherwise cost
// more than the import itself.
func diskMatchesBaseline(dst string, e baselineEntry) (bool, bool) {
	st, err := os.Stat(dst)
	if err != nil {
		return false, false // missing on disk
	}
	if st.Size() == e.Size && st.ModTime().UnixNano() == e.MTime {
		return true, true
	}
	data, err := os.ReadFile(dst)
	if err != nil {
		return false, true
	}
	return sha256Hex(data) == e.SHA, true
}

// classify decides what to do with one incoming file. It never writes.
func classify(dst string, incoming []byte, base *ImportBaseline, rel string) FileStatus {
	inSHA := sha256Hex(incoming)
	e, tracked := base.Files[rel]

	if !tracked {
		// We have no record of this path. If it exists, somebody else owns it.
		cur, err := os.ReadFile(dst)
		if err != nil {
			return StatusNew
		}
		if sha256Hex(cur) == inSHA {
			return StatusUnchanged
		}
		return StatusConflict
	}

	same, exists := diskMatchesBaseline(dst, e)
	if !exists {
		return StatusNew // the human deleted it; putting it back is not a conflict
	}
	if same {
		if inSHA == e.SHA {
			return StatusUnchanged
		}
		return StatusUpdated
	}
	// Locally edited.
	if inSHA == e.SHA {
		return StatusKeptLocal // upstream produced the same bytes as last time
	}
	return StatusConflict
}

// recordWritten stamps a file into the baseline after it has been written.
func (b *ImportBaseline) recordWritten(dst, rel string, data []byte) {
	e := baselineEntry{SHA: sha256Hex(data), Size: int64(len(data))}
	if st, err := os.Stat(dst); err == nil {
		e.Size, e.MTime = st.Size(), st.ModTime().UnixNano()
	}
	b.Files[rel] = e
}

// sortOutcomes keeps the report stable: conflicts first (they need a human),
// then the rest alphabetically.
func sortOutcomes(out []FileOutcome) {
	rank := map[FileStatus]int{StatusConflict: 0, StatusKeptLocal: 1, StatusNew: 2, StatusUpdated: 3, StatusUnchanged: 4}
	sort.SliceStable(out, func(i, j int) bool {
		if rank[out[i].Status] != rank[out[j].Status] {
			return rank[out[i].Status] < rank[out[j].Status]
		}
		return out[i].Rel < out[j].Rel
	})
}
