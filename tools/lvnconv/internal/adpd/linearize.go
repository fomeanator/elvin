package adpd

import (
	"html"
	"regexp"
	"sort"
	"strings"
)

// ── structural vs. player-choice disambiguation ──────────────────────────────
//
// articy stores the whole flow as a graph: a node with several outgoing edges is
// either a real player choice (the targets are the menu lines) OR structural
// branching (a scene transition, a routing hub, a logic split). The earlier
// decoder turned EVERY fan-out into a `choice`, so scene delimiters ("Сцена N. …")
// and empty routing hubs surfaced as bogus "Дальше"/"Сцена N" buttons — the novel
// read as a branch index, not a story. We classify each target and keep a `choice`
// only where the options are genuine dialogue lines; structural fan-outs collapse
// to a single sequential continuation (the scenes/sub-flows they point at are
// still emitted — buildExportAll surfaces every node — just not as menu options).

var sceneTextRe = regexp.MustCompile(`^\s*Сцена\s+\d+\b`)

// nodeClass labels a node by what it contributes to the flow.
func (fl flow) nodeClass(n uint32) string {
	if ln, ok := fl.logic[n]; ok {
		if ln.cond {
			return "cond"
		}
		return "instr"
	}
	t := strings.TrimSpace(fl.text[n])
	switch {
	case t == "":
		return "empty" // a routing hub — never a player option
	case sceneTextRe.MatchString(t):
		return "scene" // a scene delimiter — a transition, never a player option
	default:
		return "text" // a real dialogue line — a valid menu option
	}
}

// linearizeStructuralFanouts rewrites the graph so only genuine menus stay
// branching. For each fan-out: if ≥2 distinct targets are real dialogue lines it
// is a player choice (keep just those text options); otherwise it is structural
// and collapses to one continuation — preferring a dialogue line, then a scene
// transition, then a logic node, then the first edge — so the story flows on
// instead of presenting a menu of delimiters.
func linearizeStructuralFanouts(fl flow) {
	for n := range fl.nodes {
		es := fl.succ[n]
		if len(es) < 2 {
			continue
		}
		seen := map[uint32]bool{}
		var uniq, textEdges []edge
		for _, e := range es {
			if seen[e.dst] {
				continue
			}
			seen[e.dst] = true
			uniq = append(uniq, e)
			if fl.nodeClass(e.dst) == "text" {
				textEdges = append(textEdges, e)
			}
		}
		if len(uniq) < 2 {
			fl.succ[n] = uniq
			continue
		}
		if len(textEdges) >= 2 {
			fl.succ[n] = textEdges // a real choice between dialogue lines
			continue
		}
		// Structural: keep a single continuation so the flow stays linear here.
		pick := uniq[0]
		for _, want := range []string{"text", "scene", "instr", "cond"} {
			done := false
			for _, e := range uniq {
				if fl.nodeClass(e.dst) == want {
					pick, done = e, true
					break
				}
			}
			if done {
				break
			}
		}
		fl.succ[n] = []edge{pick}
	}
}

// ── hierarchy spine ──────────────────────────────────────────────────────────
//
// The 0x02 connection graph is shattered into hundreds of islands; the connective
// tissue (scene→scene, chapter→chapter) is the container nesting. hierarchyOrder
// walks the container tree depth-first in child-list (authoring) order and returns
// every flow node in that order — the story's spine. On the test novels this
// recovers ~98% of content as one ordered sequence (vs ~144 disconnected 0x02
// components).

func (fl flow) hierarchyOrder() []uint32 {
	isChild := map[uint32]bool{}
	for _, ch := range fl.childrenOf {
		for _, c := range ch {
			isChild[c] = true
		}
	}
	var tops []uint32
	for c := range fl.contSet {
		if !isChild[c] {
			tops = append(tops, c)
		}
	}
	sort.Slice(tops, func(i, j int) bool { return tops[i] < tops[j] })

	visited := map[uint32]bool{}
	var order []uint32
	// A scene/chapter name lives on its container (FlowFragment/Dialog DisplayName,
	// e.g. "Сцена 8. Двор общаги"). Emit it as a narration beat on entry so
	// AutoStage turns it into a background — the scene transitions the player sees.
	synth := uint32(0xF0000000)
	var dfs func(n uint32, depth int)
	dfs = func(n uint32, depth int) {
		if visited[n] || depth > 1<<16 {
			return
		}
		visited[n] = true
		if fl.contSet[n] {
			if name := strings.TrimSpace(fl.text[n]); name != "" {
				fl.text[synth] = name // a synthetic narration node (no speaker)
				order = append(order, synth)
				synth++
			}
			for _, c := range fl.childrenOf[n] {
				dfs(c, depth+1)
			}
			return
		}
		order = append(order, n) // a content / logic node
	}
	for _, t := range tops {
		dfs(t, 0)
	}
	return order
}

// linearizeByComponents reconstructs the flow with FAITHFUL reconvergence: within
// a scene the 0x02 pin graph (articy's own TraverseFlow, via pinGraph.nextStops)
// drives the successors, so a choice's branches rejoin at their shared next stop
// (the merge points the spine flattens away). Scenes are not 0x02-connected, so any
// node the entry's flow doesn't reach is chained — in authoring order — onto a
// reached dead-end leaf (a single goto, never a bogus choice). Self-validates 100%
// coverage; returns ok=false to fall back to linearizeByHierarchy if anything would
// be stranded.
// linearizeFaithful is the port of articy's TraverseFlow: it builds the flow graph
// over ONLY emittable nodes (DialogFragment / Condition / Outcome), connected by
// forward pin-flow (reachFromPin descends/surfaces containers, passes through
// hubs). A DF with ≥2 forward targets is a player choice whose branches reconverge
// forward at their shared next node — no backward "revisit the menu" loop unless
// the author genuinely wired one. Conditions stay as if/then/else (kept in
// fl.logic), Outcomes as set. Returns the root entries + ok; ok=false (stranded
// content) falls back to the older heuristics.
// tops are the entry container(s) to descend from; allowed bounds it to a chapter
// subtree (nil = whole novel).
func linearizeFaithful(fl flow, tops []uint32, allowed map[uint32]bool) ([]uint32, bool) {
	if fl.pg == nil {
		return nil, false
	}
	pg := fl.pg
	entries := pg.rootEntry(tops, allowed)
	if len(entries) == 0 {
		return nil, false
	}

	var emit []uint32
	for n, c := range pg.class {
		if !pg.isEmittable(n) {
			continue
		}
		if allowed != nil && !allowed[n] {
			continue
		}
		emit = append(emit, n)
		fl.nodes[n] = true
		if c == cidCondition {
			// [in, out-true, out-false] → two branches, tagged by source pin so
			// emitModels' conditionPins can split them into the true/false outputs.
			ops := pg.outPins(n)
			var es []edge
			for _, op := range ops {
				for _, t := range pg.reachFromPin(op, allowed) {
					es = append(es, edge{src: n, dst: t, srcPin: op})
				}
			}
			fl.succ[n] = es
			continue
		}
		// DialogFragment (say / choice) or Outcome (set): single output pin, its
		// forward targets. ≥2 targets ⇒ a choice.
		var targets []uint32
		seen := map[uint32]bool{}
		for _, p := range pg.outPins(n) {
			for _, t := range pg.reachFromPin(p, allowed) {
				if !seen[t] {
					seen[t] = true
					targets = append(targets, t)
				}
			}
		}
		var es []edge
		for _, t := range targets {
			es = append(es, edge{src: n, dst: t})
		}
		fl.succ[n] = es
	}

	// Coverage: reach from the root entries; chain any stranded emittable island
	// (pockets entered only by Jump, etc.) onto a reached dead-end leaf so nothing
	// is lost — a single forward goto, never a bogus choice.
	_, R := bfs(fl, entries, 1<<30)
	var leaves []uint32
	for _, n := range emit {
		if R[n] && len(fl.succ[n]) == 0 {
			leaves = append(leaves, n)
		}
	}
	sort.Slice(emit, func(i, j int) bool { return emit[i] < emit[j] })
	extra := append([]uint32{}, entries...)
	for _, x := range emit {
		if R[x] {
			continue
		}
		// Chain the stranded island onto a reached dead-end leaf (a forward goto), so
		// it flows in continuously. If there's no leaf to chain from, surface the
		// island as an extra entry (a chapter-hub branch) — never drop it.
		if len(leaves) > 0 {
			l := leaves[len(leaves)-1]
			leaves = leaves[:len(leaves)-1]
			fl.succ[l] = []edge{{src: l, dst: x}}
		} else {
			extra = append(extra, x)
		}
		_, sub := bfs(fl, []uint32{x}, 1<<30)
		for k := range sub {
			if !R[k] {
				R[k] = true
				if pg.isEmittable(k) && len(fl.succ[k]) == 0 {
					leaves = append(leaves, k)
				}
			}
		}
	}
	return extra, true
}

// forwardEdges returns a node's forward pin-flow edges over emittable targets —
// articy's TraverseFlow reduced to "the next stop(s) leaving this node". A
// Condition keeps its per-pin (true/false) source so emitModels can split it.
// allowed (nil = whole novel) bounds the flow to a chapter subtree — a cross-
// boundary edge is simply dropped, so a chapter file ends where the story leaves it.
func forwardEdges(pg *pinGraph, n uint32, allowed map[uint32]bool) []edge {
	if pg.class[n] == cidCondition {
		var es []edge
		for _, op := range pg.outPins(n) {
			for _, t := range pg.reachFromPin(op, allowed) {
				es = append(es, edge{src: n, dst: t, srcPin: op})
			}
		}
		return es
	}
	var es []edge
	seen := map[uint32]bool{}
	for _, p := range pg.outPins(n) {
		for _, t := range pg.reachFromPin(p, allowed) {
			if !seen[t] {
				seen[t] = true
				es = append(es, edge{src: n, dst: t})
			}
		}
	}
	return es
}

// linearizeAnchored is the true articy linearizer: it anchors the flow to the
// REAL container hierarchy (parentOf), so ANY project yields ONE continuous,
// chapter-ordered spine — never a fan-out menu of disconnected islands (the
// regression that turned a novel into a 54-button "Дальше" index).
//
//   - Sequencing and choices come from the forward pin-flow (reachFromPin) — this
//     IS articy's own TraverseFlow: a DialogFragment with ≥2 forward targets is a
//     real player choice whose branches reconverge forward; Conditions stay if/else,
//     Outcomes stay set. So within any connected region the order and menus are exact.
//   - The ENTRY and the order of otherwise-disconnected regions come from the
//     hierarchy: the root's chapter children, ordered by the episode number in their
//     name ("Эпизод 3. …" → 3), each entered through its input pin. Projects without
//     numbered chapters fall back to authoring order — still one spine.
//
// Every emittable node lands on the single spine; a stranded pocket is chained onto
// a reached dead-end as a forward goto (in chapter/reading order), never surfaced as
// a bogus option. Returns a single entry; ok=false only without a hierarchy root.
//
// allowed (nil = whole novel) bounds everything to a subtree — this is how the
// chapter splitter reuses the exact same linearizer per chapter (root = the chapter
// container, allowed = its subtree), so a chapter file is linearised identically to
// the whole novel, just scoped.
func linearizeAnchored(fl flow, root uint32, allowed map[uint32]bool) ([]uint32, bool) {
	pg := fl.pg
	if pg == nil {
		return nil, false
	}
	inScope := func(n uint32) bool { return allowed == nil || allowed[n] }
	// 1. forward pin-flow graph over emittable nodes.
	var emit []uint32
	for n := range pg.class {
		if !pg.isEmittable(n) || !inScope(n) {
			continue
		}
		emit = append(emit, n)
		fl.nodes[n] = true
		fl.succ[n] = forwardEdges(pg, n, allowed)
	}
	if len(emit) == 0 {
		return nil, false
	}
	sort.Slice(emit, func(i, j int) bool { return emit[i] < emit[j] })

	// 2. chapters = root's children (in scope), ordered by the number in the name.
	kids := completeChildren(fl)
	chapters := []uint32{}
	for _, c := range kids[root] {
		if inScope(c) {
			chapters = append(chapters, c)
		}
	}
	sort.SliceStable(chapters, func(i, j int) bool {
		ni, nj := chapterNum(fl.text[chapters[i]]), chapterNum(fl.text[chapters[j]])
		if ni != nj {
			return ni < nj
		}
		return chapters[i] < chapters[j]
	})
	chapIndex := map[uint32]int{}
	for i, c := range chapters {
		chapIndex[c] = i
	}
	chapterRank := func(n uint32) int {
		cur := n
		for i := 0; i < 1<<16; i++ {
			p, ok := fl.parentOf[cur]
			if !ok || p == root {
				if idx, ok := chapIndex[cur]; ok {
					return idx
				}
				return len(chapters)
			}
			cur = p
		}
		return len(chapters)
	}

	// 3. forward-roots (no predecessor). Every one needs an incoming edge to be
	// reachable at all; a node WITH a predecessor is already reached from some root.
	// Ordered by chapter (episode number) then ordinal so the spine reads in story
	// order.
	hasPred := map[uint32]bool{}
	for _, n := range emit {
		for _, e := range fl.succ[n] {
			hasPred[e.dst] = true
		}
	}
	var froots []uint32
	for _, n := range emit {
		if !hasPred[n] {
			froots = append(froots, n)
		}
	}
	if len(froots) == 0 { // fully cyclic (no head anywhere) — enter at the lowest node
		froots = []uint32{emit[0]}
	}
	sort.SliceStable(froots, func(i, j int) bool {
		ri, rj := chapterRank(froots[i]), chapterRank(froots[j])
		if ri != rj {
			return ri < rj
		}
		return froots[i] < froots[j]
	})

	// entry = the first chapter's real start (descend its input pin), pulled to the
	// front. Everything else keeps chapter order.
	entry := froots[0]
	if len(chapters) > 0 {
		if es := pg.rootEntry([]uint32{chapters[0]}, allowed); len(es) > 0 {
			entry = es[0]
		}
	}
	ordered := []uint32{entry}
	for _, f := range froots {
		if f != entry {
			ordered = append(ordered, f)
		}
	}

	// 4. chain the roots into ONE spine: root[i-1] flows — through a natural ending
	// (leaf) it reaches — into root[i]. Consuming ONE fresh leaf per link (never the
	// last region's) is what keeps the story END reachable from everywhere; the
	// earlier bug drained every leaf and left the novel an exit-less loop.
	//
	// firstFreeLeaf walks the FROZEN pin-flow (pinDst, snapshotted before we add any
	// chain edges) so a chain edge can never route it to another region's leaf and
	// close an exit-less loop between weakly-connected roots.
	pinDst := make(map[uint32][]uint32, len(emit))
	for _, n := range emit {
		ds := make([]uint32, 0, len(fl.succ[n]))
		for _, e := range fl.succ[n] {
			ds = append(ds, e.dst)
		}
		pinDst[n] = ds
	}
	usedLeaf := map[uint32]bool{}
	firstFreeLeaf := func(r uint32) (uint32, bool) {
		vis := map[uint32]bool{r: true}
		q := []uint32{r}
		for i := 0; i < len(q); i++ {
			x := q[i]
			if len(pinDst[x]) == 0 && !usedLeaf[x] {
				return x, true
			}
			for _, d := range pinDst[x] {
				if !vis[d] {
					vis[d] = true
					q = append(q, d)
				}
			}
		}
		return 0, false
	}
	// dfFallback adds an exit edge to dst when no free leaf is available. It must land
	// on a DialogFragment: a DF may carry any number of outgoing connections (it just
	// branches), but giving an Instruction/Outcome a second output pin makes the
	// back-end reject it ("flow runs out"). Prefers the given node, else the latest DF.
	var latestDF uint32
	hasDF := false
	for _, n := range emit {
		if pg.class[n] == cidDialogFrag && (!hasDF || n > latestDF) {
			latestDF, hasDF = n, true
		}
	}
	dfFallback := func(preferred, dst uint32) {
		src := preferred
		if pg.class[src] != cidDialogFrag {
			if !hasDF {
				return // no DF anywhere (logic-only scope) — nothing safe to attach to
			}
			src = latestDF
		}
		fl.succ[src] = append(fl.succ[src], edge{src: src, dst: dst})
	}
	prev := ordered[0]
	for i := 1; i < len(ordered); i++ {
		if l, ok := firstFreeLeaf(prev); ok {
			usedLeaf[l] = true
			fl.succ[l] = []edge{{src: l, dst: ordered[i]}}
		} else { // no free leaf under prev — branch at a DialogFragment
			dfFallback(prev, ordered[i])
		}
		prev = ordered[i]
	}

	// 4b. coverage: a sub-region whose ONLY entry was a cross-scope edge (dropped by
	// `allowed` when scoping to a chapter) has no forward-root, so the root chain
	// misses it. Chain any still-unreachable beat onto a reached ending so no content
	// is lost — the chapter carries every line that belongs to it.
	_, reached := bfs(fl, []uint32{entry}, 1<<30)
	var pool []uint32
	for _, n := range emit {
		if reached[n] && len(fl.succ[n]) == 0 {
			pool = append(pool, n)
		}
	}
	stranded := make([]uint32, 0)
	for _, n := range emit {
		if !reached[n] {
			stranded = append(stranded, n)
		}
	}
	sort.SliceStable(stranded, func(i, j int) bool {
		ri, rj := chapterRank(stranded[i]), chapterRank(stranded[j])
		if ri != rj {
			return ri < rj
		}
		return stranded[i] < stranded[j]
	})
	for _, s := range stranded {
		if reached[s] {
			continue
		}
		if len(pool) > 0 {
			l := pool[len(pool)-1]
			pool = pool[:len(pool)-1]
			fl.succ[l] = []edge{{src: l, dst: s}}
		} else {
			dfFallback(prev, s)
		}
		_, sub := bfs(fl, []uint32{s}, 1<<30)
		for k := range sub {
			if !reached[k] {
				reached[k] = true
				if len(fl.succ[k]) == 0 {
					pool = append(pool, k)
				}
			}
		}
		prev = s
	}

	// 5. guarantee reach-to-END: chaining can still trap a node in a sink cycle when
	// two weakly-connected roots cross-link. Break each such cycle minimally —
	// redirect ONE edge of its deepest (highest-ordinal = latest) node to a real
	// ending — so a player can never dead-loop, without adding a spurious choice.
	ensureCanEnd(fl, emit)

	// 6. scene backgrounds: articy names each scene on its Dialog container
	// ("Сцена 16. Кухня общаги"), not inline — so drop them into the flow as a
	// narration beat at each scene's entry. AutoStage turns such a line into a `bg`,
	// and collectArt then makes a grey placeholder for any location without art — so
	// the import has backgrounds (real or fill-in) and visible scene transitions.
	// Runs AFTER ensureCanEnd: a marker is a pass-through narration node (M→entry), so
	// it preserves every path — but it isn't in `emit`, so ensureCanEnd must not see
	// it (it would read a path through a marker as a dead end and cut real content).
	entry = injectSceneMarkers(fl, allowed, entry)
	return []uint32{entry}, true
}

// sceneContainerRe matches an articy scene container's name ("Сцена 16. Кухня
// общаги") — the same shape AutoStage converts into a `bg`.
var sceneContainerRe = regexp.MustCompile(`^\s*Сцена\s+\d+\.`)

// injectSceneMarkers drops each scene's name into the flow as a narration beat at
// the scene's entry, so AutoStage emits a background there. A synthetic node holds
// the name; every edge ENTERING the scene from outside is rerouted through it (so
// it fires on scene entry, not on internal loops). Returns the (possibly wrapped)
// entry node.
func injectSceneMarkers(fl flow, allowed map[uint32]bool, entry uint32) uint32 {
	pg := fl.pg
	inScope := func(n uint32) bool { return allowed == nil || allowed[n] }
	sceneName := func(container uint32) string {
		n := strings.TrimSpace(html.UnescapeString(fl.text[container]))
		if sceneContainerRe.MatchString(n) {
			return n
		}
		return ""
	}
	// scene of a node = its nearest Dialog ancestor that carries a scene name.
	sceneOf := func(n uint32) uint32 {
		cur := n
		for i := 0; i < 1<<16; i++ {
			p, ok := fl.parentOf[cur]
			if !ok {
				return 0
			}
			if pg.class[p] == cidDialog && sceneName(p) != "" {
				return p
			}
			cur = p
		}
		return 0
	}
	var emit []uint32
	for n := range pg.class {
		if pg.isEmittable(n) && inScope(n) {
			emit = append(emit, n)
		}
	}
	sc := make(map[uint32]uint32, len(emit))
	preds := map[uint32][]uint32{}
	for _, n := range emit {
		sc[n] = sceneOf(n)
	}
	for _, n := range emit {
		for _, e := range fl.succ[n] {
			preds[e.dst] = append(preds[e.dst], n)
		}
	}
	// scenes present in the flow, in a stable order.
	var scenes []uint32
	seenScene := map[uint32]bool{}
	for _, n := range emit {
		if s := sc[n]; s != 0 && !seenScene[s] {
			seenScene[s] = true
			scenes = append(scenes, s)
		}
	}
	sort.Slice(scenes, func(i, j int) bool { return scenes[i] < scenes[j] })

	synth := uint32(0xE0000000)
	for _, s := range scenes {
		es := pg.rootEntry([]uint32{s}, allowed)
		if len(es) == 0 || sc[es[0]] != s {
			continue // scene entry not resolvable / not actually inside the scene
		}
		e := es[0]
		m := synth
		synth++
		fl.text[m] = sceneName(s)
		fl.succ[m] = []edge{{src: m, dst: e}}
		rerouted := false
		for _, x := range preds[e] {
			if sc[x] == s {
				continue // an in-scene edge stays direct (no re-fire on internal loops)
			}
			for i := range fl.succ[x] {
				if fl.succ[x][i].dst == e {
					fl.succ[x][i].dst = m
					rerouted = true
				}
			}
		}
		if entry == e {
			entry = m
			rerouted = true
		}
		if !rerouted { // nothing enters from outside — don't leave an orphan marker
			delete(fl.text, m)
			delete(fl.succ, m)
		}
	}
	return entry
}

// ensureCanEnd redirects the minimal set of edges so every emittable node can reach
// a terminal (a natural ending). It repeatedly finds nodes that cannot reach any
// terminal, takes the highest-ordinal one (the latest content = the intended tail),
// and ADDS an edge from it to a terminal (never replacing its edges — replacing
// would orphan its successors and drop content). Each pass frees at least one sink
// cycle; the added edge is a rare "way out of the loop" beat.
func ensureCanEnd(fl flow, emit []uint32) {
	for pass := 0; pass < len(emit)+1; pass++ {
		preds := map[uint32][]uint32{}
		var term uint32
		haveTerm := false
		canEnd := map[uint32]bool{}
		var q []uint32
		for _, n := range emit {
			if len(fl.succ[n]) == 0 {
				canEnd[n] = true
				q = append(q, n)
				if !haveTerm {
					term, haveTerm = n, true
				}
			}
			for _, e := range fl.succ[n] {
				preds[e.dst] = append(preds[e.dst], n)
			}
		}
		if !haveTerm {
			// The whole scope is cyclic — no ending exists at all (e.g. a chapter whose
			// only real ending was a cross-boundary edge). Make the latest beat (highest
			// ordinal = the chapter's tail) the ending, then re-evaluate.
			var w uint32
			found := false
			for _, n := range emit {
				if fl.pg.class[n] == cidDialogFrag && (!found || n > w) {
					w, found = n, true
				}
			}
			if !found {
				for _, n := range emit {
					if !found || n > w {
						w, found = n, true
					}
				}
			}
			if !found {
				return
			}
			fl.succ[w] = nil // a leaf → the story END
			continue
		}
		for i := 0; i < len(q); i++ {
			for _, p := range preds[q[i]] {
				if !canEnd[p] {
					canEnd[p] = true
					q = append(q, p)
				}
			}
		}
		// Prefer to add the exit edge on a DialogFragment — it can legitimately carry
		// an extra outgoing connection (a "leave the loop" beat). Appending to an
		// Instruction/Condition would give it a second output pin, which the back-end
		// rejects ("flow runs out"); for a rare logic-only cycle, redirect instead.
		var worstDF, worstAny uint32
		foundDF, foundAny := false, false
		for _, n := range emit {
			if canEnd[n] {
				continue
			}
			if !foundAny || n > worstAny {
				worstAny, foundAny = n, true
			}
			if fl.pg.class[n] == cidDialogFrag && (!foundDF || n > worstDF) {
				worstDF, foundDF = n, true
			}
		}
		switch {
		case foundDF:
			fl.succ[worstDF] = append(fl.succ[worstDF], edge{src: worstDF, dst: term})
		case foundAny:
			fl.succ[worstAny] = []edge{{src: worstAny, dst: term}} // logic-only cycle: redirect
		default:
			return // every node can reach an ending
		}
	}
}

func linearizeByComponents(fl flow) (uint32, bool) {
	if fl.pg == nil {
		return 0, false
	}
	order := fl.hierarchyOrder()
	if len(order) == 0 {
		return 0, false
	}
	pg := fl.pg
	isOption := func(t uint32) bool {
		txt := strings.TrimSpace(fl.text[t])
		return txt != "" && !sceneTextRe.MatchString(txt)
	}

	var stops []uint32
	for i, n := range order {
		if ln, ok := fl.logic[n]; ok && ln.cond {
			delete(fl.logic, n)
		}
		fl.nodes[n] = true
		hasNext := i+1 < len(order)
		if !pg.isStop(n) {
			// scene-name beats / logic glue: chain along the authoring spine so they
			// are emitted and lead into their scene's first stop.
			if hasNext {
				fl.succ[n] = []edge{{src: n, dst: order[i+1]}}
			} else {
				fl.succ[n] = nil
			}
			continue
		}
		stops = append(stops, n)
		raw := pg.nextStops(n)
		var opts []uint32
		seen := map[uint32]bool{}
		for _, t := range raw {
			if !seen[t] && isOption(t) {
				seen[t] = true
				opts = append(opts, t)
			}
		}
		switch {
		case len(opts) >= 2 && len(opts) <= maxChoiceOptions:
			var es []edge
			for _, t := range opts {
				es = append(es, edge{src: n, dst: t}) // branches reconverge via their own succ
			}
			fl.succ[n] = es
		case len(raw) >= 1:
			fl.succ[n] = []edge{{src: n, dst: raw[0]}} // in-scene linear flow
		default:
			fl.succ[n] = nil // leaf: a scene/branch end
		}
	}

	entry := order[0]
	_, R := bfs(fl, []uint32{entry}, 1<<30)
	var leaves []uint32
	for _, n := range stops {
		if R[n] && len(fl.succ[n]) == 0 {
			leaves = append(leaves, n)
		}
	}
	for _, x := range order {
		if R[x] {
			continue
		}
		if len(leaves) == 0 {
			return 0, false // nothing safe to chain from → fall back
		}
		l := leaves[len(leaves)-1]
		leaves = leaves[:len(leaves)-1]
		fl.succ[l] = []edge{{src: l, dst: x}}
		_, sub := bfs(fl, []uint32{x}, 1<<30)
		for k := range sub {
			if !R[k] {
				R[k] = true
				if pg.isStop(k) && len(fl.succ[k]) == 0 {
					leaves = append(leaves, k)
				}
			}
		}
	}
	for _, n := range stops {
		if !R[n] {
			return 0, false // would strand content → fall back
		}
	}
	return entry, true
}

// linearizeByHierarchy reconstructs the playable flow the way articy's own
// ArticyFlowPlayer traverses it (decompiled TraverseFlow): a scene's local 0x02
// connection graph carries the in-scene flow — including real player choices (a
// fragment whose output fans out to several dialogue lines) — and the container
// hierarchy chains scenes and chapters. Concretely:
//
//   - 0x02 connections drive flow WITHIN a Dialog; a ≥2-dialogue-line fan-out
//     stays a choice (linearizeStructuralFanouts already dropped scene/empty
//     pseudo-choices);
//   - entering a container routes to its first child (descent);
//   - a node with no outgoing connection (a scene/branch exit) continues to the
//     next sibling — and a container's last child climbs to the container's own
//     next sibling — so scenes and chapters reconverge into one connected novel.
//
// Returns the entry (the first top-level container) and ok=false when there is no
// decodable hierarchy (e.g. a synthetic test graph), so the caller falls back to
// the 0x02 component export.
func linearizeByHierarchy(fl flow) (uint32, bool) {
	order := fl.hierarchyOrder()
	if len(order) == 0 {
		return 0, false
	}

	// Real player choices come from articy's own pin traversal when available
	// (nextStops resolves the stops reachable from a node, descending containers),
	// else the direct 0x02 dialogue-line fan-out. Kept only when ≥2 options are
	// genuine dialogue lines (scene-marker / empty stops are transitions, not
	// options). nextStops is capped to direct successors as a safety net so a
	// mis-resolved deep traversal can't strand a node in convert.
	isOption := func(t uint32) bool {
		txt := strings.TrimSpace(fl.text[t])
		return txt != "" && !sceneTextRe.MatchString(txt)
	}
	choiceOpts := map[uint32][]uint32{}
	for _, n := range order {
		// Only a DialogFragment (a stop) presents a player choice — never an
		// instruction/condition (that would emit a node with multiple continuations
		// convert.go rejects). Options come from articy's pin traversal when
		// available (descends containers), else the direct 0x02 fan-out.
		if fl.pg != nil && !fl.pg.isStop(n) {
			continue
		}
		var dsts []uint32
		if fl.pg != nil {
			dsts = fl.pg.nextStops(n)
		} else {
			for _, e := range fl.succ[n] {
				dsts = append(dsts, e.dst)
			}
		}
		var opts []uint32
		seen := map[uint32]bool{}
		for _, t := range dsts {
			if !seen[t] && isOption(t) {
				seen[t] = true
				opts = append(opts, t)
			}
		}
		// A real player menu is a small local fan-out. A large one is structural —
		// a node whose pin traversal descended into many scenes/chapters — and must
		// not become a giant bogus choice; let it linearize through the spine.
		if len(opts) >= 2 && len(opts) <= maxChoiceOptions {
			choiceOpts[n] = opts
		}
	}

	// The authoring-order spine guarantees full coverage and reconvergence; a choice
	// node offers its pin-resolved options plus a fall-through to the next authored
	// node so the backbone (hence coverage) is never broken. The fall-through is the
	// honest cost of not having articy's runtime reconvergence: a choice's branches
	// live in disjoint 0x02 islands, so without it ~99% of content is stranded.
	for i, n := range order {
		if ln, ok := fl.logic[n]; ok && ln.cond {
			delete(fl.logic, n)
		}
		next := uint32(0)
		hasNext := i+1 < len(order)
		if hasNext {
			next = order[i+1]
		}
		if opts, ok := choiceOpts[n]; ok {
			seen := map[uint32]bool{}
			var es []edge
			for _, t := range opts {
				if !seen[t] {
					seen[t] = true
					es = append(es, edge{src: n, dst: t})
				}
			}
			if hasNext && !seen[next] {
				es = append(es, edge{src: n, dst: next})
			}
			fl.succ[n] = es
		} else if hasNext {
			fl.succ[n] = []edge{{src: n, dst: next}}
		} else {
			fl.succ[n] = nil
		}
		fl.nodes[n] = true
	}
	return order[0], true
}
