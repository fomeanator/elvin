// Package importer is the one-shot articy:draft (.adpd) → playable-novel pipeline
// behind the IDE's single "Import articy" button. Given an extracted .adpd project
// directory it produces everything a title needs to appear in the game, the admin
// IDE and the content server at once:
//
//   - a compiled .lvn script (adpd → articy model → .lvn), auto-staged so scenes
//     get backgrounds and speaking characters walk on;
//   - the referenced art, resolved from the project's Assets, with character
//     sprites matted (white background cut out) — keyed to the paths the script
//     uses (/content/art/*, /content/bg/*);
//   - a manifest title entry (carousel card + first chapter) wired to the script.
//
// It performs no I/O to the server itself — it returns the artifacts so the caller
// (the lvnconv CLI, or the server's import endpoint) writes them wherever content
// lives. That keeps the pipeline reusable and testable.
package importer

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/adpd"
	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/articy"
)

// Options controls a single import.
type Options struct {
	ID        string // title id / script base name, e.g. "soviet"
	Name      string // display name for the carousel card
	Subtitle  string // small line under the name
	Start     int    // adpd start node ordinal (-1 = story opening)
	Max       int    // cap chapter at N nodes (0 = no cap)
	AutoStage bool   // emit bg/actor staging (default on via Run)
	Localize  bool   // extract text into a <script>.<lang>.json catalog (i18n)
}

// ArtFile is one resolved asset and the content-relative path it must be written
// to (matching the sprite_url the script references): "art/<file>" or "bg/<file>".
type ArtFile struct {
	Rel  string
	Data []byte
}

// Manifest shapes (a subset of the engine's LvnManifest, enough to add a title).
type Chapter struct {
	ID        string `json:"id"`
	Number    int    `json:"number"`
	ScriptURL string `json:"script_url"`
	BgURL     string `json:"bg_url,omitempty"`
}
type Season struct {
	Chapters []Chapter `json:"chapters"`
}
type Title struct {
	ID       string   `json:"id"`
	Name     string   `json:"name"`
	Subtitle string   `json:"subtitle,omitempty"`
	CoverURL string   `json:"cover_url,omitempty"`
	Seasons  []Season `json:"seasons"`
}

// Result is everything an import produced. ScriptRel is the content-relative path
// for Lvn ("scripts/<id>.lvn"); Art carries the resolved/matted assets; Title is
// the manifest entry to splice in.
type Result struct {
	ScriptRel string
	Lvn       []byte
	Art       []ArtFile
	Title     Title
	Stats     map[string]int // op counts: say/choice/bg/actor/set/if…
	MissingBg []string       // scene locations with no matching art file (rendered dark)

	// Localization (set only when Options.Localize): the extracted string catalog
	// (text_id → string), the project language code, and the content-relative path
	// the catalog must be written to ("scripts/<id>.<lang>.json"). The runtime
	// loads it per locale beside the script; other languages are extra catalogs
	// against the same keys.
	Catalog    map[string]string
	Lang       string
	CatalogRel string
}

// Run executes the whole pipeline against an extracted .adpd project directory.
func Run(projectDir string, opt Options) (*Result, error) {
	if opt.ID == "" {
		opt.ID = "imported"
	}
	if opt.Name == "" {
		opt.Name = opt.ID
	}
	if opt.Start == 0 {
		opt.Start = -1
	}

	js, err := adpd.BuildExportJSON(projectDir, opt.Start, opt.Max)
	if err != nil {
		return nil, fmt.Errorf("adpd export: %w", err)
	}
	doc, err := articy.Convert(js, "")
	if err != nil {
		return nil, fmt.Errorf("articy convert: %w", err)
	}

	if opt.AutoStage {
		cast, err := adpd.Cast(projectDir)
		if err != nil {
			return nil, fmt.Errorf("adpd cast: %w", err)
		}
		AutoStage(doc, cast) // reads inline say text — must run before Localize
	}

	// Resolve art before localization swaps say text for keys (art reads sprite_url,
	// not text, but keep the ordering intent explicit).
	art, missing, firstBg := collectArt(projectDir, doc)

	var catalog map[string]string
	var lang, catalogRel string
	if opt.Localize {
		catalog = Localize(doc)
		if lang, err = adpd.Lang(projectDir); err != nil {
			lang = "und"
		}
		catalogRel = "scripts/" + opt.ID + "." + lang + ".json"
	} else {
		StripStableIds(doc) // keys are only needed for the catalog
	}

	lvn, err := json.MarshalIndent(doc, "", " ")
	if err != nil {
		return nil, err
	}

	res := &Result{
		ScriptRel:  "scripts/" + opt.ID + ".lvn",
		Lvn:        lvn,
		Art:        art,
		Stats:      opStats(doc),
		MissingBg:  missing,
		Catalog:    catalog,
		Lang:       lang,
		CatalogRel: catalogRel,
	}
	cover := firstBg // a real first-scene background beats a 404 placeholder
	res.Title = Title{
		ID:       opt.ID,
		Name:     opt.Name,
		Subtitle: opt.Subtitle,
		CoverURL: cover,
		Seasons: []Season{{Chapters: []Chapter{{
			ID:        opt.ID + "-ch1",
			Number:    1,
			ScriptURL: "/content/" + res.ScriptRel,
			BgURL:     cover,
		}}}},
	}
	return res, nil
}

func opStats(doc *articy.Doc) map[string]int {
	out := map[string]int{}
	for _, c := range doc.Script {
		if op, ok := c["op"].(string); ok {
			out[op]++
		}
	}
	return out
}

// placeholder sizes: characters are portrait, backgrounds/props 16:9.
const (
	phCharW, phCharH = 512, 768
	phBgW, phBgH     = 1280, 720
)

// collectArt resolves every sprite_url the staged script references to a file on
// disk under projectDir, returning the bytes keyed to the content path. Character
// sprites (art/*) are matted; backgrounds (bg/*) are copied as-is. Anything with
// no real art gets a labelled grey placeholder (a dummy showing the combination
// name) so the whole novel is visible — every character, pose and background —
// before any art exists. Returns the files, the scene locations still missing real
// art, and a content URL for the first background (the title cover).
func collectArt(projectDir string, doc *articy.Doc) (art []ArtFile, missingBg []string, firstBg string) {
	index := buildAssetIndex(projectDir)
	seen := map[string]bool{}
	add := func(rel string, data []byte) {
		if seen[rel] {
			return
		}
		seen[rel] = true
		art = append(art, ArtFile{Rel: rel, Data: data})
	}

	for _, c := range doc.Script {
		op, _ := c["op"].(string)
		url, _ := c["sprite_url"].(string)
		if url == "" {
			continue
		}
		base := filepath.Base(url) // "Тимур_Обычный.png" / "Двор.jpg"
		label := stem(base)
		switch op {
		case "actor", "obj":
			if p, ok := index[normKey(label)]; ok {
				if data, err := os.ReadFile(p); err == nil {
					if matted, merr := Matte(data); merr == nil {
						add("art/"+base, matted)
					} else {
						add("art/"+base, data) // non-fatal: ship the original
					}
					continue
				}
			}
			add("art/"+base, Placeholder(label, phCharW, phCharH)) // dummy character
		case "bg":
			if p := lookupBg(index, label); p != "" {
				if data, err := os.ReadFile(p); err == nil {
					add("bg/"+base, data)
					if firstBg == "" {
						firstBg = "/content/bg/" + base
					}
					continue
				}
			}
			missingBg = append(missingBg, label)
			add("bg/"+base, Placeholder(label, phBgW, phBgH)) // dummy background
			if firstBg == "" {
				firstBg = "/content/bg/" + base
			}
		}
	}
	sort.Strings(missingBg)
	return art, dedupe(missingBg), firstBg
}

// buildAssetIndex maps normKey(name-without-hash) → file path for every image
// under projectDir.
func buildAssetIndex(projectDir string) map[string]string {
	index := map[string]string{}
	_ = filepath.Walk(projectDir, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return nil
		}
		switch strings.ToLower(filepath.Ext(path)) {
		case ".png", ".jpg", ".jpeg":
			key := normKey(stripHash(stem(filepath.Base(path))))
			if _, exists := index[key]; !exists {
				index[key] = path
			}
		}
		return nil
	})
	return index
}

// lookupBg resolves a scene location to a background file: exact key first, then a
// loose substring match (the disk file often carries extra qualifiers).
func lookupBg(index map[string]string, loc string) string {
	k := normKey(loc)
	if p, ok := index[k]; ok {
		return p
	}
	for key, p := range index {
		if strings.Contains(key, k) || strings.Contains(k, key) {
			return p
		}
	}
	return ""
}

func stem(name string) string { return strings.TrimSuffix(name, filepath.Ext(name)) }

func dedupe(in []string) []string {
	seen := map[string]bool{}
	out := in[:0]
	for _, s := range in {
		if !seen[s] {
			seen[s] = true
			out = append(out, s)
		}
	}
	return out
}
