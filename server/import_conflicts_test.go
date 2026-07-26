package main

// Every test here drives a REAL conflict: a real import writes the file, a
// hand edit lands on top, a second real import parks its version. Fabricating
// a `.incoming` file by hand would test the endpoint against a fiction — and
// the property that matters is not "the parked file disappeared", it is "the
// next import agrees with the decision", which only the importer can answer.

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
)

const lvnV1 = `{"scene":"s","script":[{"op":"say","text":"generated v1"}]}`
const lvnV2 = `{"scene":"s","script":[{"op":"say","text":"generated v2, longer"}]}`
const lvnMine = `{"scene":"s","script":[{"op":"say","text":"the author fixed this line by hand"}]}`

func conflictAPIFor(t *testing.T) *conflictAPI {
	t.Helper()
	s := &server{content: t.TempDir(), adminToken: "t"}
	return &conflictAPI{srv: s, admin: NewAdminService(s.content, "t", nil, nil)}
}

// runImport writes one script through the real three-way merge.
func runImport(t *testing.T, dir, id, rel, body string) *importer.WriteReport {
	t.Helper()
	res := &importer.Result{Scripts: []importer.ScriptFile{{Rel: rel, Data: []byte(body)}}}
	res.Title.ID = id
	rep, err := importer.WriteToContentDir(dir, res)
	if err != nil {
		t.Fatalf("import: %v", err)
	}
	return rep
}

func importStatus(rep *importer.WriteReport, rel string) importer.FileStatus {
	for _, f := range rep.Files {
		if f.Rel == rel {
			return f.Status
		}
	}
	return ""
}

// stageConflict: import v1, hand-edit it, import v2 → parked conflict.
func stageConflict(t *testing.T, a *conflictAPI, rel, v2 string) {
	t.Helper()
	dir := a.srv.content
	runImport(t, dir, "novel", rel, lvnV1)
	if err := os.WriteFile(filepath.Join(dir, filepath.FromSlash(rel)), []byte(lvnMine), 0o644); err != nil {
		t.Fatal(err)
	}
	rep := runImport(t, dir, "novel", rel, v2)
	if got := importStatus(rep, rel); got != importer.StatusConflict {
		t.Fatalf("setup: status = %q, want conflict", got)
	}
	if _, err := os.Stat(filepath.Join(dir, filepath.FromSlash(rel)) + ".incoming"); err != nil {
		t.Fatalf("setup: nothing parked: %v", err)
	}
}

func listConflictsHTTP(t *testing.T, a *conflictAPI, query string) (*httptest.ResponseRecorder, map[string]any) {
	t.Helper()
	req := httptest.NewRequest("GET", "/v1/admin/import-conflicts"+query, nil)
	req.Header.Set("Authorization", "Bearer t")
	rec := httptest.NewRecorder()
	a.handleList(rec, req)
	var out map[string]any
	_ = json.Unmarshal(rec.Body.Bytes(), &out)
	return rec, out
}

func resolveHTTP(t *testing.T, a *conflictAPI, body string) (*httptest.ResponseRecorder, map[string]any) {
	t.Helper()
	req := httptest.NewRequest("POST", "/v1/admin/import-conflicts/resolve", strings.NewReader(body))
	req.Header.Set("Authorization", "Bearer t")
	rec := httptest.NewRecorder()
	a.handleResolve(rec, req)
	var out map[string]any
	_ = json.Unmarshal(rec.Body.Bytes(), &out)
	return rec, out
}

func readContent(t *testing.T, a *conflictAPI, rel string) string {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(a.srv.content, filepath.FromSlash(rel)))
	if err != nil {
		t.Fatalf("read %s: %v", rel, err)
	}
	return string(data)
}

func rowOf(t *testing.T, out map[string]any, i int) map[string]any {
	t.Helper()
	rows, _ := out["conflicts"].([]any)
	if i >= len(rows) {
		t.Fatalf("conflict row %d missing in %v", i, out)
	}
	row, _ := rows[i].(map[string]any)
	return row
}

// ── listing ─────────────────────────────────────────────────────────────────

func TestListShowsBothSidesAndADiff(t *testing.T) {
	a := conflictAPIFor(t)
	stageConflict(t, a, "scripts/ch1.lvn", lvnV2)

	rec, out := listConflictsHTTP(t, a, "")
	if rec.Code != http.StatusOK {
		t.Fatalf("want 200, got %d: %s", rec.Code, rec.Body.String())
	}
	if n, _ := out["count"].(float64); n != 1 {
		t.Fatalf("count = %v, want 1", out["count"])
	}
	row := rowOf(t, out, 0)
	if row["rel"] != "scripts/ch1.lvn" || row["incoming_rel"] != "scripts/ch1.lvn.incoming" {
		t.Fatalf("paths = %v / %v", row["rel"], row["incoming_rel"])
	}
	mine, _ := row["mine"].(map[string]any)
	inc, _ := row["incoming"].(map[string]any)
	if mine["exists"] != true || inc["exists"] != true {
		t.Fatalf("both sides must exist: %v / %v", mine, inc)
	}
	if mine["size"] == inc["size"] {
		t.Errorf("sizes should differ: %v", row)
	}
	if mine["modified"] == "" || inc["modified"] == "" {
		t.Errorf("both sides need a timestamp: %v / %v", mine, inc)
	}
	diff, _ := row["diff"].(string)
	if !strings.Contains(diff, "-") || !strings.Contains(diff, "the author fixed this line by hand") ||
		!strings.Contains(diff, "generated v2") {
		t.Fatalf("diff must show both versions:\n%s", diff)
	}
	if row["undoable"] != true {
		t.Errorf("a script resolution is history-eligible, want undoable=true: %v", row["undoable"])
	}
	if titles := row["titles"]; titles == nil {
		t.Errorf("the owning title should be named: %v", row)
	}
}

// Binary art has no honest diff — only metadata, plus the reason.
func TestBinaryConflictIsNotDiffed(t *testing.T) {
	a := conflictAPIFor(t)
	art := filepath.Join(a.srv.content, "art")
	if err := os.MkdirAll(art, 0o755); err != nil {
		t.Fatal(err)
	}
	png := []byte("\x89PNG\r\n\x1a\n\x00\x00\x00mine")
	if err := os.WriteFile(filepath.Join(art, "bg.png"), png, 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(art, "bg.png.incoming"), append(png, 0, 1, 2, 3), 0o644); err != nil {
		t.Fatal(err)
	}
	_, out := listConflictsHTTP(t, a, "")
	row := rowOf(t, out, 0)
	if row["text"] != false {
		t.Fatalf("png must not be treated as text: %v", row)
	}
	if d, _ := row["diff"].(string); d != "" {
		t.Fatalf("binary art must not be diffed, got:\n%s", d)
	}
	if note, _ := row["diff_note"].(string); !strings.Contains(note, "binary") {
		t.Fatalf("diff_note = %q, want it to say why", note)
	}
	if row["undoable"] != false {
		t.Errorf("binary art is excluded from editorial history — undoable must be false")
	}
}

func TestListRelFiltersAndUnknownIs404(t *testing.T) {
	a := conflictAPIFor(t)
	stageConflict(t, a, "scripts/ch1.lvn", lvnV2)
	rec, out := listConflictsHTTP(t, a, "?rel=scripts/ch1.lvn")
	if rec.Code != http.StatusOK {
		t.Fatalf("want 200, got %d", rec.Code)
	}
	if n, _ := out["count"].(float64); n != 1 {
		t.Fatalf("count = %v, want 1", out["count"])
	}
	rec, _ = listConflictsHTTP(t, a, "?rel=scripts/nope.lvn")
	if rec.Code != http.StatusNotFound {
		t.Fatalf("unknown rel: want 404, got %d", rec.Code)
	}
}

// Bookkeeping is never content: .lvn-import/.history hold no conflicts to show.
func TestScanSkipsBookkeepingDirs(t *testing.T) {
	a := conflictAPIFor(t)
	stageConflict(t, a, "scripts/ch1.lvn", lvnV2)
	for _, d := range []string{".history/scripts/ch1.lvn", ".lvn-import", "state", "services"} {
		p := filepath.Join(a.srv.content, filepath.FromSlash(d))
		if err := os.MkdirAll(p, 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(p, "x.json.incoming"), []byte("{}"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	_, out := listConflictsHTTP(t, a, "")
	if n, _ := out["count"].(float64); n != 1 {
		t.Fatalf("count = %v, want only the real conflict: %v", out["count"], out["conflicts"])
	}
}

// ── resolving: "mine" ───────────────────────────────────────────────────────

// THE property. Keeping the hand edit must both drop the parked file AND move
// the baseline, or the next import re-raises the identical conflict — and it
// must NOT license that import to overwrite the edit either.
func TestResolveMineSurvivesTheNextImport(t *testing.T) {
	a := conflictAPIFor(t)
	rel := "scripts/ch1.lvn"
	stageConflict(t, a, rel, lvnV2)

	rec, out := resolveHTTP(t, a, `{"rel":"scripts/ch1.lvn","choice":"mine"}`)
	if rec.Code != http.StatusOK {
		t.Fatalf("want 200, got %d: %s", rec.Code, rec.Body.String())
	}
	if bl, _ := out["baselines"].([]any); len(bl) != 1 || bl[0] != "novel" {
		t.Fatalf("baselines = %v, want [novel]", out["baselines"])
	}
	if got := readContent(t, a, rel); got != lvnMine {
		t.Fatalf("the author's file changed: %s", got)
	}
	if _, err := os.Stat(filepath.Join(a.srv.content, rel+".incoming")); !os.IsNotExist(err) {
		t.Fatalf("the parked version must be gone, stat err = %v", err)
	}
	// Undoable: the pre-resolution bytes are in .history.
	if hs, _ := os.ReadDir(filepath.Join(a.srv.content, ".history", rel)); len(hs) == 0 {
		t.Errorf("no .history snapshot — the resolution is not rollback-able")
	}

	// Re-import the SAME export: no conflict, and the edit is still there.
	rep := runImport(t, a.srv.content, "novel", rel, lvnV2)
	if got := importStatus(rep, rel); got != importer.StatusKeptLocal {
		t.Fatalf("re-import status = %q, want kept_local (conflict resurrected or edit overwritten)", got)
	}
	if len(rep.Conflicts) != 0 {
		t.Fatalf("the resolved conflict came back: %v", rep.Conflicts)
	}
	if got := readContent(t, a, rel); got != lvnMine {
		t.Fatalf("the re-import CLOBBERED the resolved hand edit: %s", got)
	}
	if _, _ = listConflictsHTTP(t, a, ""); true {
		if _, out := listConflictsHTTP(t, a, ""); out["count"].(float64) != 0 {
			t.Fatalf("listing still shows a conflict: %v", out)
		}
	}
}

// Upstream moving AGAIN after a resolution is a fresh disagreement, and must
// be raised again — resolving one conflict is not a blanket licence.
func TestResolveMineStillCatchesTheNextRealChange(t *testing.T) {
	a := conflictAPIFor(t)
	rel := "scripts/ch1.lvn"
	stageConflict(t, a, rel, lvnV2)
	if rec, _ := resolveHTTP(t, a, `{"rel":"scripts/ch1.lvn","choice":"mine"}`); rec.Code != http.StatusOK {
		t.Fatalf("resolve: %d", rec.Code)
	}
	v3 := `{"scene":"s","script":[{"op":"say","text":"generated v3 — a genuinely new export"}]}`
	rep := runImport(t, a.srv.content, "novel", rel, v3)
	if got := importStatus(rep, rel); got != importer.StatusConflict {
		t.Fatalf("status = %q, want conflict for a NEW upstream change", got)
	}
	if got := readContent(t, a, rel); got != lvnMine {
		t.Fatalf("the hand edit was overwritten: %s", got)
	}
}

// ── resolving: "incoming" ───────────────────────────────────────────────────

func TestResolveIncomingInstallsItAndSettlesTheNextImport(t *testing.T) {
	a := conflictAPIFor(t)
	rel := "scripts/ch1.lvn"
	stageConflict(t, a, rel, lvnV2)

	rec, out := resolveHTTP(t, a, `{"rel":"scripts/ch1.lvn","choice":"incoming"}`)
	if rec.Code != http.StatusOK {
		t.Fatalf("want 200, got %d: %s", rec.Code, rec.Body.String())
	}
	if out["resolved"] != true {
		t.Fatalf("response = %v", out)
	}
	if got := readContent(t, a, rel); got != lvnV2 {
		t.Fatalf("the incoming version was not installed: %s", got)
	}
	if _, err := os.Stat(filepath.Join(a.srv.content, rel+".incoming")); !os.IsNotExist(err) {
		t.Fatalf("the parked version must be gone, stat err = %v", err)
	}
	// The author's version is recoverable: it was snapshotted before the write.
	hs, _ := os.ReadDir(filepath.Join(a.srv.content, ".history", rel))
	if len(hs) == 0 {
		t.Fatalf("overwriting a hand edit without a .history snapshot is data loss")
	}
	back, err := os.ReadFile(filepath.Join(a.srv.content, ".history", rel, hs[0].Name()))
	if err != nil || string(back) != lvnMine {
		t.Fatalf("history holds %q, want the author's version", string(back))
	}

	rep := runImport(t, a.srv.content, "novel", rel, lvnV2)
	if got := importStatus(rep, rel); got != importer.StatusUnchanged {
		t.Fatalf("re-import status = %q, want unchanged", got)
	}
	if len(rep.Conflicts) != 0 {
		t.Fatalf("the resolved conflict came back: %v", rep.Conflicts)
	}
}

// ── the gate ────────────────────────────────────────────────────────────────

// A structurally broken chapter cannot be installed, and a rejected resolution
// must leave the disk EXACTLY as it was — file, parked version and baseline.
func TestBrokenIncomingIsRejectedAndNothingMoves(t *testing.T) {
	a := conflictAPIFor(t)
	rel := "scripts/ch1.lvn"
	broken := `{"scene":"s","script":[{"op":"goto","label":"nowhere"}]}`
	stageConflict(t, a, rel, broken)

	rec, out := resolveHTTP(t, a, `{"rel":"scripts/ch1.lvn","choice":"incoming"}`)
	if rec.Code != http.StatusUnprocessableEntity {
		t.Fatalf("want 422, got %d: %s", rec.Code, rec.Body.String())
	}
	if errs := strList(out["errors"]); len(errs) == 0 {
		t.Fatalf("422 must say what is wrong: %v", out)
	}
	if got := readContent(t, a, rel); got != lvnMine {
		t.Fatalf("a rejected resolution touched the file: %s", got)
	}
	if got := readContent(t, a, rel+".incoming"); got != broken {
		t.Fatalf("a rejected resolution dropped the parked version: %s", got)
	}
	if _, err := os.Stat(filepath.Join(a.srv.content, ".history", rel)); err == nil {
		t.Errorf("a rejected resolution must not even snapshot")
	}
	// Still unresolved, so the conflict is still listed and still re-raised.
	if _, out := listConflictsHTTP(t, a, ""); out["count"].(float64) != 1 {
		t.Fatalf("the conflict should still be open: %v", out)
	}
}

// Not even a JSON document.
func TestGarbageIncomingIsRejected(t *testing.T) {
	a := conflictAPIFor(t)
	rel := "scripts/ch1.lvn"
	stageConflict(t, a, rel, "not json at all")
	rec, _ := resolveHTTP(t, a, `{"rel":"scripts/ch1.lvn","choice":"incoming"}`)
	if rec.Code != http.StatusUnprocessableEntity {
		t.Fatalf("want 422, got %d: %s", rec.Code, rec.Body.String())
	}
	if got := readContent(t, a, rel); got != lvnMine {
		t.Fatalf("file changed: %s", got)
	}
}

// The gate cuts both ways: blessing the author's own broken file as the
// shipped version is the same failure from the other side.
func TestBrokenMineIsRejectedToo(t *testing.T) {
	a := conflictAPIFor(t)
	rel := "scripts/ch1.lvn"
	dir := a.srv.content
	runImport(t, dir, "novel", rel, lvnV1)
	if err := os.WriteFile(filepath.Join(dir, filepath.FromSlash(rel)), []byte(`{"script":`), 0o644); err != nil {
		t.Fatal(err)
	}
	runImport(t, dir, "novel", rel, lvnV2)
	rec, _ := resolveHTTP(t, a, `{"rel":"scripts/ch1.lvn","choice":"mine"}`)
	if rec.Code != http.StatusUnprocessableEntity {
		t.Fatalf("want 422, got %d: %s", rec.Code, rec.Body.String())
	}
	if _, err := os.Stat(filepath.Join(dir, rel+".incoming")); err != nil {
		t.Fatalf("the parked version must survive a rejection: %v", err)
	}
}

// A .lvns sidecar is editable source, not a compiled script: it must resolve
// without being run through the .lvn parser.
func TestSidecarResolvesWithoutTheLvnGate(t *testing.T) {
	a := conflictAPIFor(t)
	rel := "scripts/ch1.lvns"
	dir := a.srv.content
	runImport(t, dir, "novel", rel, "# v1\nsay: hi\n")
	if err := os.WriteFile(filepath.Join(dir, filepath.FromSlash(rel)), []byte("# mine\nsay: hello\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	runImport(t, dir, "novel", rel, "# v2\nsay: hey there\n")
	rec, _ := resolveHTTP(t, a, `{"rel":"scripts/ch1.lvns","choice":"incoming"}`)
	if rec.Code != http.StatusOK {
		t.Fatalf("want 200, got %d: %s", rec.Code, rec.Body.String())
	}
	if got := readContent(t, a, rel); got != "# v2\nsay: hey there\n" {
		t.Fatalf("sidecar = %q", got)
	}
}

// ── refusals ────────────────────────────────────────────────────────────────

func TestResolveRejectsTraversalAndJunk(t *testing.T) {
	a := conflictAPIFor(t)
	stageConflict(t, a, "scripts/ch1.lvn", lvnV2)
	outside := filepath.Join(filepath.Dir(a.srv.content), "outside.lvn")
	if err := os.WriteFile(outside, []byte(lvnV1), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(outside+".incoming", []byte(lvnV2), 0o644); err != nil {
		t.Fatal(err)
	}
	for _, body := range []string{
		`{"rel":"../outside.lvn","choice":"incoming"}`,
		`{"rel":"scripts/../../outside.lvn","choice":"incoming"}`,
		`{"rel":"/etc/passwd","choice":"incoming"}`,
		`{"rel":"","choice":"mine"}`,
		`{"rel":".lvn-import/novel.json","choice":"mine"}`,
		`{"rel":"scripts/ch1.lvn","choice":"theirs"}`,
		`{"rel":"scripts/ch1.lvn","choice":"mine","title":"../evil"}`,
	} {
		rec, _ := resolveHTTP(t, a, body)
		if rec.Code != http.StatusBadRequest {
			t.Errorf("%s → %d, want 400", body, rec.Code)
		}
	}
	// The file outside the content root is untouched.
	if got, _ := os.ReadFile(outside); string(got) != lvnV1 {
		t.Fatalf("a file outside the content root was written: %s", got)
	}
	// And the real conflict is still open.
	if _, out := listConflictsHTTP(t, a, ""); out["count"].(float64) != 1 {
		t.Fatalf("conflict state changed: %v", out)
	}
}

func TestResolveUnknownPathIs404(t *testing.T) {
	a := conflictAPIFor(t)
	rec, _ := resolveHTTP(t, a, `{"rel":"scripts/never.lvn","choice":"mine"}`)
	if rec.Code != http.StatusNotFound {
		t.Fatalf("want 404, got %d: %s", rec.Code, rec.Body.String())
	}
}

func TestConflictEndpointsNeedTheAdminToken(t *testing.T) {
	a := conflictAPIFor(t)
	stageConflict(t, a, "scripts/ch1.lvn", lvnV2)
	for _, tc := range []struct {
		name    string
		req     *http.Request
		handler func(http.ResponseWriter, *http.Request)
		want    int
	}{
		{"list no token", httptest.NewRequest("GET", "/v1/admin/import-conflicts", nil), a.handleList, http.StatusUnauthorized},
		{"resolve no token", httptest.NewRequest("POST", "/v1/admin/import-conflicts/resolve",
			strings.NewReader(`{"rel":"scripts/ch1.lvn","choice":"incoming"}`)), a.handleResolve, http.StatusUnauthorized},
	} {
		rec := httptest.NewRecorder()
		tc.handler(rec, tc.req)
		if rec.Code != tc.want {
			t.Errorf("%s → %d, want %d", tc.name, rec.Code, tc.want)
		}
	}
	// An unauthorized resolve must not have resolved anything.
	if got := readContent(t, a, "scripts/ch1.lvn"); got != lvnMine {
		t.Fatalf("file changed without a token: %s", got)
	}
	// With admin disabled entirely the surface does not exist.
	a.srv.adminToken = ""
	rec := httptest.NewRecorder()
	a.handleList(rec, httptest.NewRequest("GET", "/v1/admin/import-conflicts", nil))
	if rec.Code != http.StatusForbidden {
		t.Errorf("admin disabled → %d, want 403", rec.Code)
	}
}

func TestWrongMethods(t *testing.T) {
	a := conflictAPIFor(t)
	rec := httptest.NewRecorder()
	req := httptest.NewRequest("POST", "/v1/admin/import-conflicts", nil)
	req.Header.Set("Authorization", "Bearer t")
	a.handleList(rec, req)
	if rec.Code != http.StatusMethodNotAllowed {
		t.Errorf("POST to the listing → %d, want 405", rec.Code)
	}
	rec = httptest.NewRecorder()
	req = httptest.NewRequest("GET", "/v1/admin/import-conflicts/resolve", nil)
	req.Header.Set("Authorization", "Bearer t")
	a.handleResolve(rec, req)
	if rec.Code != http.StatusMethodNotAllowed {
		t.Errorf("GET on resolve → %d, want 405", rec.Code)
	}
}

// Two operators clicking at once must not both "resolve" one conflict: the
// second finds nothing parked. (Run with -race, which is what this is for.)
func TestConcurrentResolvesPickOneWinner(t *testing.T) {
	a := conflictAPIFor(t)
	rel := "scripts/ch1.lvn"
	stageConflict(t, a, rel, lvnV2)

	codes := make(chan int, 2)
	body := `{"rel":"scripts/ch1.lvn","choice":"incoming"}`
	for i := 0; i < 2; i++ {
		go func() {
			req := httptest.NewRequest("POST", "/v1/admin/import-conflicts/resolve", strings.NewReader(body))
			req.Header.Set("Authorization", "Bearer t")
			rec := httptest.NewRecorder()
			a.handleResolve(rec, req)
			codes <- rec.Code
		}()
	}
	got := map[int]int{}
	got[<-codes]++
	got[<-codes]++
	if got[http.StatusOK] != 1 || got[http.StatusNotFound] != 1 {
		t.Fatalf("codes = %v, want exactly one 200 and one 404", got)
	}
	if s := readContent(t, a, rel); s != lvnV2 {
		t.Fatalf("file = %s", s)
	}
}

// A conflict on a path no baseline tracks still resolves; the title is
// inferred from the single title in the root, and the next import agrees.
func TestUntrackedConflictInfersItsBaseline(t *testing.T) {
	a := conflictAPIFor(t)
	dir := a.srv.content
	rel := "scripts/novel/ch2.lvn"
	// A file the import never wrote (the author created it by hand), plus a
	// baseline for the title so there is something to infer from.
	runImport(t, dir, "novel", "scripts/novel/ch1.lvn", lvnV1)
	if err := os.MkdirAll(filepath.Join(dir, "scripts", "novel"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, filepath.FromSlash(rel)), []byte(lvnMine), 0o644); err != nil {
		t.Fatal(err)
	}
	rep := runImport(t, dir, "novel", rel, lvnV2)
	if got := importStatus(rep, rel); got != importer.StatusConflict {
		t.Fatalf("setup status = %q, want conflict", got)
	}
	rec, out := resolveHTTP(t, a, `{"rel":"scripts/novel/ch2.lvn","choice":"mine"}`)
	if rec.Code != http.StatusOK {
		t.Fatalf("want 200, got %d: %s", rec.Code, rec.Body.String())
	}
	if bl, _ := out["baselines"].([]any); len(bl) != 1 || bl[0] != "novel" {
		t.Fatalf("baselines = %v, want the inferred [novel]", out["baselines"])
	}
	rep = runImport(t, dir, "novel", rel, lvnV2)
	if got := importStatus(rep, rel); got != importer.StatusKeptLocal {
		t.Fatalf("re-import status = %q, want kept_local", got)
	}
}
