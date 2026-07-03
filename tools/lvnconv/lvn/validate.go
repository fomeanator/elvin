package lvn

import (
	"fmt"
	"regexp"
	"sort"
	"strings"
)

// A narration line that starts with a word + an `=` looks like a command whose
// parameters didn't parse and fell through to dialogue text.
var reFailedOp = regexp.MustCompile(`^([a-z_]+)\b[^:]*=`)

// KnownOps is the registry of command ops the runtime understands. An op
// outside this set is a content error, not a silent no-op — the same hard
// rule the front-ends apply to staging tags, enforced here for any .lvn.
var KnownOps = map[string]bool{
	"say": true, "choice": true, "bg": true, "actor": true, "obj": true,
	"fade": true, "dim": true, "flash": true, "tint": true, "blur": true,
	"camera": true, "particles": true,
	"audio": true, "wait": true, "input": true, "preload": true, "text_pace": true,
	"text": true,               // reactive HUD/stat label
	"save": true, "load": true, // snapshot save/load
	"label": true, "goto": true, "if": true,
	"set": true, "inc": true, "hint": true,
	"call": true, "return": true,
	"anim": true, // script-driven tween (lvns `anim`/`move` compile to this)
}

// OpFields is the set of accepted top-level field keys per op, used to catch
// typo'd keys (e.g. `fade too=` instead of `to=`). Only ops with a CLOSED field
// set are listed: say/choice/actor/obj are intentionally omitted because they
// carry open-ended keys (catalog-defined emotion axes, a large placement
// vocabulary, localization ids), where strict checking would false-positive.
var OpFields = map[string][]string{
	"bg":        {"id", "sprite_url"},
	"fade":      {"to", "duration"},
	"dim":       {"alpha", "duration"},
	"flash":     {"color", "duration"},
	"tint":      {"color", "alpha", "duration"},
	"blur":      {"alpha", "duration"},
	"camera":    {"action", "amplitude", "factor", "x", "y", "duration", "mode"},
	"particles": {"type", "on"},
	"audio":     {"channel", "url", "action", "fade", "volume", "loop"},
	"wait":      {"ms"},
	"input":     {"var", "prompt", "default", "max"},
	"preload":   {"assets"},
	"text_pace": {"cps"},
	"goto":      {"label"},
	"if":        {"expr", "then", "else", "cond"},
	"set":       {"key", "value", "expr", "default"},
	"inc":       {"key", "by"},
	"hint":      {"text", "show"},
	"call":      {"label"},
	"return":    {},
	"label":     {"id"},
	"save":      {"slot"},
	"load":      {"slot"},
	"text":      {"id", "text", "hide", "x", "y", "anchor", "size", "color", "font"},
	"anim":      {"id", "anim", "stop", "channel", "mode"},
}

// EnumValues lists the CLOSED value sets per (op, field). A value outside the set
// is almost always a typo (`position="lft"`). Only fully-closed sets are here —
// colour names also accept hex, so they're deliberately excluded.
var EnumValues = map[string]map[string][]string{
	"fade":      {"to": {"black", "white", "clear", ""}},
	"particles": {"type": {"rain", "snow"}},
	"audio":     {"channel": {"music", "ambient", "sfx"}, "action": {"play", "stop"}},
	"camera":    {"action": {"shake", "zoom", "pan", "reset"}},
	"actor":     {"position": {"left", "center", "right", "far_left", "far_right", "offscreen_left", "offscreen_right"}},
}

// Builtin labels are resolved by the runtime and need no definition.
var builtinLabels = map[string]bool{"__end": true}

// fallThroughTerminators are ops after which control never slides into the
// following command: they transfer the cursor elsewhere (goto/return/if) or
// pause for a deliberate branch (choice). Any OTHER op "falls through" into the
// next command — which, when the next command is a jump-target label, means the
// block is entered both by a jump AND by the cursor walking in from above. That
// is the classic button-screen footgun: a player taps the dialogue instead of a
// hotspot and slides into a section meant to be reached only by a click, running
// the chapter forward until it unexpectedly ends.
var fallThroughTerminators = map[string]bool{
	"goto": true, "return": true, "if": true, "choice": true,
}

// Severity grades a finding. Errors must gate a build; warnings are advisory.
type Severity int

const (
	SevWarning Severity = iota
	SevError
)

func (s Severity) String() string {
	if s == SevError {
		return "error"
	}
	return "warning"
}

// Issue is a single validation finding.
type Issue struct {
	Index int      // command index in script, or -1 for document-level
	Op    string   // op of the offending command, if any
	Msg   string   // human-readable description
	Sev   Severity // error (build-gating) or warning (advisory)
}

func (i Issue) String() string {
	if i.Index < 0 {
		return "doc: " + i.Msg
	}
	return fmt.Sprintf("script[%d] %s: %s", i.Index, i.Op, i.Msg)
}

// Validate runs the source-agnostic structural checks a build must pass. It
// classifies every finding as an error or a warning so the authoring tools can
// surface both:
//
// Errors (a build must not ship these):
//   - a command with no op, or an op outside KnownOps;
//   - a label with no id, or a duplicate label id;
//   - any jump target (goto/if/choice/call/on_click) that resolves to no label.
//
// Warnings (advisory — likely-unintended, the cause of "the game ends early"):
//   - a jump-target label that is ALSO reachable by fall-through (tap-advance
//     slides into a section meant to be reached only by a jump);
//   - a label defined but never targeted and not reachable by fall-through (dead);
//   - unbalanced braces in say/who text (interpolation will misrender);
//   - a choice option with neither a goto nor a body (silently falls through).
func Validate(d *Doc) []Issue {
	var issues []Issue
	addErr := func(i int, op, msg string) { issues = append(issues, Issue{i, op, msg, SevError}) }
	addWarn := func(i int, op, msg string) { issues = append(issues, Issue{i, op, msg, SevWarning}) }

	// Pass 0: required document-level blocks.
	if d.Scene == "" {
		addWarn(-1, "scene", "no `scene` header — add `scene <name>` at the top of the chapter")
	}
	if len(d.Script) == 0 {
		addWarn(-1, "", "the script is empty — there are no commands to play")
	}

	// Pass 1: collect defined labels (detect duplicates).
	defined := map[string]bool{}
	for i, c := range d.Script {
		if c.Op() != "label" {
			continue
		}
		id := c.Str("id")
		if id == "" {
			addErr(i, "label", "label has no id")
			continue
		}
		if defined[id] {
			addErr(i, "label", fmt.Sprintf("duplicate label %q", id))
		}
		defined[id] = true
	}

	// Pass 2: walk commands — check ops, jump targets, text, choices.
	targeted := map[string]bool{}
	ref := func(i int, op, target string) {
		if target == "" {
			return
		}
		targeted[target] = true
		if !defined[target] && !builtinLabels[target] {
			addErr(i, op, fmt.Sprintf("jump to undefined label %q", target))
		}
	}

	var walk func(i int, c Cmd)
	walk = func(i int, c Cmd) {
		op := c.Op()
		if op == "" {
			addErr(i, "", "command has no op")
			return
		}
		if !KnownOps[op] {
			// Not an engine op — either a typo or a HOST-DEFINED op (authored via
			// `ext`, handled by the game's LvnOps.Register). The runtime ignores
			// it when unhandled, so this is a warning, not an error.
			addWarn(i, op, fmt.Sprintf("unknown op %q — a typo, or host-defined (needs LvnOps.Register in the game)", op))
			return
		}
		// Unknown-key check: a typo'd key (e.g. `fade too=`) compiles clean and
		// then silently no-ops at runtime. Only ops with a closed field set are
		// checked (see OpFields).
		if fields, ok := OpFields[op]; ok {
			allowed := map[string]bool{"op": true}
			for _, f := range fields {
				allowed[f] = true
			}
			var bad []string
			for k := range c {
				if !allowed[k] {
					bad = append(bad, k)
				}
			}
			sort.Strings(bad)
			for _, k := range bad {
				msg := fmt.Sprintf("unknown field %q for op %q", k, op)
				if s := suggest(k, fields); s != "" {
					msg += fmt.Sprintf(" — did you mean %q?", s)
				}
				addWarn(i, op, msg)
			}
		}
		// Enumerated-value check: a value outside a closed set (e.g.
		// `position="lft"`) is almost always a typo. Only present string fields
		// with a fully-closed value set are checked (see EnumValues).
		if enums, ok := EnumValues[op]; ok {
			for field, allowed := range enums {
				raw, present := c[field]
				if !present {
					continue
				}
				val, isStr := raw.(string)
				if !isStr {
					continue
				}
				if !inSet(allowed, val) {
					msg := fmt.Sprintf("%s=%q is not a known value (expected: %s)", field, val, strings.Join(nonEmpty(allowed), ", "))
					if s := suggest(val, allowed); s != "" {
						msg += fmt.Sprintf(" — did you mean %q?", s)
					}
					addWarn(i, op, msg)
				}
			}
		}
		switch op {
		case "goto", "call":
			ref(i, op, c.Str("label"))
		case "if":
			ref(i, op, c.Str("then"))
			ref(i, op, c.Str("else"))
		case "say":
			if msg := braceIssue(c.Str("text")); msg != "" {
				addWarn(i, op, msg)
			}
			if msg := braceIssue(c.Str("who")); msg != "" {
				addWarn(i, op, "speaker name: "+msg)
			}
			// Narration that begins like a command + `=` is almost always a
			// command with a syntax slip that silently fell through to dialogue.
			if c.Str("who") == "" {
				if mm := reFailedOp.FindStringSubmatch(c.Str("text")); mm != nil && KnownOps[mm[1]] {
					addWarn(i, op, fmt.Sprintf("looks like a %q command but its syntax didn't parse — it became dialogue text", mm[1]))
				}
			}
		case "obj", "actor":
			// A clickable hotspot jumps to a label, either directly
			// ("on_click": "label") or via an object ("on_click": {"goto": "label"}).
			switch v := c["on_click"].(type) {
			case string:
				ref(i, op, v)
			case map[string]any:
				ref(i, op, Cmd(v).Str("goto"))
			}
			// Drag & drop branches jump too: on_drop is "target:label" pairs
			// (space/comma separated — the runtime's ParseDropMap syntax),
			// on_drop_miss a plain label. Typos here used to slip through and
			// the labels they reach read as dead.
			if raw := c.Str("on_drop"); raw != "" {
				for _, pair := range strings.FieldsFunc(raw, func(r rune) bool { return r == ' ' || r == ',' }) {
					if k := strings.Index(pair, ":"); k > 0 && k < len(pair)-1 {
						ref(i, op, pair[k+1:])
					} else {
						addWarn(i, op, fmt.Sprintf("on_drop pair %q is not target:label", pair))
					}
				}
			}
			ref(i, op, c.Str("on_drop_miss"))
		case "input":
			// Text input writes the player's string into a variable — without
			// the variable the whole stop is pointless.
			if c.Str("var") == "" {
				addErr(i, op, "input needs var= (the variable that receives the text)")
			}
		case "choice":
			opts, _ := c["options"].([]any)
			if len(opts) == 0 {
				addWarn(i, op, "choice has no options")
			}
			// A timed choice: timeout seconds + the branch taken on expiry.
			// Either half alone is an authoring mistake.
			if tg := c.Str("timeout_goto"); tg != "" {
				ref(i, op, tg)
				if c["timeout"] == nil {
					addWarn(i, op, "timeout_goto without timeout= — the timer never starts")
				}
			} else if c["timeout"] != nil {
				addWarn(i, op, "timeout without timeout_goto — nowhere to go when time runs out")
			}
			for oi, o := range opts {
				om, ok := o.(map[string]any)
				if !ok {
					continue
				}
				oc := Cmd(om)
				_, hasBody := oc["body"].([]any)
				if oc.Str("goto") == "" && !hasBody {
					addWarn(i, op, fmt.Sprintf("option %d has no goto and no body (falls through)", oi))
				}
				ref(i, "choice", oc.Str("goto"))
				if body, ok := oc["body"].([]any); ok {
					for _, b := range body {
						if bm, ok := b.(map[string]any); ok {
							walk(i, Cmd(bm))
						}
					}
				}
			}
		}
	}
	for i, c := range d.Script {
		walk(i, c)
	}

	// Pass 3: fall-through into a jump-target label. A label entered BOTH by a
	// jump and by the cursor sliding in from a non-terminating command above is
	// the "chapter ends unexpectedly" footgun.
	for i, c := range d.Script {
		if i == 0 || c.Op() != "label" {
			continue
		}
		id := c.Str("id")
		if id == "" || !targeted[id] {
			continue // unreachable-by-jump labels are plain linear flow, not a trap
		}
		if strings.HasPrefix(id, "__") {
			continue // compiler-generated loop/if labels: fall-through is by design
		}
		prev := d.Script[i-1].Op()
		if !fallThroughTerminators[prev] {
			addWarn(i, "label", fmt.Sprintf(
				"label %q is a jump target but also reached by fall-through from %q above — "+
					"add a `goto` (e.g. goto __end) before it if the block above should stop here",
				id, prev))
		}
	}

	// Pass 4: lint — labels defined but never targeted. Fall-through-reachable
	// labels are legitimate linear flow, so this is only a warning when the label
	// is also the first command or sits after a terminator (truly unreachable).
	var unused []string
	for id := range defined {
		if !targeted[id] {
			unused = append(unused, id)
		}
	}
	sort.Strings(unused)
	for _, id := range unused {
		addWarn(-1, "label", fmt.Sprintf("label %q is never targeted (dead, or fall-through only)", id))
	}

	// Pass 5: likely-typo variable reads. A variable read in an expression or a
	// {interpolation} that is never set — AND is a near-miss of a variable that IS
	// set — is almost always a typo (`if expr="scoore>=1"` when `score` is set).
	// We deliberately only flag close typos of defined vars, never every unknown
	// name: a novel legitimately reads vars seeded from an earlier chapter or the
	// host, so flagging all unknowns would be noise. Only runs when the doc sets
	// at least one var of its own.
	setVars := collectDefinedVars(d.Script)
	if len(setVars) > 0 {
		definedList := make([]string, 0, len(setVars))
		for k := range setVars {
			definedList = append(definedList, k)
		}
		sort.Strings(definedList)
		var checkExpr func(i int, op, expr string)
		checkExpr = func(i int, op, expr string) {
			for _, id := range exprIdents(expr) {
				if setVars[id] {
					continue
				}
				if s := suggest(id, definedList); s != "" && len(s) >= 4 && s != id {
					addWarn(i, op, fmt.Sprintf("variable %q is read but never set — did you mean %q?", id, s))
				}
			}
		}
		for i, c := range d.Script {
			switch c.Op() {
			case "if":
				checkExpr(i, "if", c.Str("expr"))
			case "set":
				checkExpr(i, "set", c.Str("expr"))
			case "say", "text":
				for _, in := range interpolationExprs(c.Str("text")) {
					checkExpr(i, c.Op(), in)
				}
			case "choice":
				if opts, ok := c["options"].([]any); ok {
					for _, o := range opts {
						if om, ok := o.(map[string]any); ok {
							checkExpr(i, "choice", Cmd(om).Str("expr"))
						}
					}
				}
			}
		}
	}

	return issues
}

// collectDefinedVars gathers every variable the document assigns: set/inc keys,
// on_click set-maps, and set/inc inside choice option bodies.
func collectDefinedVars(script []Cmd) map[string]bool {
	defined := map[string]bool{}
	var visit func(cmds []Cmd)
	visit = func(cmds []Cmd) {
		for _, c := range cmds {
			switch c.Op() {
			case "set", "inc":
				if k := c.Str("key"); k != "" {
					defined[k] = true
				}
			}
			if oc, ok := c["on_click"].(map[string]any); ok {
				if setm, ok := oc["set"].(map[string]any); ok {
					for k := range setm {
						defined[k] = true
					}
				}
			}
			if c.Op() == "choice" {
				if opts, ok := c["options"].([]any); ok {
					for _, o := range opts {
						om, ok := o.(map[string]any)
						if !ok {
							continue
						}
						if body, ok := om["body"].([]any); ok {
							var bcmds []Cmd
							for _, b := range body {
								if bm, ok := b.(map[string]any); ok {
									bcmds = append(bcmds, Cmd(bm))
								}
							}
							visit(bcmds)
						}
					}
				}
			}
		}
	}
	visit(script)
	return defined
}

var (
	identRe  = regexp.MustCompile(`[A-Za-z_][A-Za-z0-9_.]*`)
	strLitRe = regexp.MustCompile(`"[^"]*"|'[^']*'`)
	// keywords/operators an expression may contain that are not variables.
	exprKeywords = map[string]bool{
		"true": true, "false": true, "null": true, "nil": true,
		"and": true, "or": true, "not": true, "mod": true,
	}
)

// exprIdents pulls the variable identifiers out of an expression, dropping string
// literals, boolean/keyword tokens, numeric literals, and function-call names.
func exprIdents(expr string) []string {
	if expr == "" {
		return nil
	}
	expr = strLitRe.ReplaceAllString(expr, " ") // a quoted literal is not a variable
	var out []string
	for _, m := range identRe.FindAllStringIndex(expr, -1) {
		id := expr[m[0]:m[1]]
		if exprKeywords[strings.ToLower(id)] {
			continue
		}
		// A name immediately followed by '(' is a function call, not a variable.
		j := m[1]
		for j < len(expr) && (expr[j] == ' ' || expr[j] == '\t') {
			j++
		}
		if j < len(expr) && expr[j] == '(' {
			continue
		}
		out = append(out, id)
	}
	return out
}

// interpolationExprs returns the contents of each {…} span in text ({{ and }} are
// literal-brace escapes), for variable-read checking.
func interpolationExprs(s string) []string {
	var out []string
	for i := 0; i < len(s); i++ {
		if s[i] != '{' {
			continue
		}
		if i+1 < len(s) && s[i+1] == '{' { // literal "{{"
			i++
			continue
		}
		end := strings.IndexByte(s[i+1:], '}')
		if end < 0 {
			break
		}
		out = append(out, s[i+1:i+1+end])
		i += end + 1
	}
	return out
}

func inSet(set []string, v string) bool {
	for _, s := range set {
		if s == v {
			return true
		}
	}
	return false
}

// nonEmpty drops the "" sentinel (used to mean "absent is fine") from an enum
// set so it doesn't show up in the human-readable "expected:" hint.
func nonEmpty(set []string) []string {
	out := make([]string, 0, len(set))
	for _, s := range set {
		if s != "" {
			out = append(out, s)
		}
	}
	return out
}

// suggest returns the closest option to bad within edit distance 2 (the likely
// typo correction), or "" if none is close enough.
func suggest(bad string, options []string) string {
	best, bestD := "", 3
	for _, o := range options {
		if o == "" {
			continue
		}
		d := levenshtein(bad, o)
		if d < bestD {
			best, bestD = o, d
		}
	}
	return best
}

// levenshtein is the classic edit distance, for typo suggestions.
func levenshtein(a, b string) int {
	ra, rb := []rune(a), []rune(b)
	prev := make([]int, len(rb)+1)
	for j := range prev {
		prev[j] = j
	}
	for i := 1; i <= len(ra); i++ {
		cur := make([]int, len(rb)+1)
		cur[0] = i
		for j := 1; j <= len(rb); j++ {
			cost := 1
			if ra[i-1] == rb[j-1] {
				cost = 0
			}
			cur[j] = min3(cur[j-1]+1, prev[j]+1, prev[j-1]+cost)
		}
		prev = cur
	}
	return prev[len(rb)]
}

func min3(a, b, c int) int {
	if b < a {
		a = b
	}
	if c < a {
		a = c
	}
	return a
}

// braceIssue reports an unbalanced-brace problem in interpolated text, or "" if
// the braces are balanced. `{{` and `}}` are literal-brace escapes.
func braceIssue(s string) string {
	depth := 0
	for i := 0; i < len(s); i++ {
		switch s[i] {
		case '{':
			if i+1 < len(s) && s[i+1] == '{' {
				i++
				continue
			}
			depth++
		case '}':
			if i+1 < len(s) && s[i+1] == '}' {
				i++
				continue
			}
			depth--
			if depth < 0 {
				return "unbalanced '}' in text"
			}
		}
	}
	if depth > 0 {
		return "unbalanced '{' in text (missing '}')"
	}
	return ""
}
