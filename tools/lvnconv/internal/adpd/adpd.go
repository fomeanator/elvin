// Package adpd is the articy:draft binary-project front-end. It reads an
// `ADPD8` Flow partition directly (no JSON export needed), reconstructs the full
// articy object model — including instructions and conditions — and emits it in
// the same JSON shape the `articy` package consumes, so a raw `.adpd` project
// converts through the very same back-end as a JSON export.
//
// Format (see ../../docs/articy-adpd-format.md): the body is a sequence of
// length-prefixed objects. Each object header is
//
//	<bodyLen:uint32> <u16> <u8> <typecode:u8> 00 00 00
//
// where bodyLen counts the bytes after the uint32 up to the next object, and the
// (C, typecode) pair identifies the object kind. An object body is a flat run of
// typed property entries <seq:u16><propid:u16><tag:u8><value>. Objects are flat
// siblings (a fragment's text/speaker/pins/connections are separate objects whose
// parent propid 0x0c points back at it). Connections carry propid 0x02 =
// [src, dst, srcPin, dstPin] — the directed edges.
package adpd

import (
	"encoding/binary"
	"fmt"
)

// property ids (field names are not stored in the binary)
const (
	pConn    = 0x02  // connection: 4 refs [src, dst, srcPin, dstPin]
	pInstr   = 0x03  // pin leave-instruction (X = N;)
	pParent  = 0x0c  // parent/owner ordinal
	pSelf    = 0x39  // self ordinal (edge id of a flow node)
	pCond    = 0x79  // pin enter-condition ({ns}.{var} == ...;)
	pID      = 0x3a  // object GUID id (stable localization key)
	pColor   = 0x01  // DialogFragment marker BackgroundColor (tag 0xee = packed RGBA)
	pCaption = 0x100 // speaker caption (string) / colour (non-string)
	pText    = 0x200 // line text (HTML)
)

// articyDefaultColor is articy:draft's default DialogFragment marker colour (a
// light blue). A fragment left at the default carries no deliberate emotion
// marker, so the decoder treats it as "no colour" and omits it.
const articyDefaultColor = "#c8e2e7"

// colorHex renders articy's packed RGBA colour word — tag 0xee, bytes R,G,B,A in
// ascending memory, so the entries() reader's little-endian u32 is A<<24|B<<16|
// G<<8|R — as "#rrggbb" (alpha dropped).
func colorHex(u uint32) string {
	return fmt.Sprintf("#%02x%02x%02x", byte(u), byte(u>>8), byte(u>>16))
}

// object kinds, by the header's (C, typecode) pair (legacy heuristic; the Global
// Variables / Entities partitions are still classified this way).
type kind struct{ c, t byte }

var (
	kFragment   = kind{1, 4}  // DialogueFragment content (has Text)
	kSpeaker    = kind{2, 6}  // speaker reference (has caption)
	kLogic      = kind{1, 17} // Instruction / Condition node
	kConnection = kind{3, 11} // a flow edge (has 0x02)
)

// Flow-partition class ids — the authoritative type discriminator (uint16 at
// model offset+4), recovered from the editor's [ClassId(N)] attributes via
// decompilation. The model header is <int32 size><uint16 classId><byte version>
// <int32 numProps>; see lvn-adpd-format-truth. (Earlier code keyed on
// (version, numProps&0xFF), which only clustered by accident.)
const (
	cidMLText      uint16 = 24  // ArticyMultiLanguageText — a line's text
	cidModelDep    uint16 = 9   // ModelDependency — a reference (speaker)
	cidConnection  uint16 = 4   // Connection — a flow edge [src,dst,srcPin,dstPin]
	cidPin         uint16 = 10  // input/output Pin
	cidDialog      uint16 = 74  // Dialog — a scene container
	cidFlowFrag    uint16 = 76  // FlowFragment — a chapter container
	cidCondition   uint16 = 162 // Condition — an if split
	cidOutcome     uint16 = 163 // Outcome — a pin script (set/inc)
	cidDialogFrag  uint16 = 75  // DialogFragment — a dialogue node
	cidStoryFolder uint16 = 80  // the project root
	cidHub         uint16 = 77  // Hub (absent in the test projects)
	cidJump        uint16 = 78  // Jump (absent in the test projects)
)

type prop struct {
	tag byte
	s   string
	u   uint32
}

type entry struct {
	propid uint16
	prop
}

// header returns (bodyLen, classId, C, typecode) at o, or ok=false. The model
// frame is <int32 size><uint16 classId><byte version><int32 numProps>; C/typecode
// are the legacy (version, numProps&0xFF) heuristic kept for the other partitions.
func header(d []byte, o, idx int) (uint32, uint16, byte, byte, bool) {
	if o+11 > idx || d[o+8] != 0 || d[o+9] != 0 || d[o+10] != 0 {
		return 0, 0, 0, 0, false
	}
	bl := binary.LittleEndian.Uint32(d[o:])
	if bl <= 4 || int(bl) >= idx || o+4+int(bl) > idx {
		return 0, 0, 0, 0, false
	}
	return bl, binary.LittleEndian.Uint16(d[o+4:]), d[o+6], d[o+7], true
}

// entries parses [a,b) as a flat run of property entries.
func entries(d []byte, a, b int) []entry {
	var out []entry
	o := a
	for o < b && o+5 <= b {
		seq := binary.LittleEndian.Uint16(d[o:])
		pid := binary.LittleEndian.Uint16(d[o+2:])
		tag := d[o+4]
		v := o + 5
		ok := false
		switch {
		case tag == 0x12 && pid < 0x400 && seq < 0x600 && v+4 <= b:
			ln := int(binary.LittleEndian.Uint32(d[v:]))
			if ln >= 0 && ln < 200000 && v+4+ln <= b {
				out = append(out, entry{pid, prop{tag: tag, s: string(d[v+4 : v+4+ln])}})
				o = v + 4 + ln
				ok = true
			}
		case (tag == 0xf6 || tag == 0xf7) && pid < 0x400 && seq < 0x600 && v+8 <= b:
			out = append(out, entry{pid, prop{tag: tag}})
			o = v + 8
			ok = true
		case (tag == 0xfa || tag == 0xfb || tag == 0xfc || tag == 0xfd || tag == 0xfe || tag == 0xee || tag == 0xef) &&
			pid < 0x400 && seq < 0x600 && v+4 <= b:
			out = append(out, entry{pid, prop{tag: tag, u: binary.LittleEndian.Uint32(d[v:])}})
			o = v + 4
			ok = true
		}
		if !ok {
			o++
		}
	}
	return out
}

// findStart returns the offset whose length-prefixed object chain runs longest
// (the real object stream begins after a short partition preamble).
func findStart(d []byte, idx int) int {
	best, bestLen := -1, 0
	for o := 24; o < 3000; o++ {
		if _, _, _, _, ok := header(d, o, idx); !ok {
			continue
		}
		p, n := o, 0
		for p < idx {
			bl, _, _, _, ok := header(d, p, idx)
			if !ok {
				break
			}
			p += 4 + int(bl)
			n++
		}
		if n > bestLen {
			best, bestLen = o, n
		}
	}
	return best
}

type object struct {
	classId uint16
	c, t    byte
	es      []entry
}

func (o object) u32(pid uint16) (uint32, bool) {
	for _, e := range o.es {
		if e.propid == pid && (e.tag == 0xfe || e.tag == 0xfa || e.tag == 0xfb || e.tag == 0xfc || e.tag == 0xfd) {
			return e.u, true
		}
	}
	return 0, false
}

func (o object) str(pid uint16) string {
	for _, e := range o.es {
		if e.propid == pid && e.tag == 0x12 {
			return e.s
		}
	}
	return ""
}

// color returns the packed RGBA colour word of the tag-0xee property pid, if any.
func (o object) color(pid uint16) (uint32, bool) {
	for _, e := range o.es {
		if e.propid == pid && e.tag == 0xee {
			return e.u, true
		}
	}
	return 0, false
}

func (o object) refs(pid uint16) []uint32 {
	var r []uint32
	for _, e := range o.es {
		if e.propid == pid && e.tag == 0xfe {
			r = append(r, e.u)
		}
	}
	return r
}

// walkObjects returns every length-prefixed object in the body.
func walkObjects(d []byte, idx int) []object {
	start := findStart(d, idx)
	if start < 0 {
		return nil
	}
	var objs []object
	for o := start; o < idx; {
		bl, classId, c, t, ok := header(d, o, idx)
		if !ok {
			break
		}
		objs = append(objs, object{classId: classId, c: c, t: t, es: entries(d, o+11, o+4+int(bl))})
		o += 4 + int(bl)
	}
	return objs
}
