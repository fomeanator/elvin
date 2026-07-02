package adpd

import (
	"fmt"
	"os"
	"testing"
)

// TestReach measures the pure forward pin-flow graph (no stitching): forward reach
// from the biggest region head + can-reach-a-leaf (END) — to separate pin-flow
// cyclicity from stitching artifacts.
//
//	ADPD_DIAG=<dir> go test ./internal/adpd -run TestReach -v
func TestReach(t *testing.T) {
	path := os.Getenv("ADPD_DIAG")
	if path == "" {
		t.Skip("set ADPD_DIAG")
	}
	fl, _, err := loadFlow(path)
	if err != nil {
		t.Fatal(err)
	}
	pg := fl.pg
	// The counters live in the production path now (pinFlowDiag/pinFlowHealth,
	// surfaced by every import as LinearizeReport); this test is the per-node
	// drill-down on top of them.
	emit, canEnd := pinFlowDiag(fl)
	ce := 0
	for _, n := range emit {
		if canEnd[n] {
			ce++
		}
	}
	fmt.Printf("PIN-FLOW (no stitch): emittable=%d can-END=%.1f%%\n",
		len(emit), 100*float64(ce)/float64(len(emit)))

	trapped := len(emit) - ce
	fmt.Printf("  trapped (cannot reach any leaf): %d\n", trapped)
	// sample a few trapped nodes with text
	shown := 0
	for _, n := range emit {
		if !canEnd[n] && fl.text[n] != "" {
			txt := fl.text[n]
			if len(txt) > 50 {
				txt = txt[:50]
			}
			fmt.Printf("    trapped [%d c=%d] %s: %s\n", n, pg.class[n], fl.sp[n], txt)
			shown++
			if shown >= 6 {
				break
			}
		}
	}
}
