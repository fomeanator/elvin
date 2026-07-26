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

	// inferred — сколько узлов получили родителя не из pParent, а по инцидентным
	// связям (inferMissingParents). Число попадает в отчёт: это мера того, насколько
	// иерархия проекта восстановлена догадкой, а не прочитана.
	inferred int
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
	selfOf, selfKnown := selfOrdinals(objs)
	vm := varMap(objs)
	fl := flow{
		text: map[uint32]string{}, guid: map[uint32]string{}, sp: map[uint32]string{},
		color: map[uint32]string{},
		logic: map[uint32]logicNode{}, succ: map[uint32][]edge{}, nodes: map[uint32]bool{},
		childrenOf: map[uint32][]uint32{}, contSet: map[uint32]bool{},
		parentOf: map[uint32]uint32{},
		pg:       buildPinGraph(objs, selfOf, selfKnown),
	}
	// Кандидаты в родителя для узлов, у которых pParent не раскодировался: контейнер,
	// внутри которого нарисована инцидентная связь (у самого объекта связи pParent
	// есть). См. inferMissingParents — без этого узел выпадает из всех глав и рвёт поток.
	parentHint := map[uint32][]uint32{}
	for i, o := range objs {
		// Real hierarchy: every flow node records its parent container (pParent).
		if self := selfOf[i]; selfKnown[i] {
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
				if par, ok := o.u32(pParent); ok {
					parentHint[r[0]] = append(parentHint[r[0]], par)
					parentHint[r[1]] = append(parentHint[r[1]], par)
				}
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
			if self := selfOf[i]; selfKnown[i] {
				if u, ok := o.color(pColor); ok {
					if hex := colorHex(u); hex != articyDefaultColor {
						fl.color[self] = hex // a deliberate emotion marker (non-default)
					}
				}
			}
		case cidDialog, cidFlowFrag, cidStoryFolder: // container — ordered children
			if self := selfOf[i]; selfKnown[i] {
				fl.contSet[self] = true
				if ch := o.refs(pChild); len(ch) > 0 {
					fl.childrenOf[self] = ch // ordered child list (authoring order)
				}
			}
		case cidCondition: // an if split (0x79 holds the GUID-encoded script)
			if self := selfOf[i]; selfKnown[i] {
				expr := resolveExpr(o.str(pCond), vm)
				if expr == "" {
					expr = resolveExpr(o.str(pInstr), vm)
				}
				if expr != "" {
					fl.logic[self] = logicNode{cond: true, expr: expr}
				}
			}
		case cidOutcome: // a pin script — set/inc
			if self := selfOf[i]; selfKnown[i] {
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
	fl.inferred = inferMissingParents(fl, parentHint)
	return fl
}

// inferMissingParents достраивает иерархию для узлов, у которых свойство pParent
// не раскодировалось, и возвращает их число.
//
// Зачем: главы — это поддеревья контейнеров (parentOf), и линеаризатор считает
// «своими» только узлы внутри поддерева. Узел без родителя не попадает НИ В ОДНУ
// главу, а поток, дошедший до него, обрывается на границе scope — тихо, без
// единого предупреждения. Поймано на живом партнёрского проекта: у реплики 63877 («Затем поднял
// руки, и я…») в бинарнике не разобрался pParent, и Эпизод 11 обрывался на 14-й
// реплике из 739 — две трети главы игрок не видел никогда.
//
// Восстанавливаем по объекту связи: он сам лежит в том же контейнере, что и его
// концы, и его собственный pParent разбирается нормально. Берём самого частого
// кандидата-контейнер среди инцидентных связей.
func inferMissingParents(fl flow, hint map[uint32][]uint32) int {
	n := 0
	for node := range fl.pg.class {
		if _, ok := fl.parentOf[node]; ok {
			continue
		}
		votes := map[uint32]int{}
		for _, cand := range hint[node] {
			if cand == node || !fl.contSet[cand] {
				continue
			}
			votes[cand]++
		}
		best, bestN := uint32(0), 0
		for c, v := range votes {
			if v > bestN || (v == bestN && c < best) {
				best, bestN = c, v
			}
		}
		if bestN > 0 {
			fl.parentOf[node] = best
			n++
		}
	}
	return n
}
