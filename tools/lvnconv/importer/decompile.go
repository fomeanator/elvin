package importer

import (
	"fmt"
	"regexp"
	"sort"
	"strings"

	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/articy"
)

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
		case "set":
			key := str(c["key"])
			if key == "" {
				continue
			}
			e, hasExpr := c["expr"].(string)
			// A dotted/namespaced key (Music.House) isn't a bare identifier, so the
			// `k = v` assignment form won't parse — fall back to the generic op form,
			// which quotes the key.
			if simpleKeyRe.MatchString(key) {
				if hasExpr && e != "" {
					line(key + " = " + e)
				} else {
					line(key + " = " + literal(c["value"]))
				}
			} else {
				line(genericOp("set", c))
			}
		case "inc":
			key := str(c["key"])
			by := c["by"]
			if by == nil {
				by = 1
			}
			if simpleKeyRe.MatchString(key) {
				line(key + " = " + key + " + " + literal(by))
			} else {
				line(genericOp("inc", c))
			}
		case "say":
			line(sayLine(c))
		case "choice":
			for _, o := range asList(c["options"]) {
				if opt, ok := toMap(o); ok {
					line(choiceOption(opt))
				}
			}
		default:
			line(genericOp(op, c))
		}
	}
	return []byte(b.String())
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
	if style == "" && text != "" && !strings.Contains(text, "\"") {
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
	out += " text=" + quote(text)
	if style != "" {
		out += " style=" + quote(style)
	}
	return out
}

// choiceOption renders one option as `- text -> label [params]`.
func choiceOption(o map[string]any) string {
	text := oneLine(str(o["text"]))
	target := str(o["goto"])
	if target == "" {
		// body-only option: reuse the body's own goto, else end.
		target = "__end"
		for _, bc := range asList(o["body"]) {
			if m, ok := toMap(bc); ok && m["op"] == "goto" {
				if l := str(m["label"]); l != "" {
					target = l
				}
			}
		}
	}
	line := "- " + text + " -> " + target
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
	if e := str(o["expr"]); e != "" {
		line += " expr=" + quote(e)
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
