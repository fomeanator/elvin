package importer

import (
	"fmt"
	"regexp"
	"sort"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// directiveWords are the words the .lvns parser treats as statement PREFIXES
// before it ever considers dialogue — a variable named `def` decompiled as
// `def = 0` re-parses as a broken preset, and worst of all `return = 0`
// re-parses as an INJECTED `return` op that exits the chapter (live-hit:
// rpg-inv/goblin-battle's `def` armor stat). Any key/speaker hitting this
// set must take the quoted generic form instead.
var directiveWords = map[string]bool{
	"def": true, "scene": true, "actor_map": true, "return": true,
	"call": true, "choice": true, "if": true, "voice": true,
	"for": true, "while": true, "func": true, "ext": true,
}

// hasFieldsBeyond reports whether the op carries any key besides the listed
// ones — the "this op has attributes the short form can't encode" test.
func hasFieldsBeyond(c articy.Cmd, allowed ...string) bool {
	for k := range c {
		ok := false
		for _, a := range allowed {
			if k == a {
				ok = true
				break
			}
		}
		if !ok {
			return true
		}
	}
	return false
}

// ToLvns decompiles a compiled .lvn document back into editable Elvin Script, so
// an imported novel can be reworked in the panel as source (not raw JSON). It is
// deterministic (fixed command order) and round-trips: recompiling the output
// reproduces the same command stream.
//
// Control flow is kept flat (labels + jumps) but the goto noise is trimmed:
//   - only labels that something actually jumps to are emitted;
//   - a `goto L` immediately followed by `:L` is dropped (natural fall-through);
//   - a two-way `if` becomes `if <expr> -> then` + a `-> else` fall-through line;
//   - a trailing `goto __end` is dropped.
func ToLvns(doc *articy.Doc) []byte {
	s := doc.Script
	ref := referencedLabels(s)

	var b strings.Builder
	if doc.Scene != "" {
		fmt.Fprintf(&b, "scene %s\n\n", doc.Scene)
	}
	line := func(str string) { b.WriteString(str); b.WriteByte('\n') }

	// actor_map declarations: a say's who_id (the stage-highlight/lip-sync
	// key — see stage.go, "without it the protagonist never lit back up")
	// has nowhere else to live in the terse "Who: text" dialogue form. The
	// parser already reconstructs who_id from an `actor_map Who=id` directive
	// (convert.go) — without emitting one here, EVERY say whose who_id isn't
	// exactly the naive slug of who (which is every renamed protagonist line,
	// and any NPC the template's speaker_names gave a display name that
	// differs from its articy label) silently lost who_id on a decompile →
	// recompile round-trip: no crash, just a speaker who never highlights.
	curActorMap := map[string]string{}
	if ids := actorMapDecls(s); len(ids) > 0 {
		for _, who := range ids {
			line("actor_map " + who.name + "=" + who.id)
			curActorMap[who.name] = who.id
		}
		b.WriteByte('\n')
	}

	for i := 0; i < len(s); i++ {
		c := s[i]
		op, _ := c["op"].(string)
		switch op {
		case "label":
			id, _ := c["id"].(string)
			if id != "" && ref[id] {
				line(":" + id)
			}
		case "goto":
			lbl, _ := c["label"].(string)
			if lbl == "" || nextLabelIs(s, i, lbl) {
				continue // fall-through — no jump needed
			}
			if lbl == "__end" && i == len(s)-1 {
				continue // trailing end is implicit
			}
			line("-> " + lbl)
		case "call":
			line("call " + str(c["label"]))
		case "return":
			line("return")
		case "if":
			expr := condExpr(c)
			then := str(c["then"])
			els := str(c["else"])
			if expr == "" || then == "" {
				continue
			}
			line("if " + expr + " -> " + then)
			// false path falls to the auto label the arrow-if creates, then jumps —
			// unless the else IS the next command anyway.
			if els != "" && !nextLabelIs(s, i, els) {
				line("-> " + els)
			}
		case "set", "inc":
			if l := dataOpLine(op, c); l != "" {
				line(l)
			}
		case "say":
			who, _ := c["who"].(string)
			whoID, _ := c["who_id"].(string)
			// The same display name can front more than one who_id over the
			// course of a script (a flashback double of a live character,
			// two unnamed extras both captioned "Мужчина"). The upfront
			// actor_map block only covers each name's FIRST id — redeclare
			// inline the moment a later say's id for that name changes, so
			// the terse "Name: text" form the parser reattaches who_id
			// through never drifts onto the wrong actor mid-script.
			if who != "" && whoID != "" && curActorMap[who] != whoID {
				line("actor_map " + who + "=" + whoID)
				curActorMap[who] = whoID
			}
			line(sayLine(c))
		case "text":
			// A reactive label has a POSITIONAL grammar the flat k=v form
			// cannot express — see textLine.
			line(textLine(c))
		case "choice":
			// Attributes that live on the choice op ITSELF (timeout,
			// timeout_goto, …) get their own `choice k=v` line before the
			// options — the parser's pendingChoice already understands it.
			// Without this they silently vanished on every re-save
			// (live-hit: tour-ch02's 3 timed choices lost their timers).
			if hasFieldsBeyond(c, "op", "options") {
				line(genericOp("choice", c))
			}
			for _, o := range asList(c["options"]) {
				if opt, ok := toMap(o); ok {
					for _, l := range choiceOptionLines(opt) {
						line(l)
					}
				}
			}
		default:
			line(otherOpLine(op, c))
		}
	}
	return []byte(b.String())
}

type actorMapping struct{ name, id string }

// actorMapDecls collects every distinct (who, who_id) pair a say makes, in
// first-seen order — the actor_map lines ToLvns must emit so the parser can
// reconstruct who_id later: it has no fallback derivation for it at all, an
// unmapped speaker's say simply carries no who_id.
func actorMapDecls(s []articy.Cmd) []actorMapping {
	seen := map[string]bool{}
	var out []actorMapping
	for _, c := range s {
		if c["op"] != "say" {
			continue
		}
		who, _ := c["who"].(string)
		id, _ := c["who_id"].(string)
		if who == "" || id == "" || seen[who] {
			continue
		}
		seen[who] = true
		out = append(out, actorMapping{who, id})
	}
	return out
}

// referencedLabels collects every label that some command jumps to, so unreferenced
// labels (pure fall-through markers) can be dropped.
func referencedLabels(s []articy.Cmd) map[string]bool {
	ref := map[string]bool{}
	mark := func(v any) {
		if l, ok := v.(string); ok && l != "" {
			ref[l] = true
		}
	}
	for _, c := range s {
		switch c["op"] {
		case "goto", "call":
			mark(c["label"])
		case "if":
			mark(c["then"])
			mark(c["else"])
		case "choice":
			for _, o := range asList(c["options"]) {
				if opt, ok := toMap(o); ok {
					mark(opt["goto"])
					for _, bc := range asList(opt["body"]) {
						if m, ok := toMap(bc); ok && m["op"] == "goto" {
							mark(m["label"])
						}
					}
				}
			}
		}
	}
	return ref
}

// nextLabelIs reports whether label `lbl` is defined on the run of label commands
// immediately after index i (so a goto to it is a no-op fall-through).
func nextLabelIs(s []articy.Cmd, i int, lbl string) bool {
	for j := i + 1; j < len(s); j++ {
		if s[j]["op"] != "label" {
			return false
		}
		if id, _ := s[j]["id"].(string); id == lbl {
			return true
		}
	}
	return false
}

// sayLine renders a say as terse dialogue ("Who: text") or bare narration when
// that round-trips cleanly, else the unambiguous generic form.
func sayLine(c articy.Cmd) string {
	who := str(c["who"])
	text := oneLine(str(c["text"]))
	style := str(c["style"])
	// The terse form is only safe when the text can't be mistaken for syntax:
	//   - unbalanced «/» (a verse split across lines opens a « it never
	//     closes) would make the parser swallow the REST OF THE FILE into one
	//     multi-line string — live-hit: soviet's «Союз нерушимый…» either
	//     hard-failed recompile or silently glued 4 lines into one;
	//   - a full «…» wrap would be stripped as quoting by stripQuotes (the
	//     author's guillemets around a quote/verse are prose, not syntax);
	//   - a leading "-"/"->"/":"/"#" reads as an option/goto/label/comment;
	//   - a who that is itself a directive word would parse as a command.
	chevBalanced := strings.Count(text, "«") == strings.Count(text, "»")
	chevWrapped := strings.HasPrefix(text, "«") && strings.HasSuffix(text, "»")
	terseSafe := chevBalanced && !chevWrapped &&
		!strings.HasPrefix(text, "-") && !strings.HasPrefix(text, ":") && !strings.HasPrefix(text, "#") &&
		!directiveWords[who] && !strings.HasPrefix(who, "-")
	if style == "" && text != "" && !strings.Contains(text, "\"") && terseSafe {
		if who != "" && !strings.ContainsAny(who, ":\"") {
			return who + ": " + text
		}
		if who == "" && !strings.Contains(text, ": ") {
			return text // narration with no speaker-colon ambiguity
		}
	}
	out := "say"
	if who != "" {
		out += " who=" + quote(who)
	}
	// The terse "Name: text" form gets who_id from an actor_map declaration
	// the caller keeps current; this explicit command form bypasses that
	// entirely (the parser only consults actor_map inside the dialogue-line
	// regex), so who_id has to ride along on the line itself or it's lost.
	if whoID := str(c["who_id"]); whoID != "" {
		out += " who_id=" + quote(whoID)
	}
	out += " text=" + quote(text)
	if style != "" {
		out += " style=" + quote(style)
	}
	return out
}

// dataOpLine renders a `set`/`inc` as the terse assignment form when that
// round-trips, else the unambiguous generic form. Returns "" for a keyless set
// (nothing to write).
//
// A dotted/namespaced key (Music.House) isn't a bare identifier, so the `k = v`
// assignment form won't parse — fall back to the generic op form, which quotes
// the key. Same fallback when the op carries ANY field beyond op/key/value/expr
// — the short form can't encode them, and silently dropping `default:true` would
// turn a declared default into an unconditional assignment that resets the
// player's progress on every chapter entry. And when the key IS a directive word
// (`def`, `return`, `scene`…): `return = 0` round-trips as an INJECTED `return`
// op that exits the chapter (live-hit: rpg-inv's `def`).
func dataOpLine(op string, c articy.Cmd) string {
	key := str(c["key"])
	if op == "inc" {
		by := c["by"]
		if by == nil {
			by = 1
		}
		if simpleKeyRe.MatchString(key) && !directiveWords[key] {
			return key + " = " + key + " + " + literal(by)
		}
		return genericOp("inc", c)
	}
	if key == "" {
		return ""
	}
	e, hasExpr := c["expr"].(string)
	simple := simpleKeyRe.MatchString(key) && !directiveWords[key]
	for k := range c {
		if k != "op" && k != "key" && k != "value" && k != "expr" {
			simple = false
			break
		}
	}
	if !simple {
		return genericOp("set", c)
	}
	if hasExpr && e != "" {
		return key + " = " + e
	}
	return key + " = " + literal(c["value"])
}

// otherOpLine renders any remaining command as one .lvns statement.
//
// A host-defined op (LvnOps.Register / `ext` in the language) is NOT in the
// parser's KnownOps — printing it bare (`leaderboard_submit board="quiz"`)
// produced a line the recompile rejects as an unknown command, breaking
// round-trip for every embedding game. The `ext` prefix is the documented
// spelling for exactly this.
func otherOpLine(op string, c articy.Cmd) string {
	if !lvns.KnownOps[op] {
		return "ext " + genericOp(op, c)
	}
	return genericOp(op, c)
}

// choiceOptionLines renders one option: the `- text -> label [params]` header
// and, when the option carries a `body`, the `{ … }` block holding the commands
// the runtime runs on pick.
//
// Before the block form existed the body was thrown away and only its `goto`
// survived, so every "ask this once" pool (`set _once_… true` + jump) lost the
// flag it set and never emptied — audit O3, 14 live options in soviet alone.
func choiceOptionLines(o map[string]any) []string {
	body := asList(o["body"])
	if len(body) == 0 {
		if t := str(o["goto"]); t != "" {
			return []string{choiceOption(o, t)}
		}
		// Neither target nor body: the runtime falls through past the choice.
		// An empty block is the only spelling that says so — a bare `- text`
		// doesn't parse, and the old `-> __end` fallback turned a fall-through
		// into a jump to the end of the chapter.
		return []string{choiceOption(o, "") + " {", "}"}
	}
	// The jump the body ends with IS the option's target: hoist it onto the
	// header so an option still reads (and greps) as `- text -> label`, and the
	// block carries only what runs before the jump.
	target := str(o["goto"])
	if m, ok := toMap(body[len(body)-1]); ok && m["op"] == "goto" {
		if l := str(m["label"]); l != "" {
			target = l
			body = body[:len(body)-1]
		}
	}
	out := []string{choiceOption(o, target) + " {"}
	for _, bc := range body {
		m, ok := toMap(bc)
		if !ok {
			continue
		}
		bop, _ := m["op"].(string)
		var l string
		switch bop {
		case "goto":
			l = "-> " + str(m["label"])
		case "set", "inc":
			l = dataOpLine(bop, articy.Cmd(m))
		default:
			l = otherOpLine(bop, articy.Cmd(m))
		}
		if l != "" {
			out = append(out, "    "+l)
		}
	}
	return append(out, "}")
}

// choiceOption renders one option header as `- text [-> label] [params]`. An
// empty target is the body-only form (the flow falls through past the choice);
// the caller then always appends a `{ … }` block, without which the line would
// not re-parse.
func choiceOption(o map[string]any, target string) string {
	text := oneLine(str(o["text"]))
	line := "- " + text
	if target != "" {
		line += " -> " + target
	}
	if v := str(o["requires_stat"]); v != "" {
		line += " requires_stat=" + quote(v)
		if m := o["requires_min"]; m != nil {
			line += " requires_min=" + literal(m)
		} else if m := o["min"]; m != nil {
			line += " requires_min=" + literal(m)
		}
	}
	if cost, ok := o["cost"].(map[string]any); ok {
		v := str(cost["var"])
		if v == "" {
			v = str(cost["currency"])
		}
		if v != "" && cost["amount"] != nil {
			line += " cost=" + v + ":" + literal(cost["amount"])
		}
	} else if cs := str(o["cost"]); cs != "" {
		line += " cost=" + quote(cs)
	}
	// wallet_cost is a REAL price the runtime charges before the pick goes
	// through (LvnPlayer.Choose spends it) — distinct from `cost`, which is
	// only the narrative display line. Without this, an imported "[premium]"
	// choice silently turns free the moment its chapter round-trips through
	// the panel's decompile→recompile (the option keeps its "20 crystals"
	// caption but never actually charges anything).
	if wc, ok := o["wallet_cost"].(map[string]any); ok {
		cur := str(wc["currency"])
		if cur == "" {
			cur = str(wc["var"])
		}
		// The amount is rendered BARE: literal() quotes a string amount
		// ("20"), and `wallet_cost="\"20\" crystals"` fails the parser's
		// amount-then-currency match, leaving the price a plain string the
		// runtime reads as FREE (LvnPlayer.Choose only spends a {currency,
		// amount} object).
		if cur != "" && wc["amount"] != nil {
			line += " wallet_cost=" + quote(bareNumber(wc["amount"])+" "+cur)
		}
	}
	if e := str(o["expr"]); e != "" {
		line += " expr=" + quote(e)
	}
	// effects is the "+2 Матвей" choice-preview hint (AnnotateChoiceEffects) —
	// cosmetic only, never executed, but worth round-tripping so an author who
	// re-saves a chapter through the panel doesn't lose the preview until the
	// next full articy re-import regenerates it.
	if effs := asList(o["effects"]); len(effs) > 0 {
		var parts []string
		for _, e := range effs {
			m, ok := toMap(e)
			if !ok {
				continue
			}
			label := str(m["label"])
			delta := 0
			switch d := m["delta"].(type) {
			case float64:
				delta = int(d)
			case int:
				delta = d
			case int64:
				delta = int(d)
			}
			// The wire form joins entries with "," — a label containing one
			// comes back TRUNCATED ("Иван, брат" → "брат"). This is a
			// cosmetic preview hint, so drop the unrepresentable entry and
			// keep the rest honest rather than shipping a wrong name.
			if label == "" || delta == 0 || strings.Contains(label, ",") {
				continue
			}
			parts = append(parts, fmt.Sprintf("%s:%+d", label, delta))
		}
		if len(parts) > 0 {
			line += " effects=" + quote(strings.Join(parts, ","))
		}
	}
	return line
}

// genericOp renders any other op as `op key=value …` over its scalar fields
// (arrays/objects/null are dropped — a crude but always-parseable form).
func genericOp(op string, c articy.Cmd) string {
	keys := make([]string, 0, len(c))
	for k := range c {
		if k != "op" {
			keys = append(keys, k)
		}
	}
	sort.Strings(keys)
	out := op
	for _, k := range keys {
		if v, ok := scalar(c[k]); ok {
			out += " " + k + "=" + v
		}
	}
	return out
}

// condExpr renders an if's condition as an expression string: a literal `expr`,
// else a structured `cond` {key,op,value}.
func condExpr(c articy.Cmd) string {
	if e, ok := c["expr"].(string); ok && e != "" {
		return e
	}
	cond, ok := c["cond"].(map[string]any)
	if !ok {
		return ""
	}
	sym := map[string]string{"eq": "==", "ne": "!=", "lt": "<", "lte": "<=", "gt": ">", "gte": ">="}
	o := sym[str(cond["op"])]
	if o == "" {
		o = "=="
	}
	return fmt.Sprintf("%s %s %s", str(cond["key"]), o, literal(cond["value"]))
}

// ── small value helpers ──────────────────────────────────────────────────────

func asList(v any) []any {
	if l, ok := v.([]any); ok {
		return l
	}
	return nil
}

// toMap accepts both a plain map and the named articy.Cmd (a Go type assertion to
// map[string]any does NOT match the named type, so options built in-memory as
// articy.Cmd would otherwise be silently skipped).
func toMap(v any) (map[string]any, bool) {
	switch m := v.(type) {
	case map[string]any:
		return m, true
	case articy.Cmd:
		return map[string]any(m), true
	default:
		return nil, false
	}
}

var simpleKeyRe = regexp.MustCompile(`^[A-Za-z_][A-Za-z0-9_]*$`)

func str(v any) string {
	if s, ok := v.(string); ok {
		return s
	}
	return ""
}

func oneLine(s string) string {
	return strings.TrimSpace(strings.NewReplacer("\r", " ", "\n", " ").Replace(s))
}

func quote(s string) string {
	s = oneLine(s)
	s = strings.ReplaceAll(s, "\"", "\\\"")
	return "\"" + s + "\""
}

// literal renders a scalar as bare .lvns (numbers/bools bare, strings quoted).
func literal(v any) string {
	switch t := v.(type) {
	case nil:
		return "null"
	case bool:
		if t {
			return "true"
		}
		return "false"
	case float64:
		if t == float64(int64(t)) {
			return fmt.Sprintf("%d", int64(t))
		}
		return fmt.Sprintf("%g", t)
	case int, int64:
		return fmt.Sprintf("%d", t)
	case string:
		return quote(t)
	default:
		return quote(fmt.Sprintf("%v", t))
	}
}

// scalar renders a value for `key=value`, or reports false for non-scalars.
func scalar(v any) (string, bool) {
	switch v.(type) {
	case nil:
		return "", false
	case []any, map[string]any:
		return "", false
	case string:
		return quote(v.(string)), true
	default:
		return literal(v), true
	}
}

// textLine renders a reactive HUD label in the form its parser actually reads:
// `text <id> [k=v …] «template»` (or `text <id> hide`). genericOp's flat
// `text color=… id=… text=…` is NOT that grammar: the parser takes the first
// bare word as the id and everything after the params as the template, so a
// decompiled label came back either as an on-screen SAY line (the whole
// command printed as dialogue) or as a label with id `id="code"` and every
// value shifted by one field. Falls back to genericOp only when no positional
// form exists at all (an id that is empty or carries spaces/quotes).
func textLine(c articy.Cmd) string {
	id := str(c["id"])
	if id == "" || strings.ContainsAny(id, " \t\"") {
		return genericOp("text", c)
	}
	out := "text " + id
	keys := make([]string, 0, len(c))
	for k := range c {
		if k != "op" && k != "id" && k != "text" && k != "hide" {
			keys = append(keys, k)
		}
	}
	sort.Strings(keys)
	for _, k := range keys {
		if v, ok := scalar(c[k]); ok {
			out += " " + k + "=" + v
		}
	}
	if b, _ := c["hide"].(bool); b {
		return out + " hide" // must be the last token, with no template after it
	}
	tmpl := oneLine(str(c["text"]))
	if tmpl == "" {
		return out
	}
	// The template is quoted with «…» — unless the author's own text already
	// uses guillemets, in which case "…" (the parser accepts both) keeps the
	// delimiters unambiguous.
	if strings.ContainsAny(tmpl, "«»") {
		return out + " " + quote(tmpl)
	}
	return out + " «" + tmpl + "»"
}

// bareNumber renders a price amount without quotes, accepting the string form
// a hand-edited script may carry ("20"). Falls back to the value as written
// when it is not a number at all — better a visible round-trip failure than a
// silently wrong price.
func bareNumber(v any) string {
	if s, ok := v.(string); ok {
		return strings.TrimSpace(s)
	}
	return literal(v)
}
