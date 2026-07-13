package importer

// Bundle import — the "five files → playable novel" pipeline behind the admin's
// one-shot importer. The author picks five things and the server does the rest:
//
//   articy archive (.rar/.zip)  — the articy:draft project (Flow partition → script)
//   backgrounds  (.zip)         — NEW/Cold_<Location>.png → /content/bg
//   heroine      (.zip)         — Cold_Main/…_Hairs_N / …_clothes_N → a wardrobe sprite
//   characters   (.zip)         — Cold_<Name>/Cold_<Name>_<Emotion>.png → emotion sprites
//   variables    (.xlsx)        — variable defaults + a cell-colour emotion legend
//
// RunBundle extracts everything to a staging dir, runs the normal articy import,
// then merges the real background/character/heroine art and the spreadsheet vars
// on top. The asset mergers (MapBackgrounds/MapCharacters/MapHeroine) and the
// spreadsheet parser (ParseVarsXlsx) live in sibling files.

import (
	"archive/zip"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

// BundleInputs are the five source files the admin selects. Any of the four
// art/var inputs may be empty (import proceeds with whatever is provided); the
// articy archive is required.
type BundleInputs struct {
	ArticyArchive  string // .rar or .zip of the exported articy project
	BackgroundsZip string
	HeroineZip     string
	CharactersZip  string
	VarsXlsx       string
}

// RunBundle extracts the five inputs under stageDir, imports the articy flow, and
// (once the asset mergers land) layers the real backgrounds / characters / heroine
// art and the spreadsheet variables on top. Returns a Result ready for
// WriteToContentDir + the manifest merges. contentDir is where art is copied.
func RunBundle(in BundleInputs, contentDir, stageDir string, opt Options) (*Result, error) {
	if in.ArticyArchive == "" {
		return nil, fmt.Errorf("articy archive is required")
	}
	articyRoot, err := extractArchive(in.ArticyArchive, filepath.Join(stageDir, "articy"))
	if err != nil {
		return nil, fmt.Errorf("extract articy: %w", err)
	}
	projectDir, err := findArticyProject(articyRoot)
	if err != nil {
		return nil, err
	}
	// The bundle keys off a novel-authoring Template (role names, wardrobe layout,
	// audio cues, …). Resolve it once and thread the SAME instance through Run (via
	// opt) and PostProcessBundle, so a project's conventions are applied uniformly.
	tpl := opt.Template.resolve()
	opt.Template = tpl

	// Spreadsheet: the Rosetta Stone. Its cell-colour legend feeds the emotion
	// import; its roster / locations / wardrobe drive the post-process wiring below.
	var xd XlsxData
	if in.VarsXlsx != "" {
		if d, err := ParseVarsXlsx(in.VarsXlsx); err == nil {
			xd = d
			if opt.EmotionColors == nil {
				opt.EmotionColors = map[string]string{}
			}
			// The xlsx legend is authoritative (11 emotions, and its colors match
			// the story markers). Lowercase the labels so `emotion=<x>` lines up
			// with the character art axis values (Idle→idle, Happy→happy, …).
			for hex, emo := range xd.EmotionColors {
				opt.EmotionColors[strings.ToUpper(hex)] = strings.ToLower(emo)
			}
		}
	}

	// The protagonist is shown by DEFAULT (she's never explicitly `actor`-ed in the
	// script — that's the convention) but has no articy avatar. Inject her into the
	// AutoStage cast under her dialogue label so she's staged (left/POV) like anyone
	// else; her layered entity resolves via the "Главный_герой" alias (bundle_wire).
	if xd.Protagonist != nil && strings.TrimSpace(xd.Protagonist.TechName) != "" {
		if opt.ExtraCast == nil {
			opt.ExtraCast = map[string]string{}
		}
		if label := strings.TrimSpace(tpl.Staging.ProtagonistLabel); label != "" {
			opt.ExtraCast[label] = xd.Protagonist.TechName
		}
	}

	res, err := Run(projectDir, opt)
	if err != nil {
		return nil, err
	}
	if res.Sprites == nil {
		res.Sprites = map[string]any{}
	}

	// Real art from the three zips, layered over the auto-built placeholders.
	if in.BackgroundsZip != "" {
		if dir, err := extractArchive(in.BackgroundsZip, filepath.Join(stageDir, "bg")); err == nil {
			_, _ = MapBackgrounds(dir, contentDir)
		}
	}
	// Characters (incl. the protagonist/heroine — she is a roster row too) are built
	// sheet-driven from the CHARACTERS archive: MapCharacters resolves each exact
	// emotion/clothes/hair art stem the spreadsheet names and emits a layered entity.
	// Her folder carries her Hairs/clothes wardrobe art, so no separate heroine pass
	// is needed (the HeroineZip is a byte-identical duplicate of her character folder).
	if in.CharactersZip != "" {
		if dir, err := extractArchive(in.CharactersZip, filepath.Join(stageDir, "chars")); err == nil {
			if chars, warns, err := MapCharacters(dir, contentDir, xd, tpl); err == nil {
				for id, ent := range chars {
					res.Sprites[id] = ent
				}
				res.Warnings = append(res.Warnings, warns...)
			}
		}
	}

	// Wire the spreadsheet mappings over the imported result: connect the story
	// actors to their emotion-art entities, stamp outfit={Wardrobe.<Name>}, build
	// each character's wardrobe block, rewrite backgrounds, and turn Music/Sound
	// cues into audio ops.
	PostProcessBundle(res, xd, contentDir, tpl)
	// STRUCTURAL INVARIANT: meta passes (template wiring, wardrobe swaps,
	// speaker renames) must never break a script the base converter produced
	// valid — live-hit when the wardrobe swap dropped labels that tail branch
	// bodies still jumped to. Validate every script after the LAST rewrite and
	// surface errors as import warnings (the author sees them in the report
	// instead of the runtime refusing the chapter later).
	for _, sf := range res.Scripts {
		if !strings.HasSuffix(sf.Rel, ".lvn") {
			continue
		}
		vdoc, verr := lvn.Parse(sf.Data)
		if verr != nil {
			res.Warnings = append(res.Warnings, fmt.Sprintf("%s: post-import parse failed: %v", sf.Rel, verr))
			continue
		}
		for _, is := range lvn.Validate(vdoc) {
			if is.Sev == lvn.SevError {
				res.Warnings = append(res.Warnings, fmt.Sprintf("%s: post-import validation: %v", sf.Rel, is))
			}
		}
	}
	// Seed the game's declared variables (wardrobe indices, stats) as defaults at
	// the top of the first chapter, so the novel opens with the right state.
	if len(res.Scripts) > 0 && len(xd.Vars) > 0 {
		prependVarDefaults(&res.Scripts[0], xd.Vars)
	}
	// A bundle-imported novel is SELF-CONTAINED — its own protagonist, cast and
	// wardrobe, NOT our shared heroine. Mark it standalone and name its own hero so
	// the app resolves title.hero (not manifest.hero) for it.
	res.Title.Type = "standalone"
	if xd.Protagonist != nil && xd.Protagonist.StoryName != "" {
		res.Title.Hero = Slug(xd.Protagonist.StoryName)
	}
	return res, nil
}

// prependVarDefaults inserts a `set default` op for each declared variable at the
// front of a chapter's op list — the engine only applies a default when the var
// is still unset, so this is safe over the script's own logic.
func prependVarDefaults(sf *ScriptFile, vars []VarDecl) {
	ops, rewrap, ok := decodeScriptOps(sf.Data)
	if !ok {
		return
	}
	seed := make([]map[string]any, 0, len(vars))
	seen := map[string]bool{}
	for _, v := range vars {
		if v.Key == "" || seen[v.Key] {
			continue
		}
		seen[v.Key] = true
		op := map[string]any{"op": "set", "default": true, "key": v.Key}
		if f, err := strconv.ParseFloat(strings.TrimSpace(v.Default), 64); err == nil {
			op["value"] = f
		} else if v.Default == "true" || v.Default == "false" {
			op["value"] = v.Default == "true"
		} else {
			op["value"] = v.Default
		}
		seed = append(seed, op)
	}
	if len(seed) == 0 {
		return
	}
	ops = append(seed, ops...)
	if b, err := json.Marshal(rewrap(ops)); err == nil {
		sf.Data = b
	}
}

// copyFileOnce copies src→dst unless dst already exists with the same size.
func copyFileOnce(src, dst string) error {
	si, err := os.Stat(src)
	if err != nil {
		return err
	}
	if di, err := os.Stat(dst); err == nil && di.Size() == si.Size() {
		return nil
	}
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

// extractArchive unpacks src (.rar via the `unar` CLI, .zip via the stdlib) into
// a fresh dst directory and returns dst. Other extensions are treated as an
// already-extracted directory and returned as-is.
func extractArchive(src, dst string) (string, error) {
	if src == "" {
		return "", nil
	}
	if fi, err := os.Stat(src); err == nil && fi.IsDir() {
		return src, nil // already extracted
	}
	if err := os.MkdirAll(dst, 0o755); err != nil {
		return "", err
	}
	switch strings.ToLower(filepath.Ext(src)) {
	case ".zip":
		return dst, unzipTo(src, dst)
	case ".rar":
		// No stdlib RAR; shell out. `unar` ships on the import host (brew/apt).
		unar, err := exec.LookPath("unar")
		if err != nil {
			return "", fmt.Errorf("unar not found (needed for .rar): %w", err)
		}
		cmd := exec.Command(unar, "-quiet", "-force-overwrite", "-output-directory", dst, src)
		if out, err := cmd.CombinedOutput(); err != nil {
			return "", fmt.Errorf("unar %s: %v: %s", filepath.Base(src), err, out)
		}
		return dst, nil
	default:
		return "", fmt.Errorf("unsupported archive %q (want .zip or .rar)", filepath.Ext(src))
	}
}

// unzipTo extracts a .zip into dst, guarding against Zip-Slip.
func unzipTo(src, dst string) error {
	zr, err := zip.OpenReader(src)
	if err != nil {
		return err
	}
	defer zr.Close()
	root := filepath.Clean(dst)
	for _, f := range zr.File {
		p := filepath.Clean(filepath.Join(root, f.Name))
		if p != root && !strings.HasPrefix(p, root+string(os.PathSeparator)) {
			return fmt.Errorf("zip entry escapes dir: %s", f.Name)
		}
		if f.FileInfo().IsDir() {
			if err := os.MkdirAll(p, 0o755); err != nil {
				return err
			}
			continue
		}
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			return err
		}
		rc, err := f.Open()
		if err != nil {
			return err
		}
		w, err := os.Create(p)
		if err != nil {
			rc.Close()
			return err
		}
		_, cerr := io.Copy(w, rc)
		w.Close()
		rc.Close()
		if cerr != nil {
			return cerr
		}
	}
	return nil
}

// findArticyProject locates the articy project root under an extracted tree — the
// directory that holds a `Partitions/'Flow'-…adpd`. articy exports nest the
// project one level down (e.g. Cold_13_08_25/Partitions/…), so walk to find it.
func findArticyProject(root string) (string, error) {
	if root == "" {
		return "", fmt.Errorf("empty articy root")
	}
	var found string
	err := filepath.WalkDir(root, func(path string, d os.DirEntry, err error) error {
		if err != nil || found != "" {
			return nil
		}
		if d.IsDir() && d.Name() == "Partitions" {
			// does it contain a Flow partition?
			entries, _ := os.ReadDir(path)
			for _, e := range entries {
				if strings.Contains(e.Name(), "'Flow'") && strings.HasSuffix(e.Name(), ".adpd") {
					found = filepath.Dir(path) // the project root is the parent of Partitions/
					return filepath.SkipAll
				}
			}
		}
		return nil
	})
	if err != nil {
		return "", err
	}
	if found == "" {
		return "", fmt.Errorf("no articy Flow partition found under %s", root)
	}
	return found, nil
}
