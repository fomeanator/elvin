package importer

import (
	"bytes"
	"image"
	"image/color"
	"image/draw"
	"image/png"
	"strings"

	"golang.org/x/image/font"
	"golang.org/x/image/font/gofont/goregular"
	"golang.org/x/image/font/opentype"
	"golang.org/x/image/math/fixed"
)

// Placeholder colours — a mid-grey field with a dark-grey diagonal cross and
// label, the "no art yet" dummy (like Unity's grey primitives) so staging is
// visible before any real art exists.
var (
	phFill  = color.RGBA{0x80, 0x80, 0x80, 0xff}
	phMark  = color.RGBA{0x44, 0x44, 0x46, 0xff}
	phLabel = color.RGBA{0x2a, 0x2a, 0x2c, 0xff}
)

var phFace = mustFace()

func mustFace() *opentype.Font {
	f, err := opentype.Parse(goregular.TTF)
	if err != nil {
		panic(err)
	}
	return f
}

// Placeholder renders a w×h PNG dummy for a missing asset: a grey field, a
// dark-grey diagonal cross, and the combination name centred on top — so a scene
// shows exactly what is staged (which character, which pose, which background) and
// how it moves, with no real art. label is shown verbatim (Cyrillic supported).
func Placeholder(label string, w, h int) []byte {
	if w <= 0 {
		w = 512
	}
	if h <= 0 {
		h = 512
	}
	img := image.NewRGBA(image.Rect(0, 0, w, h))
	draw.Draw(img, img.Bounds(), &image.Uniform{phFill}, image.Point{}, draw.Src)

	// A thick diagonal cross (corner to corner, both ways).
	thick := 1 + (w+h)/240
	drawDiag(img, w, h, thick, true, phMark)
	drawDiag(img, w, h, thick, false, phMark)
	// A 1px frame so adjacent dummies read as separate boxes.
	drawFrame(img, w, h, phMark)

	drawLabel(img, label, w, h)

	var buf bytes.Buffer
	_ = png.Encode(&buf, img)
	return buf.Bytes()
}

func drawDiag(img *image.RGBA, w, h, thick int, down bool, c color.RGBA) {
	for x := 0; x < w; x++ {
		yc := x * h / w
		if !down {
			yc = h - 1 - yc
		}
		for dy := -thick; dy <= thick; dy++ {
			y := yc + dy
			if y >= 0 && y < h {
				img.SetRGBA(x, y, c)
			}
		}
	}
}

func drawFrame(img *image.RGBA, w, h int, c color.RGBA) {
	for x := 0; x < w; x++ {
		img.SetRGBA(x, 0, c)
		img.SetRGBA(x, h-1, c)
	}
	for y := 0; y < h; y++ {
		img.SetRGBA(0, y, c)
		img.SetRGBA(w-1, y, c)
	}
}

// drawLabel word-wraps the label to fit and draws it centred, on a faint plate so
// it stays readable over the cross.
func drawLabel(img *image.RGBA, label string, w, h int) {
	label = strings.TrimSpace(strings.NewReplacer("_", " ", "\n", " ").Replace(label))
	if label == "" {
		return
	}
	size := float64(w) / 11
	if size < 12 {
		size = 12
	}
	face, err := opentype.NewFace(phFace, &opentype.FaceOptions{Size: size, DPI: 72, Hinting: font.HintingFull})
	if err != nil {
		return
	}
	defer face.Close()

	lines := wrapText(label, face, int(float64(w)*0.88))
	lineH := face.Metrics().Height.Ceil()
	total := lineH * len(lines)
	y := (h-total)/2 + face.Metrics().Ascent.Ceil()

	// faint backing plate behind the text block for contrast
	plate := color.RGBA{0x80, 0x80, 0x80, 0xff}
	py0 := (h-total)/2 - lineH/4
	py1 := py0 + total + lineH/2
	for yy := py0; yy < py1; yy++ {
		if yy < 0 || yy >= h {
			continue
		}
		for xx := w / 12; xx < w-w/12; xx++ {
			img.SetRGBA(xx, yy, plate)
		}
	}

	d := &font.Drawer{Dst: img, Src: image.NewUniform(phLabel), Face: face}
	for _, line := range lines {
		lw := font.MeasureString(face, line).Ceil()
		d.Dot = fixed.P((w-lw)/2, y)
		d.DrawString(line)
		y += lineH
	}
}

func wrapText(s string, face font.Face, maxW int) []string {
	words := strings.Fields(s)
	if len(words) == 0 {
		return []string{s}
	}
	var lines []string
	cur := words[0]
	for _, wd := range words[1:] {
		if font.MeasureString(face, cur+" "+wd).Ceil() <= maxW {
			cur += " " + wd
		} else {
			lines = append(lines, cur)
			cur = wd
		}
	}
	return append(lines, cur)
}
