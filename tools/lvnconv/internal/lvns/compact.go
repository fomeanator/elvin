package lvns

// compact.go — the readability pass over GENERATED .lvns source.
//
// An articy import produces a formally correct script that no human wants to
// own: partner feedback, verbatim, is «код lvns это повторения на повторении
// одних и тех же fade и так далее, этим сложно управлять». The numbers back it
// up. One imported chapter in the tuning corpus spends 933 of its 1937 lines
// on `actor …` statements written out of only 56 distinct shapes, each ~104
// characters of `enter="fade" position=… sprite_url=…` repeated verbatim
// around every single line of dialogue. Across the corpus there are 31424
// `enter="fade"`/`exit="fade"` parameters and 36 actual `fade` commands: the
// «одни и те же fade» the partner is drowning in are not redundant commands to
// delete, they are one parameter restated 31424 times.
//
// Compact rewrites that source into an equivalent, far quieter one:
//
//   - the repeated head of a statement is hoisted into a `def` preset (already
//     a language feature) so the call site carries only what CHANGES;
//   - a `set key=… expr=…` becomes the assignment it always was (`Rel.Ivan =
//     Rel.Ivan +1`), including the dotted keys the decompiler used to refuse.
//
// THE RULE THAT GOVERNS ALL OF IT: compaction must not change the compiled
// document by one byte. Not "the same op counts" — the same JSON. Every
// transform here is therefore proposed, not applied: Compact recompiles both
// the input and its own output and hands back the ORIGINAL unless the two
// documents are byte-identical. A rule that is wrong on some input is a rule
// that silently does nothing there, never a rule that loses a command.
//
// What is deliberately NOT here: dropping a `fade`/`dim`/`tint`/`bg` that
// merely repeats the current state. That is real redundancy, and removing it
// really would shorten the file — by deleting a command from the compiled
// story. That is not compaction, it is loss, and it belongs (if anywhere) in a
// lint the author accepts by hand.

import (
	"encoding/json"
	"sort"
	"strconv"
	"strings"
)

// Compact returns an equivalent but much less repetitive rendering of
// generated .lvns source. It is a no-op (returns src unchanged) whenever the
// rewrite cannot be proven safe: unparseable input, or a compiled document
// that differs from the original by so much as one byte.
func Compact(src []byte) []byte {
	out := compactSource(string(src))
	if out == string(src) {
		return src
	}
	before, err := Convert(string(src))
	if err != nil {
		return src // input is not valid source — nothing to prove anything against
	}
	after, err := Convert(out)
	if err != nil {
		return src
	}
	a, err1 := json.Marshal(before)
	b, err2 := json.Marshal(after)
	if err1 != nil || err2 != nil || string(a) != string(b) {
		return src
	}
	return []byte(out)
}

// ── the source model ─────────────────────────────────────────────────────────

// kvTok is one `key=value` token, kept as written so a rewrite never has to
// re-quote (and so never has to re-derive) a value.
type kvTok struct{ key, text string }

// stmt is a statement the compactor understands: an op head followed by
// nothing but key=value tokens. Everything else (dialogue, labels, options,
// positional forms like `anim`/`text`/`voice`) is left untouched.
type stmt struct {
	idx    int // index into the line slice
	indent string
	head   string // "actor", "set", … — the words before the first k=v token
	toks   []kvTok
}

// presetableOps are the ops whose generic `op k=v …` spelling the parser reads
// back through parseKeyValue, so a `def` preset that reassembles exactly that
// spelling is guaranteed to parse the same way.
//
// Excluded on purpose: the positional grammars (`anim`, `move`, `play`,
// `defanim`, `text`, `voice`, `ext`, `call`, `goto`, `label`, `if`, `return`),
// where the first token is not a k=v pair; and `choice`, whose bare attribute
// line is consumed by pendingChoice and must stay adjacent to its options.
var presetableOps = map[string]bool{
	"actor": true, "bg": true, "obj": true,
	"fade": true, "dim": true, "flash": true, "tint": true, "blur": true,
	"camera": true, "particles": true,
	"audio": true, "wait": true, "input": true, "preload": true, "text_pace": true,
	"hint": true, "save": true, "load": true,
	"set": true, "inc": true,
	"wardrobe_show": true,
}

// reservedNames can never be a preset name: the parser dispatches on them
// before (or instead of) preset expansion, so shadowing one silently breaks a
// directive. KnownOps is rejected by the parser itself; these are the words it
// does not consider ops but still treats as syntax.
var reservedNames = map[string]bool{
	"def": true, "defanim": true, "scene": true, "actor_map": true,
	"func": true, "for": true, "while": true, "else": true, "in": true,
}

// splitKVTokens splits `k="v v" k2=3` into its tokens, keeping each one's
// source text. Reports false when the string is not made of k=v pairs alone.
func splitKVTokens(s string) ([]kvTok, bool) {
	var out []kvTok
	s = strings.TrimSpace(s)
	for s != "" {
		eq := strings.IndexByte(s, '=')
		if eq <= 0 {
			return nil, false
		}
		key := s[:eq]
		if strings.ContainsAny(key, " \t") || !isValidKey(key) {
			return nil, false
		}
		rest := s[eq+1:]
		if rest == "" {
			return nil, false
		}
		var vlen int
		if q := rest[0]; q == '"' || q == '\'' {
			end := -1
			for i := 1; i < len(rest); i++ {
				if rest[i] != q {
					continue
				}
				nb := 0
				for j := i - 1; j >= 1 && rest[j] == '\\'; j-- {
					nb++
				}
				if nb%2 == 0 {
					end = i
					break
				}
			}
			if end < 0 {
				return nil, false
			}
			vlen = end + 1
		} else {
			vlen = len(rest)
			if sp := strings.IndexAny(rest, " \t"); sp >= 0 {
				vlen = sp
			}
		}
		out = append(out, kvTok{key: key, text: s[:eq+1+vlen]})
		s = strings.TrimSpace(rest[vlen:])
	}
	return out, len(out) > 0
}

// parseStmt reads one source line as an op-with-parameters statement.
func parseStmt(idx int, raw string) (stmt, bool) {
	trimmed := strings.TrimLeft(raw, " \t")
	indent := raw[:len(raw)-len(trimmed)]
	if trimmed == "" || strings.ContainsAny(trimmed, "«»") {
		return stmt{}, false
	}
	sp := strings.IndexAny(trimmed, " \t")
	if sp <= 0 {
		return stmt{}, false
	}
	head := trimmed[:sp]
	if !presetableOps[head] {
		return stmt{}, false
	}
	toks, ok := splitKVTokens(trimmed[sp:])
	if !ok {
		return stmt{}, false
	}
	// A duplicate key would make "which value wins" depend on token order —
	// the one thing preset expansion changes. Leave such a line alone.
	seen := map[string]bool{}
	for _, t := range toks {
		if seen[t.key] {
			return stmt{}, false
		}
		seen[t.key] = true
	}
	return stmt{idx: idx, indent: indent, head: head, toks: toks}, true
}

// ── the passes ───────────────────────────────────────────────────────────────

func compactSource(src string) string {
	lines := strings.Split(src, "\n")

	// Lines inside a multi-line «…» are prose, not statements: never touch
	// them, and never let one masquerade as a candidate.
	prose := make([]bool, len(lines))
	depth := 0
	for i, l := range lines {
		if depth > 0 {
			prose[i] = true
		}
		depth = chevRun(depth, l)
		if depth < 0 {
			depth = 0
		}
	}

	terseAssignments(lines, prose)
	lines = presetFactor(lines, prose)
	return strings.Join(lines, "\n")
}

// terseAssignments rewrites `set key="A.b" expr="A.b +1"` as `A.b = A.b +1`.
//
// The decompiler already emits the assignment form for an expr set on a BARE
// identifier; it refuses a dotted one (`Relationships.Roman`) on the theory
// that the short form will not parse. It does: the parser's key rule
// (isValidKey) accepts dots and non-ASCII letters alike, which is why 826 of
// the tuning corpus's noisiest statements were spelled the long way for no
// reason at all.
//
// Only the expr form qualifies. `set key=… value=…` must keep its long
// spelling: an assignment always compiles to `expr`, so shortening a literal
// `value` would rewrite the command, not the text.
func terseAssignments(lines []string, prose []bool) {
	for i, raw := range lines {
		if prose[i] {
			continue
		}
		st, ok := parseStmt(i, raw)
		if !ok || st.head != "set" || len(st.toks) != 2 {
			continue
		}
		var key, expr string
		var haveKey, haveExpr bool
		for _, t := range st.toks {
			v, quoted := unquoteToken(t.text)
			switch t.key {
			case "key":
				key, haveKey = v, quoted
			case "expr":
				expr, haveExpr = v, quoted
			}
		}
		if !haveKey || !haveExpr || key == "" || expr == "" {
			continue
		}
		if !isValidKey(key) || KnownOps[key] || reservedNames[key] {
			continue
		}
		// The assignment form puts the expression on the line BARE, outside any
		// quoting: anything that reads as syntax there has to stay quoted.
		if strings.ContainsAny(expr, "«»{}\"'\n\r") || strings.Contains(expr, "//") ||
			strings.HasPrefix(expr, "-") || strings.HasPrefix(expr, "#") ||
			strings.HasPrefix(expr, ":") || strings.Contains(expr, "=") {
			continue
		}
		lines[i] = st.indent + key + " = " + expr
	}
}

// unquoteToken returns the value of a `k="v"` token and whether it was a
// quoted string (as opposed to a bare number/bool/identifier).
func unquoteToken(text string) (string, bool) {
	eq := strings.IndexByte(text, '=')
	if eq < 0 {
		return "", false
	}
	v := text[eq+1:]
	if len(v) >= 2 && (v[0] == '"' || v[0] == '\'') && v[len(v)-1] == v[0] {
		inner := v[1 : len(v)-1]
		inner = strings.ReplaceAll(inner, "\\\"", "\"")
		inner = strings.ReplaceAll(inner, "\\'", "'")
		return inner, true
	}
	return v, false
}

// minPresetGain is how many bytes a preset must save AT EACH CALL SITE before
// it is worth the indirection. A preset that saves ten characters buys nothing
// and costs the reader a name to look up; the actor boilerplate this pass
// exists for saves seventy.
const minPresetGain = 24

// minPresetUses is the support a preset needs. Two uses can already pay for a
// long template, but three is where a reader starts seeing a PATTERN rather
// than a coincidence.
const minPresetUses = 3

// maxShapesPerPass caps the quadratic candidate search, and maxPresetsPerHead
// caps how many times it is repeated for one op. Neither binds on real content
// (the busiest chapter in the tuning corpus factors 40 presets out of ~56
// distinct actor shapes); they exist so a hostile or degenerate file cannot
// turn a server-side import into a hang.
const (
	maxShapesPerPass  = 256
	maxPresetsPerHead = 128
)

type presetCand struct {
	head string
	toks []string // token texts, in the order the statements write them
	set  map[string]bool
	uses []int // indices into the stmt slice
	name string
	gain int
}

// identityKeys name WHICH thing a statement is about, as opposed to how it
// looks this time. They decide how statements are grouped into presets, and
// getting that grouping right is the whole difference between a file an author
// can read and one they cannot: a byte-optimal factoring of the corpus hoists
// `enter="fade" position="right" show=true` — shared by every character — into
// one nameless preset and pushes `id=` down to the call site, which is strictly
// WORSE than the boilerplate it replaced. Grouping by identity instead yields
// `Valery_in emotion="happy"`: the preset says who and where, the call site
// says what changed.
var identityKeys = []string{"id", "key"}

// facetKeys split one identity's statements into the handful of presets an
// author would actually have written by hand — an actor's entrance and its
// exit are two different presets, not one preset with an `exit=` argument.
var facetKeys = []string{"show", "hide", "action"}

// presetFactor hoists the repeated head of a family of statements into a `def`
// preset and rewrites every member as `<name> <what differs>`.
//
// Statements that name an entity are grouped by (op, identity, facet) and the
// preset is what the whole group has in common. Statements that name nothing
// (an `audio` line, say) fall back to a search over pairwise intersections of
// their parameter sets, which finds the same kind of shared head without any
// per-op knowledge.
func presetFactor(lines []string, prose []bool) []string {
	var stmts []stmt
	for i, raw := range lines {
		if prose[i] {
			continue
		}
		if st, ok := parseStmt(i, raw); ok {
			stmts = append(stmts, st)
		}
	}
	if len(stmts) < minPresetUses {
		return lines
	}

	taken := reservedWords(lines)
	byHead := map[string][]int{}
	for i, st := range stmts {
		byHead[st.head] = append(byHead[st.head], i)
	}
	heads := make([]string, 0, len(byHead))
	for h := range byHead {
		heads = append(heads, h)
	}
	sort.Strings(heads)

	assigned := make([]int, len(stmts)) // 1-based preset index, 0 = untouched
	var chosen []*presetCand
	keep := func(c *presetCand) {
		chosen = append(chosen, c)
		taken[c.name] = true
		for _, s := range c.uses {
			assigned[s] = len(chosen)
		}
	}
	// factor repeatedly hoists the best-paying preset out of a pool until
	// nothing left in it pays for itself.
	factor := func(pool []int, h string) {
		for n := 0; len(pool) >= minPresetUses && n < maxPresetsPerHead; n++ {
			best := bestCandidate(stmts, pool, h, taken)
			if best == nil {
				return
			}
			keep(best)
			inUse := map[int]bool{}
			for _, s := range best.uses {
				inUse[s] = true
			}
			var rest []int
			for _, s := range pool {
				if !inUse[s] {
					rest = append(rest, s)
				}
			}
			pool = rest
		}
	}
	for _, h := range heads {
		named, anon := splitByIdentity(stmts, byHead[h])
		// Identity groups first, in first-appearance order, so the preset block
		// reads in the order the chapter introduces its cast. Factoring inside
		// a group (rather than taking the whole group's intersection) is what
		// keeps one odd `exit="none"` from stripping `exit="fade"` off the
		// twenty statements that do share it.
		for _, g := range named {
			factor(g, h)
		}
		factor(anon, h)
	}
	if len(chosen) == 0 {
		return lines
	}

	// Rewrite the call sites.
	for i, st := range stmts {
		p := assigned[i]
		if p == 0 {
			continue
		}
		cand := chosen[p-1]
		out := cand.name
		for _, t := range st.toks {
			if !cand.set[t.text] {
				out += " " + t.text
			}
		}
		lines[st.idx] = st.indent + out
	}

	// A preset must be declared before its first use; declaring them all in a
	// block under the header (scene / actor_map) keeps the noise in ONE place
	// an author can read top-to-bottom, which is the whole point. Order the
	// block by first use, so it introduces the cast in the order the chapter
	// does. (A copy — `assigned` indexes into `chosen` as it stands.)
	ordered := append([]*presetCand(nil), chosen...)
	sort.SliceStable(ordered, func(i, j int) bool {
		return firstUseLine(stmts, ordered[i]) < firstUseLine(stmts, ordered[j])
	})
	block := make([]string, 0, len(ordered)+1)
	for _, c := range ordered {
		block = append(block, "def "+c.name+" "+c.head+" "+strings.Join(c.toks, " "))
	}
	block = append(block, "")
	at := headerEnd(lines)
	out := make([]string, 0, len(lines)+len(block))
	out = append(out, lines[:at]...)
	out = append(out, block...)
	out = append(out, lines[at:]...)
	return out
}

func firstUseLine(stmts []stmt, c *presetCand) int {
	m := 1 << 30
	for _, s := range c.uses {
		if stmts[s].idx < m {
			m = stmts[s].idx
		}
	}
	return m
}

// headerEnd is the index of the first line that is neither blank, a comment,
// nor a leading `scene`/`actor_map` directive — where the preset block goes.
func headerEnd(lines []string) int {
	at := 0
	for i, l := range lines {
		t := strings.TrimSpace(l)
		if t == "" || strings.HasPrefix(t, "#") ||
			strings.HasPrefix(t, "scene ") || strings.HasPrefix(t, "scene:") ||
			strings.HasPrefix(t, "actor_map ") {
			at = i + 1
			continue
		}
		break
	}
	return at
}

// reservedWords collects every word that already starts a line, so a preset
// name can never shadow one. Preset expansion keys off the first field of a
// line: a preset accidentally named after a speaker, a variable or a directive
// would rewrite that line into something else entirely.
func reservedWords(lines []string) map[string]bool {
	out := map[string]bool{}
	for _, l := range lines {
		t := strings.TrimSpace(l)
		if w := firstField(t); w != "" {
			out[w] = true
			// `Vera_in: Раз.` gives firstField "Vera_in:" — with the colon,
			// which is NOT what a preset would shadow. But the emotion form
			// (`Vera_in [sad]: Раз.`) and the spaced form (`Vera_in : Раз.`)
			// both give the bare name, and those a preset WOULD swallow.
			// Reserve the speaker either way.
			out[strings.TrimRight(w, ":")] = true
		}
		// A preset the source already declares owns its name: ours is emitted
		// at the top of the file, so a later duplicate `def` would silently
		// take over halfway down.
		if strings.HasPrefix(t, "def ") {
			if w := firstField(strings.TrimSpace(t[4:])); w != "" {
				out[w] = true
			}
		}
	}
	for w := range reservedNames {
		out[w] = true
	}
	for w := range KnownOps {
		out[w] = true
	}
	return out
}

// tokenText returns the source text of the statement's token for `key`.
func (s stmt) tokenText(key string) (string, bool) {
	for _, t := range s.toks {
		if t.key == key {
			return t.text, true
		}
	}
	return "", false
}

// splitByIdentity partitions one op family into groups that stage the same
// entity in the same way (same id, same show/hide/action facet) and the
// leftover statements that name no entity at all.
func splitByIdentity(stmts []stmt, pool []int) (named [][]int, anon []int) {
	groups := map[string][]int{}
	var order []string
	for _, s := range pool {
		id := ""
		for _, k := range identityKeys {
			if t, ok := stmts[s].tokenText(k); ok {
				id = t
				break
			}
		}
		if id == "" {
			anon = append(anon, s)
			continue
		}
		key := id
		for _, k := range facetKeys {
			if t, ok := stmts[s].tokenText(k); ok {
				key += "\x00" + t
			}
		}
		if _, seen := groups[key]; !seen {
			order = append(order, key)
		}
		groups[key] = append(groups[key], s)
	}
	for _, k := range order {
		named = append(named, groups[k])
	}
	return named, anon
}

// scoreCandidate turns a (head, shared tokens, members) triple into a preset,
// or nil when the indirection would not pay for itself.
func scoreCandidate(head string, texts []string, uses []int, taken map[string]bool) *presetCand {
	if len(texts) == 0 || len(uses) < minPresetUses {
		return nil
	}
	set := map[string]bool{}
	size := 0
	for _, t := range texts {
		set[t] = true
		size += 1 + len(t)
	}
	name := presetName(head, texts, taken)
	perUse := len(head) + size - len(name)
	if perUse < minPresetGain {
		return nil
	}
	gain := len(uses)*perUse - (len("def ") + len(name) + 1 + len(head) + size + 1)
	if gain <= 0 {
		return nil
	}
	sorted := append([]int(nil), uses...)
	sort.Ints(sorted)
	return &presetCand{head: head, toks: texts, set: set, uses: sorted, name: name, gain: gain}
}

// bestCandidate picks the preset that saves the most bytes over `pool`.
func bestCandidate(stmts []stmt, pool []int, head string, taken map[string]bool) *presetCand {
	// Distinct token sets, with their statement members.
	type shape struct {
		toks []string
		set  map[string]bool
		mem  []int
	}
	shapes := map[string]*shape{}
	var order []string
	for _, s := range pool {
		var texts []string
		for _, t := range stmts[s].toks {
			texts = append(texts, t.text)
		}
		key := strings.Join(sortedCopy(texts), "\x00")
		sh := shapes[key]
		if sh == nil {
			set := map[string]bool{}
			for _, t := range texts {
				set[t] = true
			}
			sh = &shape{toks: texts, set: set}
			shapes[key] = sh
			order = append(order, key)
		}
		sh.mem = append(sh.mem, s)
	}
	if len(shapes) == 0 {
		return nil
	}
	sort.Strings(order)
	// Pairwise intersection is quadratic in the number of DISTINCT shapes.
	// That is nothing on real content (a chapter has tens), but this runs on
	// the server's import path, where one pathological file must not be able
	// to stall a request. Consider the most-used shapes and stop there.
	if len(order) > maxShapesPerPass {
		sort.SliceStable(order, func(i, j int) bool {
			return len(shapes[order[i]].mem) > len(shapes[order[j]].mem)
		})
		order = order[:maxShapesPerPass]
		sort.Strings(order)
	}

	// Candidate token sets: every full shape, plus every pairwise intersection.
	cands := map[string][]string{}
	add := func(texts []string) {
		if len(texts) == 0 {
			return
		}
		cands[strings.Join(sortedCopy(texts), "\x00")] = texts
	}
	for _, k := range order {
		add(shapes[k].toks)
	}
	for i := 0; i < len(order); i++ {
		for j := i + 1; j < len(order); j++ {
			a, b := shapes[order[i]], shapes[order[j]]
			var inter []string
			for _, t := range a.toks {
				if b.set[t] {
					inter = append(inter, t)
				}
			}
			add(inter)
		}
	}

	ckeys := make([]string, 0, len(cands))
	for k := range cands {
		ckeys = append(ckeys, k)
	}
	sort.Strings(ckeys)

	var best *presetCand
	for _, k := range ckeys {
		texts := cands[k]
		want := map[string]bool{}
		for _, t := range texts {
			want[t] = true
		}
		var uses []int
		for _, ok := range order {
			sh := shapes[ok]
			covered := true
			for t := range want {
				if !sh.set[t] {
					covered = false
					break
				}
			}
			if covered {
				uses = append(uses, sh.mem...)
			}
		}
		c := scoreCandidate(head, texts, uses, taken)
		if c != nil && (best == nil || c.gain > best.gain) {
			best = c
		}
	}
	return best
}

func sortedCopy(in []string) []string {
	out := append([]string(nil), in...)
	sort.Strings(out)
	return out
}

// presetName builds a readable, collision-free identifier for a preset:
// the entity it stages ("Valery"), how ("_in"/"_out" for a show/hide), or the
// op itself when there is nothing better to name it after.
func presetName(head string, texts []string, taken map[string]bool) string {
	val := func(key string) (string, bool) {
		for _, t := range texts {
			if strings.HasPrefix(t, key+"=") {
				v, _ := unquoteToken(t)
				return v, true
			}
		}
		return "", false
	}
	base := ""
	for _, k := range []string{"id", "key", "channel", "name", "url"} {
		if v, ok := val(k); ok {
			if b := identify(v); b != "" {
				base = b
				break
			}
		}
	}
	suffix := "_" + head
	if v, ok := val("show"); ok {
		if v == "true" {
			suffix = "_in"
		} else if v == "false" {
			suffix = "_out"
		}
	}
	if base == "" {
		base, suffix = head, "_preset"
	}
	name := base + suffix
	if len(name) > 40 {
		name = name[:40]
	}
	if !isIdentWord(name) {
		name = "preset_" + head
	}
	// Two presets can legitimately want the same name (the same actor leaving
	// with `exit="fade"` in one scene and `exit="none"` in another) — number
	// them rather than growing a tail of underscores.
	if taken[name] || KnownOps[name] || reservedNames[name] {
		base := name
		for n := 2; ; n++ {
			name = base + "_" + strconv.Itoa(n)
			if !taken[name] && !KnownOps[name] && !reservedNames[name] {
				break
			}
		}
	}
	return name
}

// identify turns an arbitrary id ("Главный_герой", "/content/art/Valery.png",
// "Music.ELECTRA") into an ASCII identifier fragment — `def` names must be
// plain identifiers.
func identify(v string) string {
	// A file extension is noise; a dotted VARIABLE name is not (chopping
	// "Music.ELECTRA" to "Music" makes every music flag in the chapter want
	// the same preset name). Only strip the suffix of something that is
	// actually a path.
	if i := strings.LastIndexAny(v, "/\\"); i >= 0 {
		v = v[i+1:]
		if d := strings.LastIndexByte(v, '.'); d > 0 {
			v = v[:d]
		}
	}
	var b strings.Builder
	for _, r := range v {
		if s, ok := translit[r]; ok {
			b.WriteString(s)
			continue
		}
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '_':
			b.WriteRune(r)
		default:
			b.WriteByte('_')
		}
	}
	out := strings.Trim(b.String(), "_")
	for strings.Contains(out, "__") {
		out = strings.ReplaceAll(out, "__", "_")
	}
	if out == "" {
		return ""
	}
	if out[0] >= '0' && out[0] <= '9' {
		out = "n" + out
	}
	if len(out) > 28 {
		out = out[:28]
	}
	return out
}

var translit = map[rune]string{
	'а': "a", 'б': "b", 'в': "v", 'г': "g", 'д': "d", 'е': "e", 'ё': "e",
	'ж': "zh", 'з': "z", 'и': "i", 'й': "y", 'к': "k", 'л': "l", 'м': "m",
	'н': "n", 'о': "o", 'п': "p", 'р': "r", 'с': "s", 'т': "t", 'у': "u",
	'ф': "f", 'х': "h", 'ц': "c", 'ч': "ch", 'ш': "sh", 'щ': "sch", 'ъ': "",
	'ы': "y", 'ь': "", 'э': "e", 'ю': "yu", 'я': "ya",
	'А': "A", 'Б': "B", 'В': "V", 'Г': "G", 'Д': "D", 'Е': "E", 'Ё': "E",
	'Ж': "Zh", 'З': "Z", 'И': "I", 'Й': "Y", 'К': "K", 'Л': "L", 'М': "M",
	'Н': "N", 'О': "O", 'П': "P", 'Р': "R", 'С': "S", 'Т': "T", 'У': "U",
	'Ф': "F", 'Х': "H", 'Ц': "C", 'Ч': "Ch", 'Ш': "Sh", 'Щ': "Sch", 'Ъ': "",
	'Ы': "Y", 'Ь': "", 'Э': "E", 'Ю': "Yu", 'Я': "Ya",
}
