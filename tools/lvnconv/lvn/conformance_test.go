package lvn

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// The op dictionary has three implementations: this validator (the reference),
// grammar.json (pinned by grammar_sync_test.go) and the C# runtime — which
// mirrors the dictionary twice over, in its dispatch switches and in the public
// Lvn.StagingOps.Known set. A fourth, the browser playground's own JS player,
// was deleted: it charged every new op a second implementation and drifted from
// the reference between releases. This file pins what is left — cheaply, with no
// Unity — against conformance/ops-owners.json, and checks the shared corpus
// under conformance/cases is well formed before the runtime runners read it.
//
// The point is not today's snapshot but tomorrow's drift: add an op to KnownOps
// and TestOpOwnersCoverKnownOps goes red; declare it engine-owned without a
// handler and TestEngineOwnedOpsHaveCSharpHandlers goes red. Runtime semantics
// (as opposed to mere dispatch) are the corpus runners' job — see
// conformance/README.md.

// ── the ownership table ─────────────────────────────────────────────────────

type opOwner struct {
	Owner  string `json:"owner"`  // engine | shell
	CSharp string `json:"csharp"` // player | stage | player+stage | shell-op
	Note   string `json:"note"`
}

type ownersFile struct {
	Ops map[string]opOwner `json:"ops"`
}

// repoRoot walks up from the package directory until it finds the conformance
// corpus, so the test survives being run from anywhere in the module.
func repoRoot(t *testing.T) string {
	t.Helper()
	dir, err := filepath.Abs(".")
	if err != nil {
		t.Fatalf("cwd: %v", err)
	}
	for i := 0; i < 8; i++ {
		if _, err := os.Stat(filepath.Join(dir, "conformance", "ops-owners.json")); err == nil {
			return dir
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			break
		}
		dir = parent
	}
	t.Fatal("conformance/ops-owners.json not found above the package dir — the op-ownership table is the contract; restore it rather than deleting this test")
	return ""
}

func loadOwners(t *testing.T) (ownersFile, string) {
	t.Helper()
	root := repoRoot(t)
	data, err := os.ReadFile(filepath.Join(root, "conformance", "ops-owners.json"))
	if err != nil {
		t.Fatalf("ops-owners.json unreadable: %v", err)
	}
	var o ownersFile
	if err := json.Unmarshal(data, &o); err != nil {
		t.Fatalf("ops-owners.json invalid: %v", err)
	}
	if len(o.Ops) == 0 {
		t.Fatal("ops-owners.json has no \"ops\" rows")
	}
	return o, root
}

// checkOwnersCoverOps compares an op dictionary against the ownership table and
// returns one complaint per mismatch. It takes the dictionary as an argument (not
// KnownOps directly) so the guard can be pointed at a doctored dictionary and
// proven to bite — see TestGuardBitesOnADriftedOp.
func checkOwnersCoverOps(ops map[string]bool, owners ownersFile) []string {
	var bad []string
	add := func(format string, a ...any) { bad = append(bad, fmt.Sprintf(format, a...)) }
	for op := range ops {
		row, ok := owners.Ops[op]
		if !ok {
			add("op %q is in KnownOps but has no row in conformance/ops-owners.json — "+
				"say which package owns it (engine or shell) and where it dispatches", op)
			continue
		}
		switch row.Owner {
		case "engine", "shell":
		default:
			add("op %q: owner=%q is not engine or shell", op, row.Owner)
		}
		switch row.CSharp {
		case "player", "stage", "player+stage", "shell-op":
		default:
			add("op %q: csharp=%q is not a known dispatch site", op, row.CSharp)
		}
		// An op the engine package owns cannot be dispatched by the shell, and
		// vice versa — that pairing is exactly what the table exists to state.
		if (row.Owner == "shell") != (row.CSharp == "shell-op") {
			add("op %q: owner=%q contradicts csharp=%q", op, row.Owner, row.CSharp)
		}
	}
	for op := range owners.Ops {
		if !ops[op] {
			add("ops-owners.json has a row for %q which is not in KnownOps", op)
		}
	}
	sort.Strings(bad)
	return bad
}

func TestOpOwnersCoverKnownOps(t *testing.T) {
	owners, _ := loadOwners(t)
	for _, msg := range checkOwnersCoverOps(KnownOps, owners) {
		t.Error(msg)
	}
}

// ── the C# op registry ──────────────────────────────────────────────────────

// Lvn.StagingOps.Known (com.lvn.engine/Runtime/StagingOps.cs) is the C# copy of
// this dictionary: a public HashSet a host asks "is this name part of the
// language, or mine?" inside ILvnStage.ApplyStage. Nothing in the runtime
// dispatches on it, which is precisely why it drifted to 24 of 30 ops in silence
// — and a count assertion in CameraRigTests then froze the gap. So it is read
// out of the source here, the same way the dispatch sites are, and diffed
// against KnownOps: a mirror nobody executes has to be checked, not trusted.
const (
	csharpKnownOpsFile   = "unity/Packages/com.lvn.engine/Runtime/StagingOps.cs"
	csharpKnownOpsAnchor = "Known = new HashSet<string>"
)

// checkCSharpKnownOps takes both sets as arguments so the guard can be pointed at
// a doctored pair and proven to bite — see TestGuardBitesOnADriftedOp.
func checkCSharpKnownOps(ops, known map[string]bool) []string {
	var bad []string
	add := func(format string, a ...any) { bad = append(bad, fmt.Sprintf(format, a...)) }
	for op := range ops {
		if !known[op] {
			add("op %q is in KnownOps but missing from Lvn.StagingOps.Known (%s) — "+
				"a host asking that set whether %q is a real op gets a false negative, "+
				"so add it there whenever you add it here", op, csharpKnownOpsFile, op)
		}
	}
	for op := range known {
		if !ops[op] {
			add("Lvn.StagingOps.Known lists %q, which is not in KnownOps — either the op was "+
				"dropped from the language and the C# mirror kept it, or it is a host op that "+
				"belongs in LvnOps.Register rather than in the language registry", op)
		}
	}
	sort.Strings(bad)
	return bad
}

// scrapeCSharpKnownOps reads the literal initializer of StagingOps.Known.
func scrapeCSharpKnownOps(t *testing.T, root string) map[string]bool {
	t.Helper()
	path := filepath.Join(root, csharpKnownOpsFile)
	body := methodBody(t, path, csharpKnownOpsAnchor)
	out := map[string]bool{}
	for _, m := range reQuoted.FindAllStringSubmatch(body, -1) {
		if m[1] != "" {
			out[m[1]] = true
		}
	}
	if len(out) == 0 {
		t.Fatalf("%s: no ops found in the %q initializer — did the set move or change shape?",
			csharpKnownOpsFile, csharpKnownOpsAnchor)
	}
	return out
}

func TestCSharpKnownOpsMirrorKnownOps(t *testing.T) {
	root := repoRoot(t)
	for _, msg := range checkCSharpKnownOps(KnownOps, scrapeCSharpKnownOps(t, root)) {
		t.Error(msg)
	}
}

// ── the C# runtime ──────────────────────────────────────────────────────────

// csharpDispatch is where the C# side actually decides what an op does, scraped
// from the two switches that would really run plus the shell's registrations.
type csharpDispatch struct {
	player map[string]bool // case labels in LvnPlayer.Advance
	stage  map[string]bool // case labels in VnStage.ApplyStage
	shell  map[string]bool // LvnOps.Register(...) in com.lvn.engine.shell
}

func checkCSharpDispatch(ops map[string]bool, owners ownersFile, d csharpDispatch) []string {
	var bad []string
	add := func(format string, a ...any) { bad = append(bad, fmt.Sprintf(format, a...)) }

	// Neither dispatch site may switch on something that is not an op at all —
	// that would be a handler nobody can ever reach from a valid .lvn.
	for _, site := range []struct {
		name  string
		cases map[string]bool
	}{{"LvnPlayer.Advance", d.player}, {"VnStage.ApplyStage", d.stage}} {
		for op := range site.cases {
			if !ops[op] {
				add("%s handles %q, which is not in KnownOps", site.name, op)
			}
		}
	}

	for op, row := range owners.Ops {
		inPlayer, inStage := d.player[op], d.stage[op]
		switch row.CSharp {
		case "player":
			if !inPlayer {
				add("op %q is declared csharp=player but LvnPlayer.Advance has no case for it — "+
					"without a case it falls into default: and is silently forwarded to a stage that ignores it", op)
			}
			if inStage {
				add("op %q is declared csharp=player but VnStage.ApplyStage also handles it (declare player+stage)", op)
			}
		case "stage":
			if !inStage {
				add("op %q is declared csharp=stage but VnStage.ApplyStage has no case for it — silent no-op", op)
			}
			if inPlayer {
				add("op %q is declared csharp=stage but LvnPlayer.Advance also handles it (declare player+stage)", op)
			}
		case "player+stage":
			if !inPlayer || !inStage {
				add("op %q is declared csharp=player+stage but is handled by player=%v stage=%v", op, inPlayer, inStage)
			}
		case "shell-op":
			if inPlayer || inStage {
				add("op %q is declared shell-owned, yet the engine package dispatches it — "+
					"move the row to owner=engine or remove the engine handler", op)
			}
			if !d.shell[op] {
				add("op %q is declared csharp=shell-op but no LvnOps.Register(%q, …) exists in com.lvn.engine.shell", op, op)
			}
		}
	}

	// A shell registration for an op the table calls engine-owned means the
	// public single-package install silently disagrees with the bundled app.
	for op := range d.shell {
		if row, ok := owners.Ops[op]; ok && row.Owner != "shell" {
			add("com.lvn.engine.shell registers %q, but ops-owners.json says owner=%q", op, row.Owner)
		}
	}
	sort.Strings(bad)
	return bad
}

func scrapeCSharp(t *testing.T, root string) csharpDispatch {
	t.Helper()
	return csharpDispatch{
		player: caseLabels(t, filepath.Join(root,
			"unity/Packages/com.lvn.engine/Runtime/LvnPlayer.cs"), "public void Advance()"),
		stage: caseLabels(t, filepath.Join(root,
			"unity/Packages/com.lvn.engine/Runtime/UI/VnStage.Commands.cs"), "public void ApplyStage(JObject command)"),
		// Хост-опы регистрируются НЕ ТОЛЬКО в оболочке: сервисный слой
		// (кошелёк, реклама, лидерборды, аналитика) — такой же хост и
		// поставляется тем же пакетом продуктов. Искать только в shell значит
		// объявить несуществующим всё, что регистрирует services.
		shell: mergeOps(
			registeredOps(t, filepath.Join(root, "unity/Packages/com.lvn.engine.shell")),
			registeredOps(t, filepath.Join(root, "unity/Packages/com.lvn.engine.services")),
		),
	}
}

// mergeOps — объединение двух наборов зарегистрированных опов.
func mergeOps(a, b map[string]bool) map[string]bool {
	out := make(map[string]bool, len(a)+len(b))
	for k := range a {
		out[k] = true
	}
	for k := range b {
		out[k] = true
	}
	return out
}

func TestEngineOwnedOpsHaveCSharpHandlers(t *testing.T) {
	owners, root := loadOwners(t)
	for _, msg := range checkCSharpDispatch(KnownOps, owners, scrapeCSharp(t, root)) {
		t.Error(msg)
	}
}

// TestGuardBitesOnADriftedOp is the guard's own guard. A contract test that
// passes because it checks nothing is worse than no test, so every half is run
// against a DOCTORED dictionary here: an op nobody declared, an op declared
// engine-owned with no handler anywhere, and a C# registry that has fallen a step
// behind the language (and one that has run ahead of it). Each must be reported
// by name.
func TestGuardBitesOnADriftedOp(t *testing.T) {
	owners, root := loadOwners(t)
	dispatch := scrapeCSharp(t, root)

	// 1. A new op lands in KnownOps and nobody fills in the table.
	drifted := map[string]bool{"fictional_op": true}
	for op := range KnownOps {
		drifted[op] = true
	}
	msgs := checkOwnersCoverOps(drifted, owners)
	if !anyContains(msgs, "fictional_op") {
		t.Errorf("an op added to KnownOps without a table row went UNREPORTED — the guard is asleep; got %v", msgs)
	}

	// 2. The table row exists and claims the engine handles it, but no switch does.
	doctored := ownersFile{Ops: map[string]opOwner{}}
	for op, row := range owners.Ops {
		doctored.Ops[op] = row
	}
	doctored.Ops["fictional_op"] = opOwner{Owner: "engine", CSharp: "stage"}
	msgs = checkCSharpDispatch(drifted, doctored, dispatch)
	if !anyContains(msgs, "fictional_op") {
		t.Errorf("an engine-owned op with no C# handler went UNREPORTED — the guard is asleep; got %v", msgs)
	}

	// 3. The public C# registry falls behind the language — the exact defect this
	//    half was written for: StagingOps.Known once held 24 of 30 ops with no
	//    symptom, because nothing dispatches on it.
	csharp := scrapeCSharpKnownOps(t, root)
	msgs = checkCSharpKnownOps(drifted, csharp)
	if !anyContains(msgs, "fictional_op") {
		t.Errorf("an op missing from Lvn.StagingOps.Known went UNREPORTED — the guard is asleep; got %v", msgs)
	}
	// …and the other direction: a C# registry naming something the language dropped.
	ahead := map[string]bool{"fictional_op": true}
	for op := range csharp {
		ahead[op] = true
	}
	msgs = checkCSharpKnownOps(KnownOps, ahead)
	if !anyContains(msgs, "fictional_op") {
		t.Errorf("a stale op left in Lvn.StagingOps.Known went UNREPORTED — the guard is asleep; got %v", msgs)
	}

	// 4. And the real tables must still be clean, or (1)–(3) prove nothing.
	if msgs := checkOwnersCoverOps(KnownOps, owners); len(msgs) != 0 {
		t.Errorf("undoctored table already fails: %v", msgs)
	}
	if msgs := checkCSharpKnownOps(KnownOps, csharp); len(msgs) != 0 {
		t.Errorf("undoctored C# registry already fails: %v", msgs)
	}
}

func anyContains(msgs []string, needle string) bool {
	for _, m := range msgs {
		if strings.Contains(m, needle) {
			return true
		}
	}
	return false
}

// ── the corpus ──────────────────────────────────────────────────────────────

type confCase struct {
	ID       string            `json:"id"`
	Title    string            `json:"title"`
	Why      string            `json:"why"`
	Runtimes []string          `json:"runtimes"`
	Picks    []json.RawMessage `json:"picks"`
	Inputs   []string          `json:"inputs"`
	Doc      json.RawMessage   `json:"doc"`
	Expect   struct {
		Stops     []map[string]json.RawMessage `json:"stops"`
		Vars      map[string]any               `json:"vars"`
		ExprTrue  []string                     `json:"expr_true"`
		ExprFalse []string                     `json:"expr_false"`
		Stage     []map[string]any             `json:"stage"`
		Scene     json.RawMessage              `json:"scene"`
		Labels    []string                     `json:"labels"`
	} `json:"expect"`
}

func TestConformanceCasesWellFormed(t *testing.T) {
	owners, root := loadOwners(t)
	dir := filepath.Join(root, "conformance", "cases")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("conformance/cases unreadable: %v", err)
	}
	seen := map[string]string{}
	files := 0
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		files++
		name := e.Name()
		data, err := os.ReadFile(filepath.Join(dir, name))
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		var c confCase
		if err := json.Unmarshal(data, &c); err != nil {
			t.Errorf("%s: invalid JSON: %v", name, err)
			continue
		}
		if c.ID == "" || c.Title == "" || c.Why == "" {
			t.Errorf("%s: id/title/why are all required (a case whose failure can't be read gets deleted)", name)
		}
		if prev, dup := seen[c.ID]; dup {
			t.Errorf("%s: duplicate case id %q (also in %s)", name, c.ID, prev)
		}
		seen[c.ID] = name

		if len(c.Runtimes) == 0 {
			t.Errorf("%s: runtimes must list at least one runtime", name)
		}
		for _, r := range c.Runtimes {
			if r != "csharp" {
				t.Errorf("%s: unknown runtime %q", name, r)
			}
		}
		if len(c.Expect.Stops) == 0 {
			t.Errorf("%s: expect.stops is the spine of a case — assert the stop trace", name)
		}

		// The driving inputs must line up with the stops the case predicts,
		// otherwise the runner starves (or silently ignores leftovers).
		choices, inputs := 0, 0
		for _, stop := range c.Expect.Stops {
			for kind := range stop {
				switch kind {
				case "say", "wait", "end":
				case "choice":
					choices++
				case "input":
					inputs++
				default:
					t.Errorf("%s: unknown stop kind %q", name, kind)
				}
			}
		}
		if len(c.Picks) != choices {
			t.Errorf("%s: %d choice stops but %d picks", name, choices, len(c.Picks))
		}
		if len(c.Inputs) != inputs {
			t.Errorf("%s: %d input stops but %d inputs", name, inputs, len(c.Inputs))
		}

		// The document must be a real, valid .lvn built from real ops.
		doc, err := Parse(c.Doc)
		if err != nil {
			t.Errorf("%s: doc is not a .lvn: %v", name, err)
			continue
		}
		for _, issue := range Validate(doc) {
			if issue.Sev == SevError {
				t.Errorf("%s: doc fails validation: %s", name, issue)
			}
		}
		for _, op := range opsUsed(doc) {
			row, ok := owners.Ops[op]
			if !ok {
				t.Errorf("%s: uses op %q which is not in the ownership table", name, op)
				continue
			}
			_ = row
		}
	}
	if files == 0 {
		t.Fatal("conformance/cases holds no cases")
	}
}

// TestCorpusCoversTheCoreOps keeps the corpus honest about its own reach: the
// flow/state/text ops are the ones every runtime must agree on, so none of them
// may sit uncovered. Staging ops are covered representatively (bg/actor/fade),
// not exhaustively — the ownership table is what guards the rest.
func TestCorpusCoversTheCoreOps(t *testing.T) {
	_, root := loadOwners(t)
	covered := map[string]bool{}
	entries, err := os.ReadDir(filepath.Join(root, "conformance", "cases"))
	if err != nil {
		t.Fatalf("conformance/cases unreadable: %v", err)
	}
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		data, err := os.ReadFile(filepath.Join(root, "conformance", "cases", e.Name()))
		if err != nil {
			t.Fatal(err)
		}
		var c confCase
		if err := json.Unmarshal(data, &c); err != nil {
			continue // reported by TestConformanceCasesWellFormed
		}
		if doc, err := Parse(c.Doc); err == nil {
			for _, op := range opsUsed(doc) {
				covered[op] = true
			}
		}
	}
	core := []string{"say", "choice", "label", "goto", "if", "call", "return",
		"set", "inc", "input", "wait", "bg", "actor", "fade"}
	for _, op := range core {
		if !covered[op] {
			t.Errorf("no conformance case exercises %q — the runtimes are free to disagree about it", op)
		}
	}
}

// opsUsed lists the distinct ops a document uses, choice option bodies included.
func opsUsed(d *Doc) []string {
	set := map[string]bool{}
	var visit func(cmds []Cmd)
	visit = func(cmds []Cmd) {
		for _, c := range cmds {
			if op := c.Op(); op != "" {
				set[op] = true
			}
			opts, _ := c["options"].([]any)
			for _, o := range opts {
				om, ok := o.(map[string]any)
				if !ok {
					continue
				}
				body, ok := om["body"].([]any)
				if !ok {
					continue
				}
				var inner []Cmd
				for _, b := range body {
					if bm, ok := b.(map[string]any); ok {
						inner = append(inner, Cmd(bm))
					}
				}
				visit(inner)
			}
		}
	}
	visit(d.Script)
	out := make([]string, 0, len(set))
	for op := range set {
		out = append(out, op)
	}
	sort.Strings(out)
	return out
}

// ── source scraping ─────────────────────────────────────────────────────────

var (
	reCaseLabel = regexp.MustCompile(`\bcase\s+"([^"]*)"`)
	reRegister  = regexp.MustCompile(`LvnOps\.Register\(\s*"([^"]+)"`)
	reQuoted    = regexp.MustCompile(`"([^"]*)"`)
)

// caseLabels returns the string `case` labels inside the C# method whose
// signature contains sig. Reading the dispatch site itself is the only honest
// check: a table that claims a handler exists must be checkable against the
// switch that would actually run.
func caseLabels(t *testing.T, path, sig string) map[string]bool {
	t.Helper()
	body := methodBody(t, path, sig)
	out := map[string]bool{}
	for _, m := range reCaseLabel.FindAllStringSubmatch(body, -1) {
		out[m[1]] = true
	}
	if len(out) == 0 {
		t.Fatalf("%s: no case labels found in %q — did the dispatch site move?", filepath.Base(path), sig)
	}
	return out
}

// methodBody extracts the brace-balanced body that follows sig, with comments
// and string literals stripped so neither can throw off the brace count.
func methodBody(t *testing.T, path, sig string) string {
	t.Helper()
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("%s unreadable: %v", path, err)
	}
	src := stripCommentsAndStrings(string(raw))
	at := strings.Index(src, sig)
	if at < 0 {
		t.Fatalf("%s: signature %q not found — the dispatch site was renamed; fix this test's anchor", path, sig)
	}
	open := strings.IndexByte(src[at:], '{')
	if open < 0 {
		t.Fatalf("%s: no body after %q", path, sig)
	}
	i := at + open
	depth := 0
	for j := i; j < len(src); j++ {
		switch src[j] {
		case '{':
			depth++
		case '}':
			depth--
			if depth == 0 {
				return src[i : j+1]
			}
		}
	}
	t.Fatalf("%s: unbalanced braces after %q", path, sig)
	return ""
}

// stripCommentsAndStrings blanks out comments and the CONTENTS of string
// literals, keeping the quotes and every byte offset-neutral construct we care
// about: `case "x"` survives because we keep short literals intact, while
// braces and slashes inside longer strings can no longer be miscounted.
func stripCommentsAndStrings(src string) string {
	var b strings.Builder
	b.Grow(len(src))
	for i := 0; i < len(src); i++ {
		c := src[i]
		switch {
		case c == '/' && i+1 < len(src) && src[i+1] == '/':
			for i < len(src) && src[i] != '\n' {
				i++
			}
			b.WriteByte('\n')
		case c == '/' && i+1 < len(src) && src[i+1] == '*':
			i += 2
			for i+1 < len(src) && !(src[i] == '*' && src[i+1] == '/') {
				i++
			}
			i++
		case c == '"' || c == '\'' || c == '`':
			quote := c
			b.WriteByte(quote)
			i++
			var lit strings.Builder
			for i < len(src) && src[i] != quote {
				if src[i] == '\\' && i+1 < len(src) {
					i++ // an escaped quote is not the closing one
					lit.WriteByte(' ')
					i++
					continue
				}
				lit.WriteByte(src[i])
				i++
			}
			// Keep the literal only when it can't disturb brace counting — that
			// is enough for the `case "op"` labels this scraper is after.
			s := lit.String()
			if strings.ContainsAny(s, "{}") {
				s = strings.Repeat(" ", len(s))
			}
			b.WriteString(s)
			if i < len(src) {
				b.WriteByte(quote)
			}
		default:
			b.WriteByte(c)
		}
	}
	return b.String()
}

// registeredOps collects every LvnOps.Register("op", …) under dir (a package),
// skipping tests and samples — a test double or a sample plugin is not a
// shipped handler.
func registeredOps(t *testing.T, dir string) map[string]bool {
	t.Helper()
	out := map[string]bool{}
	err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if info.IsDir() {
			if strings.Contains(info.Name(), "Tests") || strings.HasSuffix(info.Name(), "~") {
				return filepath.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(path, ".cs") {
			return nil
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		for _, m := range reRegister.FindAllStringSubmatch(string(data), -1) {
			out[m[1]] = true
		}
		return nil
	})
	if err != nil {
		t.Fatalf("scanning %s: %v", dir, err)
	}
	return out
}

// The SIXTH mirror of the op dictionary: the Unity .lvns compiler carries its
// own KnownOps HashSet (Editor/LvnsCompiler.cs). It had fallen six entries
// behind convert.go, and the cost is unusually high — a word missing from that
// set does not error, it falls into the narration branch and PRINTS ITSELF to
// the player. `input var=…` rendered as a dialogue line in the Unity import
// path, which the README advertises as the two-minute way in.
//
// Same scrape-and-diff trick the other mirrors use. Constructs the Unity
// compiler knowingly does not lower are listed in UnsupportedSourceOps there
// and are expected to be absent here — the test asserts that gap is DECLARED,
// so "not implemented" can never quietly become "silently narration" again.
func TestUnityCompilerKnownOpsMirrorsSource(t *testing.T) {
	csPath := filepath.Join("..", "..", "..", "unity", "Packages", "com.lvn.engine",
		"Editor", "LvnsCompiler.cs")
	src, err := os.ReadFile(csPath)
	if err != nil {
		t.Skipf("Unity package not present: %v", err)
	}
	clean := stripCommentsAndStrings(string(src))
	_ = clean // the sets below are read from the raw file: they ARE string literals

	known := literalSet(t, string(src), "KnownOps = new HashSet<string>")
	unsupported := literalSet(t, string(src), "UnsupportedSourceOps = new Dictionary<string, string>")
	if len(known) == 0 {
		t.Fatal("could not scrape KnownOps from LvnsCompiler.cs — the anchor moved; fix this test, do not delete it")
	}
	if len(unsupported) == 0 {
		t.Fatal("could not scrape UnsupportedSourceOps from LvnsCompiler.cs — the anchor moved")
	}

	for op := range lvnsSourceOps() {
		if known[op] || unsupported[op] {
			continue
		}
		t.Errorf("`%s` is a .lvns construct but the Unity compiler neither lists it in KnownOps "+
			"nor declares it in UnsupportedSourceOps — a line starting with it becomes DIALOGUE TEXT "+
			"on screen. Add it to one of the two sets in Editor/LvnsCompiler.cs.", op)
	}
	src2 := lvnsSourceOps()
	for op := range known {
		if !src2[op] {
			t.Errorf("the Unity compiler lists `%s` in KnownOps, but the reference compiler does not "+
				"know it — remove it or add it to convert.go", op)
		}
	}
}

// literalSet scrapes the quoted names out of a C# collection initialiser.
func literalSet(t *testing.T, src, anchor string) map[string]bool {
	t.Helper()
	at := strings.Index(src, anchor)
	if at < 0 {
		return nil
	}
	open := strings.Index(src[at:], "{")
	if open < 0 {
		return nil
	}
	rest := src[at+open+1:]
	end := strings.Index(rest, "};")
	if end < 0 {
		return nil
	}
	out := map[string]bool{}
	for _, m := range regexp.MustCompile(`"([a-z_]+)"`).FindAllStringSubmatch(rest[:end], -1) {
		out[m[1]] = true
	}
	return out
}

// lvnsSourceOps is the reference .lvns vocabulary (internal/lvns KnownOps),
// scraped from source so this test needs no import of an internal package.
func lvnsSourceOps() map[string]bool {
	b, err := os.ReadFile(filepath.Join("..", "internal", "lvns", "convert.go"))
	if err != nil {
		return nil
	}
	at := strings.Index(string(b), "KnownOps = map[string]bool{")
	if at < 0 {
		return nil
	}
	rest := string(b)[at:]
	end := strings.Index(rest, "\n}")
	if end < 0 {
		return nil
	}
	out := map[string]bool{}
	for _, m := range regexp.MustCompile(`"([a-z_]+)"`).FindAllStringSubmatch(rest[:end], -1) {
		out[m[1]] = true
	}
	return out
}
