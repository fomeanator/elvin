package importer

import (
	"strings"
	"testing"

	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/articy"
	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/lvns"
)

func sayTexts(s []lvns.Cmd) []string {
	var out []string
	for _, c := range s {
		if c["op"] == "say" {
			out = append(out, str(c["text"]))
		}
	}
	return out
}

func TestDecompileRoundTrip(t *testing.T) {
	doc := &articy.Doc{Scene: "t", Script: []articy.Cmd{
		{"op": "label", "id": "start"}, // unreferenced → should be dropped
		{"op": "say", "who": "Mara", "text": "Hello there"},
		{"op": "say", "text": "A quiet room."},
		{"op": "set", "key": "gold", "value": float64(5)},
		{"op": "choice", "options": []any{
			map[string]any{"text": "Buy", "goto": "buy", "cost": map[string]any{"var": "gold", "amount": float64(5)}},
			map[string]any{"text": "Leave", "goto": "leave"},
		}},
		{"op": "label", "id": "buy"},
		{"op": "say", "text": "Bought."},
		{"op": "goto", "label": "leave"}, // adjacent to :leave → should be dropped
		{"op": "label", "id": "leave"},
		{"op": "say", "text": "Bye."},
	}}

	src := string(ToLvns(doc))
	back, err := lvns.Convert(src)
	if err != nil {
		t.Fatalf("recompile failed: %v\n---\n%s", err, src)
	}

	// Fidelity: the spoken lines survive the round-trip, in order.
	want := []string{"Hello there", "A quiet room.", "Bought.", "Bye."}
	got := sayTexts(back.Script)
	if strings.Join(got, "|") != strings.Join(want, "|") {
		t.Errorf("say texts = %v, want %v\n---\n%s", got, want, src)
	}

	// Paid choice: the option survives with its target and its cost is emitted in
	// the `var:amount` shorthand (which the choice-cost front-end turns into a
	// structured cost; on its own it round-trips as a display string).
	if !strings.Contains(src, "cost=gold:5") {
		t.Errorf("paid choice cost not emitted as gold:5\n---\n%s", src)
	}
	var found bool
	for _, c := range back.Script {
		if c["op"] != "choice" {
			continue
		}
		for _, o := range asList(c["options"]) {
			opt := o.(map[string]any)
			if str(opt["text"]) == "Buy" && str(opt["goto"]) == "buy" {
				found = true
			}
		}
	}
	if !found {
		t.Errorf("Buy option lost\n---\n%s", src)
	}

	// Goto reduction: the unreferenced label and the adjacent goto are gone.
	if strings.Contains(src, ":start") {
		t.Errorf("unreferenced label :start should be dropped\n---\n%s", src)
	}
	if strings.Count(src, "-> leave") != 1 { // only the choice option, not the dropped goto
		t.Errorf("redundant goto leave not reduced\n---\n%s", src)
	}
}
