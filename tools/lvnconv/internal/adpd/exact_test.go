package adpd

import (
	"encoding/binary"
	"os"
	"sort"
	"testing"
)

// Exact .adpd reader per the decompiled BinarySnapshotReader (articy:draft X 4.3).
// Header(16): u32 "ADPD", i32 version, i64 offset→tail(IdPool,StringPool); models
// from byte 20, count(i32) at byte 16. Model: i32 size(=7+props), u16 classId,
// byte version, i32 numProps, props. This validates the true framing and recovers
// the real classId histogram (the type discriminator the old decoder ignored).
type exr struct {
	d   []byte
	pos int
}

func (r *exr) u32() uint32 { v := binary.LittleEndian.Uint32(r.d[r.pos:]); r.pos += 4; return v }
func (r *exr) i32() int32  { return int32(r.u32()) }
func (r *exr) u16() uint16 { v := binary.LittleEndian.Uint16(r.d[r.pos:]); r.pos += 2; return v }
func (r *exr) i64() int64 {
	v := int64(binary.LittleEndian.Uint64(r.d[r.pos:]))
	r.pos += 8
	return v
}

func TestExactFraming(t *testing.T) {
	path := os.Getenv("SOVIET_FLOW")
	if path == "" {
		t.Skip("set SOVIET_FLOW to a Flow .adpd partition")
	}
	d, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	r := &exr{d: d}
	// header
	fourcc := r.u32()
	want := uint32('A') | uint32('D')<<8 | uint32('P')<<16 | uint32('D')<<24
	if fourcc != want {
		t.Fatalf("fourcc=%08x want %08x", fourcc, want)
	}
	version := r.i32()
	offset := r.i64() // relative to current pos (16)
	tail := 16 + int(offset)
	t.Logf("FileVersion=%d tailOffset=%d (abs %d) fileLen=%d", version, offset, tail, len(d))

	// pools at tail
	pr := &exr{d: d, pos: tail}
	idPoolBytes := pr.u32()
	idStart := pr.pos
	nIds := pr.i32()
	t.Logf("IdPool: byteSize=%d count=%d", idPoolBytes, nIds)
	pr.pos = idStart + int(idPoolBytes)
	strPoolBytes := pr.u32()
	strStart := pr.pos
	nStr := pr.i32()
	t.Logf("StringPool: byteSize=%d count=%d", strPoolBytes, nStr)
	_ = strStart

	// models
	r.pos = 16
	modelCount := r.i32() // at byte 16
	r.pos = 20
	t.Logf("modelCount=%d (models start @20)", modelCount)

	hist := map[uint16]int{}   // classId → count
	verOf := map[uint16]byte{} // classId → a version seen
	npOf := map[uint16][]int{} // classId → numProps samples
	read := 0
	for read < int(modelCount) && r.pos+8 <= tail {
		start := r.pos
		size := int(r.i32())
		classId := r.u16()
		ver := r.d[r.pos]
		r.pos++
		nProps := int(r.i32())
		hist[classId]++
		verOf[classId] = ver
		if len(npOf[classId]) < 1 {
			npOf[classId] = append(npOf[classId], nProps)
		}
		// skip property bytes using size: next model at start+4+size
		r.pos = start + 4 + size
		read++
	}
	t.Logf("models read=%d (chain ended @%d, tail @%d)", read, r.pos, tail)

	// classId histogram (top kinds)
	type kv struct {
		id uint16
		n  int
	}
	var rows []kv
	for id, n := range hist {
		rows = append(rows, kv{id, n})
	}
	sort.Slice(rows, func(i, j int) bool { return rows[i].n > rows[j].n })
	t.Log("=== classId histogram (top) ===")
	for i, row := range rows {
		if i > 20 {
			break
		}
		t.Logf("  classId=%-5d n=%-6d ver=%d numProps~%v", row.id, row.n, verOf[row.id], npOf[row.id])
	}
	t.Logf("distinct classIds=%d", len(hist))
}
