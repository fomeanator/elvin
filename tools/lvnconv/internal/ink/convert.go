package ink

import (
	"fmt"
	"regexp"
	"strconv"
	"strings"
)

// Cmd is one .lvn command object.
type Cmd map[string]any

// Doc is the .lvn document shape ({scene?, script}).
type Doc struct {
	Scene  string `json:"scene,omitempty"`
	Script []Cmd  `json:"script"`
}

// ── Tokenizer ───────────────────────────────────────────────────────────────

type tokKind int

const (
	tokKnot tokKind = iota
	tokStitch
	tokChoice
	tokGather
	tokDivert
	tokLogic // ~ x = ... / VAR x = ...
	tokTag   // standalone "# cmd: args" line
	tokText
	tokCondStart // "{expr:"
	tokCondElse  // "- else:"
	tokCondEnd   // "}"
	tokConst     // CONST NAME = value
	tokCall      // "-> knot ->" (tunnel call)
	tokReturn    // "->->" (tunnel return)
)

type token struct {
	kind   tokKind
	ln     int
	depth  int    // tokChoice / tokGather
	name   string // tokKnot / tokStitch / tokGather (label), tokDivert/tokCall (target)
	text   string // payload
	sticky bool   // tokChoice: '+' (repeatable); '*' is once-only, like ink
}

var (
	reKnot    = regexp.MustCompile(`^={2,}\s*(\w+)\s*=*\s*$`)
	reStitch  = regexp.MustCompile(`^=\s*(\w+)\s*$`)
	reDivert  = regexp.MustCompile(`^->\s*([\w.]+)\s*$`)
	reCall    = regexp.MustCompile(`^->\s*([\w.]+)\s*->\s*$`)
	reGather  = regexp.MustCompile(`^(-+)\s*(?:\((\w+)\)\s*)?(.*)$`)
	reCondTop = regexp.MustCompile(`^\{\s*(.+?)\s*:\s*$`)
	reVar     = regexp.MustCompile(`^VAR\s+(\w+)\s*=\s*(.+)$`)
	reConst   = regexp.MustCompile(`^CONST\s+(\w+)\s*=\s*(.+)$`)
	urlGuard  = "\x00PROTO\x00"
)

func tokenize(src string) ([]token, error) {
	var toks []token
	for i, raw := range strings.Split(src, "\n") {
		ln := i + 1
		// Strip // comments, protecting URL "://".
		line := strings.ReplaceAll(raw, "://", urlGuard)
		if idx := strings.Index(line, "//"); idx >= 0 {
			line = line[:idx]
		}
		line = strings.TrimSpace(strings.ReplaceAll(line, urlGuard, "://"))
		if line == "" {
			continue
		}

		switch {
		case strings.HasPrefix(line, "INCLUDE"),
			strings.HasPrefix(line, "LIST"),
			strings.HasPrefix(line, "EXTERNAL"):
			return nil, fmt.Errorf("line %d: %q is not supported by the ink2lvn subset", ln, strings.Fields(line)[0])
		}

		if m := reConst.FindStringSubmatch(line); m != nil {
			toks = append(toks, token{kind: tokConst, ln: ln, text: m[1] + " = " + m[2]})
			continue
		}
		if m := reVar.FindStringSubmatch(line); m != nil {
			toks = append(toks, token{kind: tokLogic, ln: ln, text: m[1] + " = " + m[2]})
			continue
		}
		if strings.HasPrefix(line, "==") && strings.Contains(line, "function") {
			return nil, fmt.Errorf("line %d: ink functions are not supported", ln)
		}
		if m := reKnot.FindStringSubmatch(line); m != nil {
			toks = append(toks, token{kind: tokKnot, ln: ln, name: m[1]})
			continue
		}
		if strings.HasPrefix(line, "==") {
			return nil, fmt.Errorf("line %d: cannot parse knot header %q (name must be a single identifier)", ln, line)
		}
		if m := reStitch.FindStringSubmatch(line); m != nil {
			toks = append(toks, token{kind: tokStitch, ln: ln, name: m[1]})
			continue
		}
		if strings.HasPrefix(line, "->") {
			if strings.TrimSpace(line) == "->->" {
				toks = append(toks, token{kind: tokReturn, ln: ln})
				continue
			}
			if m := reCall.FindStringSubmatch(line); m != nil {
				toks = append(toks, token{kind: tokCall, ln: ln, name: m[1]})
				continue
			}
			m := reDivert.FindStringSubmatch(line)
			if m == nil {
				return nil, fmt.Errorf("line %d: cannot parse divert %q", ln, line)
			}
			toks = append(toks, token{kind: tokDivert, ln: ln, name: m[1]})
			continue
		}
		if c := line[0]; c == '*' || c == '+' {
			depth := 0
			rest := line
			for rest != "" && (rest[0] == c || rest[0] == ' ' || rest[0] == '\t') {
				if rest[0] == c {
					depth++
				}
				rest = rest[1:]
			}
			toks = append(toks, token{kind: tokChoice, ln: ln, depth: depth,
				text: strings.TrimSpace(rest), sticky: c == '+'})
			continue
		}
		if line == "}" {
			toks = append(toks, token{kind: tokCondEnd, ln: ln})
			continue
		}
		if strings.HasPrefix(line, "{") {
			// switch-форма: "{" отдельной строкой, ветки "- cond:" ниже.
			if line == "{" {
				toks = append(toks, token{kind: tokCondStart, ln: ln, text: ""})
				continue
			}
			if m := reCondTop.FindStringSubmatch(line); m != nil && !strings.Contains(line, "}") {
				toks = append(toks, token{kind: tokCondStart, ln: ln, text: m[1]})
				continue
			}
			// Inline {…} blocks are TEXT alternatives ({cond: a|b}, {a|b|c},
			// {~…}) — the engine expands them at say-time, so they pass
			// through verbatim. Flow inside braces can't ride along though.
			if strings.Contains(line, "}") && !strings.Contains(line, "->") {
				toks = append(toks, token{kind: tokText, ln: ln, text: line})
				continue
			}
			return nil, fmt.Errorf("line %d: inline {…} with diverts is not supported — use the multiline form `{cond:` … `- else:` … `}`", ln)
		}
		if strings.HasPrefix(line, "-") {
			m := reGather.FindStringSubmatch(line)
			dashes := m[1]
			rest := strings.TrimSpace(m[3])
			if m[2] == "" && rest == "else:" {
				toks = append(toks, token{kind: tokCondElse, ln: ln})
				continue
			}
			// "- <условие>:" — else-if ветка многоветочного условия. Чтобы
			// не путать с gather-репликой, требуем оператор сравнения/логики.
			if m[2] == "" && strings.HasSuffix(rest, ":") {
				expr := strings.TrimSpace(strings.TrimSuffix(rest, ":"))
				if reCompound.MatchString(expr) || reCond.MatchString(expr) && strings.ContainsAny(expr, "<>=!") {
					toks = append(toks, token{kind: tokCondElse, ln: ln, text: expr})
					continue
				}
			}
			toks = append(toks, token{kind: tokGather, ln: ln, depth: len(dashes), name: m[2]})
			if rest != "" {
				// Content on the gather line — retokenize it as its own line.
				sub, err := tokenize(rest)
				if err != nil {
					return nil, fmt.Errorf("line %d: %w", ln, err)
				}
				for _, t := range sub {
					t.ln = ln
					toks = append(toks, t)
				}
			}
			continue
		}
		if strings.HasPrefix(line, "~") {
			toks = append(toks, token{kind: tokLogic, ln: ln, text: strings.TrimSpace(line[1:])})
			continue
		}
		if strings.HasPrefix(line, "#") {
			toks = append(toks, token{kind: tokTag, ln: ln, text: line})
			continue
		}
		toks = append(toks, token{kind: tokText, ln: ln, text: line})
	}
	return toks, nil
}

// ── Parser ──────────────────────────────────────────────────────────────────

type fixup struct {
	cmd   Cmd    // command holding the reference
	field string // "label" / "then" / "else" / "goto"
	knot  string // knot in scope when the reference was written
	ln    int
}

type parser struct {
	toks     []token
	pos      int
	doc      *Doc
	actors   map[string]string // speaker label -> actor id
	consts   map[string]any    // CONST NAME = value substitutions
	labels   map[string]bool
	declared map[string]bool // every label that WILL exist (pre-scan) — for visit counts
	seenRefs map[string]bool // identifiers mentioned in conditions/logic (pre-scan)
	fixups   []fixup
	knot     string // current knot for stitch/label scoping
	autoSeq  int
	endUsed  bool
}

// coerceRef resolves a literal, substituting declared CONST names.
func (p *parser) coerceRef(s string) any {
	if v, ok := p.consts[s]; ok {
		return v
	}
	return coerce(s)
}

var reIdent = regexp.MustCompile(`[A-Za-z_][A-Za-z0-9_.]*`)

// prescan walks the token stream before parsing to learn (a) every label
// that will exist — knots, stitches, named gathers — and (b) every
// identifier mentioned in a condition or logic line. Together these drive
// ink visit counts: a label referenced in a condition gets an `inc
// __seen_<label>` stamped right after it, and the reference itself reads
// that counter.
func (p *parser) prescan() {
	knot := ""
	for _, t := range p.toks {
		switch t.kind {
		case tokKnot:
			knot = t.name
			p.declared[t.name] = true
		case tokStitch:
			if knot != "" {
				p.declared[knot+"."+t.name] = true
			}
		case tokGather:
			if t.name == "" {
				continue
			}
			if knot != "" {
				p.declared[knot+"."+t.name] = true
			} else {
				p.declared[t.name] = true
			}
		case tokCondStart, tokLogic, tokChoice:
			for _, w := range reIdent.FindAllString(t.text, -1) {
				p.seenRefs[w] = true
			}
		case tokText, tokTag:
			// Only identifiers inside {…} alternatives count as references —
			// prose words must not trigger visit instrumentation.
			for _, block := range braceBlocks(t.text) {
				for _, w := range reIdent.FindAllString(block, -1) {
					p.seenRefs[w] = true
				}
			}
		}
	}
}

// braceBlocks returns the contents of every top-level {…} block in s.
func braceBlocks(s string) []string {
	var out []string
	depth, start := 0, 0
	for i := 0; i < len(s); i++ {
		switch s[i] {
		case '{':
			if depth == 0 {
				start = i + 1
			}
			depth++
		case '}':
			if depth > 0 {
				depth--
				if depth == 0 {
					out = append(out, s[start:i])
				}
			}
		}
	}
	return out
}

// rewriteAlternatives rewrites label references inside text alternatives so
// the engine's TextAlternatives sees plain variables: `{meeting: a|b}` →
// `{__seen_meeting: a|b}`, `{intro}` → `{__seen_intro}`. Branch texts are
// processed recursively; everything else passes through untouched.
func (p *parser) rewriteAlternatives(s string) string {
	if !strings.Contains(s, "{") {
		return s
	}
	var sb strings.Builder
	depth, start := 0, -1
	for i := 0; i < len(s); i++ {
		c := s[i]
		if c == '{' {
			if depth == 0 {
				start = i + 1
			}
			depth++
			continue
		}
		if c == '}' && depth > 0 {
			depth--
			if depth == 0 {
				sb.WriteByte('{')
				sb.WriteString(p.rewriteBlock(s[start:i]))
				sb.WriteByte('}')
			}
			continue
		}
		if depth == 0 {
			sb.WriteByte(c)
		}
	}
	return sb.String()
}

func (p *parser) rewriteBlock(body string) string {
	mode := ""
	inner := body
	if len(inner) > 0 && (inner[0] == '~' || inner[0] == '&' || inner[0] == '!') {
		mode = string(inner[0])
		inner = inner[1:]
	}
	colon := -1
	if mode == "" {
		colon = topLevelIndex(inner, ':')
	}
	if colon >= 0 {
		cond := p.substituteExpr(inner[:colon])
		parts := splitTopLevel(inner[colon+1:], '|')
		for i := range parts {
			parts[i] = p.rewriteAlternatives(parts[i])
		}
		return cond + ":" + strings.Join(parts, "|")
	}
	parts := splitTopLevel(inner, '|')
	if len(parts) == 1 && mode == "" {
		// bare `{ident}` — a label reference prints its visit count in ink
		w := strings.TrimSpace(inner)
		if lbl := p.labelRef(w, p.knot); lbl != "" {
			return seenVar(lbl)
		}
		return body
	}
	for i := range parts {
		parts[i] = p.rewriteAlternatives(parts[i])
	}
	return mode + strings.Join(parts, "|")
}

func topLevelIndex(s string, target byte) int {
	depth := 0
	for i := 0; i < len(s); i++ {
		switch s[i] {
		case '{':
			depth++
		case '}':
			depth--
		case target:
			if depth == 0 {
				return i
			}
		}
	}
	return -1
}

func splitTopLevel(s string, sep byte) []string {
	var parts []string
	depth, start := 0, 0
	for i := 0; i < len(s); i++ {
		switch s[i] {
		case '{':
			depth++
		case '}':
			depth--
		case sep:
			if depth == 0 {
				parts = append(parts, s[start:i])
				start = i + 1
			}
		}
	}
	return append(parts, s[start:])
}

// labelRef resolves an identifier to a declared label (qualified by the
// current knot when needed); "" when it is not a label.
func (p *parser) labelRef(word, knot string) string {
	if p.declared[word] {
		return word
	}
	if knot != "" && p.declared[knot+"."+word] {
		return knot + "." + word
	}
	return ""
}

func seenVar(label string) string { return "__seen_" + strings.ReplaceAll(label, ".", "_") }

// markVisit emits the visit-count increment for a label, but only when some
// condition actually reads it — unreferenced labels stay clean.
func (p *parser) markVisit(qualified, bare string) {
	if p.seenRefs[bare] || p.seenRefs[qualified] {
		p.emit(Cmd{"op": "inc", "key": seenVar(qualified), "by": int64(1)})
	}
}

// Convert parses ink source and returns the .lvn document.
func Convert(src string) (*Doc, error) {
	toks, err := tokenize(src)
	if err != nil {
		return nil, err
	}
	p := &parser{
		toks:     toks,
		doc:      &Doc{Script: []Cmd{}},
		actors:   map[string]string{},
		consts:   map[string]any{},
		labels:   map[string]bool{},
		declared: map[string]bool{},
		seenRefs: map[string]bool{},
	}
	p.prescan()
	if err := p.run(); err != nil {
		return nil, err
	}
	return p.doc, nil
}

func (p *parser) peek() *token {
	if p.pos >= len(p.toks) {
		return nil
	}
	return &p.toks[p.pos]
}

func (p *parser) emit(c Cmd) { p.doc.Script = append(p.doc.Script, c) }

func (p *parser) emitLabel(id string, ln int) error {
	if p.labels[id] {
		return fmt.Errorf("line %d: duplicate label %q", ln, id)
	}
	p.labels[id] = true
	p.emit(Cmd{"op": "label", "id": id})
	return nil
}

func (p *parser) autoLabel(hint string) string {
	p.autoSeq++
	base := p.knot
	if base == "" {
		base = "top"
	}
	return fmt.Sprintf("__%s_%s%d", base, hint, p.autoSeq)
}

func (p *parser) run() error {
	for p.peek() != nil {
		t := p.peek()
		switch t.kind {
		case tokKnot:
			p.pos++
			p.knot = t.name
			if err := p.emitLabel(t.name, t.ln); err != nil {
				return err
			}
			p.markVisit(t.name, t.name)
		case tokStitch:
			p.pos++
			if p.knot == "" {
				return fmt.Errorf("line %d: stitch %q outside of a knot", t.ln, t.name)
			}
			if err := p.emitLabel(p.knot+"."+t.name, t.ln); err != nil {
				return err
			}
			p.markVisit(p.knot+"."+t.name, t.name)
		default:
			mark := p.pos
			if err := p.parseSequence(0); err != nil {
				return err
			}
			if p.pos == mark {
				return fmt.Errorf("line %d: unexpected token at top level (stray `}`, `- else:` or gather?)", t.ln)
			}
		}
	}
	if p.endUsed {
		if err := p.emitLabel("__end", 0); err != nil {
			return err
		}
	}
	return p.resolveFixups()
}

// parseSequence consumes flow at weave depth d until a terminator (knot,
// stitch, EOF, cond else/end, or a choice/gather belonging to an outer level).
func (p *parser) parseSequence(d int) error {
	for {
		t := p.peek()
		if t == nil {
			return nil
		}
		switch t.kind {
		case tokKnot, tokStitch, tokCondElse, tokCondEnd:
			return nil

		case tokGather:
			if t.depth <= d {
				return nil
			}
			return fmt.Errorf("line %d: gather depth %d has no matching choice level", t.ln, t.depth)

		case tokChoice:
			if t.depth <= d {
				return nil
			}
			if t.depth > d+1 {
				return fmt.Errorf("line %d: choice depth %d skips a level (parent flow is depth %d)", t.ln, t.depth, d)
			}
			if err := p.parseChoiceGroup(d + 1); err != nil {
				return err
			}

		case tokDivert:
			p.pos++
			p.emitGoto(t.name, t.ln)

		case tokCall:
			p.pos++
			c := Cmd{"op": "call", "label": t.name}
			p.fixups = append(p.fixups, fixup{cmd: c, field: "label", knot: p.knot, ln: t.ln})
			p.emit(c)

		case tokReturn:
			p.pos++
			p.emit(Cmd{"op": "return"})

		case tokConst:
			p.pos++
			m := reAssign.FindStringSubmatch(t.text)
			if m == nil {
				return fmt.Errorf("line %d: cannot parse CONST %q", t.ln, t.text)
			}
			p.consts[m[1]] = p.coerceRef(strings.TrimSpace(m[2]))

		case tokLogic:
			p.pos++
			c, err := p.parseLogic(t.text, t.ln)
			if err != nil {
				return err
			}
			p.emit(c)

		case tokTag:
			p.pos++
			if err := p.handleTags(t.text, nil, t.ln); err != nil {
				return err
			}

		case tokText:
			p.pos++
			if err := p.parseTextLine(t.text, t.ln); err != nil {
				return err
			}

		case tokCondStart:
			if err := p.parseCondBlock(d); err != nil {
				return err
			}

		default:
			return fmt.Errorf("line %d: unexpected token", t.ln)
		}
	}
}

// ── Choices ─────────────────────────────────────────────────────────────────

type option struct {
	cmd    Cmd
	body   []Cmd
	divert string // explicit divert target ("" = falls through to gather)
	ln     int
}

func (p *parser) parseChoiceGroup(depth int) error {
	groupLn := p.peek().ln
	var opts []option

	for {
		t := p.peek()
		if t == nil || t.kind != tokChoice || t.depth != depth {
			break
		}
		sticky := t.sticky
		p.pos++
		opt, err := p.parseOption(t.text, t.ln)
		if err != nil {
			return err
		}

		// Option body: flow at this depth until the next option/gather/etc.
		// Copy it out — truncating doc.Script and appending again would
		// otherwise overwrite the aliased backing array.
		mark := len(p.doc.Script)
		if err := p.parseSequence(depth); err != nil {
			return err
		}
		opt.body = append([]Cmd(nil), p.doc.Script[mark:]...)
		p.doc.Script = p.doc.Script[:mark]

		// An explicit trailing divert in the body ends the option.
		if n := len(opt.body); n > 0 {
			if last := opt.body[n-1]; last["op"] == "goto" {
				opt.divert, _ = last["label"].(string)
			}
		}

		// Ink semantics: `*` options are once-only — gate on a hidden flag
		// that selecting the option sets. `+` options stay repeatable.
		if !sticky {
			key := p.autoLabel("once")
			gate := key + " == 0"
			if e, ok := opt.cmd["expr"].(string); ok {
				opt.cmd["expr"] = "(" + e + ") && " + gate
			} else {
				opt.cmd["expr"] = gate
			}
			opt.body = append([]Cmd{{"op": "set", "key": key, "value": int64(1)}}, opt.body...)
		}
		opts = append(opts, *opt)
	}

	// Closing gather (optional). Without it every option must divert.
	gatherLabel := ""
	if t := p.peek(); t != nil && t.kind == tokGather && t.depth == depth {
		p.pos++
		switch {
		case t.name == "":
			gatherLabel = p.autoLabel("g")
		case p.knot != "":
			// Named weave labels are knot-scoped, like stitches — addressed
			// from outside as "knot.name" (bare refs inside the knot resolve
			// via the same fixup path).
			gatherLabel = p.knot + "." + t.name
		default:
			gatherLabel = t.name
		}
	} else {
		for _, o := range opts {
			if o.divert == "" {
				return fmt.Errorf("line %d: choice option has no divert and no closing gather — flow would run out", o.ln)
			}
		}
	}

	// Wire options: empty body + explicit divert goes straight to the target;
	// otherwise the option jumps to a generated label holding its body.
	type pending struct {
		label string
		body  []Cmd
		next  string // divert target or gather
		ln    int
	}
	var bodies []pending
	for i := range opts {
		o := &opts[i]
		bodyOnlyGoto := len(o.body) == 1 && o.body[0]["op"] == "goto" && o.divert != ""
		switch {
		case len(o.body) == 0 && o.divert != "":
			p.optionGoto(o.cmd, o.divert, o.ln)
		case bodyOnlyGoto:
			p.optionGoto(o.cmd, o.divert, o.ln)
		case len(o.body) == 0 && gatherLabel != "":
			p.optionGoto(o.cmd, gatherLabel, o.ln)
		default:
			lbl := p.autoLabel("c")
			p.optionGoto(o.cmd, lbl, o.ln)
			next := o.divert
			if next == "" {
				next = gatherLabel
			}
			bodies = append(bodies, pending{label: lbl, body: o.body, next: next, ln: o.ln})
		}
	}

	optCmds := make([]any, len(opts))
	for i := range opts {
		optCmds[i] = opts[i].cmd
	}
	p.emit(Cmd{"op": "choice", "options": optCmds})

	for _, b := range bodies {
		if err := p.emitLabel(b.label, b.ln); err != nil {
			return err
		}
		hasTrailingDivert := false
		for i, c := range b.body {
			p.emit(c)
			if i == len(b.body)-1 && c["op"] == "goto" {
				hasTrailingDivert = true
			}
		}
		if !hasTrailingDivert && b.next != "" {
			p.emitGoto(b.next, b.ln)
		}
	}
	if gatherLabel != "" {
		if err := p.emitLabel(gatherLabel, groupLn); err != nil {
			return err
		}
		if !strings.HasPrefix(gatherLabel, "__") {
			bare := gatherLabel
			if i := strings.LastIndexByte(bare, '.'); i >= 0 {
				bare = bare[i+1:]
			}
			p.markVisit(gatherLabel, bare)
		}
	}
	return nil
}

var (
	reOptCond = regexp.MustCompile(`^\{\s*([^{}]+?)\s*\}\s*`)
	reOptCost = regexp.MustCompile(`\(\s*([0-9]+)\s+(\w+)\s*\)\s*$`)
	reOptStat = regexp.MustCompile(`(?:^|\s)#\s*stat\s*:\s*(\S+)\s+([0-9]+)\s*$`)
	reTailDiv = regexp.MustCompile(`->\s*([\w.]+|END|DONE)\s*$`)
)

func (p *parser) parseOption(text string, ln int) (*option, error) {
	o := &option{cmd: Cmd{}, ln: ln}

	// {cond} prefix → option "if" (or free-form "expr" when compound).
	if m := reOptCond.FindStringSubmatch(text); m != nil {
		cond, expr := p.condition(m[1])
		if cond != nil {
			o.cmd["if"] = cond
		} else {
			o.cmd["expr"] = expr
		}
		text = strings.TrimSpace(text[len(m[0]):])
	}
	// "-> target" tail → option divert.
	if m := reTailDiv.FindStringSubmatchIndex(text); m != nil {
		sm := reTailDiv.FindStringSubmatch(text)
		o.divert = sm[1]
		text = strings.TrimSpace(text[:m[0]])
	}
	// Ink brackets: "pre[shown]post" — VN never echoes the choice, so the
	// visible text is simply the line with brackets removed. Strip them
	// before the stat/cost tails so "[Текст (50 soft)]" still parses.
	text = strings.NewReplacer("[", "", "]", "").Replace(text)
	text = strings.TrimSpace(text)
	// "# stat: Name N" tag → requires_stat / requires_min.
	if m := reOptStat.FindStringSubmatchIndex(text); m != nil {
		sm := reOptStat.FindStringSubmatch(text)
		min, _ := strconv.ParseInt(sm[2], 10, 64)
		o.cmd["requires_stat"] = sm[1]
		o.cmd["requires_min"] = min
		text = strings.TrimSpace(text[:m[0]])
	}
	// "(N currency)" tail → cost.
	if m := reOptCost.FindStringSubmatchIndex(text); m != nil {
		sm := reOptCost.FindStringSubmatch(text)
		amt, _ := strconv.ParseInt(sm[1], 10, 64)
		o.cmd["cost"] = map[string]any{"currency": sm[2], "amount": amt}
		text = strings.TrimSpace(text[:m[0]])
	}
	if text == "" {
		return nil, fmt.Errorf("line %d: choice option has no text", ln)
	}
	o.cmd["text"] = text
	return o, nil
}

func (p *parser) optionGoto(c Cmd, target string, ln int) {
	if target == "END" || target == "DONE" {
		p.endUsed = true
		c["goto"] = "__end"
		return
	}
	c["goto"] = target
	p.fixups = append(p.fixups, fixup{cmd: c, field: "goto", knot: p.knot, ln: ln})
}

// ── Conditionals ────────────────────────────────────────────────────────────

// newIf builds the if command with either the structured cond or expr form.
func newIf(cond map[string]any, expr string) Cmd {
	c := Cmd{"op": "if"}
	if cond != nil {
		c["cond"] = cond
	} else {
		c["expr"] = expr
	}
	return c
}

// branch is one arm of a {cond: … - cond2: … - else: …} block.
type condBranch struct {
	cond map[string]any // structured form (nil when expr is used)
	expr string
	body []Cmd
	els  bool // plain "- else:" arm
}

func (p *parser) parseCondBlock(d int) error {
	t := p.peek()
	p.pos++

	parseBody := func() ([]Cmd, error) {
		mark := len(p.doc.Script)
		if err := p.parseSequence(d); err != nil {
			return nil, err
		}
		body := append([]Cmd(nil), p.doc.Script[mark:]...)
		p.doc.Script = p.doc.Script[:mark]
		return body, nil
	}

	var branches []condBranch
	if t.text == "" {
		// switch-форма "{": контента до первой "- cond:" ветки быть не должно
		body, err := parseBody()
		if err != nil {
			return err
		}
		if len(body) > 0 {
			return fmt.Errorf("line %d: содержимое до первой `- условие:` ветки в switch-форме", t.ln)
		}
	} else {
		first := condBranch{}
		first.cond, first.expr = p.condition(t.text)
		body, err := parseBody()
		if err != nil {
			return err
		}
		first.body = body
		branches = []condBranch{first}
	}

	for {
		n := p.peek()
		if n == nil || n.kind != tokCondElse {
			break
		}
		p.pos++
		br := condBranch{els: n.text == ""}
		if !br.els {
			br.cond, br.expr = p.condition(n.text)
		}
		body, err := parseBody()
		if err != nil {
			return err
		}
		br.body = body
		branches = append(branches, br)
		if br.els {
			break
		}
	}

	n := p.peek()
	if n == nil || n.kind != tokCondEnd {
		if n != nil && n.kind == tokChoice {
			return fmt.Errorf("line %d: choices inside {…} conditionals are not supported — divert to a knot instead", n.ln)
		}
		return fmt.Errorf("line %d: conditional opened here is never closed with `}`", t.ln)
	}
	p.pos++

	if len(branches) == 0 {
		return fmt.Errorf("line %d: conditional has no branches", t.ln)
	}

	// Two-arm form with single diverts → direct lvn `if` (no desugaring).
	if len(branches) <= 2 && !branches[0].els && isSingleGoto(branches[0].body) &&
		(len(branches) == 1 || branches[1].els && isSingleGoto(branches[1].body)) {
		c := newIf(branches[0].cond, branches[0].expr)
		c["then"] = branches[0].body[0]["label"]
		if len(branches) == 2 {
			c["else"] = branches[1].body[0]["label"]
		}
		p.emit(c)
		return nil
	}

	// General chain: each conditional arm checks and falls through to the
	// next; the else arm (when present) runs last.
	endLbl := p.autoLabel("ifend")
	for _, br := range branches {
		if br.els {
			for _, c := range br.body {
				p.emit(c)
			}
			break
		}
		thenLbl := p.autoLabel("ift")
		nextLbl := p.autoLabel("ife")
		c := newIf(br.cond, br.expr)
		c["then"] = thenLbl
		c["else"] = nextLbl
		p.emit(c)
		if err := p.emitLabel(thenLbl, t.ln); err != nil {
			return err
		}
		for _, cmd := range br.body {
			p.emit(cmd)
		}
		p.emitGoto(endLbl, t.ln)
		if err := p.emitLabel(nextLbl, t.ln); err != nil {
			return err
		}
	}
	return p.emitLabel(endLbl, t.ln)
}

func isSingleGoto(body []Cmd) bool {
	return len(body) == 1 && body[0]["op"] == "goto"
}

var (
	reCond = regexp.MustCompile(`^(?:(not)\s+)?([\w.]+)\s*(?:(==|!=|>=|<=|>|<|eq|ne|gt|gte|lt|lte)\s*(.+))?$`)
	// Anything the single-clause Condition can't hold — compound logic or a
	// second comparison in the value — falls through to the expr evaluator.
	reCompound = regexp.MustCompile(`\|\||&&|==|!=|>=|<=|[<>+*/%()]|\band\b|\bor\b|\bnot\b`)
)

// condition turns an ink condition into either a structured single-clause
// cond (preferred — server tooling understands it) or a free-form `expr`
// the engine evaluates. Label references become visit-count reads, consts
// inline, and anything the simple grammar can't express falls through to
// expr verbatim (with the same substitutions).
func (p *parser) condition(s string) (cond map[string]any, expr string) {
	m := reCond.FindStringSubmatch(strings.TrimSpace(s))
	if m == nil || reCompound.MatchString(m[4]) {
		return nil, p.substituteExpr(s)
	}
	neg, key, op, val := m[1] != "", m[2], m[3], m[4]
	if lbl := p.labelRef(key, p.knot); lbl != "" {
		if neg {
			// "not knot": missing counter must read as true — only the
			// expression evaluator treats null as 0, so use expr here.
			return nil, "!" + seenVar(lbl)
		}
		if op == "" {
			return map[string]any{"key": seenVar(lbl), "op": "gte", "value": int64(1)}, ""
		}
		key = seenVar(lbl)
	}
	switch {
	case op == "" && !neg:
		return map[string]any{"key": key, "op": "eq", "value": true}, ""
	case op == "" && neg:
		return map[string]any{"key": key, "op": "eq", "value": false}, ""
	case neg:
		return nil, p.substituteExpr(s)
	default:
		return map[string]any{"key": key, "op": normalizeOp(op), "value": p.coerceRef(strings.TrimSpace(val))}, ""
	}
}

// substituteExpr rewrites an ink expression for the engine evaluator:
// CONST names inline as literals, label references become __seen counters.
func (p *parser) substituteExpr(s string) string {
	return reIdent.ReplaceAllStringFunc(strings.TrimSpace(s), func(w string) string {
		switch w {
		case "true", "false", "null", "and", "or", "not":
			return w
		}
		if v, ok := p.consts[w]; ok {
			return constLiteral(v)
		}
		if lbl := p.labelRef(w, p.knot); lbl != "" {
			return seenVar(lbl)
		}
		return w
	})
}

func constLiteral(v any) string {
	switch x := v.(type) {
	case string:
		return strconv.Quote(x)
	case bool:
		if x {
			return "true"
		}
		return "false"
	case nil:
		return "null"
	default:
		return fmt.Sprint(x)
	}
}

// ── Text lines ──────────────────────────────────────────────────────────────

var (
	reTagStart = regexp.MustCompile(`(?:^|\s)#\s*[A-Za-zА-Яа-я_]+\s*:`)
	reSpeaker  = regexp.MustCompile(`^([^:\[\]{}]+?)(?:\s*\[(\w+)\])?:\s*(.*)$`)
)

func (p *parser) parseTextLine(line string, ln int) error {
	// Trailing divert on the same line: "text -> target".
	divert := ""
	if m := reTailDiv.FindStringSubmatchIndex(line); m != nil {
		sm := reTailDiv.FindStringSubmatch(line)
		divert = sm[1]
		line = strings.TrimSpace(line[:m[0]])
	}

	// Split off "# cmd: args" tags (colors like #1a2b3c are never preceded
	// by whitespace, so the tag regex does not match them).
	var tags string
	if loc := reTagStart.FindStringIndex(line); loc != nil {
		tags = strings.TrimSpace(line[loc[0]:])
		line = strings.TrimSpace(line[:loc[0]])
	}

	var say Cmd
	if line != "" {
		// Label refs inside {…} text alternatives become __seen counters
		// before the line is split into speaker/text.
		say = p.sayFor(p.rewriteAlternatives(line))
	}
	if tags != "" {
		if err := p.handleTags(tags, say, ln); err != nil {
			return err
		}
	}
	if say != nil {
		// Emotion swap rides before the say (same convention as lvn-from-md).
		if em, ok := say["__emotion"]; ok {
			delete(say, "__emotion")
			if who, _ := say["who"].(string); who != "" {
				if id, mapped := p.actors[who]; mapped {
					p.emit(Cmd{"op": "actor", "id": id, "emotion": em})
				}
			}
		}
		p.emit(say)
	}
	if divert != "" {
		p.emitGoto(divert, ln)
	}
	return nil
}

// sayFor builds the say command for a content line. With a declared actors
// map only mapped names are speakers (so "Внимание: опасность" stays
// narration); without one any "Name: text" line is a speaker, as in
// lvn-from-md.
func (p *parser) sayFor(line string) Cmd {
	if m := reSpeaker.FindStringSubmatch(line); m != nil {
		speaker := strings.TrimSpace(m[1])
		_, mapped := p.actors[speaker]
		if mapped || len(p.actors) == 0 {
			c := Cmd{"op": "say", "who": speaker, "text": strings.TrimSpace(m[3])}
			if m[2] != "" {
				c["__emotion"] = m[2]
			}
			return c
		}
	}
	return Cmd{"op": "say", "who": nil, "text": line}
}

// ── Tags → staging commands ─────────────────────────────────────────────────

func (p *parser) handleTags(s string, say Cmd, ln int) error {
	for _, part := range splitTags(s) {
		kv := strings.SplitN(part, ":", 2)
		name := strings.ToLower(strings.TrimSpace(strings.TrimPrefix(kv[0], "#")))
		args := ""
		if len(kv) == 2 {
			args = strings.TrimSpace(kv[1])
		}
		if err := p.handleTag(name, args, say, ln); err != nil {
			return err
		}
	}
	return nil
}

// splitTags splits "# a: x # b: y" into ["# a: x", "# b: y"].
func splitTags(s string) []string {
	idx := reTagStart.FindAllStringIndex(s, -1)
	if len(idx) == 0 {
		return nil
	}
	var out []string
	for i, loc := range idx {
		end := len(s)
		if i+1 < len(idx) {
			end = idx[i+1][0]
		}
		out = append(out, strings.TrimSpace(s[loc[0]:end]))
	}
	return out
}

func (p *parser) handleTag(name, args string, say Cmd, ln int) error {
	switch name {
	case "scene":
		p.doc.Scene = args
		return nil
	case "actors":
		for _, pair := range strings.Split(args, ",") {
			kv := strings.SplitN(strings.TrimSpace(pair), "=", 2)
			if len(kv) != 2 {
				return fmt.Errorf("line %d: actors tag expects `Имя=actor_id, ...`", ln)
			}
			p.actors[strings.TrimSpace(kv[0])] = strings.TrimSpace(kv[1])
		}
		return nil
	case "todo", "title", "author":
		return nil

	case "style":
		if say == nil {
			return fmt.Errorf("line %d: # style: must be attached to a dialogue line", ln)
		}
		say["style"] = args
		return nil
	case "say":
		if say == nil {
			return fmt.Errorf("line %d: # say: must be attached to a dialogue line", ln)
		}
		applyKV(say, splitArgs(args))
		return nil

	case "bg", "actor", "set", "inc", "goto", "wait", "fade", "audio":
		c, err := parseAt(name, args)
		if err != nil {
			return fmt.Errorf("line %d: %w", ln, err)
		}
		if name == "goto" {
			p.emitGoto(c["label"].(string), ln)
			return nil
		}
		p.emit(c)
		return nil

	case "dim":
		fields := splitArgs(args)
		c := Cmd{"op": "dim", "alpha": 0.4, "duration": 0.5}
		if len(fields) > 0 {
			if v, err := strconv.ParseFloat(fields[0], 64); err == nil {
				c["alpha"] = v
			}
		}
		if len(fields) > 1 {
			if v, err := strconv.ParseFloat(fields[1], 64); err == nil {
				c["duration"] = v
			}
		}
		p.emit(c)
		return nil

	case "camera":
		fields := splitArgs(args)
		if len(fields) == 0 {
			return fmt.Errorf("line %d: camera tag expects an action (shake/zoom)", ln)
		}
		c := Cmd{"op": "camera", "action": fields[0]}
		applyKV(c, fields[1:])
		p.emit(c)
		return nil

	case "particles":
		fields := splitArgs(args)
		if len(fields) == 0 {
			return fmt.Errorf("line %d: particles tag expects a type", ln)
		}
		on := true
		if len(fields) > 1 && (fields[1] == "off" || fields[1] == "false") {
			on = false
		}
		p.emit(Cmd{"op": "particles", "type": fields[0], "on": on})
		return nil

	case "preload":
		var assets []any
		for _, u := range splitArgs(args) {
			kind := "sprite"
			low := strings.ToLower(u)
			if strings.HasSuffix(low, ".ogg") || strings.HasSuffix(low, ".mp3") || strings.HasSuffix(low, ".wav") {
				kind = "audio"
			}
			assets = append(assets, map[string]any{"url": u, "kind": kind})
		}
		if len(assets) == 0 {
			return fmt.Errorf("line %d: preload tag expects at least one URL", ln)
		}
		p.emit(Cmd{"op": "preload", "assets": assets})
		return nil

	case "hint":
		if args == "off" {
			p.emit(Cmd{"op": "hint", "show": false})
		} else {
			p.emit(Cmd{"op": "hint", "text": args, "show": true})
		}
		return nil

	case "stat":
		return fmt.Errorf("line %d: # stat: belongs on a choice option line", ln)
	}
	return fmt.Errorf("line %d: unknown tag #%s (typo?)", ln, name)
}

// ── Logic (~ / VAR) ─────────────────────────────────────────────────────────

var (
	reAssign         = regexp.MustCompile(`^(\w+)\s*=\s*(.+)$`)
	reIncDec         = regexp.MustCompile(`^(\w+)\s*=\s*(\w+)\s*([+-])\s*([0-9]+)$`)
	reCompoundAssign = regexp.MustCompile(`^(\w+)\s*([+-])=\s*(.+)$`)
	reLiteral        = regexp.MustCompile(`^(?:-?[0-9]+(?:\.[0-9]+)?|true|false|null|"[^"]*"|'[^']*')$`)
)

func (p *parser) parseLogic(s string, ln int) (Cmd, error) {
	if m := reIncDec.FindStringSubmatch(s); m != nil && m[1] == m[2] {
		n, _ := strconv.ParseInt(m[4], 10, 64)
		if m[3] == "-" {
			n = -n
		}
		return Cmd{"op": "inc", "key": m[1], "by": n}, nil
	}
	// составные присваивания `x += y` / `x -= 2*y` → set expr
	if m := reCompoundAssign.FindStringSubmatch(s); m != nil {
		rhs := p.substituteExpr(strings.TrimSpace(m[3]))
		if n, err := strconv.ParseInt(rhs, 10, 64); err == nil {
			if m[2] == "-" {
				n = -n
			}
			return Cmd{"op": "inc", "key": m[1], "by": n}, nil
		}
		return Cmd{"op": "set", "key": m[1], "expr": fmt.Sprintf("%s %s (%s)", m[1], m[2], rhs)}, nil
	}
	if m := reAssign.FindStringSubmatch(s); m != nil {
		key, rhs := m[1], strings.TrimSpace(m[2])
		if strings.HasPrefix(rhs, "=") {
			return nil, fmt.Errorf("line %d: %q is a comparison, not an assignment", ln, s)
		}
		if _, isConst := p.consts[rhs]; isConst || reLiteral.MatchString(rhs) {
			return Cmd{"op": "set", "key": key, "value": p.coerceRef(rhs)}, nil
		}
		// Anything else — var copies, arithmetic, logic — is an expression
		// the engine evaluates at runtime (`~ x = a + b * 2`).
		return Cmd{"op": "set", "key": key, "expr": p.substituteExpr(rhs)}, nil
	}
	return nil, fmt.Errorf("line %d: cannot parse logic %q (subset: `x = value`, `x = x + 1`, `x = <выражение>`)", ln, s)
}

// ── Diverts / fixups ────────────────────────────────────────────────────────

func (p *parser) emitGoto(target string, ln int) {
	if target == "END" || target == "DONE" {
		p.endUsed = true
		p.emit(Cmd{"op": "goto", "label": "__end"})
		return
	}
	c := Cmd{"op": "goto", "label": target}
	p.fixups = append(p.fixups, fixup{cmd: c, field: "label", knot: p.knot, ln: ln})
	p.emit(c)
}

func (p *parser) resolveFixups() error {
	for _, f := range p.fixups {
		target, _ := f.cmd[f.field].(string)
		if p.labels[target] {
			continue
		}
		// Bare stitch name referenced from inside its knot.
		if f.knot != "" && p.labels[f.knot+"."+target] {
			f.cmd[f.field] = f.knot + "." + target
			continue
		}
		return fmt.Errorf("line %d: divert to unknown target %q", f.ln, target)
	}
	return nil
}

// ── Shared helpers (same conventions as lvn-from-md) ────────────────────────

func parseAt(op, args string) (Cmd, error) {
	switch op {
	case "bg", "actor":
		fields := splitArgs(args)
		c := Cmd{"op": op}
		if len(fields) > 0 && !strings.Contains(fields[0], "=") {
			c["id"] = fields[0]
			fields = fields[1:]
		}
		applyKV(c, fields)
		return c, nil

	case "set":
		fields := splitArgs(args)
		if len(fields) < 2 {
			return nil, fmt.Errorf("set: expected `key value`")
		}
		return Cmd{"op": "set", "key": fields[0], "value": coerce(fields[1])}, nil

	case "inc":
		fields := splitArgs(args)
		if len(fields) < 2 {
			return nil, fmt.Errorf("inc: expected `key by`")
		}
		n, err := strconv.ParseInt(fields[1], 10, 64)
		if err != nil {
			return nil, fmt.Errorf("inc: by must be integer")
		}
		return Cmd{"op": "inc", "key": fields[0], "by": n}, nil

	case "goto":
		fields := splitArgs(args)
		if len(fields) == 0 {
			return nil, fmt.Errorf("goto: expected label")
		}
		return Cmd{"op": "goto", "label": fields[0]}, nil

	case "wait":
		fields := splitArgs(args)
		ms := int64(500)
		if len(fields) > 0 {
			n, err := strconv.ParseInt(fields[0], 10, 64)
			if err != nil {
				return nil, fmt.Errorf("wait: ms must be integer")
			}
			ms = n
		}
		return Cmd{"op": "wait", "ms": ms}, nil

	case "fade":
		fields := splitArgs(args)
		c := Cmd{"op": "fade", "to": "black", "duration": 0.5}
		if len(fields) > 0 {
			c["to"] = fields[0]
		}
		if len(fields) > 1 {
			if d, err := strconv.ParseFloat(fields[1], 64); err == nil {
				c["duration"] = d
			}
		}
		return c, nil

	case "audio":
		fields := splitArgs(args)
		c := Cmd{"op": "audio"}
		if len(fields) > 0 {
			c["channel"] = fields[0]
		}
		if len(fields) > 1 {
			c["action"] = fields[1]
		}
		rest := fields[2:]
		if len(rest) > 0 && !strings.Contains(rest[0], "=") {
			c["url"] = rest[0]
			rest = rest[1:]
		}
		applyKV(c, rest)
		return c, nil
	}
	return nil, fmt.Errorf("unknown command %q", op)
}

func splitArgs(s string) []string {
	s = strings.TrimSpace(s)
	if s == "" {
		return nil
	}
	return regexp.MustCompile(`\s+`).Split(s, -1)
}

func applyKV(c Cmd, fields []string) {
	for _, f := range fields {
		eq := strings.Index(f, "=")
		if eq <= 0 {
			continue
		}
		c[f[:eq]] = coerce(f[eq+1:])
	}
}

func coerce(s string) any {
	switch s {
	case "true":
		return true
	case "false":
		return false
	case "null":
		return nil
	}
	if n, err := strconv.ParseInt(s, 10, 64); err == nil {
		return n
	}
	if f, err := strconv.ParseFloat(s, 64); err == nil {
		return f
	}
	if len(s) >= 2 && (s[0] == '"' && s[len(s)-1] == '"' || s[0] == '\'' && s[len(s)-1] == '\'') {
		return s[1 : len(s)-1]
	}
	return s
}

func normalizeOp(o string) string {
	switch o {
	case "==":
		return "eq"
	case "!=":
		return "ne"
	case ">":
		return "gt"
	case ">=":
		return "gte"
	case "<":
		return "lt"
	case "<=":
		return "lte"
	}
	return o
}
