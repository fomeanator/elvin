package lvn

// Ext-grammar: a project/plugin-supplied declaration of its HOST-DEFINED ops
// (the ones authored via `ext <op> …` and executed by the game through
// LvnOps.Register). The core language is a closed contract — this file is the
// sanctioned way to WIDEN the known world per project, so a declared host op
// validates like a built-in (field/enum/required checks, IDE completion)
// instead of tripping the "unknown op" warning, while a typo'd one still
// fails loudly.
//
// The declaration NEVER changes code generation: `ext` compiles the same with
// or without it. It only feeds the validator and the editor tooling, so the
// Go and C# compilers stay byte-identical mirrors.
//
// Convention: `ext-grammar.json` beside the script (or one directory up, e.g.
// content/ext-grammar.json for content/scripts/*.lvn), or passed explicitly
// via -ext-grammar.

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
)

// ExtOp declares one host-defined op: its closed field set, which of those
// fields are mandatory, closed value sets per field, which fields hold jump
// targets, and authoring help.
type ExtOp struct {
	Doc      string              `json:"doc,omitempty"`
	Fields   []string            `json:"fields,omitempty"`
	Required []string            `json:"required,omitempty"`
	Enums    map[string][]string `json:"enums,omitempty"`
	// Labels lists fields whose value is a LABEL REFERENCE (the host jumps
	// there via ctx.GoTo). The validator then treats the target like any
	// goto's: it must exist, and the label stops counting as dead.
	Labels  []string `json:"labels,omitempty"`
	Snippet string   `json:"snippet,omitempty"`
}

// ExtGrammar is the root of an ext-grammar.json.
type ExtGrammar struct {
	Name string           `json:"name,omitempty"`
	Doc  string           `json:"doc,omitempty"`
	Ops  map[string]ExtOp `json:"ops"`
}

// OpNames returns the declared op names, sorted (for suggestions and logs).
func (g *ExtGrammar) OpNames() []string {
	if g == nil {
		return nil
	}
	names := make([]string, 0, len(g.Ops))
	for op := range g.Ops {
		names = append(names, op)
	}
	sort.Strings(names)
	return names
}

// allFields is the op's complete allowed field set: Fields ∪ Required ∪
// Labels (listing a field only under `required`/`labels` is fine — both
// imply allowed).
func (o ExtOp) allFields() []string {
	seen := map[string]bool{}
	var out []string
	all := append(append(append([]string{}, o.Fields...), o.Required...), o.Labels...)
	for _, f := range all {
		if f != "" && !seen[f] {
			seen[f] = true
			out = append(out, f)
		}
	}
	sort.Strings(out)
	return out
}

// ParseExtGrammar parses and sanity-checks a declaration. Unknown JSON keys,
// redeclared built-ins and enums on undeclared fields are declaration bugs and
// fail loudly — the same "unknown is an error" rule the language itself keeps.
func ParseExtGrammar(data []byte) (*ExtGrammar, error) {
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.DisallowUnknownFields()
	var g ExtGrammar
	if err := dec.Decode(&g); err != nil {
		return nil, err
	}
	if len(g.Ops) == 0 {
		return nil, fmt.Errorf("ext-grammar: no ops declared")
	}
	for op, spec := range g.Ops {
		if op == "" {
			return nil, fmt.Errorf("ext-grammar: empty op name")
		}
		if KnownOps[op] {
			return nil, fmt.Errorf("ext-grammar: %q is a built-in op — extensions cannot redeclare the core language", op)
		}
		allowed := map[string]bool{}
		for _, f := range spec.allFields() {
			allowed[f] = true
		}
		for field := range spec.Enums {
			if !allowed[field] {
				return nil, fmt.Errorf("ext-grammar: op %q declares an enum for %q which is not in its fields", op, field)
			}
		}
	}
	return &g, nil
}

// LoadExtGrammar reads a declaration from disk.
func LoadExtGrammar(path string) (*ExtGrammar, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	g, err := ParseExtGrammar(data)
	if err != nil {
		return nil, fmt.Errorf("%s: %w", path, err)
	}
	return g, nil
}

// checkExtOp validates one command against its ext-grammar declaration with
// the same rigour built-ins get: unknown/typo'd fields and out-of-set enum
// values warn (they no-op at runtime), a missing required field is an error —
// the author of the declaration said the op cannot work without it. Fields
// declared as label references go through ref() like any goto target.
func checkExtOp(i int, c Cmd, op string, spec ExtOp, addErr, addWarn func(int, string, string), ref func(int, string, string)) {
	fields := spec.allFields()
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
		msg := fmt.Sprintf("unknown field %q for host op %q", k, op)
		if s := suggest(k, fields); s != "" {
			msg += fmt.Sprintf(" — did you mean %q?", s)
		}
		addWarn(i, op, msg)
	}
	for _, rf := range spec.Required {
		if _, has := c[rf]; !has {
			addErr(i, op, fmt.Sprintf("host op %q requires field %q (per ext-grammar)", op, rf))
		}
	}
	for field, set := range spec.Enums {
		v, isStr := c[field].(string)
		if !isStr || v == "" {
			continue
		}
		ok := false
		for _, s := range set {
			if v == s {
				ok = true
				break
			}
		}
		if !ok {
			msg := fmt.Sprintf("host op %q: %s=%q is outside its declared set", op, field, v)
			if s := suggest(v, set); s != "" {
				msg += fmt.Sprintf(" — did you mean %q?", s)
			}
			addWarn(i, op, msg)
		}
	}
	for _, lf := range spec.Labels {
		ref(i, op, c.Str(lf))
	}
}

// FindExtGrammar looks for the conventional sidecar near a script: in the
// file's own directory, then one level up (content/ext-grammar.json covering
// content/scripts/*.lvn). Returns (nil, "", nil) when there is none — absence
// is not an error, it just means the closed core grammar applies.
func FindExtGrammar(nearFile string) (*ExtGrammar, string, error) {
	dir := filepath.Dir(nearFile)
	for _, cand := range []string{
		filepath.Join(dir, "ext-grammar.json"),
		filepath.Join(filepath.Dir(dir), "ext-grammar.json"),
	} {
		if _, err := os.Stat(cand); err != nil {
			continue
		}
		g, err := LoadExtGrammar(cand)
		if err != nil {
			return nil, cand, err // a present-but-broken sidecar must not be skipped silently
		}
		return g, cand, nil
	}
	return nil, "", nil
}
