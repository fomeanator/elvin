package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// howto/CAPABILITIES.md is not prose: with an AI agent writing the games, the
// documentation IS the API. A contradiction inside it ("defanim works" / "defanim
// is planned") is not noise the reader filters out — it is a direct cause of wrong
// generated code, and a feature documented but implemented nowhere (see the `func`
// story) survives exactly as long as nobody compiles it. So the doc is pinned
// mechanically, the same way grammar_sync_test.go pins the op tables:
//
//	1. the §1 op catalog == validate.go KnownOps, both directions — no phantom
//	   op in the docs, no undocumented op in the code;
//	2. the file never both claims and denies the same construct;
//	3. nothing the file says WORKS is without a witness: some example that CI
//	   already compiles and validates to zero warnings must exercise it.
//
// Rule 3 is what makes phantom features impossible as a class. The witness
// examples are howto/*/*.lvns + examples/*.lvns (the "compile-gate every authored
// example" CI step); howto/every-command/ exists to host the leftovers.
//
// The parser deliberately understands only a few very regular shapes (see
// parseCapabilities). A false red here would get the gate switched off, so it
// prefers checking less over guessing: constructs are named in backticks,
// polarity comes from the ✅/❌ that leads a status cell, and a "⚠" cell is
// read as neither.

const capsDocPath = "howto/CAPABILITIES.md"

// Repo root as seen from this package's directory (tools/lvnconv/lvn).
func capsRepoRoot() string { return filepath.Join("..", "..", "..") }

var (
	capsTok   = regexp.MustCompile("`([^`]+)`")
	capsName  = regexp.MustCompile(`^[A-Za-z_][A-Za-z0-9_]*(=[A-Za-z0-9_.]*)?$`)
	capsBold  = regexp.MustCompile(`\*\*([^*]+)\*\*`)
	capsNoRun = regexp.MustCompile("(?i)there is no ((?:`[^`]+`[/, ]*(?:or |and )?)+)")
	capsHead  = regexp.MustCompile(`^## (\d+)\.`)
)

// capsDoc is the doc reduced to the statements a machine can check: which ops it
// catalogs, and which constructs it says do (or do not) work, with the line
// numbers to point a human at. A construct is keyed by its NAME across the whole
// file — the doc must not use one name for two different things.
type capsDoc struct {
	ops     map[string]int    // §1 catalog: op → line
	works   map[string][]int  // construct → lines claiming "✅ it works"
	absent  map[string][]int  // construct → lines claiming "❌ it does not exist"
	options map[string]string // §3 choice option field → its Type cell
}

// names pulls the construct names out of a markdown fragment: backticked tokens
// that look like an identifier or a key=value (so `.lvns`, `VnStage.cs` and
// prose like `t:v …` are not mistaken for constructs).
func capsNames(fragment string) []string {
	var out []string
	for _, m := range capsTok.FindAllStringSubmatch(fragment, -1) {
		tok := m[1]
		if !capsName.MatchString(tok) {
			continue
		}
		if tok == "true" || tok == "false" { // bare literals, not constructs
			continue
		}
		out = append(out, tok)
	}
	return out
}

// parseCapabilities reads the doc and extracts, from a handful of regular
// shapes only:
//
//	§1 catalog row  | `op` | what it does | fields |        → a catalogued op
//	status row      | `thing` … | ✅/❌ …  |                → a claim about `thing`
//	limit row       | ✅/❌ **`thing` …** | workaround |     → a claim about `thing`
//	prose/limit     … there is no `a`/`b` …                 → `a`, `b` do not exist
//	§3 option row   | `field` | functional/display/… | … |  → an option field
//
// Anything else in the file is prose the gate does not read.
func parseCapabilities(t *testing.T) capsDoc {
	t.Helper()
	path := filepath.Join(capsRepoRoot(), capsDocPath)
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("%s (the capability contract) unreadable: %v", capsDocPath, err)
	}
	d := capsDoc{
		ops:     map[string]int{},
		works:   map[string][]int{},
		absent:  map[string][]int{},
		options: map[string]string{},
	}
	section := ""
	for i, raw := range strings.Split(string(data), "\n") {
		ln := i + 1
		line := strings.TrimSpace(raw)
		if m := capsHead.FindStringSubmatch(line); m != nil {
			section = m[1]
		}
		// The "there is no `x`" phrasing is a denial wherever it appears.
		for _, m := range capsNoRun.FindAllStringSubmatch(line, -1) {
			for _, n := range capsNames(m[1]) {
				d.absent[n] = append(d.absent[n], ln)
			}
		}
		if !strings.HasPrefix(line, "|") {
			continue
		}
		cells := strings.Split(strings.Trim(line, "|"), "|")
		for j := range cells {
			cells[j] = strings.TrimSpace(cells[j])
		}
		if len(cells) < 2 {
			continue
		}
		lead := func(s, marker string) bool { return strings.HasPrefix(s, marker) }

		// §1 — the op catalog. Three-cell rows named by a backticked op.
		if section == "1" && len(cells) == 3 && strings.HasPrefix(cells[0], "`") {
			for _, n := range capsNames(cells[0]) {
				d.ops[n] = ln
			}
		}
		// §3 — the choice option field table.
		if section == "3" && len(cells) == 3 && strings.HasPrefix(cells[0], "`") {
			for _, n := range capsNames(cells[0]) {
				d.options[n] = cells[1]
			}
		}
		// A status row: the construct is named in the first cell, the verdict
		// leads the second (§6's animation matrix, §1's host-op table).
		if len(cells) == 2 && (lead(cells[1], "✅") || lead(cells[1], "❌")) {
			m := d.works
			if lead(cells[1], "❌") {
				m = d.absent
			}
			for _, n := range capsNames(cells[0]) {
				m[n] = append(m[n], ln)
			}
		}
		// A limit row (§8): the verdict leads the first cell and the construct
		// sits in its bolded subject. Only the subject counts — the rest of the
		// cell qualifies the claim ("no `if` INSIDE a body", "`wait ms=` is only
		// a fixed pause") and reading it would produce false contradictions.
		if lead(cells[0], "✅") || lead(cells[0], "❌") {
			m := d.works
			if lead(cells[0], "❌") {
				m = d.absent
			}
			if b := capsBold.FindStringSubmatch(cells[0]); b != nil {
				for _, n := range capsNames(b[1]) {
					m[n] = append(m[n], ln)
				}
			}
		}
	}
	// A gate that quietly parses nothing is worse than no gate: it reports green
	// forever. Fail loudly if the shapes above stopped matching the document.
	if d.ops["say"] == 0 || d.ops["goto"] == 0 || len(d.works) == 0 ||
		len(d.absent) == 0 || d.options["goto"] == "" {
		t.Fatalf("%s: the tables this gate reads no longer parse (ops=%d works=%d absent=%d "+
			"options=%d) — fix parseCapabilities instead of letting the gate pass vacuously",
			capsDocPath, len(d.ops), len(d.works), len(d.absent), len(d.options))
	}
	return d
}

// capsHostOps are ops the reference engine deliberately does NOT implement: they are
// registered by a host package, so no engine-only example can exercise them and
// rule 3 would be unsatisfiable. The list must stay tiny and each entry must say
// who owns the op (the ownership table lives in conformance/).
var capsHostOps = map[string]string{
	"wardrobe_show": "owned by com.lvn.engine.shell (NovelApp registers it, WardrobeSheet draws it); " +
		"a bare com.lvn.engine host silently ignores it, so no howto example can be its witness",
}

func TestCapabilitiesOpCatalogMatchesKnownOps(t *testing.T) {
	d := parseCapabilities(t)
	for op, ln := range d.ops {
		if !KnownOps[op] {
			t.Errorf("%s:%d documents op %q, which is not in KnownOps — either a phantom "+
				"in the docs or an op the validator forgot", capsDocPath, ln, op)
		}
	}
	var missing []string
	for op := range KnownOps {
		if _, ok := d.ops[op]; !ok {
			missing = append(missing, op)
		}
	}
	sort.Strings(missing)
	for _, op := range missing {
		t.Errorf("op %q is in KnownOps but nowhere in the %s §1 catalog — an op an agent "+
			"can emit and no document describes", op, capsDocPath)
	}
}

func TestCapabilitiesHasNoSelfContradiction(t *testing.T) {
	d := parseCapabilities(t)
	both := map[string]bool{}
	for name := range d.works {
		if _, ok := d.absent[name]; ok {
			both[name] = true
		}
	}
	var keys []string
	for name := range both {
		keys = append(keys, name)
	}
	sort.Strings(keys)
	for _, name := range keys {
		t.Errorf("%s contradicts itself about %q: works at %s, absent at %s — check the "+
			"sources and delete the wrong half; an agent reading this file cannot tell "+
			"which half to believe", capsDocPath, name,
			capsLines(d.works[name]), capsLines(d.absent[name]))
	}
	// The §1 catalog is itself a "this works" claim: an op cannot be catalogued
	// as a runtime command and declared nonexistent elsewhere in the file. This
	// is exactly how "no player text input (there is no `input`)" survived next
	// to a documented, implemented `input` op.
	for name, lines := range d.absent {
		if ln, ok := d.ops[name]; ok {
			t.Errorf("%s:%d catalogs %q as a working op, but %s says it does not exist",
				capsDocPath, ln, name, capsLines(lines))
		}
	}
}

// capsLines renders claim line numbers as clickable file:line references.
func capsLines(lines []int) string {
	var parts []string
	for _, ln := range lines {
		parts = append(parts, fmt.Sprintf("%s:%d", capsDocPath, ln))
	}
	return strings.Join(parts, ", ")
}

// capsWitness is everything the gated examples demonstrably compile: the ops they
// emit, every field key and scalar value in the compiled commands (including
// `key=value` pairs, so `mode=queue` or `interp=spline` can be matched), and the
// tokens present in the sources with comments stripped (so `defanim`, which emits
// no command at all, still has a witness).
type capsWitness struct {
	ops    map[string]bool
	tokens map[string]bool
	source string
}

func capsCollectWitnesses(t *testing.T) capsWitness {
	t.Helper()
	root := capsRepoRoot()
	var files []string
	for _, pat := range []string{
		filepath.Join(root, "howto", "*", "*.lvns"),
		filepath.Join(root, "examples", "*.lvns"),
	} {
		got, err := filepath.Glob(pat)
		if err != nil {
			t.Fatalf("glob %s: %v", pat, err)
		}
		files = append(files, got...)
	}
	if len(files) == 0 {
		t.Fatalf("no gated examples found under howto/ or examples/ — rule 3 cannot be checked")
	}
	w := capsWitness{ops: map[string]bool{}, tokens: map[string]bool{}}
	var code []string
	for _, f := range files {
		src, err := os.ReadFile(f)
		if err != nil {
			t.Fatalf("read %s: %v", f, err)
		}
		for _, line := range strings.Split(string(src), "\n") {
			if i := strings.Index(line, "//"); i >= 0 {
				line = line[:i] // a mention in a comment is not a witness
			}
			code = append(code, line)
		}
		// ФАЙЛОМ, а не текстом: у ConvertFile есть путь, относительно которого
		// резолвятся include. Гейт, компилирующий текст, проверяет не то, что
		// произойдёт при сборке, — и первый же пример с include ломает его,
		// хотя сам пример совершенно законен (поймано на живой правке).
		doc, err := lvns.ConvertFile(f)
		if err != nil {
			// The compile gate in CI covers this; failing here too keeps the
			// witness set from silently shrinking.
			t.Errorf("gated example %s no longer compiles: %v", f, err)
			continue
		}
		for _, cmd := range doc.Script {
			if op, _ := cmd["op"].(string); op != "" {
				w.ops[op] = true
			}
			capsWalkWitness(map[string]any(cmd), w.tokens)
		}
	}
	w.source = strings.Join(code, "\n")
	return w
}

// walkWitness records, for every object in a compiled command, its keys, its
// scalar values and their `key=value` pairing.
func capsWalkWitness(v any, out map[string]bool) {
	switch x := v.(type) {
	case map[string]any:
		for k, val := range x {
			out[k] = true
			if s := capsScalar(val); s != "" {
				out[s] = true
				out[k+"="+s] = true
			}
			capsWalkWitness(val, out)
		}
	case []any:
		for _, item := range x {
			capsWalkWitness(item, out)
		}
	}
}

func capsScalar(v any) string {
	switch x := v.(type) {
	case string:
		return x
	case bool:
		return strconv.FormatBool(x)
	case float64:
		return strconv.FormatFloat(x, 'f', -1, 64)
	case int:
		return strconv.Itoa(x)
	}
	return ""
}

func (w capsWitness) has(token string) bool {
	if w.tokens[token] {
		return true
	}
	// Whole-token match in the sources: `defanim`, `play`, `orient=true` …
	re := regexp.MustCompile(`(^|[^A-Za-z0-9_])` + regexp.QuoteMeta(token) + `($|[^A-Za-z0-9_])`)
	return re.MatchString(w.source)
}

func TestDocumentedConstructsHaveAWitnessExample(t *testing.T) {
	d := parseCapabilities(t)
	w := capsCollectWitnesses(t)
	const fix = "add it to a gated example (howto/every-command/ exists for exactly this) " +
		"or drop the claim — an undocumented working feature is safer than a documented phantom"

	var ops []string
	for op := range d.ops {
		ops = append(ops, op)
	}
	sort.Strings(ops)
	for _, op := range ops {
		if w.ops[op] || capsHostOps[op] != "" {
			continue
		}
		t.Errorf("op %q is documented (%s:%d) but no gated example compiles into it — %s",
			op, capsDocPath, d.ops[op], fix)
	}

	// ПРЕФИКСНЫЕ КОМАНДЫ — тоже конструкции, и свидетель им нужен так же.
	//
	// Правило выше проверяет ОПЕРАЦИИ, а `voice <url>` операцией не становится:
	// он приклеивается полем к следующей реплике. Из-за этого озвучка —
	// описанная в CAPABILITIES, реализованная в обоих компиляторах и в плеере —
	// не имела НИ ОДНОГО компилируемого примера, и гейт молчал: смотрел не туда.
	//
	// Свидетелем считается поле в собранном документе: так проверяется вся
	// цепочка (синтаксис → компилятор → поле команды), а не наличие слова в
	// исходнике.
	for _, field := range []string{"voice"} {
		if !w.tokens[field] {
			t.Errorf("конструкция %q документирована, но ни один пример в неё не компилируется — %s",
				field, fix)
		}
	}

	// `ext <op> k=v` — ЕДИНСТВЕННАЯ дверь к операции, которой у движка нет:
	// игра регистрирует её сама. В документе от неё не остаётся ни слова «ext»,
	// ни отдельного поля — компилируется она в команду с ИМЕНЕМ ХОСТ-ОПЕРАЦИИ,
	// поэтому проверять надо не токен, а факт: есть ли среди собранных команд
	// хоть одна, чей op не знает движок. Без такой проверки шов, на котором
	// держится расширяемость языка, оставался без единого примера.
	extWitness := false
	for op := range w.ops {
		if !KnownOps[op] {
			extWitness = true
			break
		}
	}
	if !extWitness {
		t.Errorf("`ext <op>` документирован как escape hatch, но ни один пример им не пользуется — %s", fix)
	}

	var claimed []string
	for name := range d.works {
		claimed = append(claimed, name)
	}
	sort.Strings(claimed)
	for _, name := range claimed {
		if w.has(name) || capsHostOps[name] != "" {
			continue
		}
		t.Errorf("%s claims %q works (%s) but no gated example uses it — %s",
			capsDocPath, name, capsLines(d.works[name]), fix)
	}

	// §3's choice option fields: the ones the table calls functional must be
	// exercised too. `body` is marked "(.lvn only)" because no .lvns syntax
	// produces it — that exemption lives in the doc, not here.
	var fields []string
	for f := range d.options {
		fields = append(fields, f)
	}
	sort.Strings(fields)
	for _, f := range fields {
		kind := d.options[f]
		if !strings.Contains(kind, "functional") || strings.Contains(kind, "`.lvn` only") {
			continue
		}
		if w.tokens[f] {
			continue
		}
		t.Errorf("%s documents choice option field %q as functional, but no gated example's "+
			"compiled choice carries it — %s", capsDocPath, f, fix)
	}
}

// ЖАНРОВОЕ РУКОВОДСТВО НЕ СПОРИТ С ГЛАВНЫМ СПРАВОЧНИКОМ.
//
// CAPABILITIES.md пинится тестами выше, а вот howto/<жанр>/README.md писались
// по ходу и стареют молча. Живой случай: два руководства учили автора, что
// команда `hint` — заглушка («at runtime it is a no-op»), и предлагали обходить
// её через `say`. На деле она давно рисует карточку с автоскрытием, и то же
// самое написано в CAPABILITIES строкой «✅ `hint` is rendered». Путаница
// пошла от ТЁЗКИ: поле `hint=` у опции выбора действительно игнорируется.
//
// Цена ошибки выше обычной: по этим руководствам учится ИИ-агент автора, и
// «команда не работает» он принимает как факт — навсегда переставая её
// применять.
//
// Проверка узкая и потому надёжная: если жанровый README называет команду
// нерабочей, а справочник её так не называет — расхождение внутри документации.
func TestGenreGuidesDoNotContradictTheCapabilities(t *testing.T) {
	root := capsRepoRoot()
	caps, err := os.ReadFile(filepath.Join(root, capsDocPath))
	if err != nil {
		t.Fatalf("%s: %v", capsDocPath, err)
	}
	capsText := string(caps)

	// «`cmd` … no-op» / «`cmd` … does not render» в пределах одного предложения.
	deadClaim := regexp.MustCompile("`([a-z_]+)`[^.\n]{0,160}?(no-op|does not render|is not implemented)")

	var clashes []string
	err = filepath.Walk(filepath.Join(root, "howto"), func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasSuffix(path, ".md") {
			return nil
		}
		if strings.HasSuffix(path, capsDocPath) || strings.Contains(path, "CAPABILITIES") {
			return nil // сам справочник пинится отдельно и вправе объявлять заглушки
		}
		body, err := os.ReadFile(path)
		if err != nil {
			return nil
		}
		for _, m := range deadClaim.FindAllStringSubmatch(string(body), -1) {
			op := m[1]
			if !KnownOps[op] {
				continue // не команда языка — обычная проза
			}
			// Справочник согласен, что она мертва? Тогда спора нет. Согласие
			// ищем В ТОЙ ЖЕ СТРОКЕ, где справочник называет команду: иначе
			// «silent no-op» про соседнюю команду гасит проверку для всех —
			// на этом первая версия теста и промолчала.
			agrees := false
			for _, line := range strings.Split(capsText, "\n") {
				if !strings.Contains(line, "`"+op+"`") {
					continue
				}
				if strings.Contains(line, "no-op") || strings.Contains(line, "does not render") ||
					strings.Contains(line, "is not implemented") {
					agrees = true
					break
				}
			}
			if agrees {
				continue
			}
			rel, _ := filepath.Rel(root, path)
			clashes = append(clashes, fmt.Sprintf("%s: называет %q нерабочей, а CAPABILITIES — нет", filepath.ToSlash(rel), op))
		}
		return nil
	})
	if err != nil {
		t.Fatalf("walk howto: %v", err)
	}
	sort.Strings(clashes)

	if len(clashes) > 0 {
		t.Fatalf("руководство спорит со справочником (%d):\n  %s\n\n"+
			"По этим файлам учится ИИ-агент автора: «команда не работает» он принимает как факт. "+
			"Либо руководство отстало — поправьте его, либо команда правда мертва — тогда скажите это и в CAPABILITIES.",
			len(clashes), strings.Join(clashes, "\n  "))
	}
}
