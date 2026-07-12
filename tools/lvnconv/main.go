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
	case "validate":
		cmdValidate(os.Args[2:])
	case "probe":
		cmdProbe(os.Args[2:])
	case "optimize":
		cmdOptimize(os.Args[2:])
	case "locale":
		cmdLocale(os.Args[2:])
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
  lvnconv validate <in.lvn> [-strict] [-ext-grammar file.json]
  lvnconv probe    <in.lvn>
  lvnconv optimize -i <content-dir> [-max 2560] [-quality 85] [-apply] [-rewrite-refs]
  lvnconv locale   -lang <code>[,<code>…] [-check] [-prune] <script.lvns|.lvn>…

convert  compile a source script to a .lvn container (stdout if -o omitted)
validate run structural checks on a .lvn (unknown op, dangling jumps, dup labels)
         -strict treats lint warnings (unused labels) as failures
         -ext-grammar declares the project's host ops ("ext ..." lines) so
         they validate like built-ins; default: ext-grammar.json beside the file
probe    print a one-line summary of a .lvn (counts of ops, labels, choices)
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
//	lvnconv import <project-dir> -id soviet -name "Советское воспитание" -content ./server/content
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
	if err := importer.WriteToContentDir(*content, res); err != nil {
		die("import: " + err.Error())
	}
	fmt.Fprintf(os.Stderr, "imported %q → %s (%d ops, %d art files, %d bg unmatched)\n",
		*id, res.ScriptRel, sumStats(res.Stats), len(res.Art), len(res.MissingBg))
	printLinearizeReport(res.Linearize)
	if *localize {
		fmt.Fprintf(os.Stderr, "i18n: %d strings → %s (lang=%s)\n", len(res.Catalog), res.CatalogRel, res.Lang)
	}
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
	fmt.Fprintln(os.Stderr, line)
	for _, f := range rep.Fallbacks {
		fmt.Fprintln(os.Stderr, "  fallback: "+f)
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
		doc, err := lvns.Convert(string(src))
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
