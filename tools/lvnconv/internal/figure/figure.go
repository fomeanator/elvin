// Package figure measures WHERE INSIDE ITS CANVAS a character actually is.
//
// A layered character is a stack of full-frame pngs, and around the character
// each frame carries transparent air — however much the artist happened to
// leave. That padding is invisible but not weightless: the runtime sizes an
// actor by its BOX, so `h=0.93` means "the file is 93% of the screen tall",
// not "the character is". One cast comes out of articy with 7% padding, another
// is drawn at 58%, and the same placement yields wildly different heights — the
// "why is she so small" bug that no amount of tuning w=/h= ever really fixed.
//
// This package computes, per entity, the union of the opaque areas of EVERY
// variant of EVERY layer, as fractions of the canvas, and stores it in the
// manifest as `content`. The runtime then measures the figure instead of the
// file. The UNION (rather than the current outfit's box) is what keeps the
// character's height still while the wardrobe changes underneath.
package figure

import (
	"encoding/json"
	"fmt"
	"image"
	_ "image/jpeg"
	"image/png"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strings"
	"sync"
)

// Box is a rectangle in canvas fractions: top-left corner plus size.
type Box struct {
	X float64 `json:"x"`
	Y float64 `json:"y"`
	W float64 `json:"w"`
	H float64 `json:"h"`
}

// Result is one entity's measurement.
type Result struct {
	Entity  string
	Files   int    // frames actually measured
	Foreign int    // frames ignored for not being the doll's canvas (shop icons)
	Box     Box    // union of the opaque areas, canvas fractions
	Prev    *Box   // what the manifest said before (nil = nothing)
	Skipped string // non-empty = why this entity was left alone
	Err     error
}

// Changed reports whether writing this result would actually alter the manifest.
func (r Result) Changed() bool {
	if r.Skipped != "" || r.Err != nil {
		return false
	}
	if r.Prev == nil {
		return true
	}
	const eps = 0.0005
	d := func(a, b float64) bool { return a-b > eps || b-a > eps }
	return d(r.Prev.X, r.Box.X) || d(r.Prev.Y, r.Box.Y) ||
		d(r.Prev.W, r.Box.W) || d(r.Prev.H, r.Box.H)
}

// AlphaThreshold — a pixel counts as part of the figure above this alpha.
// Soft edges and stray anti-aliasing dust must not inflate the box.
const AlphaThreshold = 8

var tokenRe = regexp.MustCompile(`\{[^}]*\}`)

// Scan reads contentRoot/manifest.json and measures every LAYERED entity (one
// that declares an `aspect` — the paper dolls). Plain single-image sprites are
// left alone on purpose: there the position of the art INSIDE its frame is the
// author's composition (a face floating in the upper third), not padding.
func Scan(contentRoot string) ([]Result, error) {
	man, err := readManifest(contentRoot)
	if err != nil {
		return nil, err
	}
	sprites, _ := man["sprites"].(map[string]any)
	if len(sprites) == 0 {
		return nil, fmt.Errorf("manifest has no sprites")
	}
	names := make([]string, 0, len(sprites))
	for name := range sprites {
		names = append(names, name)
	}
	sort.Strings(names)

	var out []Result
	for _, name := range names {
		ent, _ := sprites[name].(map[string]any)
		if ent == nil {
			continue
		}
		res := Result{Entity: name}
		if prev, ok := ent["content"].(map[string]any); ok {
			res.Prev = &Box{X: num(prev["x"]), Y: num(prev["y"]), W: num(prev["w"]), H: num(prev["h"])}
		}
		if num(ent["aspect"]) <= 0 {
			res.Skipped = "не кукла (нет aspect)"
			out = append(out, res)
			continue
		}
		files := framesOf(contentRoot, ent)
		if len(files) == 0 {
			res.Skipped = "файлы слоёв не найдены"
			out = append(out, res)
			continue
		}
		box, measured, foreign, err := unionOf(files, num(ent["aspect"]))
		res.Files, res.Foreign, res.Box, res.Err = measured, foreign, box, err
		if err == nil && measured == 0 {
			res.Skipped = "все кадры пустые"
		}
		out = append(out, res)
	}
	return out, nil
}

// Apply writes the measured boxes back into contentRoot/manifest.json,
// touching nothing else in the file.
func Apply(contentRoot string, results []Result) (int, error) {
	man, err := readManifest(contentRoot)
	if err != nil {
		return 0, err
	}
	sprites, _ := man["sprites"].(map[string]any)
	written := 0
	for _, r := range results {
		if !r.Changed() {
			continue
		}
		ent, _ := sprites[r.Entity].(map[string]any)
		if ent == nil {
			continue
		}
		ent["content"] = map[string]any{
			"x": round4(r.Box.X), "y": round4(r.Box.Y),
			"w": round4(r.Box.W), "h": round4(r.Box.H),
		}
		written++
	}
	if written == 0 {
		return 0, nil
	}
	blob, err := json.MarshalIndent(man, "", " ")
	if err != nil {
		return 0, err
	}
	path := filepath.Join(contentRoot, "manifest.json")
	if err := os.WriteFile(path, append(blob, '\n'), 0o644); err != nil {
		return 0, err
	}
	return written, nil
}

// framesOf expands every layer template of an entity into the real files on
// disk: `hair_{hairstyle}_{hair}.png` → every hair the artist actually drew.
// Derived encodes (@mini, @2k, @1440) are skipped — they are the same picture.
func framesOf(contentRoot string, ent map[string]any) []string {
	layers, _ := ent["layers"].([]any)
	seen := map[string]bool{}
	var out []string
	for _, l := range layers {
		lm, _ := l.(map[string]any)
		url, _ := lm["url"].(string)
		if url == "" {
			continue
		}
		pattern := pathFor(contentRoot, tokenRe.ReplaceAllString(url, "*"))
		matches, _ := filepath.Glob(pattern)
		for _, m := range matches {
			if strings.Contains(filepath.Base(m), "@") || seen[m] {
				continue
			}
			seen[m] = true
			out = append(out, m)
		}
	}
	sort.Strings(out)
	return out
}

// unionOf measures every frame and returns the union of their opaque boxes in
// canvas fractions.
//
// Only frames drawn on the doll's OWN canvas count, and the doll's canvas is
// the one whose shape matches the declared aspect — that is the box the runtime
// draws every frame into. Two kinds of stranger get in otherwise: a layer
// template like `decor_{decor}.png` also matches `decor_rose_icon.png`, a
// 248×252 shop thumbnail filled edge to edge (it would report the character as
// filling 100% of the canvas), and a cast sometimes carries wider one-off
// frames — "Matvey and Valera" at 1266px against the doll's 944 — whose
// fractions simply do not mean the same thing.
func unionOf(files []string, aspect float64) (Box, int, int, error) {
	type job struct {
		box  Box
		size image.Point
		ok   bool
		err  error
	}
	results := make([]job, len(files))
	workers := runtime.NumCPU()
	if workers > 8 {
		workers = 8
	}
	var wg sync.WaitGroup
	feed := make(chan int)
	for w := 0; w < workers; w++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := range feed {
				b, size, ok, err := opaqueBox(files[i])
				results[i] = job{b, size, ok, err}
			}
		}()
	}
	for i := range files {
		feed <- i
	}
	close(feed)
	wg.Wait()

	// Чей это холст: сперва тот, чья форма совпала с объявленным aspect —
	// именно в такой бокс рантайм и рисует. Если совпадения нет, решает
	// большинство кадров, при ничьей — крупнейший.
	votes := map[image.Point]int{}
	for i, r := range results {
		if r.err != nil {
			return Box{}, 0, 0, fmt.Errorf("%s: %w", filepath.Base(files[i]), r.err)
		}
		if r.ok {
			votes[r.size]++
		}
	}
	var canvas image.Point
	if aspect > 0 {
		for size, n := range votes {
			shape := float64(size.X) / float64(size.Y)
			if shape-aspect > 0.005 || aspect-shape > 0.005 {
				continue
			}
			if canvas == (image.Point{}) || n > votes[canvas] {
				canvas = size
			}
		}
	}
	if canvas == (image.Point{}) {
		for size, n := range votes {
			best := votes[canvas]
			if n > best || (n == best && size.X*size.Y > canvas.X*canvas.Y) {
				canvas = size
			}
		}
	}

	var u Box
	measured, foreign := 0, 0
	for _, r := range results {
		if !r.ok {
			continue
		}
		if r.size != canvas {
			foreign++
			continue
		}
		if measured == 0 {
			u = r.box
		} else {
			u = union(u, r.box)
		}
		measured++
	}
	return u, measured, foreign, nil
}

func union(a, b Box) Box {
	x0, y0 := min(a.X, b.X), min(a.Y, b.Y)
	x1, y1 := max(a.X+a.W, b.X+b.W), max(a.Y+a.H, b.Y+b.H)
	return Box{X: x0, Y: y0, W: x1 - x0, H: y1 - y0}
}

// opaqueBox finds the bounding box of the visible pixels, in fractions of the
// image. An image without alpha (a jpeg) is opaque everywhere, so its box is
// the whole frame — exactly right.
func opaqueBox(path string) (Box, image.Point, bool, error) {
	f, err := os.Open(path)
	if err != nil {
		return Box{}, image.Point{}, false, err
	}
	defer f.Close()
	img, _, err := image.Decode(f)
	if err != nil {
		return Box{}, image.Point{}, false, err
	}
	b := img.Bounds()
	w, h := b.Dx(), b.Dy()
	size := image.Point{X: w, Y: h}
	if w <= 0 || h <= 0 {
		return Box{}, size, false, nil
	}
	minX, minY, maxX, maxY := w, h, -1, -1
	visit := func(x, y int, alpha uint32) {
		if alpha <= AlphaThreshold {
			return
		}
		if x < minX {
			minX = x
		}
		if x > maxX {
			maxX = x
		}
		if y < minY {
			minY = y
		}
		if y > maxY {
			maxY = y
		}
	}
	switch src := img.(type) {
	case *image.NRGBA:
		for y := 0; y < h; y++ {
			row := src.Pix[y*src.Stride : y*src.Stride+w*4]
			for x := 0; x < w; x++ {
				visit(x, y, uint32(row[x*4+3]))
			}
		}
	case *image.RGBA:
		for y := 0; y < h; y++ {
			row := src.Pix[y*src.Stride : y*src.Stride+w*4]
			for x := 0; x < w; x++ {
				visit(x, y, uint32(row[x*4+3]))
			}
		}
	default:
		for y := 0; y < h; y++ {
			for x := 0; x < w; x++ {
				_, _, _, a := img.At(b.Min.X+x, b.Min.Y+y).RGBA()
				visit(x, y, a>>8)
			}
		}
	}
	if maxX < 0 {
		return Box{}, size, false, nil // полностью прозрачный кадр
	}
	return Box{
		X: float64(minX) / float64(w),
		Y: float64(minY) / float64(h),
		W: float64(maxX-minX+1) / float64(w),
		H: float64(maxY-minY+1) / float64(h),
	}, size, true, nil
}

func readManifest(contentRoot string) (map[string]any, error) {
	blob, err := os.ReadFile(filepath.Join(contentRoot, "manifest.json"))
	if err != nil {
		return nil, err
	}
	var man map[string]any
	if err := json.Unmarshal(blob, &man); err != nil {
		return nil, err
	}
	return man, nil
}

// pathFor maps a content URL ("/content/sprites/hill/body.png") onto a file
// under contentRoot.
func pathFor(contentRoot, url string) string {
	u := strings.TrimPrefix(url, "/content/")
	u = strings.TrimPrefix(u, "content/")
	u = strings.TrimPrefix(u, "/")
	return filepath.Join(contentRoot, filepath.FromSlash(u))
}

func num(v any) float64 {
	f, _ := v.(float64)
	return f
}

func round4(v float64) float64 {
	return float64(int(v*10000+0.5)) / 10000
}

func min(a, b float64) float64 {
	if a < b {
		return a
	}
	return b
}

func max(a, b float64) float64 {
	if a > b {
		return a
	}
	return b
}

// ensure png stays linked even if a build ever drops the blank import above.
var _ = png.Decode
