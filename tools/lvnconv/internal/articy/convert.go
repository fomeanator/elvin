package articy

import (
	"encoding/json"
	"fmt"
	"regexp"
	"sort"
	"strconv"
	"strings"
)

// Cmd is one .lvn command object; Doc is the document ({scene?, script}).
type Cmd map[string]any

type Doc struct {
	Scene  string `json:"scene,omitempty"`
	Script []Cmd  `json:"script"`

	// Non-fatal repairs the generator performed (surfaced to the import
	// report so an author can fix the source flow instead of shipping them).
	Warnings []string `json:"-"`
}

// ── articy JSON export shapes ───────────────────────────────────────────────

type export struct {
	GlobalVariables []struct {
		Namespace string `json:"Namespace"`
		Variables []struct {
			Variable string `json:"Variable"`
			Type     string `json:"Type"`
			Value    string `json:"Value"`
		} `json:"Variables"`
	} `json:"GlobalVariables"`
	Packages []struct {
		Models []model `json:"Models"`
	} `json:"Packages"`
}

type model struct {
	Type       string         `json:"Type"`
	Properties map[string]any `json:"Properties"`
}

// node is a flattened flow node.
type node struct {
	id, typ string
	props   map[string]any
	outs    [][]string // per output pin: connection target ids
	outText []string   // per output pin: pin script (leave-instruction)
	inText  string     // input pin script (enter-condition)
	inbound int
}

func prop(p map[string]any, key string) string {
	s, _ := p[key].(string)
	return s
}

func arr(p map[string]any, key string) []any {
	a, _ := p[key].([]any)
	return a
}

// ── Converter ───────────────────────────────────────────────────────────────

type gen struct {
	doc      *Doc
	nodes    map[string]*node
	entities map[string]string // entity id -> display name
	labels   map[string]bool   // every label id emitted or reserved
	nodeLbl  map[string]string // node id -> reserved label
	emitted  map[string]bool
	dlgID    string
	endUsed  bool
	autoSeq  int

	// Label bookkeeping for the post-walk repair pass (see repairLabels):
	// where each node's emission starts, and which reserved labels actually
	// made it into the script as a label command.
	nodePos      map[string]int
	labelPrinted map[string]bool
}

// Convert parses an articy JSON export and emits the .lvn for one Dialogue.
func Convert(src []byte, dialogue string) (*Doc, error) {
	var ex export
	if err := json.Unmarshal(src, &ex); err != nil {
		return nil, fmt.Errorf("articy export: %w", err)
	}

	g := &gen{
		doc:          &Doc{Script: []Cmd{}},
		nodes:        map[string]*node{},
		entities:     map[string]string{},
		labels:       map[string]bool{},
		nodeLbl:      map[string]string{},
		emitted:      map[string]bool{},
		nodePos:      map[string]int{},
		labelPrinted: map[string]bool{},
	}

	var dialogues []model
	for _, pkg := range ex.Packages {
		for _, m := range pkg.Models {
			id := prop(m.Properties, "Id")
			if strings.Contains(m.Type, "Character") || m.Type == "Entity" {
				if name := prop(m.Properties, "DisplayName"); name != "" {
					g.entities[id] = name
				}
				continue
			}
			if m.Type == "Dialogue" || m.Type == "FlowFragment" {
				dialogues = append(dialogues, m)
			}
			g.addNode(m)
		}
	}

	dlg, err := pickDialogue(dialogues, dialogue)
	if err != nil {
		return nil, err
	}
	g.dlgID = prop(dlg.Properties, "Id")
	g.doc.Scene = firstNonEmpty(prop(dlg.Properties, "TechnicalName"), prop(dlg.Properties, "DisplayName"))

	// Global variables initialise at chapter start — but as DEFAULTS only
	// (`default:true`): they set the value just once, when the key doesn't exist
	// yet, so a value carried in from an earlier chapter or a saved game isn't
	// clobbered back to zero. Names stay dotted (namespace.var); the engine's
	// expression evaluator resolves them.
	for _, ns := range ex.GlobalVariables {
		for _, v := range ns.Variables {
			g.emit(Cmd{"op": "set", "key": ns.Namespace + "." + v.Variable, "value": varValue(v.Type, v.Value), "default": true})
		}
	}

	start, err := g.dialogueStart(dlg)
	if err != nil {
		return nil, err
	}
	g.countInbound(start)
	if err := g.walk(start); err != nil {
		return nil, err
	}
	g.repairLabels()
	if g.endUsed {
		g.emit(Cmd{"op": "label", "id": "__end"})
	}
	return g.doc, nil
}

// repairLabels closes the two ways a reserved label can dangle after the walk
// (live-hit by the Cold wardrobe sub-flows — a node emitted BEFORE anything
// referenced it prints no label line, and a later merge-goto then points at a
// name that was never written):
//  1. label reserved for an EMITTED node but never printed → splice the label
//     command in at the node's recorded position;
//  2. label reserved for a node that was never emitted at all → retarget every
//     reference to "__end" (chapter end) and surface a warning, so a broken
//     source flow degrades to "scene ends" instead of a validator error the
//     runtime would refuse.
func (g *gen) repairLabels() {
	// Pass 1 — splice missing label lines, descending so positions stay valid.
	type splice struct {
		pos int
		lbl string
	}
	var inserts []splice
	for nodeID, lbl := range g.nodeLbl {
		if g.labelPrinted[lbl] || !g.emitted[nodeID] {
			continue
		}
		if pos, ok := g.nodePos[nodeID]; ok {
			inserts = append(inserts, splice{pos, lbl})
			g.labelPrinted[lbl] = true
		}
	}
	sort.Slice(inserts, func(i, j int) bool { return inserts[i].pos > inserts[j].pos })
	for _, in := range inserts {
		g.doc.Script = append(g.doc.Script, nil)
		copy(g.doc.Script[in.pos+1:], g.doc.Script[in.pos:])
		g.doc.Script[in.pos] = Cmd{"op": "label", "id": in.lbl}
	}

	// Pass 2 — retarget references to labels that still don't exist.
	dangling := map[string]bool{}
	for _, lbl := range g.nodeLbl {
		if !g.labelPrinted[lbl] {
			dangling[lbl] = true
		}
	}
	if len(dangling) == 0 {
		return
	}
	retarget := func(c Cmd, key string) {
		if l, _ := c[key].(string); l != "" && dangling[l] {
			c[key] = "__end"
			g.endUsed = true
			g.doc.Warnings = append(g.doc.Warnings,
				fmt.Sprintf("%s: jump to never-emitted node label %q retargeted to chapter end — check the source flow", g.doc.Scene, l))
		}
	}
	var fixOptions func(opts []any)
	fixOptions = func(opts []any) {
		for _, o := range opts {
			opt, ok := o.(Cmd)
			if !ok {
				continue
			}
			retarget(opt, "goto")
			if body, ok := opt["body"].([]any); ok {
				for _, b := range body {
					if bc, ok := b.(Cmd); ok {
						retarget(bc, "label")
					}
				}
			}
		}
	}
	for _, c := range g.doc.Script {
		switch c["op"] {
		case "goto":
			retarget(c, "label")
		case "if":
			retarget(c, "then")
			retarget(c, "else")
		case "choice":
			if opts, ok := c["options"].([]any); ok {
				fixOptions(opts)
			}
		}
	}
}

func (g *gen) addNode(m model) {
	p := m.Properties
	n := &node{id: prop(p, "Id"), typ: m.Type, props: p, inText: pinText(arr(p, "InputPins"), 0)}
	for _, raw := range arr(p, "OutputPins") {
		pin, _ := raw.(map[string]any)
		var targets []string
		for _, c := range arr(pin, "Connections") {
			conn, _ := c.(map[string]any)
			if t := prop(conn, "Target"); t != "" {
				targets = append(targets, t)
			}
		}
		n.outs = append(n.outs, targets)
		n.outText = append(n.outText, prop(pin, "Text"))
	}
	g.nodes[n.id] = n
}

func pinText(pins []any, i int) string {
	if i >= len(pins) {
		return ""
	}
	pin, _ := pins[i].(map[string]any)
	return prop(pin, "Text")
}

func pickDialogue(ds []model, want string) (model, error) {
	if want == "" {
		if len(ds) == 1 {
			return ds[0], nil
		}
		var names []string
		for _, d := range ds {
			names = append(names, firstNonEmpty(prop(d.Properties, "TechnicalName"), prop(d.Properties, "DisplayName")))
		}
		sort.Strings(names)
		return model{}, fmt.Errorf("export has %d dialogues — pick one with -dialogue (%s)", len(ds), strings.Join(names, ", "))
	}
	for _, d := range ds {
		if prop(d.Properties, "TechnicalName") == want || prop(d.Properties, "DisplayName") == want {
			return d, nil
		}
	}
	return model{}, fmt.Errorf("dialogue %q not found in export", want)
}

func (g *gen) dialogueStart(dlg model) (string, error) {
	in := arr(dlg.Properties, "InputPins")
	if len(in) > 0 {
		pin, _ := in[0].(map[string]any)
		for _, c := range arr(pin, "Connections") {
			conn, _ := c.(map[string]any)
			if t := prop(conn, "Target"); t != "" {
				return t, nil
			}
		}
	}
	return "", fmt.Errorf("dialogue %q has no start connection (link its input pin to the first node)", g.doc.Scene)
}

// countInbound counts how many edges point at each node — a node with more
// than one inbound needs a label so later arrivals can goto it. Jump targets
// and the dialogue entry are edges too.
func (g *gen) countInbound(start string) {
	bump := func(id string) {
		if tn, ok := g.nodes[id]; ok {
			tn.inbound++
		}
	}
	bump(start)
	for _, n := range g.nodes {
		if n.typ == "Jump" {
			bump(prop(n.props, "Target"))
		}
		for _, targets := range n.outs {
			for _, t := range targets {
				bump(t)
			}
		}
	}
}

// ── Flow walk ───────────────────────────────────────────────────────────────

func (g *gen) needsLabel(n *node) bool {
	_, referenced := g.nodeLbl[n.id]
	return referenced || n.inbound > 1 || n.typ == "Hub"
}

// walk linearises flow from a node until it ends (dialogue exit) or merges
// into already-emitted flow (goto).
func (g *gen) walk(id string) error {
	for id != "" {
		if id == g.dlgID { // dialogue's own output pin — chapter end
			g.endUsed = true
			g.emit(Cmd{"op": "goto", "label": "__end"})
			return nil
		}
		n, ok := g.nodes[id]
		if !ok {
			return fmt.Errorf("connection to unknown node %s", id)
		}
		if g.emitted[n.id] {
			g.emit(Cmd{"op": "goto", "label": g.labelFor(n)})
			return nil
		}
		g.nodePos[n.id] = len(g.doc.Script) // where a late-referenced label can be spliced in
		if g.needsLabel(n) {
			l := g.labelFor(n)
			g.emit(Cmd{"op": "label", "id": l})
			g.labelPrinted[l] = true
		}
		g.emitted[n.id] = true

		switch n.typ {
		case "DialogueFragment":
			next, err := g.emitFragment(n)
			if err != nil {
				return err
			}
			id = next

		case "Hub":
			if len(n.outs) > 0 && len(n.outs[0]) > 1 {
				// Hub fanning out into MenuText-fragments — a question menu.
				return g.emitChoice(n, n.outs[0])
			}
			id = g.single(n)
			if id == "" {
				return fmt.Errorf("hub %s: flow runs out", n.id)
			}

		case "Jump":
			target := prop(n.props, "Target")
			if target == "" {
				return fmt.Errorf("jump %s has no target", n.id)
			}
			id = target

		case "Instruction":
			cmds, err := instructionCmds(prop(n.props, "Expression"))
			if err != nil {
				return fmt.Errorf("instruction %s: %w", n.id, err)
			}
			for _, c := range cmds {
				g.emit(c)
			}
			id = g.single(n)
			if id == "" {
				return fmt.Errorf("instruction %s: flow runs out", n.id)
			}

		case "Condition":
			return g.emitCondition(n)

		case "Dialogue", "FlowFragment":
			return fmt.Errorf("nested dialogue/flow %q is not supported — use a Jump or keep one dialogue per chapter", prop(n.props, "DisplayName"))

		default:
			// Custom articy templates (DialogChoice etc) keep the fragment
			// shape — Speaker/MenuText properties mark them as sayable nodes.
			if isFragmentShape(n) {
				next, err := g.emitFragment(n)
				if err != nil {
					return err
				}
				id = next
				continue
			}
			return fmt.Errorf("node %s: unsupported type %q", n.id, n.typ)
		}
	}
	return nil
}

// single returns the lone continuation of a node ("" when absent). A direct
// connection to the dialogue exit is also a valid continuation.
func (g *gen) single(n *node) string {
	if len(n.outs) > 0 && len(n.outs[0]) == 1 {
		return n.outs[0][0]
	}
	return ""
}

func (g *gen) emitFragment(n *node) (string, error) {
	// StageDirections carry `# tag:` staging commands (same syntax as
	// ink2lvn); `# style:`/`# say:` merge into this fragment's say.
	sayExtras := Cmd{}
	if sd := strings.TrimSpace(prop(n.props, "StageDirections")); sd != "" {
		if err := g.handleTags(sd, sayExtras); err != nil {
			return "", fmt.Errorf("fragment %s stage directions: %w", n.id, err)
		}
	}

	if text := strings.TrimSpace(prop(n.props, "Text")); text != "" {
		say := Cmd{"op": "say", "who": g.speaker(n), "text": text}
		// A reimport-stable line id (when the front-end supplies one): saves,
		// analytics and the localization catalog key off it, not off the text.
		if sid := prop(n.props, "StableId"); sid != "" {
			say["id"] = sid
		}
		// The articy marker colour (BackgroundColor) rides along so the importer can
		// resolve it to an emotion; it's a transient field the importer consumes.
		if col := prop(n.props, "BackgroundColor"); col != "" {
			say["color"] = col
		}
		for k, v := range sayExtras {
			say[k] = v
		}
		g.emit(say)
	}

	// Output-pin script runs when leaving the fragment.
	if instr := strings.TrimSpace(pinTextOf(n, 0)); instr != "" {
		cmds, err := instructionCmds(instr)
		if err != nil {
			return "", fmt.Errorf("fragment %s output pin: %w", n.id, err)
		}
		for _, c := range cmds {
			g.emit(c)
		}
	}

	if len(n.outs) == 0 || len(n.outs[0]) == 0 {
		return "", fmt.Errorf("fragment %s: flow runs out (connect it onward or to the dialogue exit)", n.id)
	}
	targets := n.outs[0]
	if len(targets) == 1 {
		return targets[0], nil
	}
	return "", g.emitChoice(n, targets)
}

func pinTextOf(n *node, i int) string {
	if i < len(n.outText) {
		return n.outText[i]
	}
	return ""
}

func (g *gen) speaker(n *node) any {
	if name, ok := g.entities[prop(n.props, "Speaker")]; ok {
		return name
	}
	return nil
}

// isFragmentShape reports whether a node carries dialogue-fragment fields —
// custom articy templates export their template name as Type, so shape
// matters more than the type string.
func isFragmentShape(n *node) bool {
	_, hasMenu := n.props["MenuText"]
	_, hasSpeaker := n.props["Speaker"]
	return hasMenu || hasSpeaker
}

// emitChoice handles a fragment whose output fans out into several targets —
// the articy idiom for a player choice. The option caption is the target's
// MenuText (DisplayName / connection label as fallback for Jump-style exits).
func (g *gen) emitChoice(from *node, targets []string) error {
	var opts []any
	for _, t := range targets {
		tn, ok := g.nodes[t]
		if !ok {
			return fmt.Errorf("choice from %s: connection to unknown node %s", from.id, t)
		}
		// The branch fragment's own TEXT may open with option tags ("[premium]
		// Поддержать его.") — the partner convention puts them there even when a
		// clean MenuText exists. Consume them off the spoken line (they must not
		// show in the dialogue, and a paid option must actually charge) and fold
		// them into this option.
		textTags := stripLeadingTags(tn)
		menu := strings.TrimSpace(prop(tn.props, "MenuText"))
		if menu == "" {
			// No MenuText → the branch's first spoken line IS the caption (the
			// partner authors choices as player-line fragments). DisplayName and
			// the generic "Дальше" remain as the last resorts.
			menu = firstNonEmpty(captionFromText(g, tn),
				strings.TrimSpace(prop(tn.props, "DisplayName")), "Дальше")
		}
		opt := Cmd{"goto": g.labelFor(tn)} // reserves the label → branch gets it on emission
		parseOptionTails(opt, menu)
		for _, tag := range textTags {
			switch tag {
			case "onetime", "once":
				opt["__once"] = true
			case "premium", "paid":
				opt["__premium"] = true
			}
		}
		if sid := prop(tn.props, "StableId"); sid != "" {
			opt["id"] = sid // reimport-stable option id (localization/analytics key)
		}
		if cond := strings.TrimSpace(tn.inText); cond != "" {
			opt["expr"] = cleanExpr(cond)
		}
		// [onetime]: show the option only until it's picked, so a revisitable
		// topic/examine hub exhausts instead of looping forever. A per-target flag
		// (set in the option body) gates it; combine with any input condition.
		if opt["__once"] == true {
			if lbl, ok := opt["goto"].(string); ok && lbl != "" {
				flag := "_once_" + lbl
				gate := "!" + flag
				if e, _ := opt["expr"].(string); e != "" {
					gate = gate + " && (" + e + ")"
				}
				opt["expr"] = gate
				opt["body"] = []any{
					Cmd{"op": "set", "key": flag, "value": true},
					Cmd{"op": "goto", "label": lbl},
				}
				delete(opt, "goto")
			}
		}
		delete(opt, "__once")
		// [premium] survives as a neutral marker; the IMPORTER (which knows the
		// project template's premium currency/price) turns it into a display
		// cost + wallet_cost. Direct articy converts without a template keep
		// the option playable and free — the historical behaviour.
		if opt["__premium"] == true {
			opt["premium"] = true
		}
		delete(opt, "__premium")
		opts = append(opts, opt)
	}
	g.emit(Cmd{"op": "choice", "options": opts})

	for _, t := range targets {
		if !g.emitted[t] {
			if err := g.walk(t); err != nil {
				return err
			}
		}
	}
	return nil
}

func (g *gen) emitCondition(n *node) error {
	expr := cleanExpr(prop(n.props, "Expression"))
	// Resolve each branch's target. An unconnected pin (0 connections) branches to
	// the dialogue exit instead of aborting the whole import; a fanned-out pin
	// (>1 connections) takes its first connection as the branch — the extras stay
	// in the model set and are reached via their own inbound edges. This keeps a
	// malformed/unusual condition from stranding an entire chapter's content.
	branch := func(pin int) string {
		if pin < len(n.outs) && len(n.outs[pin]) > 0 {
			return n.outs[pin][0]
		}
		return g.dlgID // → "__end"
	}
	tID, fID := branch(0), branch(1)

	// A condition with no expression can't decide — treat it as an unconditional
	// pass to its true branch rather than crashing the import.
	if expr == "" {
		tLbl, err := g.branchLabel(tID)
		if err != nil {
			return err
		}
		g.emit(Cmd{"op": "goto", "label": tLbl})
		if tID != g.dlgID && !g.emitted[tID] {
			return g.walk(tID)
		}
		return nil
	}

	tLbl, err := g.branchLabel(tID)
	if err != nil {
		return err
	}
	fLbl, err := g.branchLabel(fID)
	if err != nil {
		return err
	}
	g.emit(Cmd{"op": "if", "expr": expr, "then": tLbl, "else": fLbl})
	for _, id := range []string{tID, fID} {
		if id != g.dlgID && !g.emitted[id] {
			if err := g.walk(id); err != nil {
				return err
			}
		}
	}
	return nil
}

// branchLabel names the landing point of an if branch (reserving the label;
// "__end" for the dialogue exit).
func (g *gen) branchLabel(id string) (string, error) {
	if id == g.dlgID {
		g.endUsed = true
		return "__end", nil
	}
	n, ok := g.nodes[id]
	if !ok {
		return "", fmt.Errorf("connection to unknown node %s", id)
	}
	return g.labelFor(n), nil
}

// labelFor returns (reserving once) the label of a node: its TechnicalName
// when the writer set one, else a stable auto name from the articy id.
func (g *gen) labelFor(n *node) string {
	if l, ok := g.nodeLbl[n.id]; ok {
		return l
	}
	l := prop(n.props, "TechnicalName")
	if l == "" || g.labels[l] {
		g.autoSeq++
		l = fmt.Sprintf("n%d_%s", g.autoSeq, shortID(n.id))
	}
	g.labels[l] = true
	g.nodeLbl[n.id] = l
	return l
}

func shortID(id string) string {
	id = strings.TrimPrefix(id, "0x")
	if len(id) > 6 {
		id = id[len(id)-6:]
	}
	return strings.ToLower(id)
}

func (g *gen) emit(c Cmd) { g.doc.Script = append(g.doc.Script, c) }

// ── articy script → lvn logic ───────────────────────────────────────────────

var (
	reIncArticy = regexp.MustCompile(`^([\w.]+)\s*([+-])=\s*([0-9]+)$`)
	reSetArticy = regexp.MustCompile(`^([\w.]+)\s*=\s*([^=].*)$`)
	reLitArticy = regexp.MustCompile(`^(?:-?[0-9]+(?:\.[0-9]+)?|true|false|null|"[^"]*"|'[^']*')$`)
)

// instructionCmds converts an articy Instruction script (one or more
// `;`-separated statements) into set/inc commands.
func instructionCmds(script string) ([]Cmd, error) {
	var out []Cmd
	for _, stmt := range strings.Split(script, ";") {
		stmt = strings.TrimSpace(stmt)
		if stmt == "" {
			continue
		}
		if m := reIncArticy.FindStringSubmatch(stmt); m != nil {
			n, _ := strconv.ParseInt(m[3], 10, 64)
			if m[2] == "-" {
				n = -n
			}
			out = append(out, Cmd{"op": "inc", "key": m[1], "by": n})
			continue
		}
		if m := reSetArticy.FindStringSubmatch(stmt); m != nil {
			rhs := strings.TrimSpace(m[2])
			if reLitArticy.MatchString(rhs) {
				out = append(out, Cmd{"op": "set", "key": m[1], "value": coerce(rhs)})
			} else {
				out = append(out, Cmd{"op": "set", "key": m[1], "expr": rhs})
			}
			continue
		}
		return nil, fmt.Errorf("cannot parse statement %q (subset: `x = value`, `x += 1`, `x = <expr>`)", stmt)
	}
	return out, nil
}

func cleanExpr(s string) string {
	s = strings.TrimSpace(s)
	s = strings.TrimSuffix(s, ";")
	return strings.TrimSpace(s)
}

func varValue(typ, val string) any {
	switch typ {
	case "Boolean":
		return strings.EqualFold(val, "true")
	case "Integer":
		if n, err := strconv.ParseInt(val, 10, 64); err == nil {
			return n
		}
		return int64(0)
	default:
		return val
	}
}

// parseOptionTails extracts the shared option conventions from MenuText:
// "(50 soft)" → cost, "# stat: Name N" → requires_stat / requires_min.
var (
	reOptCost = regexp.MustCompile(`\(\s*([0-9]+)\s+(\w+)\s*\)\s*$`)
	reOptStat = regexp.MustCompile(`(?:^|\s)#\s*stat\s*:\s*(\S+)\s+([0-9]+)\s*$`)
	reOptTag  = regexp.MustCompile(`^\s*\[([^\]]+)\]\s*`) // leading menu tag: [onetime], [premium], …
)

func parseOptionTails(opt Cmd, menu string) {
	// Leading articy MenuText tags: [onetime] = show once (emitChoice turns it into a
	// once-only gate), [premium] = paid (stripped for now; playable). Any other [tag]
	// is stripped so it never leaks into the visible caption.
	for {
		m := reOptTag.FindStringSubmatch(menu)
		if m == nil {
			break
		}
		switch strings.ToLower(strings.TrimSpace(m[1])) {
		case "onetime", "once":
			opt["__once"] = true
		case "premium", "paid":
			opt["__premium"] = true
		}
		menu = menu[len(m[0]):]
	}
	if m := reOptStat.FindStringSubmatchIndex(menu); m != nil {
		sm := reOptStat.FindStringSubmatch(menu)
		min, _ := strconv.ParseInt(sm[2], 10, 64)
		opt["requires_stat"] = sm[1]
		opt["requires_min"] = min
		menu = strings.TrimSpace(menu[:m[0]])
	}
	if m := reOptCost.FindStringSubmatchIndex(menu); m != nil {
		sm := reOptCost.FindStringSubmatch(menu)
		amt, _ := strconv.ParseInt(sm[1], 10, 64)
		opt["cost"] = map[string]any{"currency": sm[2], "amount": amt}
		menu = strings.TrimSpace(menu[:m[0]])
	}
	opt["text"] = menu
}

// stripLeadingTags consumes leading "[tag]" markers off a node's Text prop
// (mutating it, so the spoken line renders clean) and returns the tags,
// lowercased. The partner writes option tags in the branch fragment's text.
func stripLeadingTags(n *node) []string {
	text, _ := n.props["Text"].(string)
	trimmed := strings.TrimSpace(text)
	var tags []string
	for {
		m := reOptTag.FindStringSubmatch(trimmed)
		if m == nil {
			break
		}
		tags = append(tags, strings.ToLower(strings.TrimSpace(m[1])))
		trimmed = trimmed[len(m[0]):]
	}
	if len(tags) > 0 {
		n.props["Text"] = trimmed
	}
	return tags
}

// captionFromText derives an option caption from the branch's first spoken
// line: the target's own Text, or — for a silent connector node — one hop
// down its single continuation. Long narration is clipped on a word.
func captionFromText(g *gen, tn *node) string {
	text := strings.TrimSpace(prop(tn.props, "Text"))
	// Silent connector fragments (empty Text, one continuation) are common at
	// branch heads — follow the single-out chain a few hops to the first line
	// that can carry the caption.
	cur := tn
	for hops := 0; text == "" && hops < 4; hops++ {
		next := g.single(cur)
		if next == "" {
			break
		}
		nn, ok := g.nodes[next]
		if !ok {
			break
		}
		stripLeadingTags(nn) // clean the spoken line; pricing stays with its own choice
		text = strings.TrimSpace(prop(nn.props, "Text"))
		cur = nn
	}
	if nl := strings.IndexAny(text, "\r\n"); nl >= 0 {
		text = strings.TrimSpace(text[:nl])
	}
	const max = 90
	if len([]rune(text)) > max {
		r := []rune(text)[:max]
		if sp := strings.LastIndexByte(string(r), ' '); sp > 40 {
			r = []rune(string(r)[:sp])
		}
		text = string(r) + "…"
	}
	return text
}

func firstNonEmpty(ss ...string) string {
	for _, s := range ss {
		if s != "" {
			return s
		}
	}
	return ""
}
