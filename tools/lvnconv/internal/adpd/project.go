package adpd

import (
	"encoding/binary"
	"encoding/json"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"unicode"
)

// ── public entry ─────────────────────────────────────────────────────────────

func findPartition(dir, kind string) string {
	var found string
	filepath.Walk(dir, func(p string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || found != "" {
			return nil
		}
		name := filepath.Base(p)
		if strings.Contains(name, kind) && strings.HasSuffix(name, ".adpd") {
			found = p
		}
		return nil
	})
	return found
}

func flowPath(path string) (string, string, error) {
	info, err := os.Stat(path)
	if err != nil {
		return "", "", err
	}
	if info.IsDir() {
		fp := findPartition(path, "Flow")
		if fp == "" {
			return "", "", fmt.Errorf("no Flow partition found under %s", path)
		}
		return fp, path, nil
	}
	return path, filepath.Dir(filepath.Dir(path)), nil
}

func loadFlow(path string) (flow, string, error) {
	fp, proj, err := flowPath(path)
	if err != nil {
		return flow{}, "", err
	}
	d, err := os.ReadFile(fp)
	if err != nil {
		return flow{}, "", err
	}
	if len(d) < 24 || string(d[:5]) != "ADPD8" {
		return flow{}, "", fmt.Errorf("%s: not an ADPD8 partition", fp)
	}
	fl := decodeFlow(d)
	if len(fl.nodes) == 0 {
		return flow{}, "", fmt.Errorf("%s: no flow connections decoded", fp)
	}
	return fl, proj, nil
}

func buildModel(fl flow, proj string, start, maxN int) (export, LinearizeReport) {
	rep := LinearizeReport{}
	rep.Emittable, rep.Trapped = pinFlowHealth(fl)

	// With an explicit -start, emit a single chapter from that node over the raw
	// 0x02 flow (after collapsing structural fan-outs). Without it, emit the WHOLE
	// novel as one connected, ordered chapter built from the container hierarchy —
	// the only structure that links the otherwise-shattered 0x02 islands.
	if start >= 0 {
		rep.Algorithm = "start"
		linearizeStructuralFanouts(fl)
		gvars := globalVars(proj, flowExprs(fl))
		if maxN <= 0 {
			maxN = math.MaxInt32
		}
		return buildExport(fl, uint32(start), maxN, gvars), rep
	}

	// True articy linearizer: anchor the forward pin-flow to the real container
	// hierarchy so the whole novel is ONE continuous, chapter-ordered spine (no
	// island-menu). Tried first whenever the project has a hierarchy root.
	if kids := completeChildren(fl); len(kids) == 0 {
		rep.Fallbacks = append(rep.Fallbacks, "anchored: project has no container hierarchy")
	} else if root, ok := hierarchyRoot(fl, kids); !ok {
		rep.Fallbacks = append(rep.Fallbacks, "anchored: no Flow root with ≥2 chapter fragments")
	} else if entries, ok := linearizeAnchored(fl, root, nil); ok {
		rep.Algorithm = "anchored"
		gvars := globalVars(proj, flowExprs(fl))
		reach, seen := bfs(fl, entries, math.MaxInt32)
		return emitModels(fl, reach, seen, entries, gvars), rep
	} else {
		rep.Fallbacks = append(rep.Fallbacks, "anchored: hierarchy-anchored linearization failed")
	}
	for k := range fl.succ { // anchored mutated succ — reset before the fallbacks
		delete(fl.succ, k)
	}

	// Faithful port of articy's TraverseFlow (forward pin-flow over emittable nodes)
	// — no spurious backward menu loops. Falls back only if it would strand content.
	if entries, ok := linearizeFaithful(fl, wholeNovelTops(fl), nil); ok {
		rep.Algorithm = "faithful"
		gvars := globalVars(proj, flowExprs(fl))
		reach, seen := bfs(fl, entries, math.MaxInt32)
		return emitModels(fl, reach, seen, entries, gvars), rep
	}
	rep.Fallbacks = append(rep.Fallbacks, "faithful: forward pin-flow would strand content")
	for k := range fl.succ { // faithful mutated succ — reset before the heuristics
		delete(fl.succ, k)
	}

	if entry, ok := linearizeByComponents(fl); ok {
		rep.Algorithm = "components"
		gvars := globalVars(proj, flowExprs(fl))
		return buildExport(fl, entry, math.MaxInt32, gvars), rep
	}
	rep.Fallbacks = append(rep.Fallbacks, "components: no safe reconvergent component chain")
	// Faithful flow would strand content — clear its partial edges and fall back to
	// the authoring-order spine (full coverage, no reconvergence).
	for k := range fl.succ {
		delete(fl.succ, k)
	}
	if entry, ok := linearizeByHierarchy(fl); ok {
		rep.Algorithm = "hierarchy"
		gvars := globalVars(proj, flowExprs(fl))
		return buildExport(fl, entry, math.MaxInt32, gvars), rep
	}
	rep.Fallbacks = append(rep.Fallbacks, "hierarchy: no authoring-order spine")
	// No container hierarchy (e.g. a synthetic test graph) — fall back to surfacing
	// every 0x02 component through a chapter hub.
	rep.Algorithm = "structural-fanouts"
	linearizeStructuralFanouts(fl)
	return buildExportAll(fl, globalVars(proj, flowExprs(fl))), rep
}

func flowExprs(fl flow) []string {
	var exprs []string
	for _, ln := range fl.logic {
		exprs = append(exprs, ln.expr)
	}
	return exprs
}

// BuildExportJSON reads the .adpd project at path (a project directory or a Flow
// partition file), reconstructs the articy model (text, speakers, choices,
// instructions and conditions), and returns it as JSON in the articy-export
// shape. start < 0 picks the story opening; maxN caps the chapter (0 = no cap).
func BuildExportJSON(path string, start, maxN int) ([]byte, error) {
	js, _, err := BuildExportJSONReport(path, start, maxN)
	return js, err
}

// BuildExportJSONReport is BuildExportJSON plus the linearizer transparency
// report — which algorithm produced the export, why the more faithful stages
// fell through, and the pin-flow trapped counter.
func BuildExportJSONReport(path string, start, maxN int) ([]byte, LinearizeReport, error) {
	fl, proj, err := loadFlow(path)
	if err != nil {
		return nil, LinearizeReport{}, err
	}
	model, rep := buildModel(fl, proj, start, maxN)
	js, err := json.Marshal(model)
	return js, rep, err
}

// Lang returns the project's primary language code (from the Settings partition,
// e.g. "ru"), or "und" when it can't be determined. Localization is done by the
// importer (importer.Localize) keyed off each fragment's StableId; this names the
// catalog sidecar (<script>.<lang>.json) the runtime loads per locale.
func Lang(path string) (string, error) {
	_, proj, err := flowPath(path)
	if err != nil {
		return "und", err
	}
	return langOf(proj), nil
}

var langCodeRe = regexp.MustCompile(`\b([a-z]{2})-[A-Z]{2}\b`)

// langOf reads the project's primary language code from the Settings partition
// ("ru-RU" → "ru"); defaults to "und" (undetermined) when absent.
func langOf(projectDir string) string {
	p := findPartition(projectDir, "Settings")
	if p == "" {
		return "und"
	}
	d, err := os.ReadFile(p)
	if err != nil {
		return "und"
	}
	if m := langCodeRe.FindSubmatch(d); m != nil {
		return string(m[1])
	}
	return "und"
}

// ── cast catalog (for auto-staging) ──────────────────────────────────────────

var (
	spritePathRe = regexp.MustCompile(`(?i)([^/\\]+\.(?:png|jpg|jpeg))$`)
	castSkip     = map[string]bool{
		"PreviewImageAsset": true, "OriginalSource": true, "Entity": true,
		"DefaultMainCharacterTemplate": true, "DisplayName": true,
		"BackgroundColor": true, "DisplayNameMultiLanguageText": true, "Text": true,
		"Attachments": true, "Articy": true,
	}
	castNameRe = regexp.MustCompile(`^(?:[A-Za-z_][\w]*|.*[А-Яа-я].*)$`)
	guidRe2    = regexp.MustCompile(`^[0-9a-f-]{36}$`)
)

// translitMap is a common Cyrillic→Latin romanization (handles digraphs like
// ю→yu, я→ya, ж→zh) so a Russian character name also matches a Latin speaker
// caption: "Тимур" → "Timur", "Люба" → "Lyuba", "Андрей" → "Andrey".
var translitMap = map[rune]string{
	'а': "a", 'б': "b", 'в': "v", 'г': "g", 'д': "d", 'е': "e", 'ё': "e",
	'ж': "zh", 'з': "z", 'и': "i", 'й': "y", 'к': "k", 'л': "l", 'м': "m",
	'н': "n", 'о': "o", 'п': "p", 'р': "r", 'с': "s", 'т': "t", 'у': "u",
	'ф': "f", 'х': "kh", 'ц': "ts", 'ч': "ch", 'ш': "sh", 'щ': "sch",
	'ъ': "", 'ы': "y", 'ь': "", 'э': "e", 'ю': "yu", 'я': "ya",
}

func transliterate(s string) string {
	hasCyr := false
	var b strings.Builder
	for _, r := range s {
		lo := unicode.ToLower(r)
		if t, ok := translitMap[lo]; ok {
			hasCyr = true
			if r != lo && t != "" { // preserve leading capital
				t = strings.ToUpper(t[:1]) + t[1:]
			}
			b.WriteString(t)
		} else {
			b.WriteRune(r)
		}
	}
	if !hasCyr {
		return ""
	}
	return b.String()
}

// Cast reads the project's Entities partition and returns character name → sprite
// filename. Each entity is keyed by every plausible name it carries (its Russian
// display name AND its Latin technical name) plus a transliteration of the Russian
// name, so a speaker caption resolves to its art regardless of language.
func Cast(path string) (map[string]string, error) {
	_, proj, err := flowPath(path)
	if err != nil {
		return nil, err
	}
	p := findPartition(proj, "Entities")
	if p == "" {
		return nil, fmt.Errorf("no Entities partition under %s", proj)
	}
	d, err := os.ReadFile(p)
	if err != nil {
		return nil, err
	}
	if len(d) < 16 { // header is a magic + an 8-byte offset; a truncated file must not panic the slice
		return nil, fmt.Errorf("Entities partition %s is truncated (%d bytes)", p, len(d))
	}
	idx := int(binary.LittleEndian.Uint64(d[8:]))
	if idx <= 0 || idx > len(d) {
		idx = len(d)
	}
	cast := map[string]string{}
	for _, o := range walkObjects(d, idx) {
		var sprite string
		var names []string
		for _, e := range o.es {
			if e.tag != 0x12 || e.s == "" {
				continue
			}
			if strings.HasPrefix(e.s, "file:///") {
				if m := spritePathRe.FindStringSubmatch(e.s); m != nil {
					sprite = m[1]
				}
			} else if len(e.s) < 28 && !castSkip[e.s] && !guidRe2.MatchString(e.s) && castNameRe.MatchString(e.s) {
				names = append(names, e.s)
			}
		}
		if sprite != "" {
			for _, n := range names {
				if _, ok := cast[n]; !ok {
					cast[n] = sprite
				}
				if tr := transliterate(n); tr != "" && tr != n {
					if _, ok := cast[tr]; !ok {
						cast[tr] = sprite
					}
				}
			}
		}
	}
	return cast, nil
}
