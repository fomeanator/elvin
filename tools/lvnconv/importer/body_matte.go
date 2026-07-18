package importer

// body_matte.go cleans a roster character's BODY layer for stacking under
// clothes. The partner style draws a teal rim light along every figure's
// contour — INTENTIONAL wherever that contour is the final silhouette (hands,
// legs, a neck, an underwear look). But the nude body's contour also runs
// UNDER the outfits, and where an outfit's silhouette is narrower the body's
// rim peeks out as a glowing halo (the Roman jacket bug). So the cleanup is
// surgical: only body regions that hug the clothes' contour from the outside
// (thin leak bands) are erased — anything reaching further (a limb, the neck)
// keeps its rim. Detached low-alpha matte crumbs are dropped too.

import (
	"image"
	"image/png"
	"os"
)

// leakReach: an uncovered body region whose farthest pixel is closer than
// this to the clothes silhouette is rim leak; real limbs extend further.
const leakReach = 24

// cleanBodyUnderClothes rewrites the body PNG with rim leaks (relative to the
// union of the given clothes PNGs) and detached fringe removed. Best-effort:
// undecodable inputs or size mismatches leave the body untouched.
func cleanBodyUnderClothes(bodyPath string, clothesPaths []string) error {
	img, w, h := decodeNRGBA(bodyPath)
	if img == nil {
		return nil
	}

	// The union silhouette of every outfit this body sits under.
	union := make([]bool, w*h)
	anyClothes := false
	for _, cp := range clothesPaths {
		c, cw, ch := decodeNRGBA(cp)
		if c == nil || cw != w || ch != h {
			continue
		}
		anyClothes = true
		for i := 0; i < w*h; i++ {
			if c.Pix[i*4+3] >= 100 {
				union[i] = true
			}
		}
	}
	if !anyClothes {
		return nil
	}

	vis := make([]bool, w*h)
	solid := make([]bool, w*h)
	for i := 0; i < w*h; i++ {
		a := img.Pix[i*4+3]
		vis[i] = a > 8
		solid[i] = a >= 200
	}
	core := dilate(erode(solid, w, h, 2), w, h, 4)
	dist := bfsDistance(union, w, h)

	// Uncovered-by-clothes body regions: flood each, measure how far it
	// reaches from the clothes contour; short reach = leak band → erase.
	kill := make([]bool, w*h)
	seen := make([]bool, w*h)
	var stack, comp []int
	for start := 0; start < w*h; start++ {
		if seen[start] || !vis[start] || !core[start] || union[start] {
			continue
		}
		stack = stack[:0]
		comp = comp[:0]
		stack = append(stack, start)
		seen[start] = true
		reach := 0
		for len(stack) > 0 {
			i := stack[len(stack)-1]
			stack = stack[:len(stack)-1]
			comp = append(comp, i)
			if dist[i] > reach {
				reach = dist[i]
			}
			x, y := i%w, i/w
			for _, n := range [4]int{i - 1, i + 1, i - w, i + w} {
				if n < 0 || n >= w*h || seen[n] {
					continue
				}
				nx := n % w
				if (x == 0 && nx == w-1) || (x == w-1 && nx == 0) {
					continue // no wrap across rows
				}
				_ = y
				if vis[n] && core[n] && !union[n] {
					seen[n] = true
					stack = append(stack, n)
				}
			}
		}
		if reach < leakReach {
			for _, i := range comp {
				kill[i] = true
			}
		}
	}

	changed := false
	for i := 0; i < w*h; i++ {
		if img.Pix[i*4+3] == 0 {
			continue
		}
		if kill[i] || (vis[i] && !core[i]) { // leak bands + detached crumbs
			img.Pix[i*4+3] = 0
			changed = true
		}
	}
	if !changed {
		return nil
	}
	out, err := os.Create(bodyPath)
	if err != nil {
		return err
	}
	defer out.Close()
	return png.Encode(out, img)
}

func decodeNRGBA(path string) (*image.NRGBA, int, int) {
	f, err := os.Open(path)
	if err != nil {
		return nil, 0, 0
	}
	src, err := png.Decode(f)
	f.Close()
	if err != nil {
		return nil, 0, 0
	}
	b := src.Bounds()
	w, h := b.Dx(), b.Dy()
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			img.Set(x, y, src.At(b.Min.X+x, b.Min.Y+y))
		}
	}
	return img, w, h
}

// bfsDistance: per-pixel step distance to the nearest true cell of mask
// (0 inside the mask), via multi-source BFS. Values cap at leakReach+1 —
// beyond that the exact number no longer matters.
func bfsDistance(mask []bool, w, h int) []int {
	const inf = leakReach + 1
	dist := make([]int, w*h)
	queue := make([]int, 0, w*h/8)
	for i := range dist {
		if mask[i] {
			queue = append(queue, i)
		} else {
			dist[i] = inf
		}
	}
	for head := 0; head < len(queue); head++ {
		i := queue[head]
		d := dist[i] + 1
		if d > inf {
			continue
		}
		x := i % w
		for _, n := range [4]int{i - 1, i + 1, i - w, i + w} {
			if n < 0 || n >= w*h || dist[n] <= d {
				continue
			}
			nx := n % w
			if (x == 0 && nx == w-1) || (x == w-1 && nx == 0) {
				continue
			}
			dist[n] = d
			queue = append(queue, n)
		}
	}
	return dist
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
