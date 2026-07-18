package importer

// body_matte.go cleans a roster character's BODY layer for stacking under
// clothes. Partner art draws a teal rim light along the nude body's contour —
// fine when the body renders alone (a bed scene), but stacked under an outfit
// whose silhouette differs the rim peeks out as a glowing halo (the Roman
// jacket bug). Detached low-alpha matte crumbs around the figure read as
// smears the same way. So for bodies that WILL sit under clothes we strip the
// teal-tinted pixels and everything outside the figure's solid core.

import (
	"image"
	"image/png"
	"os"
)

// cleanBodyUnderClothes rewrites the PNG at path with the teal rim and the
// out-of-core fringe removed. Call it only for bodies layered under clothes —
// a standalone body keeps its rim light by design.
func cleanBodyUnderClothes(path string) error {
	f, err := os.Open(path)
	if err != nil {
		return nil // no body on disk — nothing to clean
	}
	src, err := png.Decode(f)
	f.Close()
	if err != nil {
		return nil // not a decodable PNG (placeholder/fixture) — leave it be
	}
	b := src.Bounds()
	w, h := b.Dx(), b.Dy()
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			img.Set(x, y, src.At(b.Min.X+x, b.Min.Y+y))
		}
	}

	// The figure's solid core: alpha ≥ 200, eroded 2px then re-grown 4px —
	// keeps the anti-aliased edge, drops detached semi-transparent crumbs.
	solid := make([]bool, w*h)
	for i := 0; i < w*h; i++ {
		solid[i] = img.Pix[i*4+3] >= 200
	}
	core := erode(solid, w, h, 2)
	grown := dilate(core, w, h, 4)

	for i := 0; i < w*h; i++ {
		a := img.Pix[i*4+3]
		if a == 0 {
			continue
		}
		r, g, bl := img.Pix[i*4], img.Pix[i*4+1], img.Pix[i*4+2]
		mn := g
		if bl < mn {
			mn = bl
		}
		teal := a > 8 && g > 60 && bl > 60 && int(r) < int(mn)*3/4
		if teal || !grown[i] {
			img.Pix[i*4+3] = 0
		}
	}

	out, err := os.Create(path)
	if err != nil {
		return err
	}
	defer out.Close()
	return png.Encode(out, img)
}

func erode(m []bool, w, h, iters int) []bool {
	cur := append([]bool(nil), m...)
	next := make([]bool, len(m))
	for it := 0; it < iters; it++ {
		for y := 0; y < h; y++ {
			for x := 0; x < w; x++ {
				i := y*w + x
				next[i] = cur[i] &&
					x > 0 && cur[i-1] && x < w-1 && cur[i+1] &&
					y > 0 && cur[i-w] && y < h-1 && cur[i+w]
			}
		}
		cur, next = next, cur
	}
	return cur
}

func dilate(m []bool, w, h, iters int) []bool {
	cur := append([]bool(nil), m...)
	next := make([]bool, len(m))
	for it := 0; it < iters; it++ {
		for y := 0; y < h; y++ {
			for x := 0; x < w; x++ {
				i := y*w + x
				next[i] = cur[i] ||
					(x > 0 && cur[i-1]) || (x < w-1 && cur[i+1]) ||
					(y > 0 && cur[i-w]) || (y < h-1 && cur[i+w])
			}
		}
		cur, next = next, cur
	}
	return cur
}
