package adpd

import (
	"strings"
	"testing"
)

// An explicit -start pins the algorithm; no fallbacks are recorded.
func TestReportStartNode(t *testing.T) {
	fl := newFlow()
	fl.text[1] = "hello"
	fl.text[2] = "bye"
	fl.link(1, 2, 0)
	_, rep := buildModel(fl, "", 1, 0)
	if rep.Algorithm != "start" {
		t.Fatalf("algorithm = %q, want start", rep.Algorithm)
	}
	if len(rep.Fallbacks) != 0 {
		t.Fatalf("fallbacks = %v, want none", rep.Fallbacks)
	}
}

// A synthetic graph (no container hierarchy, no pin graph) walks the whole
// cascade down to structural fan-outs — and the report must say so, naming
// every stage that fell through. This is the transparency contract: a coarse
// linearizer is allowed, a silent one is not.
func TestReportCascadeFallsThroughLoudly(t *testing.T) {
	fl := newFlow()
	fl.text[1] = "a"
	fl.text[2] = "b"
	fl.link(1, 2, 0)
	// An empty pin graph: real projects always carry one; the whole-novel
	// cascade dereferences it (wholeNovelTops).
	fl.pg = &pinGraph{class: map[uint32]uint16{}, pinOf: map[uint32]uint32{},
		pins: map[uint32][]uint32{}, outPin: map[uint32][]pinEdge{}, hasOut: map[uint32]bool{}}
	_, rep := buildModel(fl, "", -1, 0)
	if rep.Algorithm == "" {
		t.Fatal("algorithm not recorded")
	}
	if rep.Algorithm == "anchored" || rep.Algorithm == "faithful" {
		t.Fatalf("synthetic graph cannot linearize as %q", rep.Algorithm)
	}
	joined := strings.Join(rep.Fallbacks, "\n")
	if !strings.Contains(joined, "anchored:") || !strings.Contains(joined, "faithful:") {
		t.Fatalf("fallback reasons missing the skipped stages:\n%s", joined)
	}
}
