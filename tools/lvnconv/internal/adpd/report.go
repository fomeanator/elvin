package adpd

// LinearizeReport says which linearizer produced the export, why every earlier
// (more faithful) stage fell through, and how healthy the raw pin-flow graph
// was. The cascade degrades gracefully — anchored → faithful → components →
// hierarchy → structural fan-outs — but a downgrade trades reconvergence and
// loop fidelity for coverage, and the author must see that instead of finding
// out from a subtly-wrong script. Surfaced by `lvnconv import/convert` and the
// server's import endpoint.
type LinearizeReport struct {
	// Algorithm that produced the export: "start" (explicit -start node),
	// "anchored", "anchored/chapters", "faithful", "components", "hierarchy"
	// or "structural-fanouts" (coarsest).
	Algorithm string `json:"algorithm"`
	// Fallbacks lists, in cascade order, why each earlier stage was skipped.
	Fallbacks []string `json:"fallbacks,omitempty"`
	// Emittable is the number of pin-flow nodes that produce script output
	// (dialogue fragments, conditions, outcomes).
	Emittable int `json:"emittable"`
	// Trapped counts emittable nodes that cannot reach any leaf in the raw
	// forward pin-flow — content stuck in cycles that every linearizer has to
	// break or abandon. A high trapped share means the export leans on
	// heuristics; see reach_test.go for the per-node drill-down.
	Trapped int `json:"trapped"`
	// Chapters is the number of chapters emitted by a chaptered import.
	Chapters int `json:"chapters,omitempty"`
}

// pinFlowDiag builds the raw forward pin-flow over emittable nodes (no
// stitching) and marks which of them can reach a leaf. Shared by the
// production health counters and the TestReach drill-down.
func pinFlowDiag(fl flow) (emit []uint32, canEnd map[uint32]bool) {
	pg := fl.pg
	if pg == nil { // synthetic graphs (tests, -start over raw 0x02 flow) have no pin graph
		return nil, map[uint32]bool{}
	}
	succ := map[uint32][]uint32{}
	for n := range pg.class {
		if !pg.isEmittable(n) {
			continue
		}
		emit = append(emit, n)
		var outs []uint32
		seen := map[uint32]bool{}
		for _, p := range pg.outPins(n) {
			for _, t := range pg.reachFromPin(p, nil) {
				if !seen[t] {
					seen[t] = true
					outs = append(outs, t)
				}
			}
		}
		succ[n] = outs
	}
	preds := map[uint32][]uint32{}
	canEnd = map[uint32]bool{}
	var q []uint32
	for _, n := range emit {
		if len(succ[n]) == 0 {
			canEnd[n] = true
			q = append(q, n)
		}
		for _, d := range succ[n] {
			preds[d] = append(preds[d], n)
		}
	}
	for len(q) > 0 {
		x := q[0]
		q = q[1:]
		for _, p := range preds[x] {
			if !canEnd[p] {
				canEnd[p] = true
				q = append(q, p)
			}
		}
	}
	return emit, canEnd
}

// pinFlowHealth reduces pinFlowDiag to the two numbers worth reporting on
// every import: emittable node count and how many of them are trapped.
func pinFlowHealth(fl flow) (emittable, trapped int) {
	emit, canEnd := pinFlowDiag(fl)
	for _, n := range emit {
		if !canEnd[n] {
			trapped++
		}
	}
	return len(emit), trapped
}
