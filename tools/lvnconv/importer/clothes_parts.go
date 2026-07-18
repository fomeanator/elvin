package importer

import (
	"fmt"
	"image"
	"image/draw"
	"image/png"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// Partner packs ship some outfits as SEVERAL stacked files —
// Cold_main_clothes_13_1.png (база: топ+брюки+кисти) + _13_2.png (куртка
// поверх) — while the engine's outfit axis wants ONE png per value. These
// helpers find the parts and bake them into the single file the catalog
// references. Losing a part loses body parts painted in it (a hand shipped
// bare-handed in the demo this way).

// partFile is one file of a multi-part outfit, with its stacking order.
type partFile struct {
	idx int
	src string
}

// splitPartSuffix recognises the "13_1" shape: a NUMERIC outfit value plus a
// numeric part index. Non-numeric bases stay whole — "red_1" is an outfit
// named red_1, only digit-digit splits are the partner's multi-part format.
func splitPartSuffix(n string) (base string, idx int, ok bool) {
	i := strings.LastIndex(n, "_")
	if i <= 0 {
		return "", 0, false
	}
	suf, err := strconv.Atoi(n[i+1:])
	if err != nil {
		return "", 0, false
	}
	if _, err := strconv.Atoi(n[:i]); err != nil {
		return "", 0, false
	}
	return n[:i], suf, true
}

// resolveParts finds cand's multi-part art: cand_1, cand_2, … (in order,
// via the same case/underscore-tolerant resolve). Nil when cand_1 is absent.
func (fi folderIndex) resolveParts(cand string) []string {
	var parts []string
	for i := 1; ; i++ {
		p, ok := fi.resolve(cand + "_" + strconv.Itoa(i))
		if !ok {
			break
		}
		parts = append(parts, p)
	}
	return parts
}

// compositeParts alpha-composites the part files IN ORDER (first at the
// bottom) onto one canvas and writes dst as png. All parts must share the
// canvas size — partner art is authored on a common frame, and silently
// offsetting a mismatched layer would misdress the character.
func compositeParts(parts []string, dst string) error {
	if len(parts) == 0 {
		return fmt.Errorf("compositeParts: no parts")
	}
	var canvas *image.NRGBA
	for _, p := range parts {
		f, err := os.Open(p)
		if err != nil {
			return err
		}
		img, err := png.Decode(f)
		f.Close()
		if err != nil {
			return fmt.Errorf("%s: %w", filepath.Base(p), err)
		}
		if canvas == nil {
			canvas = image.NewNRGBA(img.Bounds())
		} else if !img.Bounds().Eq(canvas.Bounds()) {
			return fmt.Errorf("part %s canvas %v != %v", filepath.Base(p), img.Bounds(), canvas.Bounds())
		}
		draw.Draw(canvas, canvas.Bounds(), img, img.Bounds().Min, draw.Over)
	}
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	defer out.Close()
	return png.Encode(out, canvas)
}
