package importer

import (
	"os"
	"path/filepath"
	"testing"
)

// oneScript builds the smallest Result that writes a single script file.
func oneScript(id, rel, body string) *Result {
	res := &Result{Scripts: []ScriptFile{{Rel: rel, Data: []byte(body)}}}
	res.Title.ID = id
	return res
}

func readFile(t *testing.T, p string) string {
	t.Helper()
	b, err := os.ReadFile(p)
	if err != nil {
		t.Fatalf("read %s: %v", p, err)
	}
	return string(b)
}

func statusOf(rep *WriteReport, rel string) FileStatus {
	for _, f := range rep.Files {
		if f.Rel == rel {
			return f.Status
		}
	}
	return ""
}

// The first import owns everything it writes.
func TestFirstImportIsAllNew(t *testing.T) {
	dir := t.TempDir()
	rep, err := WriteToContentDir(dir, oneScript("t", "scripts/a.lvn", `{"script":[]}`))
	if err != nil {
		t.Fatal(err)
	}
	if got := statusOf(rep, "scripts/a.lvn"); got != StatusNew {
		t.Fatalf("status = %q, want new", got)
	}
	if len(rep.Conflicts) != 0 {
		t.Fatalf("unexpected conflicts: %v", rep.Conflicts)
	}
}

// Nothing changed anywhere: the file must not even be rewritten.
func TestUnchangedReimportIsANoOp(t *testing.T) {
	dir := t.TempDir()
	body := `{"script":[{"op":"say","text":"a"}]}`
	if _, err := WriteToContentDir(dir, oneScript("t", "scripts/a.lvn", body)); err != nil {
		t.Fatal(err)
	}
	rep, err := WriteToContentDir(dir, oneScript("t", "scripts/a.lvn", body))
	if err != nil {
		t.Fatal(err)
	}
	if got := statusOf(rep, "scripts/a.lvn"); got != StatusUnchanged {
		t.Fatalf("status = %q, want unchanged", got)
	}
}

// Upstream moved, the human did not touch it — regenerate freely.
func TestUpstreamChangeOverwritesAnUntouchedFile(t *testing.T) {
	dir := t.TempDir()
	if _, err := WriteToContentDir(dir, oneScript("t", "scripts/a.lvn", `{"script":["v1"]}`)); err != nil {
		t.Fatal(err)
	}
	rep, err := WriteToContentDir(dir, oneScript("t", "scripts/a.lvn", `{"script":["v2"]}`))
	if err != nil {
		t.Fatal(err)
	}
	if got := statusOf(rep, "scripts/a.lvn"); got != StatusUpdated {
		t.Fatalf("status = %q, want updated", got)
	}
	if got := readFile(t, filepath.Join(dir, "scripts/a.lvn")); got != `{"script":["v2"]}` {
		t.Errorf("on disk = %s, want the new version", got)
	}
}

// THE case this exists for: the author fixed a line inside the engine, and the
// same export comes back. Their edit must survive untouched.
func TestHandEditSurvivesAnIdenticalReimport(t *testing.T) {
	dir := t.TempDir()
	body := `{"script":["generated"]}`
	if _, err := WriteToContentDir(dir, oneScript("t", "scripts/a.lvn", body)); err != nil {
		t.Fatal(err)
	}
	edited := `{"script":["edited by hand"]}`
	if err := os.WriteFile(filepath.Join(dir, "scripts/a.lvn"), []byte(edited), 0o644); err != nil {
		t.Fatal(err)
	}
	rep, err := WriteToContentDir(dir, oneScript("t", "scripts/a.lvn", body))
	if err != nil {
		t.Fatal(err)
	}
	if got := statusOf(rep, "scripts/a.lvn"); got != StatusKeptLocal {
		t.Fatalf("status = %q, want kept_local", got)
	}
	if got := readFile(t, filepath.Join(dir, "scripts/a.lvn")); got != edited {
		t.Errorf("the hand edit was overwritten: %s", got)
	}
}

// Both sides moved. Nothing may be overwritten, and the author has to be told.
func TestBothSidesChangedIsAConflictAndNothingIsOverwritten(t *testing.T) {
	dir := t.TempDir()
	if _, err := WriteToContentDir(dir, oneScript("t", "scripts/a.lvn", `{"script":["v1"]}`)); err != nil {
		t.Fatal(err)
	}
	edited := `{"script":["mine"]}`
	if err := os.WriteFile(filepath.Join(dir, "scripts/a.lvn"), []byte(edited), 0o644); err != nil {
		t.Fatal(err)
	}
	rep, err := WriteToContentDir(dir, oneScript("t", "scripts/a.lvn", `{"script":["v2"]}`))
	if err != nil {
		t.Fatal(err)
	}
	if got := statusOf(rep, "scripts/a.lvn"); got != StatusConflict {
		t.Fatalf("status = %q, want conflict", got)
	}
	if len(rep.Conflicts) != 1 || rep.Conflicts[0] != "scripts/a.lvn" {
		t.Errorf("conflicts = %v, want the one path", rep.Conflicts)
	}
	if got := readFile(t, filepath.Join(dir, "scripts/a.lvn")); got != edited {
		t.Errorf("the hand edit was CLOBBERED: %s", got)
	}
	// The rejected version must be inspectable, not thrown away.
	if got := readFile(t, filepath.Join(dir, "scripts/a.lvn.incoming")); got != `{"script":["v2"]}` {
		t.Errorf("incoming = %s, want the new version parked beside the file", got)
	}
}

// A file we never wrote but that already exists belongs to somebody else.
func TestPreexistingUntrackedFileIsNotClobbered(t *testing.T) {
	dir := t.TempDir()
	if err := os.MkdirAll(filepath.Join(dir, "scripts"), 0o755); err != nil {
		t.Fatal(err)
	}
	theirs := `{"script":["not ours"]}`
	if err := os.WriteFile(filepath.Join(dir, "scripts/a.lvn"), []byte(theirs), 0o644); err != nil {
		t.Fatal(err)
	}
	rep, err := WriteToContentDir(dir, oneScript("t", "scripts/a.lvn", `{"script":["ours"]}`))
	if err != nil {
		t.Fatal(err)
	}
	if got := statusOf(rep, "scripts/a.lvn"); got != StatusConflict {
		t.Fatalf("status = %q, want conflict for an untracked existing file", got)
	}
	if got := readFile(t, filepath.Join(dir, "scripts/a.lvn")); got != theirs {
		t.Errorf("somebody else's file was overwritten: %s", got)
	}
}

// A deleted file is simply put back — that is not a conflict.
func TestDeletedFileIsRestored(t *testing.T) {
	dir := t.TempDir()
	body := `{"script":["v1"]}`
	if _, err := WriteToContentDir(dir, oneScript("t", "scripts/a.lvn", body)); err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(filepath.Join(dir, "scripts/a.lvn")); err != nil {
		t.Fatal(err)
	}
	rep, err := WriteToContentDir(dir, oneScript("t", "scripts/a.lvn", body))
	if err != nil {
		t.Fatal(err)
	}
	if got := statusOf(rep, "scripts/a.lvn"); got != StatusNew {
		t.Fatalf("status = %q, want new (restored)", got)
	}
}

// Baselines are per title: two novellas writing the same path must not read
// each other's history and conclude the other's file is a hand edit.
func TestBaselinesAreScopedPerTitle(t *testing.T) {
	dir := t.TempDir()
	if _, err := WriteToContentDir(dir, oneScript("first", "scripts/shared.lvn", `{"script":["a"]}`)); err != nil {
		t.Fatal(err)
	}
	rep, err := WriteToContentDir(dir, oneScript("second", "scripts/shared.lvn", `{"script":["b"]}`))
	if err != nil {
		t.Fatal(err)
	}
	// The second title never wrote this path, and it exists with other content:
	// that is exactly "somebody else owns it".
	if got := statusOf(rep, "scripts/shared.lvn"); got != StatusConflict {
		t.Fatalf("status = %q, want conflict across titles", got)
	}
	for _, id := range []string{"first", "second"} {
		if _, err := os.Stat(filepath.Join(dir, baselineDir, id+".json")); err != nil {
			t.Errorf("baseline for %q missing: %v", id, err)
		}
	}
}
