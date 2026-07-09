package importer

// bundle_assets.go turns the extracted "Cold" art archives (backgrounds and a
// characters tree) into engine content files plus the manifest `sprites`/`bg`
// fragments the orchestrator merges into a novel's manifest.
//
// Character mapping is SHEET-DRIVEN: the «Эмоции» roster (xd.Chars, incl. the
// protagonist) and the «Гардеробыч» wardrobe (xd.Wardrobe) are authoritative for
// every emotion, outfit and hair value. For each roster character we resolve the
// exact art stem the sheet names to a real file (a small case/underscore/`_1`
// cascade that absorbs all on-disk drift) and COPY it to a canonical destination
// name — `<tech>_<emotion>.png`, `<tech>_clothes_<value>.png`,
// `<tech>_hair_<value>.png`, `<tech>_body.png` — so the emitted manifest templates
// (`/content/art/<tech>_{emotion}.png`, …) are always clean. The result is one
// `layered` LvnSpriteEntity per character with body→clothes→face(→hair) layers,
// emotion/outfit/hair axes, defaults and an authored-box `aspect`.
//
// Folder scans remain only as a FALLBACK: off-roster folders referenced by scripts
// (not in «Эмоции») get a best-effort face build, and standalone flat PNGs become
// single-image `kind:"image"` bit actors.

import (
	"fmt"
	"image/png"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// resolveSrc returns the directory the archive's files actually live in. The
// extractions nest everything under a `NEW/` folder; fall back to srcDir when
// that layout isn't present.
func resolveSrc(srcDir string) string {
	if st, err := os.Stat(filepath.Join(srcDir, "NEW")); err == nil && st.IsDir() {
		return filepath.Join(srcDir, "NEW")
	}
	return srcDir
}

// copyFile copies src→dst, creating parent dirs. It is idempotent: if dst
// already exists with the same byte size it is left untouched (a byte copy is
// enough here — these are opaque PNGs, no decode needed).
func copyFile(src, dst string) error {
	si, err := os.Stat(src)
	if err != nil {
		return err
	}
	if di, err := os.Stat(dst); err == nil && di.Size() == si.Size() {
		return nil // same size already there → skip
	}
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	data, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	return os.WriteFile(dst, data, 0o644)
}

// MapBackgrounds copies every `*.png` under srcDir into <contentDir>/bg/ and
// returns id→url, e.g. "Cold_camp" → "/content/bg/Cold_camp.png". The id is the
// filename without extension; the `NEW/` prefix is stripped.
func MapBackgrounds(srcDir, contentDir string) (map[string]string, error) {
	root := resolveSrc(srcDir)
	out := map[string]string{}
	err := filepath.WalkDir(root, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() || !strings.EqualFold(filepath.Ext(d.Name()), ".png") {
			return nil
		}
		id := stem(d.Name())
		dst := filepath.Join(contentDir, "bg", id+".png")
		if err := copyFile(path, dst); err != nil {
			return err
		}
		out[id] = "/content/bg/" + id + ".png"
		return nil
	})
	if err != nil {
		return nil, err
	}
	return out, nil
}

// nameFromFolder is a display-name fallback: the folder/stem minus its leading
// project prefix (the first "_"-segment), e.g. "Cold_Matvey" → "Matvey".
func nameFromFolder(folder string) string {
	if i := strings.Index(folder, "_"); i >= 0 && i+1 < len(folder) {
		return folder[i+1:]
	}
	return folder
}

// ── art-folder resolution ────────────────────────────────────────────────────

// folderIndex indexes a character-art folder's *.png files by lowercased stem and
// by underscore-collapsed lowercased stem, so a canonical sheet stem resolves to a
// real file regardless of case, doubled underscores ("Cold_Eduard__surprised"), or
// a project's lowercased-name drift ("Cold_commandant_Anger").
type folderIndex struct {
	exact     map[string]string // lower(stem) → abs path
	collapsed map[string]string // collapse_(lower(stem)) → abs path
}

// collapseUnderscores collapses runs of '_' into a single '_'.
func collapseUnderscores(s string) string {
	var b strings.Builder
	prev := false
	for _, r := range s {
		if r == '_' {
			if prev {
				continue
			}
			prev = true
		} else {
			prev = false
		}
		b.WriteRune(r)
	}
	return b.String()
}

func indexFolder(dir string) folderIndex {
	fi := folderIndex{exact: map[string]string{}, collapsed: map[string]string{}}
	entries, _ := os.ReadDir(dir)
	for _, f := range entries {
		if f.IsDir() || !strings.EqualFold(filepath.Ext(f.Name()), ".png") {
			continue
		}
		s := strings.ToLower(stem(f.Name()))
		p := filepath.Join(dir, f.Name())
		if _, ok := fi.exact[s]; !ok {
			fi.exact[s] = p
		}
		c := collapseUnderscores(s)
		if _, ok := fi.collapsed[c]; !ok {
			fi.collapsed[c] = p
		}
	}
	return fi
}

// resolve tries a candidate stem (case-insensitive) against the folder, first
// exactly, then with underscores collapsed.
func (fi folderIndex) resolve(cand string) (string, bool) {
	c := strings.ToLower(cand)
	if p, ok := fi.exact[c]; ok {
		return p, true
	}
	if p, ok := fi.collapsed[collapseUnderscores(c)]; ok {
		return p, true
	}
	return "", false
}

// resolveEmotionArt runs the documented emotion cascade for a sheet stem S / axis
// value v (all case-insensitive, in order): S.png → collapsed → S_1.png → (for
// thoughtfulness) the "thougfless" spelling.
func (fi folderIndex) resolveEmotionArt(stemS, v string) (string, bool) {
	if p, ok := fi.resolve(stemS); ok { // 1 & 2 (exact + underscore-collapsed)
		return p, true
	}
	if p, ok := fi.resolve(stemS + "_1"); ok { // 3: the whole Cold_Bogdan folder ships "_1"
		return p, true
	}
	if v == "thoughtfulness" { // 4: heroine Cold_main_thougfless.png
		alt := strings.ReplaceAll(strings.ToLower(stemS), "thoughtfulness", "thougfless")
		if p, ok := fi.resolve(alt); ok {
			return p, true
		}
		if p, ok := fi.resolve(alt + "_1"); ok {
			return p, true
		}
	}
	return "", false
}

// staticHair finds a fixed (non-axis) hair sheet — "<tech>_hair" or "<tech>_hair_1"
// (Bogdan / Bogdan_Flashback). It deliberately does NOT match the heroine's
// "<tech>_Hairs_<n>" axis art.
func (fi folderIndex) staticHair(tech string) (string, bool) {
	if p, ok := fi.resolve(tech + "_hair"); ok {
		return p, true
	}
	return fi.resolve(tech + "_hair_1")
}

// ── sheet-driven character mapping ───────────────────────────────────────────

// MapCharacters builds one sprite entity per roster character in xd.Chars (the
// protagonist is a roster row too), resolving each exact emotion/clothes/hair art
// stem the sheet names to a real file and copying it to a canonical destination so
// the manifest templates stay clean. Off-roster folders and standalone flat PNGs
// fall back to a folder-scan face build / single-image actor respectively.
//
// Entities are keyed by lowercased tech name (e.g. "cold_matvey", "cold_main"); the
// wiring pass (bundle_wire.go) re-keys them to the story actor id. Returns the
// entity map plus a list of non-fatal warnings for genuinely-incomplete source data
// (a rostered emotion with no art, a curated wardrobe row with no art).
func MapCharacters(srcDir, contentDir string, xd XlsxData, tpl *Template) (map[string]map[string]any, []string, error) {
	tpl = tpl.resolve()
	root := resolveSrc(srcDir)
	entries, err := os.ReadDir(root)
	if err != nil {
		return nil, nil, err
	}

	folderByLower := map[string]string{} // lower(name) → actual folder name
	var flats []string                   // root-level *.png file names
	for _, d := range entries {
		if d.IsDir() {
			folderByLower[strings.ToLower(d.Name())] = d.Name()
		} else if strings.EqualFold(filepath.Ext(d.Name()), ".png") {
			flats = append(flats, d.Name())
		}
	}

	// The variable defaults (Wardrobe.<X> = N) give the resting outfit/hair value.
	varDefault := map[string]string{}
	for _, v := range xd.Vars {
		if _, ok := varDefault[v.Key]; !ok {
			varDefault[v.Key] = strings.TrimSpace(v.Default)
		}
	}

	heroTech := ""
	if xd.Protagonist != nil {
		heroTech = strings.TrimSpace(xd.Protagonist.TechName)
	}

	out := map[string]map[string]any{}
	var warnings []string
	usedFolders := map[string]bool{}
	usedFlats := map[string]bool{}

	// 1) Roster characters (sheet-driven).
	for i := range xd.Chars {
		c := xd.Chars[i]
		tech := strings.TrimSpace(c.TechName)
		story := strings.TrimSpace(c.StoryName)
		if tech == "" || story == "" {
			continue
		}
		key := strings.ToLower(tech)
		if _, done := out[key]; done {
			continue // duplicate roster row
		}
		folder, hasFolder := folderByLower[key]
		if !hasFolder {
			// No paperdoll folder — fall back to a flat <tech>.png single-image actor.
			flat, ok := findFlat(flats, tech)
			if !ok {
				continue
			}
			if err := copyFile(filepath.Join(root, flat), filepath.Join(contentDir, "art", tech+".png")); err != nil {
				return nil, nil, err
			}
			usedFlats[strings.ToLower(flat)] = true
			out[key] = singleImageEntity(story, tech)
			if len(c.Emotions) > 0 {
				warnings = append(warnings, fmt.Sprintf(
					"%s (%s): roster lists %d emotion(s) but only a flat image %s.png exists — emitted as single-image (no emotion axis)",
					story, tech, len(c.Emotions), tech))
			}
			continue
		}
		usedFolders[key] = true
		isHero := heroTech != "" && strings.EqualFold(tech, heroTech)
		ent, w, err := buildRosterChar(contentDir, filepath.Join(root, folder), tech, story, c, xd, varDefault, isHero, tpl)
		if err != nil {
			return nil, nil, err
		}
		warnings = append(warnings, w...)
		if ent != nil {
			out[key] = ent
		}
	}

	// 2) Off-roster folders (referenced by scripts, not in «Эмоции»): fallback build.
	for lf, folder := range folderByLower {
		if usedFolders[lf] {
			continue
		}
		ent, err := buildOffRosterFolder(contentDir, filepath.Join(root, folder), folder)
		if err != nil {
			return nil, nil, err
		}
		if ent != nil {
			out[lf] = ent
		}
	}

	// 3) Standalone flat PNGs not consumed by a roster actor → single-image bit actors.
	for _, f := range flats {
		if usedFlats[strings.ToLower(f)] {
			continue
		}
		base := stem(f)
		if err := copyFile(filepath.Join(root, f), filepath.Join(contentDir, "art", base+".png")); err != nil {
			return nil, nil, err
		}
		out[strings.ToLower(base)] = singleImageEntity(nameFromFolder(base), base)
	}

	return out, warnings, nil
}

// singleImageEntity is a flat, one-layer actor (no axes) — a bit actor / death
// variant / off-roster combo shot. kind:"image" renders via the generic layer path.
func singleImageEntity(name, tech string) map[string]any {
	url := "/content/art/" + tech + ".png"
	return map[string]any{
		"name":   name,
		"kind":   "image",
		"layers": []any{url},
	}
}

// buildRosterChar composes one roster character's layered entity from its art
// folder + the sheet. Layer order is fixed body→clothes→face(→hair); body/clothes
// are omitted when absent (bodyless Edward composes on `clothes`). The heroine
// (isHero) additionally gets a hair axis from Wardrobe.mainCh_Hair.
func buildRosterChar(contentDir, folderPath, tech, story string, c CharMap, xd XlsxData, varDefault map[string]string, isHero bool, tpl *Template) (map[string]any, []string, error) {
	wt := tpl.resolve().Wardrobe
	fi := indexFolder(folderPath)
	var warnings []string
	var layers []any
	axes := map[string]any{}
	defaults := map[string]any{}
	aspectSrc := ""

	var firstErr error
	cp := func(src, dstName string) {
		if firstErr != nil {
			return
		}
		firstErr = copyFile(src, filepath.Join(contentDir, "art", dstName))
	}
	artURL := func(name string) string { return "/content/art/" + name }

	// body ------------------------------------------------------------------
	if bodyPath, ok := fi.resolve(tech + "_body"); ok {
		dst := tech + "_body.png"
		cp(bodyPath, dst)
		layers = append(layers, map[string]any{"id": "body", "url": artURL(dst)})
		aspectSrc = bodyPath
	}

	// clothes (from the wardrobe group) -------------------------------------
	grpKey := wt.VarPrefix + story
	if isHero {
		grpKey = wt.ProtagonistClothesVar
	}
	if grp, ok := xd.Wardrobe[grpKey]; ok {
		var vals []string
		for _, it := range grp {
			t := strings.TrimSpace(it.TechName)
			if t == "" {
				if strings.TrimSpace(it.Name) != "" {
					warnings = append(warnings, fmt.Sprintf(
						"%s (%s value %s %q): curated wardrobe item has no art (empty tech) — omitted; artist action needed",
						story, grpKey, it.Value, it.Name))
				}
				continue // assignment-only base state / true content gap
			}
			src, ok := fi.resolve(t)
			if !ok {
				warnings = append(warnings, fmt.Sprintf(
					"%s (%s value %s): outfit art %q not found in %s — omitted from axis",
					story, grpKey, it.Value, t, filepath.Base(folderPath)))
				continue
			}
			dst := tech + "_" + wt.OutfitInfix + "_" + it.Value + ".png"
			cp(src, dst)
			vals = append(vals, it.Value)
			if aspectSrc == "" {
				aspectSrc = src
			}
		}
		if len(vals) > 0 {
			sortNumeric(vals)
			layers = append(layers, map[string]any{"id": "clothes", "url": artURL(tech + "_" + wt.OutfitInfix + "_{outfit}.png")})
			axes["outfit"] = toAnyStrings(vals)
			defaults["outfit"] = floorDefault(varDefault[grpKey], vals)
		}
	}

	// face (emotions) -------------------------------------------------------
	if len(c.Emotions) > 0 {
		var vals []string
		for v, s := range c.Emotions {
			src, ok := fi.resolveEmotionArt(s, v)
			if !ok {
				warnings = append(warnings, fmt.Sprintf(
					"%s (%s): emotion %q art (stem %s) not found — dropped from axis",
					story, tech, v, s))
				continue
			}
			dst := tech + "_" + v + ".png"
			cp(src, dst)
			vals = append(vals, v)
			if aspectSrc == "" {
				aspectSrc = src
			}
		}
		if len(vals) > 0 {
			sort.Strings(vals)
			layers = append(layers, map[string]any{"id": "face", "url": artURL(tech + "_{emotion}.png")})
			axes["emotion"] = toAnyStrings(vals)
			defaults["emotion"] = floorDefault("idle", vals)
		}
	}

	// hair ------------------------------------------------------------------
	if isHero {
		if grp, ok := xd.Wardrobe[wt.ProtagonistHairVar]; ok {
			var vals []string
			for _, it := range grp {
				t := strings.TrimSpace(it.TechName)
				if t == "" {
					continue
				}
				src, ok := fi.resolve(t)
				if !ok {
					warnings = append(warnings, fmt.Sprintf(
						"%s (%s value %s): hair art %q not found — omitted from axis",
						story, wt.ProtagonistHairVar, it.Value, t))
					continue
				}
				dst := tech + "_" + wt.HairInfix + "_" + it.Value + ".png"
				cp(src, dst)
				vals = append(vals, it.Value)
			}
			if len(vals) > 0 {
				sortNumeric(vals)
				layers = append(layers, map[string]any{"id": "hair", "url": artURL(tech + "_" + wt.HairInfix + "_{hair}.png")})
				axes["hair"] = toAnyStrings(vals)
				defaults["hair"] = floorDefault(varDefault[wt.ProtagonistHairVar], vals)
			}
		}
	} else if hairPath, ok := fi.staticHair(tech); ok {
		dst := tech + "_hair.png"
		cp(hairPath, dst)
		layers = append(layers, map[string]any{"id": "hair", "url": artURL(dst)})
	}

	if firstErr != nil {
		return nil, warnings, firstErr
	}
	if len(layers) == 0 {
		return nil, warnings, nil
	}

	ent := map[string]any{
		"name":     story,
		"kind":     "layered",
		"layers":   layers,
		"axes":     axes,
		"defaults": defaults,
	}
	if a := pngAspect(aspectSrc); a > 0 {
		ent["aspect"] = a
	}
	return ent, warnings, nil
}

// buildOffRosterFolder builds a best-effort layered entity for a folder that isn't
// in the «Эмоции» roster but is referenced by scripts: a folder scan classifying
// body / clothes_<n> / hair / emotion, with disk-token normalization (lowercase,
// trailing "_N" stripped, known spelling typos patched). Non-authoritative but
// present so scripts don't 404.
func buildOffRosterFolder(contentDir, folderPath, folder string) (map[string]any, error) {
	entries, err := os.ReadDir(folderPath)
	if err != nil {
		return nil, err
	}
	var bodySrc, hairSrc string
	clothes := map[string]string{} // value → src
	emo := map[string]string{}     // token → src
	for _, f := range entries {
		if f.IsDir() || !strings.EqualFold(filepath.Ext(f.Name()), ".png") {
			continue
		}
		src := filepath.Join(folderPath, f.Name())
		rem := offToken(stem(f.Name()), folder)
		switch {
		case rem == "body":
			bodySrc = src
		case rem == "hair" || rem == "hair_1":
			if hairSrc == "" {
				hairSrc = src
			}
		case strings.HasPrefix(rem, "clothes"):
			n := strings.TrimLeft(strings.TrimPrefix(rem, "clothes"), "_")
			if n == "" {
				n = "1"
			}
			clothes[n] = src
		default:
			tok := patchEmotionTypos(stripTrailingNum(rem))
			if tok == "" {
				tok = "idle"
			}
			emo[tok] = src
		}
	}

	var firstErr error
	cp := func(src, dstName string) {
		if firstErr == nil {
			firstErr = copyFile(src, filepath.Join(contentDir, "art", dstName))
		}
	}
	artURL := func(name string) string { return "/content/art/" + name }
	var layers []any
	axes := map[string]any{}
	defaults := map[string]any{}
	aspectSrc := ""

	if bodySrc != "" {
		dst := folder + "_body.png"
		cp(bodySrc, dst)
		layers = append(layers, map[string]any{"id": "body", "url": artURL(dst)})
		aspectSrc = bodySrc
	}
	if len(clothes) > 0 {
		vals := make([]string, 0, len(clothes))
		for n, src := range clothes {
			vals = append(vals, n)
			cp(src, folder+"_clothes_"+n+".png")
			if aspectSrc == "" {
				aspectSrc = src
			}
		}
		sortNumeric(vals)
		layers = append(layers, map[string]any{"id": "clothes", "url": artURL(folder + "_clothes_{outfit}.png")})
		axes["outfit"] = toAnyStrings(vals)
		defaults["outfit"] = vals[0]
	}
	if len(emo) > 0 {
		vals := make([]string, 0, len(emo))
		for v, src := range emo {
			vals = append(vals, v)
			cp(src, folder+"_"+v+".png")
			if aspectSrc == "" {
				aspectSrc = src
			}
		}
		sort.Strings(vals)
		layers = append(layers, map[string]any{"id": "face", "url": artURL(folder + "_{emotion}.png")})
		axes["emotion"] = toAnyStrings(vals)
		defaults["emotion"] = floorDefault("idle", vals)
	}
	if hairSrc != "" {
		dst := folder + "_hair.png"
		cp(hairSrc, dst)
		layers = append(layers, map[string]any{"id": "hair", "url": artURL(dst)})
	}

	if firstErr != nil {
		return nil, firstErr
	}
	if len(layers) == 0 {
		return nil, nil
	}
	ent := map[string]any{
		"name":     nameFromFolder(folder),
		"kind":     "layered",
		"layers":   layers,
		"axes":     axes,
		"defaults": defaults,
	}
	if a := pngAspect(aspectSrc); a > 0 {
		ent["aspect"] = a
	}
	return ent, nil
}

// offToken strips the folder prefix from a file stem (case-insensitive), falling
// back to everything after the second underscore when a spelling drift breaks the
// prefix ("Cold_Philipp" folder / "Cold_Phillip_Surprised" file). Lowercased.
func offToken(base, folder string) string {
	lb, lf := strings.ToLower(base), strings.ToLower(folder)
	var rem string
	switch {
	case strings.HasPrefix(lb, lf+"_"):
		rem = base[len(folder)+1:]
	default:
		if parts := strings.SplitN(base, "_", 3); len(parts) == 3 {
			rem = parts[2]
		} else {
			rem = base
		}
	}
	return strings.ToLower(strings.TrimLeft(rem, "_"))
}

// stripTrailingNum drops a trailing "_<digits>" ("anger_1" → "anger").
func stripTrailingNum(s string) string {
	i := len(s)
	for i > 0 && s[i-1] >= '0' && s[i-1] <= '9' {
		i--
	}
	if i > 0 && i < len(s) && s[i-1] == '_' {
		return s[:i-1]
	}
	return s
}

// patchEmotionTypos maps the handful of misspelled emotion tokens on disk to the
// canonical legend labels.
func patchEmotionTypos(tok string) string {
	switch tok {
	case "surpriesd":
		return "surprised"
	case "throughtfulness", "thougfless", "thoughfless", "thoughtless":
		return "thoughtfulness"
	case "angry":
		return "anger"
	}
	return tok
}

// ── helpers ──────────────────────────────────────────────────────────────────

// findFlat locates a flat "<tech>.png" among the root-level pngs, case-insensitively.
func findFlat(flats []string, tech string) (string, bool) {
	want := strings.ToLower(tech) + ".png"
	for _, f := range flats {
		if strings.ToLower(f) == want {
			return f, true
		}
	}
	return "", false
}

// floorDefault returns want when it is a value with art (present in vals), else the
// first value — so a default always resolves to a real layer.
func floorDefault(want string, vals []string) string {
	if want != "" && containsStr(vals, want) {
		return want
	}
	return vals[0]
}

func containsStr(vals []string, want string) bool {
	for _, v := range vals {
		if v == want {
			return true
		}
	}
	return false
}

func toAnyStrings(ss []string) []any {
	a := make([]any, len(ss))
	for i, s := range ss {
		a[i] = s
	}
	return a
}

// sortNumeric orders wardrobe index tokens ("0","1","11","13") by leading integer.
func sortNumeric(vals []string) {
	sort.Slice(vals, func(i, j int) bool { return lessNNN(vals[i], vals[j]) })
}

// pngAspect reads a PNG's width/height ratio from its header (no full decode). 0
// when the file is missing/unreadable.
func pngAspect(path string) float64 {
	if path == "" {
		return 0
	}
	f, err := os.Open(path)
	if err != nil {
		return 0
	}
	defer f.Close()
	cfg, err := png.DecodeConfig(f)
	if err != nil || cfg.Height == 0 {
		return 0
	}
	return float64(cfg.Width) / float64(cfg.Height)
}

// lessNNN orders raw wardrobe index tokens ("0","11","112","13_1") by their
// leading integer, tie-broken lexically — so axis lists read in numeric order.
func lessNNN(a, b string) bool {
	ai, aok := leadInt(a)
	bi, bok := leadInt(b)
	if aok && bok && ai != bi {
		return ai < bi
	}
	return a < b
}

func leadInt(s string) (int, bool) {
	n, i := 0, 0
	for i < len(s) && s[i] >= '0' && s[i] <= '9' {
		n = n*10 + int(s[i]-'0')
		i++
	}
	return n, i > 0
}
