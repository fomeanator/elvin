package importer

import (
	"sort"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
)

// defaultEmotionColors maps a dialogue fragment's articy marker colour (#rrggbb)
// to an emotion token. articy authors tint fragments to cue the speaker's mood;
// this is the "Советское воспитание"/Cold legend. Overridable/extendable per
// import via Options.EmotionColors.
var defaultEmotionColors = map[string]string{
	"ffff00": "surprised",
	"00b050": "happy",
	"d6006e": "flirt",
	"0c0c0c": "fear",
	"7030a0": "sad",
	"0070c0": "thoughtfulness",
}

// mergeColors overlays colour maps left-to-right (later wins), so a caller can
// layer Options.EmotionColors over the template's legend. Nil-safe; returns nil
// when both are empty (newColorProbe then uses just the built-in default legend).
func mergeColors(maps ...map[string]string) map[string]string {
	var out map[string]string
	for _, m := range maps {
		for k, v := range m {
			if out == nil {
				out = map[string]string{}
			}
			out[k] = v
		}
	}
	return out
}

// normHex folds a colour to a lookup key: lowercased, no leading '#', and an
// optional leading "ff" alpha stripped — so "#FF00B050", "00B050" and "#00b050"
// all collide on "00b050".
func normHex(s string) string {
	s = strings.ToLower(strings.TrimPrefix(strings.TrimSpace(s), "#"))
	if len(s) == 8 && strings.HasPrefix(s, "ff") {
		s = s[2:]
	}
	return s
}

// emotionTable resolves the colour→emotion map for an import: the default legend
// with Options.EmotionColors merged over it (a caller entry extends the table, or
// overrides a legend colour). Keys are normalised so callers may pass "#FF00B050".
func emotionTable(overrides map[string]string) map[string]string {
	t := make(map[string]string, len(defaultEmotionColors)+len(overrides))
	for k, v := range defaultEmotionColors {
		t[k] = v
	}
	for k, v := range overrides {
		t[normHex(k)] = v
	}
	return t
}

// colorProbe threads fragment marker colours to emotions across one or more
// chapters. scan() stamps each say's resolved `emotion` (which AutoStage then
// copies onto the actor) and removes the transient `color`; stats() reports the
// distinct colours seen — with counts, the resolved emotion and one sample line —
// for the import Result's colour probe (so a UI can show/edit the mapping).
type colorProbe struct {
	table map[string]string
	seen  map[string]*colorAcc
	order []string
}

type colorAcc struct {
	count   int
	emotion string
	sample  string
}

func newColorProbe(overrides map[string]string) *colorProbe {
	return &colorProbe{table: emotionTable(overrides), seen: map[string]*colorAcc{}}
}

func (p *colorProbe) scan(doc *articy.Doc) {
	for _, c := range doc.Script {
		if c["op"] != "say" {
			continue
		}
		col, _ := c["color"].(string)
		if col == "" {
			continue
		}
		delete(c, "color") // transient transport field, consumed here
		key := normHex(col)
		emo := p.table[key]
		if emo != "" {
			c["emotion"] = emo // AutoStage copies this onto the actor …show=true cmd
		}
		a, ok := p.seen[key]
		if !ok {
			who, _ := c["who"].(string)
			text, _ := c["text"].(string)
			sample := text
			if who != "" {
				sample = who + ": " + text
			}
			a = &colorAcc{emotion: emo, sample: truncRunes(strings.TrimSpace(sample), 60)}
			p.seen[key] = a
			p.order = append(p.order, key)
		}
		a.count++
	}
}

// stats returns the probe, most-frequent colour first (ties broken by hex).
func (p *colorProbe) stats() []ColorStat {
	order := append([]string(nil), p.order...)
	sort.SliceStable(order, func(i, j int) bool {
		if p.seen[order[i]].count != p.seen[order[j]].count {
			return p.seen[order[i]].count > p.seen[order[j]].count
		}
		return order[i] < order[j]
	})
	out := make([]ColorStat, 0, len(order))
	for _, k := range order {
		a := p.seen[k]
		out = append(out, ColorStat{Hex: "#" + k, Count: a.count, Emotion: a.emotion, Sample: a.sample})
	}
	return out
}

// truncRunes caps s at n runes on a rune boundary (Cyrillic-safe), appending "…".
func truncRunes(s string, n int) string {
	r := []rune(s)
	if len(r) <= n {
		return s
	}
	return string(r[:n]) + "…"
}
