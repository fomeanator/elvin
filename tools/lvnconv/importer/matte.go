package importer

import (
	"bytes"
	"image"
	"image/color"
	_ "image/jpeg" // decode jpeg sources too
	"image/png"
)

// whiteCut is the per-channel floor for a pixel to count as removable background.
// articy portraits sit on a near-white field; the rim light (#b1dee2) lives on the
// silhouette and stays. 205 keeps shirts/highlights that aren't border-connected.
const whiteCut = 205

// Matte cuts the solid near-white background out of a character sprite, returning
// a PNG with that background made transparent. It flood-fills inward from the
// border, so only background connected to an edge is removed — interior white
// (clothing, catch-lights) and the teal rim survive. Non-image bytes pass through
// unchanged-ish (an error is returned so the caller can fall back to the original).
func Matte(src []byte) ([]byte, error) {
	img, _, err := image.Decode(bytes.NewReader(src))
	if err != nil {
		return nil, err
	}
	b := img.Bounds()
	w, h := b.Dx(), b.Dy()
	if w == 0 || h == 0 {
		return src, nil
	}

	// Copy into an editable NRGBA at origin (0,0).
	out := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			out.SetNRGBA(x, y, color.NRGBAModel.Convert(img.At(b.Min.X+x, b.Min.Y+y)).(color.NRGBA))
		}
	}

	bg := func(x, y int) bool {
		p := out.NRGBAAt(x, y)
		return p.R >= whiteCut && p.G >= whiteCut && p.B >= whiteCut
	}

	// BFS flood fill from every border pixel that is itself background.
	visited := make([]bool, w*h)
	queue := make([]int, 0, w*2+h*2)
	push := func(x, y int) {
		if x < 0 || y < 0 || x >= w || y >= h {
			return
		}
		i := y*w + x
		if visited[i] || !bg(x, y) {
			return
		}
		visited[i] = true
		queue = append(queue, i)
	}
	for x := 0; x < w; x++ {
		push(x, 0)
		push(x, h-1)
	}
	for y := 0; y < h; y++ {
		push(0, y)
		push(w-1, y)
	}
	for head := 0; head < len(queue); head++ {
		i := queue[head]
		x, y := i%w, i/w
		push(x-1, y)
		push(x+1, y)
		push(x, y-1)
		push(x, y+1)
	}

	for i, v := range visited {
		if v {
			p := out.Pix[i*4:]
			p[3] = 0 // alpha → transparent
		}
	}

	var buf bytes.Buffer
	if err := png.Encode(&buf, out); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}
