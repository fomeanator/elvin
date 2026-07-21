package adpd

import "encoding/binary"

// ── reconstructed flow model ─────────────────────────────────────────────────

type logicNode struct {
	cond bool // true → Condition (if), false → Instruction (set)
	expr string
}

type edge struct{ src, dst, srcPin uint32 }

type flow struct {
	text  map[uint32]string    // node ordinal → line
	guid  map[uint32]string    // node ordinal → fragment GUID (stable i18n key)
	sp    map[uint32]string    // node ordinal → speaker caption
	color map[uint32]string    // node ordinal → marker colour (#rrggbb, emotion cue)
	logic map[uint32]logicNode // node ordinal → instruction/condition
	succ  map[uint32][]edge    // node ordinal → outgoing edges
	nodes map[uint32]bool      // every node that appears in an edge

	// Container hierarchy: articy nests content in FlowFragment/Dialogue
	// containers, which list their children (in authoring order) as 0x0 refs.
	// The 0x02 connection graph alone is shattered into hundreds of islands —
	// the cross-scene/chapter flow lives in this nesting. childrenOf maps a
	// container's self ordinal → its ordered children; contSet marks containers.
	childrenOf map[uint32][]uint32
	contSet    map[uint32]bool

	// parentOf maps every flow node → its container parent (from pParent 0x0c on
	// the child). This is articy's REAL hierarchy (ArticyHierarchyManager); the
	// pChild-based childrenOf above is incomplete (a container doesn't list all its
	// children). Chapter splitting uses parentOf — the project root's FlowFragment
	// children are the chapters, each subtree complete.
	parentOf map[uint32]uint32

	// pg is articy's own pin/connection graph: for a DialogFragment it resolves the
	// stops reachable next (descending containers via pins) — the player's branches.
	pg *pinGraph
}

// container object kinds (FlowFragment / Dialogue), by (C, typecode).
var (
	kSceneCont   = kind{5, 22} // a scene / dialogue container
	kChapterCont = kind{6, 20} // a chapter / top-level container
)

const maxChoiceOptions = 8 // above this a fan-out is structural, not a player menu

const pChild = 0x00 // a container's child ordinal (repeats, in authoring order)

func decodeFlow(d []byte) flow {
	idx := int(binary.LittleEndian.Uint64(d[8:]))
	if idx <= 0 || idx > len(d) {
		idx = len(d)
	}
	objs := walkObjects(d, idx)
	vm := varMap(objs)
	fl := flow{
		text: map[uint32]string{}, guid: map[uint32]string{}, sp: map[uint32]string{},
		color: map[uint32]string{},
		logic: map[uint32]logicNode{}, succ: map[uint32][]edge{}, nodes: map[uint32]bool{},
		childrenOf: map[uint32][]uint32{}, contSet: map[uint32]bool{},
		parentOf: map[uint32]uint32{},
		pg:       buildPinGraph(objs),
	}
	for _, o := range objs {
		// Real hierarchy: every flow node records its parent container (pParent).
		if self, ok := o.u32(pSelf); ok {
			if _, isFlow := fl.pg.class[self]; isFlow {
				if par, ok := o.u32(pParent); ok {
					fl.parentOf[self] = par
				}
			}
		}
		switch o.classId {
		case cidConnection:
			r := o.refs(pConn)
			if len(r) >= 4 {
				e := edge{src: r[0], dst: r[1], srcPin: r[2]}
				fl.succ[e.src] = append(fl.succ[e.src], e)
				fl.nodes[e.src] = true
				fl.nodes[e.dst] = true
			}
		case cidMLText: // the line's text, parented to its DialogFragment
			if par, ok := o.u32(pParent); ok {
				if t := o.str(pText); t != "" {
					fl.text[par] = stripHTML(t)
					if g := o.str(pID); g != "" {
						fl.guid[par] = g
					}
				}
			}
		case cidModelDep: // a reference (the speaker), parented to the fragment
			if par, ok := o.u32(pParent); ok {
				if s := o.str(pCaption); s != "" {
					fl.sp[par] = s
				}
			}
		case cidDialogFrag: // the dialogue node itself — carries the marker BackgroundColor
			if self, ok := o.u32(pSelf); ok {
				if u, ok := o.color(pColor); ok {
					if hex := colorHex(u); hex != articyDefaultColor {
						fl.color[self] = hex // a deliberate emotion marker (non-default)
					}
				}
			}
		case cidDialog, cidFlowFrag, cidStoryFolder: // container — ordered children
			if self, ok := o.u32(pSelf); ok {
				fl.contSet[self] = true
				if ch := o.refs(pChild); len(ch) > 0 {
					fl.childrenOf[self] = ch // ordered child list (authoring order)
				}
			}
		case cidCondition: // an if split (0x79 holds the GUID-encoded script)
			if self, ok := o.u32(pSelf); ok {
				expr := resolveExpr(o.str(pCond), vm)
				if expr == "" {
					expr = resolveExpr(o.str(pInstr), vm)
				}
				if expr != "" {
					fl.logic[self] = logicNode{cond: true, expr: expr}
				}
			}
		case cidOutcome: // a pin script — set/inc
			if self, ok := o.u32(pSelf); ok {
				// Prefer the full GUID-encoded script (0x79); the readable 0x03 copy
				// is truncated with "…" for long names and must not leak into a set.
				expr := resolveExpr(o.str(pCond), vm)
				if expr == "" {
					expr = resolveExpr(o.str(pInstr), vm)
				}
				if parseableInstr(expr) {
					fl.logic[self] = logicNode{cond: false, expr: expr}
				}
			}
		}
	}
	return fl
}
