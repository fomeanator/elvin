package adpd

import (
	"encoding/binary"
	"strings"
	"testing"
	"unicode/utf8"

	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/articy"
)

func TestTruncateRunesKeepsCyrillicIntact(t *testing.T) {
	// 90 Cyrillic runes (180 bytes). A byte-slice at 80 would land mid-character
	// and produce invalid UTF-8; truncateRunes must cut on a rune boundary.
	s := strings.Repeat("я", 90)
	got := truncateRunes(s, 80)
	if !utf8.ValidString(got) {
		t.Fatalf("truncated to invalid UTF-8: %q", got)
	}
	if r := []rune(got); len(r) != 81 || string(r[:80]) != strings.Repeat("я", 80) || r[80] != '…' {
		t.Fatalf("want 80 runes + ellipsis, got %d runes: %q", len(r), got)
	}
	// Short strings pass through untouched (no ellipsis).
	if truncateRunes("привет", 80) != "привет" {
		t.Fatal("short string must be returned unchanged")
	}
}

func TestStripHTML(t *testing.T) {
	in := `<html><head><style>#s0 {x}</style></head><body><p id="s0"><span id="s1">Привет &amp; пока</span></p></body></html>`
	if got := stripHTML(in); got != "Привет & пока" {
		t.Fatalf("stripHTML = %q", got)
	}
	if got := stripHTML("  plain  "); got != "plain" {
		t.Fatalf("plain = %q", got)
	}
}

func TestResolveExpr(t *testing.T) {
	in := `{72057594037928614:Wardrobe}.{72057594037931102:Free} == true;`
	if got := resolveExpr(in, nil); got != "Wardrobe.Free == true" {
		t.Fatalf("resolveExpr = %q", got)
	}
	// a name-less {guid} reference resolves via the variable map
	m := map[string]string{"99": "Counter"}
	if got := resolveExpr(`{1:Way}.{2:x} = {99} + 1`, m); got != "Way.x = Counter + 1" {
		t.Fatalf("name-less resolve = %q", got)
	}
	if !isCondition("Wardrobe.Free == true") {
		t.Fatal("== should be a condition")
	}
	if isCondition("Music.Native = true") {
		t.Fatal("plain assignment is not a condition")
	}
}

func putStr(b []byte, seq, pid uint16, s string) []byte {
	b = binary.LittleEndian.AppendUint16(b, seq)
	b = binary.LittleEndian.AppendUint16(b, pid)
	b = append(b, 0x12)
	b = binary.LittleEndian.AppendUint32(b, uint32(len(s)))
	return append(b, s...)
}
func putU32(b []byte, seq, pid uint16, v uint32) []byte {
	b = binary.LittleEndian.AppendUint16(b, seq)
	b = binary.LittleEndian.AppendUint16(b, pid)
	b = append(b, 0xfe)
	return binary.LittleEndian.AppendUint32(b, v)
}

func TestEntries(t *testing.T) {
	var b []byte
	b = putStr(b, 1, pText, "hi")
	b = putU32(b, 1, pConn, 42)
	got := entries(b, 0, len(b))
	if len(got) != 2 || got[0].s != "hi" || got[1].u != 42 {
		t.Fatalf("entries = %+v", got)
	}
}

// newFlow is a tiny builder for buildExport tests.
func newFlow() flow {
	return flow{text: map[uint32]string{}, guid: map[uint32]string{}, sp: map[uint32]string{},
		logic: map[uint32]logicNode{}, succ: map[uint32][]edge{}, nodes: map[uint32]bool{}}
}
func (f flow) link(s, d, srcPin uint32) {
	f.succ[s] = append(f.succ[s], edge{src: s, dst: d, srcPin: srcPin})
	f.nodes[s] = true
	f.nodes[d] = true
}

func convertModels(t *testing.T, fl flow, start uint32) []articy.Cmd {
	js, err := marshalExport(buildExport(fl, start, 100, nil))
	if err != nil {
		t.Fatal(err)
	}
	doc, err := articy.Convert(js, "chapter")
	if err != nil {
		t.Fatalf("convert: %v", err)
	}
	return doc.Script
}

func count(script []articy.Cmd, op string) int {
	n := 0
	for _, c := range script {
		if c["op"] == op {
			n++
		}
	}
	return n
}

// A node fanning out to two fragments converts into a `choice`.
func TestChoiceConverts(t *testing.T) {
	fl := newFlow()
	fl.text[1] = "Question?"
	fl.text[2] = "Option A"
	fl.text[3] = "Option B"
	fl.link(1, 2, 0)
	fl.link(1, 3, 0)
	if got := count(convertModels(t, fl, 1), "choice"); got != 1 {
		t.Fatalf("expected 1 choice, got %d", got)
	}
}

// An Instruction node converts into a `set`.
func TestInstructionConverts(t *testing.T) {
	fl := newFlow()
	fl.text[1] = "start"
	fl.logic[2] = logicNode{cond: false, expr: "Music.Native = true"}
	fl.text[3] = "end"
	fl.link(1, 2, 0)
	fl.link(2, 3, 0)
	script := convertModels(t, fl, 1)
	if got := count(script, "set"); got != 1 {
		t.Fatalf("expected 1 set, got %d", got)
	}
}

// A Condition node converts into an `if`.
func TestConditionConverts(t *testing.T) {
	fl := newFlow()
	fl.text[1] = "start"
	fl.logic[2] = logicNode{cond: true, expr: "Flags.X == true"}
	fl.text[3] = "true branch"
	fl.text[4] = "false branch"
	fl.link(1, 2, 0)
	fl.link(2, 3, 10) // true pin
	fl.link(2, 4, 20) // false pin
	if got := count(convertModels(t, fl, 1), "if"); got != 1 {
		t.Fatalf("expected 1 if, got %d", got)
	}
}

// Every fragment carries its real text plus a StableId (its GUID) so the back-end
// can stamp the say with a reimport-stable key; the importer's localization pass
// keys its catalog off it. The text itself stays inline (staging needs it).
func TestStableIdOnFragment(t *testing.T) {
	fl := newFlow()
	fl.text[1], fl.guid[1] = "Привет", "g-1"
	fl.text[2], fl.guid[2] = "Пока", "g-2"
	fl.link(1, 2, 0)

	ex := buildExport(fl, 1, 100, nil)
	keys := map[string]string{}
	for _, m := range ex.Packages[0].Models {
		if m.Type != "DialogueFragment" {
			continue
		}
		tx, _ := m.Properties["Text"].(string)
		sid, _ := m.Properties["StableId"].(string)
		keys[tx] = sid
	}
	if keys["Привет"] != "g-1" || keys["Пока"] != "g-2" {
		t.Fatalf("StableId not stamped from the GUID: %v", keys)
	}
	// and it still converts cleanly, the say carrying the id through
	js, _ := marshalExport(ex)
	doc, err := articy.Convert(js, "chapter")
	if err != nil {
		t.Fatalf("convert: %v", err)
	}
	for _, c := range doc.Script {
		if c["op"] == "say" && c["id"] == nil {
			t.Errorf("say lost its stable id: %v", c)
		}
	}
}

func TestNodeID(t *testing.T) {
	if id := nodeID(7); !strings.HasPrefix(id, "node-00000007-") {
		t.Fatalf("nodeID = %q", id)
	}
}
