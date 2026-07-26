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
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
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

// ─────────────────────────────────────────────────────────────────────────────
// Resolution: the other half of the merge.
//
// Detection above parks a rejected version and walks away, which is only half
// an answer — a parked file that nobody can act on is a permanent conflict.
// The rest of this file is the resolution primitive both front-ends share (the
// admin API in server/import_conflicts.go and `lvnconv conflicts`): list what
// is parked, show the two versions, and commit one of them.
//
// The one subtlety is the BASELINE. Picking a side is not just moving bytes:
// unless the base moves too, the next import compares against the OLD base,
// sees the same two-sided divergence, and re-raises the conflict the author
// just resolved. That is why every resolution ends in RecordImportBase — and
// why the helper lives here, next to the format it writes, instead of being
// re-implemented in the server. What the new base must be is counter-intuitive
// enough to have its own note at the call site: it is the INCOMING version in
// both cases, because the base means "what upstream last produced", not "what
// is on disk".

// IncomingSuffix is what a parked (rejected) version is named: the file plus
// this suffix, beside it. WriteToContentDir writes it; everything here reads it.
const IncomingSuffix = ".incoming"

// reservedDirs are content subtrees a resolution must never address: server
// bookkeeping (.history), our own baselines (.lvn-import), player data
// (services, state). Nothing in them is import output.
var reservedDirs = map[string]bool{
	".history": true, baselineDir: true, "services": true, "state": true,
}

// Errors a caller maps onto its own vocabulary (HTTP status, exit code).
var (
	ErrBadPath        = errors.New("path escapes the content root")
	ErrBadChoice      = errors.New(`choice must be "mine" or "incoming"`)
	ErrNoConflict     = errors.New("no parked incoming version for this path")
	ErrMineMissing    = errors.New("the current file is gone — only \"incoming\" can resolve this")
	ErrInvalidContent = errors.New("the chosen version failed validation")
)

// The two sides of a conflict, as a caller names them.
const (
	ChoiceMine     = "mine"     // keep what is on disk, drop the parked version
	ChoiceIncoming = "incoming" // install the parked version over the file
)

// FileSide is one version's metadata. Size and time are all a caller gets for
// binary art — there is nothing else honest to show.
type FileSide struct {
	Exists   bool   `json:"exists"`
	Size     int64  `json:"size"`
	Modified string `json:"modified,omitempty"` // RFC3339, UTC
	SHA      string `json:"sha,omitempty"`      // sha256, hex — cheap identity for a UI
}

// Conflict is one parked import decision waiting for a human.
type Conflict struct {
	Rel         string   `json:"rel"`          // the author's file, content-relative
	IncomingRel string   `json:"incoming_rel"` // where the rejected version sits
	Mine        FileSide `json:"mine"`
	Incoming    FileSide `json:"incoming"`
	Text        bool     `json:"text"`             // false → binary, metadata only
	Titles      []string `json:"titles,omitempty"` // baselines that track this path
}

// contentRel validates a content-relative path and returns its on-disk form.
// Same posture as the server's asset handler: reject "..", absolute paths and
// backslashes outright rather than trusting Clean to have saved us, and verify
// the result still lives under the root afterwards.
func contentRel(contentDir, rel string) (string, error) {
	root := filepath.Clean(contentDir)
	if rel == "" || strings.Contains(rel, "..") || strings.ContainsAny(rel, `\`) ||
		strings.HasPrefix(rel, "/") || filepath.IsAbs(rel) {
		return "", fmt.Errorf("%w: %q", ErrBadPath, rel)
	}
	if first, _, _ := strings.Cut(rel, "/"); reservedDirs[first] {
		return "", fmt.Errorf("%w: %q is server bookkeeping, not content", ErrBadPath, rel)
	}
	dst := filepath.Clean(filepath.Join(root, filepath.FromSlash(rel)))
	if dst != root && !strings.HasPrefix(dst, root+string(os.PathSeparator)) {
		return "", fmt.Errorf("%w: %q", ErrBadPath, rel)
	}
	return dst, nil
}

func sideOf(path string, withSHA bool) FileSide {
	st, err := os.Stat(path)
	if err != nil || st.IsDir() {
		return FileSide{}
	}
	s := FileSide{Exists: true, Size: st.Size(), Modified: st.ModTime().UTC().Format(time.RFC3339)}
	if withSHA {
		if data, err := os.ReadFile(path); err == nil {
			s.SHA = sha256Hex(data)
		}
	}
	return s
}

// textExt is what we are willing to render as a diff. Everything else is art,
// audio or an archive: a "unified diff" of two PNGs is noise at best and a
// megabyte of terminal garbage at worst.
var textExt = map[string]bool{
	".lvn": true, ".lvns": true, ".json": true, ".txt": true,
	".md": true, ".csv": true, ".ink": true, ".yaml": true, ".yml": true,
}

// Diffable reports whether a path holds text we may show as a diff.
func Diffable(rel string) bool { return textExt[strings.ToLower(filepath.Ext(rel))] }

// ScanConflicts lists every parked version under a content root. It is a pure
// filesystem walk: the baselines are only consulted to say which title(s) a
// conflicted path belongs to, so a hand-deleted baseline degrades the label,
// not the listing.
func ScanConflicts(contentDir string) ([]Conflict, error) {
	root := filepath.Clean(contentDir)
	owners := baselineOwners(root)
	var out []Conflict
	err := filepath.WalkDir(root, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return nil // an unreadable corner of content must not kill the listing
		}
		if d.IsDir() {
			name := d.Name()
			if path != root && (strings.HasPrefix(name, ".") || reservedDirs[name]) {
				return filepath.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(d.Name(), IncomingSuffix) {
			return nil
		}
		relPath, err := filepath.Rel(root, path)
		if err != nil {
			return nil
		}
		incRel := filepath.ToSlash(relPath)
		rel := strings.TrimSuffix(incRel, IncomingSuffix)
		if rel == "" || strings.HasSuffix(rel, "/") {
			return nil // a bare ".incoming" names no file to resolve
		}
		c := Conflict{
			Rel:         rel,
			IncomingRel: incRel,
			Mine:        sideOf(filepath.Join(root, filepath.FromSlash(rel)), true),
			Incoming:    sideOf(path, true),
			Text:        Diffable(rel),
			Titles:      owners[rel],
		}
		out = append(out, c)
		return nil
	})
	if err != nil {
		return nil, err
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Rel < out[j].Rel })
	return out, nil
}

// ConflictDiff renders the two sides of one conflict. note is non-empty when
// there is no diff to show and says why — binary, missing side, or a pair too
// far apart to align line by line.
func ConflictDiff(contentDir string, c Conflict, maxLines int) (diff, note string) {
	if !c.Text {
		return "", fmt.Sprintf("binary content — compare by size and time (mine %d bytes, incoming %d bytes)",
			c.Mine.Size, c.Incoming.Size)
	}
	root := filepath.Clean(contentDir)
	mine, errM := os.ReadFile(filepath.Join(root, filepath.FromSlash(c.Rel)))
	inc, errI := os.ReadFile(filepath.Join(root, filepath.FromSlash(c.IncomingRel)))
	if errI != nil {
		return "", "the parked version is unreadable: " + errI.Error()
	}
	if errM != nil {
		return "", "the current file is gone — the parked version is the only one left"
	}
	if isBinary(mine) || isBinary(inc) {
		return "", "content is not text despite the extension — not diffed"
	}
	return UnifiedDiff(mine, inc, c.Rel+" (mine)", c.IncomingRel+" (incoming)", maxLines), ""
}

// isBinary is the same NUL sniff every diff tool uses, over the first 8 KiB.
func isBinary(data []byte) bool {
	if len(data) > 8192 {
		data = data[:8192]
	}
	for _, b := range data {
		if b == 0 {
			return true
		}
	}
	return false
}

// ResolveOptions injects the caller's write path. The server MUST pass a Write
// that snapshots into .history and writes atomically under its own lock (so a
// resolution is undoable like every other admin write) and a Validate that runs
// the .lvn gate — this package deliberately owns neither.
type ResolveOptions struct {
	Validate func(rel string, data []byte) error // nil = no structural check
	Write    func(rel string, data []byte) error // required
	Title    string                              // optional baseline hint
}

// ResolveResult is what a resolution did. Validator warnings are deliberately
// absent: they belong to the caller that supplied the validator, which already
// has them in hand and knows how to render them.
type ResolveResult struct {
	Rel       string   `json:"rel"`
	Choice    string   `json:"choice"`
	Bytes     int      `json:"bytes"`
	Baselines []string `json:"baselines"`      // titles whose base now records these bytes
	Note      string   `json:"note,omitempty"` // set when no baseline could be updated
}

// ResolveConflict commits one side of a parked conflict.
//
// Order matters and is the whole safety story: validate BEFORE anything moves
// (a rejected resolution leaves the disk exactly as it was), write through the
// caller's snapshotting writer, only then drop the parked file, and finally
// stamp the winning bytes into the baseline so the next import agrees with the
// decision instead of re-raising it.
//
// "mine" rewrites the file with its own current bytes on purpose: it costs one
// write and buys a .history entry, so choosing "mine" is as undoable as
// choosing "incoming", and the baseline records a size/mtime that matches the
// file exactly (diskMatchesBaseline then short-circuits without re-hashing).
func ResolveConflict(contentDir, rel, choice string, opt ResolveOptions) (*ResolveResult, error) {
	if choice != ChoiceMine && choice != ChoiceIncoming {
		return nil, fmt.Errorf("%w (got %q)", ErrBadChoice, choice)
	}
	if opt.Write == nil {
		return nil, errors.New("ResolveOptions.Write is required")
	}
	if strings.HasSuffix(rel, IncomingSuffix) {
		rel = strings.TrimSuffix(rel, IncomingSuffix) // accept either spelling
	}
	dst, err := contentRel(contentDir, rel)
	if err != nil {
		return nil, err
	}
	parked := dst + IncomingSuffix
	if st, err := os.Stat(parked); err != nil || st.IsDir() {
		return nil, fmt.Errorf("%w: %s", ErrNoConflict, rel)
	}

	// Both versions are read up front: the winner becomes the file, and the
	// INCOMING one becomes the baseline whichever side wins (see below).
	upstream, err := os.ReadFile(parked)
	if err != nil {
		return nil, err
	}
	data := upstream
	if choice == ChoiceMine {
		if data, err = os.ReadFile(dst); err != nil {
			if os.IsNotExist(err) {
				return nil, fmt.Errorf("%w: %s", ErrMineMissing, rel)
			}
			return nil, err
		}
	}

	if opt.Validate != nil {
		if err := opt.Validate(rel, data); err != nil {
			return nil, fmt.Errorf("%w: %v", ErrInvalidContent, err)
		}
	}
	if err := opt.Write(rel, data); err != nil {
		return nil, err
	}
	if err := os.Remove(parked); err != nil && !os.IsNotExist(err) {
		return nil, err
	}

	// The baseline is the three-way BASE — what upstream last produced — and
	// NOT "whatever is on disk now". For "incoming" those are the same bytes.
	// For "mine" they are not, and this is the one place where getting it
	// backwards is silently destructive: recording the author's bytes as the
	// base would tell the next import that nobody had touched the file, so it
	// would happily overwrite the very edit that was just defended. Recording
	// the INCOMING bytes instead means the next import that produces them
	// again lands on kept_local — no conflict, and the edit survives.
	res := &ResolveResult{Rel: rel, Choice: choice, Bytes: len(data)}
	res.Baselines, err = RecordImportBase(contentDir, rel, opt.Title, upstream, choice == ChoiceIncoming)
	if err != nil {
		return res, fmt.Errorf("baseline: %w", err)
	}
	if len(res.Baselines) == 0 {
		// Honest about the one case we cannot infer: the file belongs to no
		// baseline and the path names no title, so the next import of whatever
		// produces it will see an untracked, hand-owned file again.
		res.Note = "no baseline tracks this path and no title could be inferred — " +
			"pass the title explicitly if the next import re-raises this conflict"
	}
	return res, nil
}

// RecordImportBase stamps `upstream` — the bytes the import produced for rel —
// as the three-way base, and returns the titles whose baseline it updated.
//
// onDisk says whether the file now HOLDS those bytes. When it does, the entry
// also carries the file's size and mtime, which lets the next import skip
// re-hashing it (the git-index trick). When it does not (the author kept their
// own version), the mtime is deliberately left at zero: no real file has a zero
// mtime, so diskMatchesBaseline is forced to hash, finds the disk differs from
// the base, and classifies the file as locally edited — which it is.
//
// Which baselines? Every one that already tracks the path (a path shared by two
// titles must not stay conflicted for the second), plus an explicit hint if one
// was given. When nothing tracks it — the conflict came from a file the import
// never owned — the title is inferred from the path (`scripts/<id>/ch1.lvn`,
// `art/<id>/bg/x.png`, `scripts/<id>.lvn`), and failing that from a content
// root that holds exactly one title. Nothing is guessed beyond that: a wrong
// baseline entry would silently license the next import to overwrite a hand
// edit, which is the one outcome this whole mechanism exists to prevent.
func RecordImportBase(contentDir, rel, titleHint string, upstream []byte, onDisk bool) ([]string, error) {
	root := filepath.Clean(contentDir)
	dst, err := contentRel(root, rel)
	if err != nil {
		return nil, err
	}
	e := baselineEntry{SHA: sha256Hex(upstream), Size: int64(len(upstream))}
	if onDisk {
		if st, err := os.Stat(dst); err == nil {
			e.Size, e.MTime = st.Size(), st.ModTime().UnixNano()
		}
	}
	titles := append([]string{}, baselineOwners(root)[rel]...)
	if titleHint != "" && !slicesContains(titles, titleHint) {
		titles = append(titles, titleHint)
	}
	if len(titles) == 0 {
		if t := inferBaselineTitle(root, rel); t != "" {
			titles = []string{t}
		}
	}
	sort.Strings(titles)
	for _, t := range titles {
		b := loadBaseline(root, t)
		b.Files[rel] = e
		if err := b.save(root); err != nil {
			return titles, err
		}
	}
	return titles, nil
}

func slicesContains(s []string, v string) bool {
	for _, x := range s {
		if x == v {
			return true
		}
	}
	return false
}

// baselineTitles lists the titles that have a baseline in this content root.
func baselineTitles(root string) []string {
	entries, err := os.ReadDir(filepath.Join(root, baselineDir))
	if err != nil {
		return nil
	}
	var out []string
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		out = append(out, strings.TrimSuffix(e.Name(), ".json"))
	}
	sort.Strings(out)
	return out
}

// baselineOwners maps every tracked path to the titles tracking it.
func baselineOwners(root string) map[string][]string {
	owners := map[string][]string{}
	for _, t := range baselineTitles(root) {
		for rel := range loadBaseline(root, t).Files {
			owners[rel] = append(owners[rel], t)
		}
	}
	for _, v := range owners {
		sort.Strings(v)
	}
	return owners
}

// inferBaselineTitle picks the title a so-far-untracked path most likely
// belongs to, or "" when the answer is not obvious.
func inferBaselineTitle(root, rel string) string {
	titles := baselineTitles(root)
	segs := strings.Split(rel, "/")
	base := strings.TrimSuffix(segs[len(segs)-1], filepath.Ext(rel))
	var hits []string
	for _, t := range titles {
		if t == "" {
			continue
		}
		if slicesContains(segs[:len(segs)-1], t) || base == t {
			hits = append(hits, t)
		}
	}
	if len(hits) == 1 {
		return hits[0]
	}
	if len(hits) == 0 && len(titles) == 1 {
		return titles[0] // a single-title content root leaves nothing to confuse
	}
	return ""
}

// ─── unified diff ────────────────────────────────────────────────────────────
//
// Small on purpose: the panel needs to show an author "what would change", and
// pulling a diff library into a dependency-free toolchain to render three lines
// of context is not a trade worth making.

// maxDiffCells caps the LCS table. Two versions of a chapter normally share
// almost everything, so the table is tiny after the common prefix/suffix is
// trimmed; a pair that really did diverge everywhere falls back to a
// whole-file replacement rather than allocating a gigabyte.
const maxDiffCells = 1_500_000

// UnifiedDiff renders `mine` → `incoming` as a unified diff with three lines of
// context, truncated to maxLines output lines (0 = unlimited).
func UnifiedDiff(mine, incoming []byte, mineName, incomingName string, maxLines int) string {
	a, b := splitLines(mine), splitLines(incoming)
	ops := diffOps(a, b)
	changed := false
	for _, o := range ops {
		if o.kind != ' ' {
			changed = true
			break
		}
	}
	if !changed {
		return ""
	}
	var sb strings.Builder
	fmt.Fprintf(&sb, "--- %s\n+++ %s\n", mineName, incomingName)
	lines := renderHunks(ops, 3)
	if maxLines > 0 && len(lines) > maxLines {
		kept := len(lines) - maxLines
		lines = lines[:maxLines]
		lines = append(lines, fmt.Sprintf("… diff truncated, %d more line(s)", kept))
	}
	for _, l := range lines {
		sb.WriteString(l)
		sb.WriteByte('\n')
	}
	return sb.String()
}

// splitLines splits on \n and drops the trailing empty element a final newline
// produces, so "a\n" is one line, not two.
func splitLines(data []byte) []string {
	if len(data) == 0 {
		return nil
	}
	s := strings.ReplaceAll(string(data), "\r\n", "\n")
	lines := strings.Split(s, "\n")
	if n := len(lines); n > 0 && lines[n-1] == "" {
		lines = lines[:n-1]
	}
	return lines
}

type diffLine struct {
	kind byte // ' ' keep, '-' only in mine, '+' only in incoming
	text string
}

// diffOps aligns two line slices: common prefix/suffix first (the cheap win),
// LCS over what is left, or a flat replacement when that is too big to align.
func diffOps(a, b []string) []diffLine {
	p := 0
	for p < len(a) && p < len(b) && a[p] == b[p] {
		p++
	}
	s := 0
	for s < len(a)-p && s < len(b)-p && a[len(a)-1-s] == b[len(b)-1-s] {
		s++
	}
	am, bm := a[p:len(a)-s], b[p:len(b)-s]

	ops := make([]diffLine, 0, len(a)+len(b))
	for _, l := range a[:p] {
		ops = append(ops, diffLine{' ', l})
	}
	if len(am)*len(bm) > maxDiffCells {
		for _, l := range am {
			ops = append(ops, diffLine{'-', l})
		}
		for _, l := range bm {
			ops = append(ops, diffLine{'+', l})
		}
	} else {
		ops = append(ops, lcsDiff(am, bm)...)
	}
	for _, l := range a[len(a)-s:] {
		ops = append(ops, diffLine{' ', l})
	}
	return ops
}

// lcsDiff is the textbook longest-common-subsequence walk. n*m is bounded by
// the caller (maxDiffCells).
func lcsDiff(a, b []string) []diffLine {
	n, m := len(a), len(b)
	if n == 0 || m == 0 {
		out := make([]diffLine, 0, n+m)
		for _, l := range a {
			out = append(out, diffLine{'-', l})
		}
		for _, l := range b {
			out = append(out, diffLine{'+', l})
		}
		return out
	}
	w := m + 1
	tbl := make([]int32, (n+1)*w)
	for i := n - 1; i >= 0; i-- {
		for j := m - 1; j >= 0; j-- {
			if a[i] == b[j] {
				tbl[i*w+j] = tbl[(i+1)*w+j+1] + 1
			} else if tbl[(i+1)*w+j] >= tbl[i*w+j+1] {
				tbl[i*w+j] = tbl[(i+1)*w+j]
			} else {
				tbl[i*w+j] = tbl[i*w+j+1]
			}
		}
	}
	out := make([]diffLine, 0, n+m)
	i, j := 0, 0
	for i < n && j < m {
		switch {
		case a[i] == b[j]:
			out = append(out, diffLine{' ', a[i]})
			i, j = i+1, j+1
		case tbl[(i+1)*w+j] >= tbl[i*w+j+1]:
			out = append(out, diffLine{'-', a[i]})
			i++
		default:
			out = append(out, diffLine{'+', b[j]})
			j++
		}
	}
	for ; i < n; i++ {
		out = append(out, diffLine{'-', a[i]})
	}
	for ; j < m; j++ {
		out = append(out, diffLine{'+', b[j]})
	}
	return out
}

// renderHunks turns the flat alignment into @@ hunks with `ctx` lines of
// context, merging hunks that would otherwise overlap.
func renderHunks(ops []diffLine, ctx int) []string {
	// Line numbers per op, 1-based, in each file.
	oldNo := make([]int, len(ops))
	newNo := make([]int, len(ops))
	o, n := 1, 1
	for i, op := range ops {
		oldNo[i], newNo[i] = o, n
		if op.kind != '+' {
			o++
		}
		if op.kind != '-' {
			n++
		}
	}
	var out []string
	lastEnd := 0 // where the previous hunk stopped: context is never printed twice
	for i := 0; i < len(ops); {
		if ops[i].kind == ' ' {
			i++
			continue
		}
		start := max(lastEnd, i-ctx)
		end := i + 1
		// Extend while the next change is close enough to share this hunk —
		// the usual rule: a run of fewer than 2*ctx equal lines is cheaper to
		// print than a second @@ header.
		for j := i + 1; j < len(ops); j++ {
			if ops[j].kind != ' ' {
				end = j + 1
				continue
			}
			if j-end >= 2*ctx {
				break
			}
		}
		end = min(len(ops), end+ctx)
		lastEnd = end

		oldStart, newStart, oldLen, newLen := oldNo[start], newNo[start], 0, 0
		for _, op := range ops[start:end] {
			if op.kind != '+' {
				oldLen++
			}
			if op.kind != '-' {
				newLen++
			}
		}
		if oldLen == 0 {
			oldStart = oldNo[start] - 1
		}
		if newLen == 0 {
			newStart = newNo[start] - 1
		}
		out = append(out, fmt.Sprintf("@@ -%d,%d +%d,%d @@", oldStart, oldLen, newStart, newLen))
		for _, op := range ops[start:end] {
			out = append(out, string(op.kind)+op.text)
		}
		i = end
	}
	return out
}
