package adpd

import (
	"encoding/binary"
	"html"
	"os"
	"regexp"
	"strings"
)

var (
	namedRefRe = regexp.MustCompile(`\{(\d+):([^}]+)\}`) // {guid:Name}
	bareRefRe  = regexp.MustCompile(`\{(\d+)\}`)         // {guid} (no name)
)

// varMap collects every variable GUID → name seen in any logic expression, so a
// name-less {guid} reference can be resolved to its full name.
func varMap(objs []object) map[string]string {
	m := map[string]string{}
	for _, o := range objs {
		if o.classId != cidCondition && o.classId != cidOutcome {
			continue
		}
		for _, mm := range namedRefRe.FindAllStringSubmatch(o.str(pCond), -1) {
			m[mm[1]] = mm[2]
		}
		for _, mm := range namedRefRe.FindAllStringSubmatch(o.str(pInstr), -1) {
			m[mm[1]] = mm[2]
		}
	}
	return m
}

// resolveExpr turns articy's GUID-encoded expression into a plain one:
// {guid:Name} → Name, {guid} → the mapped name (full names survive even when the
// human-readable copy was truncated with "…").
func resolveExpr(s string, m map[string]string) string {
	s = namedRefRe.ReplaceAllString(s, "$2")
	s = bareRefRe.ReplaceAllStringFunc(s, func(x string) string {
		if n, ok := m[bareRefRe.FindStringSubmatch(x)[1]]; ok {
			return n
		}
		return x
	})
	return strings.TrimSpace(strings.TrimSuffix(strings.TrimSpace(s), ";"))
}

var condOpRe = regexp.MustCompile(`[=!<>]=|[<>]`)

func isCondition(expr string) bool {
	return strings.Contains(expr, "==") || condOpRe.MatchString(expr)
}

// parseableInstr reports whether every `;`-separated statement is in the subset
// the articy back-end accepts (`x = value` / `x = expr` / `x += N`). Expressions
// outside it (rare multi-ref forms) route as a Hub instead, so a single odd
// instruction never fails the whole conversion.
var (
	reIncOK = regexp.MustCompile(`^[\w.]+\s*[+-]=\s*[0-9]+$`)
	reSetOK = regexp.MustCompile(`^[\w.]+\s*=\s*[^=].*$`)
)

func parseableInstr(expr string) bool {
	any := false
	for _, s := range strings.Split(expr, ";") {
		s = strings.TrimSpace(s)
		if s == "" {
			continue
		}
		if !reIncOK.MatchString(s) && !reSetOK.MatchString(s) {
			return false
		}
		any = true
	}
	return any
}

// ── HTML stripping ───────────────────────────────────────────────────────────

var (
	reBodyOpen  = regexp.MustCompile(`(?is).*<body[^>]*>`)
	reBodyClose = regexp.MustCompile(`(?is)</body>.*`)
	reTags      = regexp.MustCompile(`(?s)<[^>]+>`)
	reSpace     = regexp.MustCompile(`\s+`)
)

func stripHTML(t string) string {
	if !strings.Contains(t, "<") {
		return strings.TrimSpace(t)
	}
	b := reBodyOpen.ReplaceAllString(t, "")
	b = reBodyClose.ReplaceAllString(b, "")
	b = reTags.ReplaceAllString(b, " ")
	b = html.UnescapeString(b) // named + numeric entities (&#171; → «)
	return strings.TrimSpace(reSpace.ReplaceAllString(b, " "))
}

// global-variable object kinds in the Global_Variables partition
var (
	kNamespace = kind{2, 7} // a namespace (name 0x03, self 0x39)
	kVariable  = kind{1, 7} // a variable (name 0x03, parent-ns 0x0c, string default 0x74)
)

const pStrDefault = 0x74 // a string variable's default value

func (o object) has(pid uint16) bool {
	for _, e := range o.es {
		if e.propid == pid {
			return true
		}
	}
	return false
}

// intVars marks variables that the flow uses arithmetically (X += N, X = <number>,
// X </>/comparison) — those are Integers; the rest default to Boolean.
var (
	reArith  = regexp.MustCompile(`([A-Za-z_][\w.]*)\s*[+-]=`)
	reNumSet = regexp.MustCompile(`([A-Za-z_][\w.]*)\s*=\s*-?\d+\b`)
	reNumCmp = regexp.MustCompile(`([A-Za-z_][\w.]*)\s*(?:<|>|<=|>=)`)
	reNumRHS = regexp.MustCompile(`=\s*([A-Za-z_][\w.]*)\s*[+\-]`)
)

func intVars(exprs []string) map[string]bool {
	m := map[string]bool{}
	add := func(re *regexp.Regexp, s string) {
		for _, mm := range re.FindAllStringSubmatch(s, -1) {
			m[mm[1]] = true
		}
	}
	for _, e := range exprs {
		add(reArith, e)
		add(reNumSet, e)
		add(reNumCmp, e)
		add(reNumRHS, e)
	}
	return m
}

// globalVars reconstructs the project's global variables with their real
// namespaces, types and defaults from the Global_Variables partition (object
// framing): String vars keep their default (0x74), arithmetic vars are Integer
// (default 0), the rest Boolean (default false). exprs are the flow's
// instruction/condition strings, used to tell Integer from Boolean.
func globalVars(projectDir string, exprs []string) []nsVars {
	p := findPartition(projectDir, "Global_Variables")
	if p == "" {
		return nil
	}
	d, err := os.ReadFile(p)
	if err != nil || len(d) < 24 {
		return nil
	}
	idx := int(binary.LittleEndian.Uint64(d[8:]))
	if idx <= 0 || idx > len(d) {
		idx = len(d)
	}
	objs := walkObjects(d, idx)

	nsName := map[uint32]string{}
	for _, o := range objs {
		if o.c == kNamespace.c && o.t == kNamespace.t {
			if self, ok := o.u32(pSelf); ok {
				nsName[self] = o.str(0x03)
			}
		}
	}
	isInt := intVars(exprs)

	byNs := map[string][]varDecl{}
	var order []string
	for _, o := range objs {
		if o.c != kVariable.c || o.t != kVariable.t {
			continue
		}
		name := o.str(0x03)
		par, _ := o.u32(pParent)
		ns := nsName[par]
		if name == "" || ns == "" {
			continue
		}
		vd := varDecl{Variable: name}
		switch {
		case o.has(pStrDefault):
			vd.Type, vd.Value = "String", o.str(pStrDefault)
		case isInt[ns+"."+name]:
			vd.Type, vd.Value = "Integer", "0"
		default:
			vd.Type, vd.Value = "Boolean", "false"
		}
		if _, seen := byNs[ns]; !seen {
			order = append(order, ns)
		}
		byNs[ns] = append(byNs[ns], vd)
	}
	var out []nsVars
	for _, ns := range order {
		out = append(out, nsVars{Namespace: ns, Variables: byNs[ns]})
	}
	return out
}
