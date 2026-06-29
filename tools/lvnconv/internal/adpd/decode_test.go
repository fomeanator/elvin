package adpd

import (
	"encoding/binary"
	"os"
	"path/filepath"
	"testing"

	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/articy"
)

// obj frames a flat run of property entries as a length-prefixed model:
//
//	<size:u32> <classId:u16> <version> <numProps-low> 00 00 00 <entries…>
//
// where size counts the bytes after the uint32 up to the next object and classId
// is the type discriminator decodeFlow keys on.
func obj(classId uint16, entries []byte) []byte {
	bl := uint32(7 + len(entries))
	out := binary.LittleEndian.AppendUint32(nil, bl)
	out = binary.LittleEndian.AppendUint16(out, classId)
	out = append(out, 1, 0, 0, 0, 0) // version, numProps-low, 0,0,0
	return append(out, entries...)
}

// partition wraps objects in an ADPD8 container: "ADPD8" magic, the tail-index
// offset (set to the end of the body, so there is no tail), two counters, then
// the object stream beginning at offset 24.
func partition(objs ...[]byte) []byte {
	d := []byte("ADPD8\x00\x00\x00")
	d = append(d, make([]byte, 16)...) // idx_off(8) + counter(4) + counter(4)
	for _, o := range objs {
		d = append(d, o...)
	}
	binary.LittleEndian.PutUint64(d[8:], uint64(len(d)))
	return d
}

// frag builds a DialogueFragment content object at ordinal par.
func frag(par uint32, guid, text string) []byte {
	var e []byte
	e = putU32(e, 1, pParent, par)
	e = putStr(e, 1, pText, text)
	e = putStr(e, 1, pID, guid)
	return obj(cidMLText, e)
}

// speaker builds a speaker-reference object pointing at fragment ordinal par.
func speaker(par uint32, caption string) []byte {
	var e []byte
	e = putU32(e, 1, pParent, par)
	e = putStr(e, 1, pCaption, caption)
	return obj(cidModelDep, e)
}

// instr builds an Instruction logic node at ordinal self.
func instr(self uint32, expr string) []byte {
	var e []byte
	e = putU32(e, 1, pSelf, self)
	e = putStr(e, 1, pInstr, expr)
	return obj(cidOutcome, e)
}

// conn builds a Connection edge object src→dst (via source pin srcPin).
func conn(src, dst, srcPin uint32) []byte {
	var e []byte
	e = putU32(e, 1, pConn, src)
	e = putU32(e, 1, pConn, dst)
	e = putU32(e, 1, pConn, srcPin)
	e = putU32(e, 1, pConn, 0) // dstPin
	return obj(cidConnection, e)
}

// A flat object stream decodes back into text, speakers, logic and edges.
func TestDecodeFlowRecoversGraph(t *testing.T) {
	d := partition(
		frag(1, "g-1", "Вопрос?"),
		speaker(1, "Тимур"),
		frag(2, "g-2", "Вариант А"),
		frag(3, "g-3", "Вариант Б"),
		instr(4, "Music.Native = true"),
		conn(1, 2, 0),
		conn(1, 3, 0),
		conn(2, 4, 0),
	)

	fl := decodeFlow(d)
	if fl.text[1] != "Вопрос?" || fl.text[2] != "Вариант А" || fl.text[3] != "Вариант Б" {
		t.Fatalf("text not recovered: %v", fl.text)
	}
	if fl.guid[1] != "g-1" {
		t.Errorf("guid not recovered: %v", fl.guid)
	}
	if fl.sp[1] != "Тимур" {
		t.Errorf("speaker not recovered: %v", fl.sp)
	}
	if ln, ok := fl.logic[4]; !ok || ln.cond || ln.expr != "Music.Native = true" {
		t.Errorf("instruction not recovered: %+v", fl.logic[4])
	}
	if len(fl.succ[1]) != 2 {
		t.Errorf("node 1 should fan out to 2 edges, got %d", len(fl.succ[1]))
	}
}

// The whole binary→model→.lvn pipeline yields a real branching chapter: the
// fan-out becomes a choice, the instruction a set, every fragment a say.
func TestBuildExportEndToEnd(t *testing.T) {
	dir := t.TempDir()
	data := partition(
		frag(1, "g-1", "Вопрос?"),
		speaker(1, "Тимур"),
		frag(2, "g-2", "Вариант А"),
		frag(3, "g-3", "Вариант Б"),
		instr(4, "Music.Native = true"),
		conn(1, 2, 0),
		conn(1, 3, 0),
		conn(2, 4, 0),
	)
	// findPartition matches a filename containing "Flow" and ending ".adpd".
	if err := os.WriteFile(filepath.Join(dir, "'Flow'-TypedPartition(x).adpd"), data, 0o644); err != nil {
		t.Fatal(err)
	}

	js, err := BuildExportJSON(dir, -1, 0)
	if err != nil {
		t.Fatalf("BuildExportJSON: %v", err)
	}
	doc, err := articy.Convert(js, "")
	if err != nil {
		t.Fatalf("convert: %v", err)
	}

	says, choices, sets := 0, 0, 0
	for _, c := range doc.Script {
		switch c["op"] {
		case "say":
			says++
		case "choice":
			choices++
		case "set":
			sets++
		}
	}
	if choices != 1 {
		t.Errorf("want 1 choice, got %d", choices)
	}
	if sets != 1 {
		t.Errorf("want 1 set, got %d", sets)
	}
	if says < 3 {
		t.Errorf("want at least 3 say lines, got %d", says)
	}
}
