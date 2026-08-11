// lvnconv is the narrative transcoder — "ffmpeg for visual novels".
//
// It takes a script in any supported authoring format and compiles it down to
// .lvn, the universal container the runtime plays. New source formats plug in
// as front-ends; the runtime never changes.
//
//	lvnconv convert -i chapter.ink   -o chapter.lvn
//	lvnconv convert -i export.json   -o chapter.lvn   -dialogue Ch1
//	lvnconv validate chapter.lvn
//	lvnconv probe   chapter.lvn
//	lvnconv walk    chapter.lvns
//
// Format is inferred from the input extension (.ink → ink, .json → articy,
// .lvn → already a container) and can be forced with -f.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/adpd"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/deps"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/ink"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

// newFlagSet builds a subcommand flag set that prints usage to stderr.
func newFlagSet(name string) *flag.FlagSet {
	fs := flag.NewFlagSet(name, flag.ExitOnError)
	fs.SetOutput(os.Stderr)
	return fs
}

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}
	switch os.Args[1] {
	case "convert":
		cmdConvert(os.Args[2:])
	case "import":
		cmdImport(os.Args[2:])
	case "detect":
		cmdDetect(os.Args[2:])
	case "conflicts":
		cmdConflicts(os.Args[2:])
	case "resync-lvns":
		cmdResyncLvns(os.Args[2:])
	case "validate":
		cmdValidate(os.Args[2:])
	case "probe":
		cmdProbe(os.Args[2:])
	case "walk":
		cmdWalk(os.Args[2:])
	case "optimize":
		cmdOptimize(os.Args[2:])
	case "locale":
		cmdLocale(os.Args[2:])
	case "deps":
		cmdDeps(os.Args[2:])
	case "-h", "--help", "help":
		usage()
	default:
		fmt.Fprintf(os.Stderr, "unknown command %q\n\n", os.Args[1])
		usage()
		os.Exit(2)
	}
}

func usage() {
	fmt.Fprint(os.Stderr, `lvnconv — narrative transcoder (ffmpeg for visual novels)

usage:
  lvnconv convert  -i <in> [-o <out.lvn>] [-f ink|articy|adpd] [-dialogue <name>]
  lvnconv convert  <articy-project-dir> [-start <ordinal>] [-max <N>]
  lvnconv detect   <articy-project-dir> [-template <name>] [-template-dir <dir>]
  lvnconv conflicts -i <content-dir> [-rel <path> [-choice mine|incoming]] [-diff]
  lvnconv validate <in.lvn> [-strict] [-ext-grammar file.json]
  lvnconv probe    <in.lvn>
  lvnconv walk     [-depth N] [-strict] [-json] <in.lvn|in.lvns>…
  lvnconv optimize -i <content-dir> [-max 2560] [-quality 85] [-apply] [-rewrite-refs]
  lvnconv locale   -lang <code>[,<code>…] [-check] [-prune] <script.lvns|.lvn>…
  lvnconv deps     sync|update|list [-C <dir>]
  lvnconv deps     add <@scope/pkg> <github:owner/repo@tag[#subdir] | file:path> [-C <dir>]

convert  compile a source script to a .lvn container (stdout if -o omitted)
validate run structural checks on a .lvn (unknown op, dangling jumps, dup labels)
         -strict treats lint warnings (unused labels) as failures
         -ext-grammar declares the project's host ops ("ext ..." lines) so
         they validate like built-ins; default: ext-grammar.json beside the file
detect   preview a Template's classification against a project WITHOUT
         importing: every speaker's role/art/line-count, scene-marker and
         emotion-legend hit rates, heuristic alias-collision suggestions —
         prints a DetectReport as JSON on stdout
probe    print a one-line summary of a .lvn (counts of ops, labels, choices)
walk     play EVERY path (all choices, both sides of every condition) and report
         unreachable content — the dead blocks a random soak run never finds
conflicts what a re-import parked instead of overwriting (a file edited by
         hand AND regenerated differently): both versions' size/time and, for
         text, a unified diff. -choice commits one side — "mine" discards the
         parked version, "incoming" installs it — validating a .lvn first and
         recording the winner as the new import baseline, so the next import
         does not re-raise the same file
optimize shrink oversized images (cap + PNG/JPEG recompress); Spine atlas pages
         only get losslessly recompressed, never resized (frame-packed atlases
         bleed under any resample). Dry run by default; -apply writes; add
         -rewrite-refs to fix manifest.json/.lvns after a png→jpg conversion.
locale   build/refresh the per-language string catalogs the runtime loads
         beside a script (<script>.<lang>.json): say lines and speaker names,
         choice options, input prompts, "text" labels. Existing translations
         are kept, new lines are prefilled with the source text; -check only
         reports coverage (exit 1 on missing keys), -prune drops stale keys.
         List every language in manifest.json "languages" to show the picker.
`)
}

// detectFormat infers the front-end from the file extension.
func detectFormat(path string) string {
	switch strings.ToLower(filepath.Ext(path)) {
	case ".ink":
		return "ink"
	case ".lvns":
		return "lvns"
	case ".json", ".articy":
		return "articy"
	case ".adpd":
		return "adpd"
	case ".lvn":
		return "lvn"
	}
	// A directory is treated as a raw articy:draft binary project.
	if info, err := os.Stat(path); err == nil && info.IsDir() {
		return "adpd"
	}
	return ""
}

// cmdImport is the one-shot pipeline behind the IDE's "Import articy" button,
// exposed on the CLI: an extracted .adpd project directory in, a playable title
// (script + matted art + manifest entry) written into a content root out.
//
//	lvnconv import <project-dir> -id my-novel -name "My Novel" -content ./server/content
func cmdImport(args []string) {
	var lead string
	if len(args) > 0 && !strings.HasPrefix(args[0], "-") {
		lead, args = args[0], args[1:]
	}
	fs := newFlagSet("import")
	dir := fs.String("i", "", "extracted articy:draft (.adpd) project directory")
	content := fs.String("content", "./server/content", "content root to write into")
	id := fs.String("id", "imported", "title id / script base name")
	name := fs.String("name", "", "display name (default: id)")
	subtitle := fs.String("subtitle", "Импорт из articy:draft (.adpd)", "carousel subtitle")
	start := fs.Int("start", -1, "start node ordinal (default: story opening)")
	maxNodes := fs.Int("max", 0, "cap chapter at N nodes (0 = no cap)")
	localize := fs.Bool("localize", false, "extract text into a <id>.<lang>.json catalog (i18n)")
	_ = fs.Parse(args)
	if *dir == "" {
		if lead != "" {
			*dir = lead
		} else if fs.NArg() == 1 {
			*dir = fs.Arg(0)
		} else {
			die("import: <project-dir> is required")
		}
	}

	res, err := importer.Run(*dir, importer.Options{
		ID: *id, Name: *name, Subtitle: *subtitle,
		Start: *start, Max: *maxNodes, AutoStage: true, Localize: *localize,
	})
	if err != nil {
		die("import: " + err.Error())
	}
	writeRep, err := importer.WriteToContentDir(*content, res)
	if err != nil {
		die("import: " + err.Error())
	}
	// Re-import is a three-way merge (importer/baseline.go): say plainly what
	// was left alone and what disagreed, or the author never learns that their
	// edit survived — or that it collided.
	if writeRep != nil {
		fmt.Printf("files: %d new, %d updated, %d unchanged, %d kept (hand-edited)\n",
			writeRep.Count(importer.StatusNew), writeRep.Count(importer.StatusUpdated),
			writeRep.Count(importer.StatusUnchanged), writeRep.Count(importer.StatusKeptLocal))
		if n := len(writeRep.Conflicts); n > 0 {
			fmt.Printf("CONFLICTS (%d): hand-edited here AND regenerated differently — nothing was overwritten.\n", n)
			for _, rel := range writeRep.Conflicts {
				fmt.Printf("  %s  (new version parked at %s.incoming)\n", rel, rel)
			}
		}
	}
	fmt.Fprintf(os.Stderr, "imported %q → %s (%d ops, %d art files, %d bg unmatched)\n",
		*id, res.ScriptRel, sumStats(res.Stats), len(res.Art), len(res.MissingBg))
	printLinearizeReport(res.Linearize)
	if *localize {
		fmt.Fprintf(os.Stderr, "i18n: %d strings → %s (lang=%s)\n", len(res.Catalog), res.CatalogRel, res.Lang)
	}
}

// cmdDetect previews the import Template's classification against a project
// WITHOUT importing: every speaker's role/art/line-count, scene-marker and
// emotion-legend hit rates, alias-collision suggestions — the tool an author
// runs (or the panel's mapper screen calls over HTTP) before deciding how to
// author a Template for a novel the built-in default doesn't already fit.
//
//	lvnconv detect <project-dir> [-template <name>] [-template-dir <dir>]
func cmdDetect(args []string) {
	var lead string
	if len(args) > 0 && !strings.HasPrefix(args[0], "-") {
		lead, args = args[0], args[1:]
	}
	fs := newFlagSet("detect")
	template := fs.String("template", "", "import template name/path (default: built-in)")
	templateDir := fs.String("template-dir", "", "directory to resolve a bare -template name against")
	_ = fs.Parse(args)
	dir := lead
	if dir == "" && fs.NArg() == 1 {
		dir = fs.Arg(0)
	}
	if dir == "" {
		die("detect: <project-dir> is required")
	}
	tpl, err := importer.ResolveTemplate(*template, *templateDir)
	if err != nil {
		die("detect: " + err.Error())
	}
	rep, err := importer.DetectRoles(dir, tpl)
	if err != nil {
		die("detect: " + err.Error())
	}
	os.Stdout.Write(mustJSON(rep))
}

// cmdResyncLvns regenerates every .lvns sidecar in a content dir from its
// compiled .lvn — the one-shot repair for sidecars generated BEFORE the
// round-trip fixes (the panel's "Save to app" compiles the SIDECAR, so a
// stale one silently strips audio/wardrobe/wallet_cost from the .lvn on the
// author's next save). A sidecar is only replaced when the regenerated
// source passes VerifyLvnsRoundTrip; drifting ones are reported and left
// untouched (better a stale-but-known file than a fresh lie).
//
//	lvnconv resync-lvns <content-dir> [-apply]
func cmdResyncLvns(args []string) {
	var lead string
	if len(args) > 0 && !strings.HasPrefix(args[0], "-") {
		lead, args = args[0], args[1:]
	}
	fs := newFlagSet("resync-lvns")
	apply := fs.Bool("apply", false, "write regenerated sidecars (default: dry-run report)")
	_ = fs.Parse(args)
	dir := lead
	if dir == "" && fs.NArg() == 1 {
		dir = fs.Arg(0)
	}
	if dir == "" {
		die("resync-lvns: <content-dir> is required")
	}
	scripts := filepath.Join(dir, "scripts")
	entries, err := os.ReadDir(scripts)
	if err != nil {
		die("resync-lvns: " + err.Error())
	}
	clean, dirty, skipped := 0, 0, 0
	for _, e := range entries {
		name := e.Name()
		if !strings.HasSuffix(name, ".lvn") {
			continue
		}
		lvnsPath := filepath.Join(scripts, strings.TrimSuffix(name, ".lvn")+".lvns")
		if _, err := os.Stat(lvnsPath); err != nil {
			skipped++ // no sidecar — hand-authored .lvn or non-sidecar content
			continue
		}
		data, err := os.ReadFile(filepath.Join(scripts, name))
		if err != nil {
			fmt.Fprintf(os.Stderr, "  %-32s read error: %v\n", name, err)
			continue
		}
		var doc articy.Doc
		if err := json.Unmarshal(data, &doc); err != nil {
			fmt.Fprintf(os.Stderr, "  %-32s parse error: %v\n", name, err)
			continue
		}
		out := importer.ToLvns(&doc)
		warnings := importer.VerifyLvnsRoundTrip(doc.Script, out)
		if len(warnings) > 0 {
			dirty++
			fmt.Fprintf(os.Stderr, "  %-32s DRIFT (left untouched):\n", name)
			for _, w := range warnings {
				fmt.Fprintf(os.Stderr, "      %s\n", w)
			}
			continue
		}
		clean++
		if *apply {
			if err := os.WriteFile(lvnsPath, out, 0o644); err != nil {
				die("resync-lvns: write " + lvnsPath + ": " + err.Error())
			}
		}
	}
	mode := "dry-run"
	if *apply {
		mode = "applied"
	}
	fmt.Fprintf(os.Stderr, "resync-lvns (%s): %d clean sidecars regenerated, %d drifting (untouched), %d without sidecar\n",
		mode, clean, dirty, skipped)
}

// printLinearizeReport surfaces the adpd cascade's choice on stderr: silent
// fallback to a coarser linearizer (or a silently dropped chapter) is a
// precision loss the author must see at import time, not in playtesting.
func printLinearizeReport(rep *adpd.LinearizeReport) {
	if rep == nil {
		return
	}
	line := "linearizer: " + rep.Algorithm
	if rep.Chapters > 0 {
		line += fmt.Sprintf(" (%d chapters)", rep.Chapters)
	}
	if rep.Emittable > 0 {
		line += fmt.Sprintf("; pin-flow: %d emittable, %d trapped (%.1f%%)",
			rep.Emittable, rep.Trapped, 100*float64(rep.Trapped)/float64(rep.Emittable))
	}
	if n := rep.Reachable + rep.Stitched; n > 0 {
		line += fmt.Sprintf("; connectivity: %d/%d read from articy (%.1f%%), %d stitched",
			rep.Reachable, n, 100*float64(rep.Reachable)/float64(n), rep.Stitched)
	}
	if rep.Jumps > 0 {
		line += fmt.Sprintf("; jumps: %d/%d resolved", rep.JumpsResolved, rep.Jumps)
	}
	fmt.Fprintln(os.Stderr, line)
	for _, f := range rep.Fallbacks {
		fmt.Fprintln(os.Stderr, "  fallback: "+f)
	}
	// Предупреждения — про сюжет, который импорт дотянул наугад или потерял.
	// Тихая потеря здесь дороже всего: именно так треть одной партнёрской главы была
	// недостижима на проде и заметили это случайно.
	for _, w := range rep.Warnings {
		fmt.Fprintln(os.Stderr, "  warning: "+w)
	}
}

func sumStats(m map[string]int) int {
	n := 0
	for _, v := range m {
		n += v
	}
	return n
}

func cmdConvert(args []string) {
	// Allow a leading positional input before flags: `convert <in> -o out.lvn`.
	// (Go's flag package stops at the first non-flag, so pull it off first.)
	var lead string
	if len(args) > 0 && !strings.HasPrefix(args[0], "-") {
		lead, args = args[0], args[1:]
	}

	fs := newFlagSet("convert")
	in := fs.String("i", "", "input file (or an articy:draft project directory)")
	out := fs.String("o", "", "output .lvn (default: stdout)")
	format := fs.String("f", "", "force input format: ink | articy | adpd")
	dialogue := fs.String("dialogue", "", "articy: Dialogue to convert (default: the only one)")
	start := fs.Int("start", -1, "adpd: start node ordinal (default: the story opening)")
	maxNodes := fs.Int("max", 0, "adpd: cap the chapter at N nodes (0 = no cap)")
	localize := fs.Bool("localize", false, "adpd: emit a text-id .lvn + a <out>.<lang>.json string catalog (i18n)")
	autostage := fs.Bool("autostage", false, "adpd: auto-emit staging — a bg per scene marker and an actor per speaking character")
	_ = fs.Parse(args)
	if *in == "" {
		switch {
		case lead != "":
			*in = lead
		case fs.NArg() == 1: // trailing positional: lvnconv convert chapter.ink
			*in = fs.Arg(0)
		default:
			die("convert: -i <input> is required")
		}
	}

	f := *format
	if f == "" {
		f = detectFormat(*in)
	}

	// adpd takes a path (a directory or a .adpd file), not pre-read bytes: it
	// reconstructs the articy model from the binary, then reuses the articy back-end.
	if f == "adpd" {
		js, rep, err := adpd.BuildExportJSONReport(*in, *start, *maxNodes)
		if err != nil {
			die("adpd: " + err.Error())
		}
		printLinearizeReport(&rep)
		doc, err := articy.Convert(js, *dialogue)
		if err != nil {
			die("adpd: " + err.Error())
		}
		// Order matters: auto-staging reads inline say text (scene markers), so it
		// must run before localization swaps text for text_id keys.
		if *autostage {
			cast, err := adpd.Cast(*in)
			if err != nil {
				die("adpd: " + err.Error())
			}
			importer.AutoStage(doc, cast, nil)
		}
		if *localize {
			catalog := importer.Localize(doc)
			writeOut(*out, mustJSON(doc))
			lang, _ := adpd.Lang(*in)
			writeCatalog(*out, lang, catalog)
		} else {
			importer.StripStableIds(doc)
			writeOut(*out, mustJSON(doc))
		}
		return
	}

	src, err := os.ReadFile(*in)
	if err != nil {
		die(err.Error())
	}

	var data []byte
	switch f {
	case "ink":
		doc, err := ink.Convert(string(src))
		if err != nil {
			die("ink: " + err.Error())
		}
		data = mustJSON(doc)
	case "lvns":
		// ConvertFile, а не Convert: только у него есть путь, относительно
		// которого резолвятся include (internal/lvns/include.go).
		doc, err := lvns.ConvertFile(*in)
		if err != nil {
			die("lvns: " + err.Error())
		}
		data = mustJSON(doc)
	case "articy":
		doc, err := articy.Convert(src, *dialogue)
		if err != nil {
			die("articy: " + err.Error())
		}
		data = mustJSON(doc)
	case "lvn":
		die("convert: input is already a .lvn — nothing to do (use validate/probe)")
	default:
		die(fmt.Sprintf("convert: cannot infer format from %q — pass -f ink|articy", *in))
	}

	writeOut(*out, data)
}

// writeOut sends bytes to a file, or stdout when out is empty.
func writeOut(out string, data []byte) {
	if out == "" {
		os.Stdout.Write(data)
		return
	}
	if err := os.WriteFile(out, data, 0o644); err != nil {
		die(err.Error())
	}
}

// writeCatalog writes the string catalog next to the .lvn as <name>.<lang>.json.
func writeCatalog(out, lang string, catalog map[string]string) {
	data := mustJSON(catalog)
	if out == "" {
		fmt.Fprintf(os.Stderr, "i18n: %d strings (no -o, catalog not written)\n", len(catalog))
		return
	}
	path := strings.TrimSuffix(out, ".lvn") + "." + lang + ".json"
	if err := os.WriteFile(path, data, 0o644); err != nil {
		die(err.Error())
	}
	fmt.Fprintf(os.Stderr, "i18n: %d strings → %s (lang=%s)\n", len(catalog), path, lang)
}

func cmdValidate(args []string) {
	fs := newFlagSet("validate")
	strict := fs.Bool("strict", false, "treat lint warnings as failures")
	extPath := fs.String("ext-grammar", "", "host-op declaration (ext-grammar.json); default: auto-detect beside the file")
	_ = fs.Parse(args)
	if fs.NArg() != 1 {
		die("validate: expected one <in.lvn>")
	}
	doc := loadLvn(fs.Arg(0))

	// Host-op declarations widen the known world per project: explicit flag
	// first, else the conventional sidecar beside the file (or one level up).
	var ext *lvn.ExtGrammar
	if *extPath != "" {
		g, err := lvn.LoadExtGrammar(*extPath)
		if err != nil {
			die("validate: " + err.Error())
		}
		ext = g
	} else if g, found, err := lvn.FindExtGrammar(fs.Arg(0)); err != nil {
		die("validate: " + err.Error())
	} else if g != nil {
		ext = g
		fmt.Fprintf(os.Stderr, "ext-grammar: %s (%d host op(s))\n", found, len(g.Ops))
	}

	issues := lvn.ValidateExt(doc, ext)
	var errs, warns int
	for _, is := range issues {
		warn := is.Sev != lvn.SevError
		if warn {
			warns++
			fmt.Fprintln(os.Stderr, "warning: "+is.String())
		} else {
			errs++
			fmt.Fprintln(os.Stderr, "error: "+is.String())
		}
	}
	if errs > 0 || (*strict && warns > 0) {
		fmt.Fprintf(os.Stderr, "FAIL: %d error(s), %d warning(s)\n", errs, warns)
		os.Exit(1)
	}
	fmt.Fprintf(os.Stderr, "OK: %d command(s), %d warning(s)\n", len(doc.Script), warns)
}

func cmdProbe(args []string) {
	fs := newFlagSet("probe")
	_ = fs.Parse(args)
	if fs.NArg() != 1 {
		die("probe: expected one <in.lvn>")
	}
	doc := loadLvn(fs.Arg(0))

	counts := map[string]int{}
	for _, c := range doc.Script {
		counts[c.Op()]++
	}
	scene := doc.Scene
	if scene == "" {
		scene = "(none)"
	}
	fmt.Printf("scene=%s commands=%d say=%d choice=%d label=%d goto=%d if=%d bg=%d actor=%d\n",
		scene, len(doc.Script), counts["say"], counts["choice"], counts["label"],
		counts["goto"], counts["if"], counts["bg"], counts["actor"])
}

func loadLvn(path string) *lvn.Doc {
	data, err := os.ReadFile(path)
	if err != nil {
		die(err.Error())
	}
	doc, err := lvn.Parse(data)
	if err != nil {
		die(err.Error())
	}
	return doc
}

func mustJSON(v any) []byte {
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		die(err.Error())
	}
	return append(data, '\n')
}

func die(msg string) {
	fmt.Fprintln(os.Stderr, "lvnconv: "+msg)
	os.Exit(1)
}

// cmdDeps — пакетная система: vendor в lvns_packages/ по lvns.package.json и
// lvns.lock. Сеть трогает ТОЛЬКО эта команда; convert собирается оффлайн.
func cmdDeps(args []string) {
	if len(args) == 0 {
		die(`deps: жду sync | update | add <@scope/pkg> <ref> | list`)
	}
	sub, rest := args[0], args[1:]
	root := "."
	// -C <dir> в любом месте хвоста
	var pos []string
	for i := 0; i < len(rest); i++ {
		if rest[i] == "-C" && i+1 < len(rest) {
			root = rest[i+1]
			i++
			continue
		}
		pos = append(pos, rest[i])
	}
	var err error
	switch sub {
	case "sync":
		err = deps.Sync(root, false)
	case "update":
		err = deps.Sync(root, true)
	case "add":
		if len(pos) != 2 {
			die(`deps add: жду имя и ссылку — lvnconv deps add "@scope/pkg" "github:owner/repo@v1.0.0"`)
		}
		err = deps.Add(root, pos[0], pos[1])
	case "list":
		err = deps.List(root, os.Stdout)
	default:
		die(fmt.Sprintf("deps: неизвестная подкоманда %q", sub))
	}
	if err != nil {
		die("deps " + sub + ": " + err.Error())
	}
}
