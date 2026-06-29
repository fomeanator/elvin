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
	"audio": true, "wait": true, "preload": true, "text_pace": true,
	"text": true,               // reactive HUD/stat label
	"save": true, "load": true, // snapshot save/load
	"label": true, "goto": true, "if": true,
	"set": true, "inc": true, "hint": true,
	"call": true, "return": true,
	"anim": true, // script-driven tween (lvns `anim`/`move` compile to this)
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
			addErr(i, op, fmt.Sprintf("unknown op %q (typo?)", op))
			return
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
		case "choice":
			opts, _ := c["options"].([]any)
			if len(opts) == 0 {
				addWarn(i, op, "choice has no options")
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

	return issues
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
