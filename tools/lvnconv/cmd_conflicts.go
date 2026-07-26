package main

// cmd_conflicts.go — resolving a re-import conflict without the panel.
//
// `lvnconv import` refuses to overwrite a file the author edited and the
// exporter regenerated differently: the author's file stays, the new version is
// parked as `<file>.incoming` (importer/baseline.go). Somebody then has to
// choose. The server exposes that choice over /v1/admin/import-conflicts; this
// is the same operation on a content directory, for the editor who ran the
// import from a terminal, for a machine with no panel deployed, and for the
// person debugging why an import keeps re-raising the same file.
//
//	lvnconv conflicts -i ./server/content                    # what is waiting
//	lvnconv conflicts -i ./server/content -rel scripts/a.lvn # the full diff
//	lvnconv conflicts -i ./server/content -rel scripts/a.lvn -choice incoming
//
// It shares the resolution primitive with the server (importer.ResolveConflict)
// so both paths pick the same side, drop the parked file and — the part that
// actually matters — stamp the winner into the import baseline, without which
// the next import re-opens the conflict that was just closed.

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

func cmdConflicts(args []string) {
	var lead string
	if len(args) > 0 && !strings.HasPrefix(args[0], "-") {
		lead, args = args[0], args[1:]
	}
	fs := newFlagSet("conflicts")
	content := fs.String("i", "./server/content", "content root to inspect")
	rel := fs.String("rel", "", "one conflicted path (content-relative)")
	choice := fs.String("choice", "", "resolve it: mine (keep the file on disk) | incoming (install the parked version)")
	title := fs.String("title", "", "baseline title id, when it cannot be inferred from the path")
	showDiff := fs.Bool("diff", false, "print the diff for every listed conflict")
	asJSON := fs.Bool("json", false, "machine-readable output")
	_ = fs.Parse(args)
	if lead != "" {
		*content = lead
	}

	if *choice != "" {
		resolveOne(*content, *rel, *choice, *title, *asJSON)
		return
	}
	listConflicts(*content, *rel, *showDiff, *asJSON)
}

func listConflicts(content, rel string, showDiff, asJSON bool) {
	all, err := importer.ScanConflicts(content)
	if err != nil {
		die("conflicts: " + err.Error())
	}
	if rel != "" {
		rel = strings.TrimSuffix(rel, importer.IncomingSuffix)
		var one []importer.Conflict
		for _, c := range all {
			if c.Rel == rel {
				one = append(one, c)
			}
		}
		if len(one) == 0 {
			die("conflicts: nothing parked at " + rel)
		}
		all, showDiff = one, true
	}

	if asJSON {
		type row struct {
			importer.Conflict
			Diff     string `json:"diff,omitempty"`
			DiffNote string `json:"diff_note,omitempty"`
		}
		out := make([]row, 0, len(all))
		for _, c := range all {
			r := row{Conflict: c}
			if showDiff {
				r.Diff, r.DiffNote = importer.ConflictDiff(content, c, 4000)
			}
			out = append(out, r)
		}
		os.Stdout.Write(mustJSON(map[string]any{"count": len(out), "conflicts": out}))
		return
	}

	if len(all) == 0 {
		fmt.Printf("no import conflicts in %s\n", content)
		return
	}
	fmt.Printf("%d conflict(s) in %s — the file on disk was kept, the new version is parked beside it\n\n", len(all), content)
	for _, c := range all {
		fmt.Printf("  %s\n", c.Rel)
		fmt.Printf("    mine     %s\n", describeSide(c.Mine))
		fmt.Printf("    incoming %s  (%s)\n", describeSide(c.Incoming), c.IncomingRel)
		if len(c.Titles) > 0 {
			fmt.Printf("    baseline %s\n", strings.Join(c.Titles, ", "))
		}
		if showDiff {
			diff, note := importer.ConflictDiff(content, c, 4000)
			if note != "" {
				fmt.Printf("    %s\n", note)
			}
			for _, line := range strings.Split(strings.TrimRight(diff, "\n"), "\n") {
				if line != "" {
					fmt.Printf("    %s\n", line)
				}
			}
		}
		fmt.Println()
	}
	fmt.Printf("resolve with:\n  lvnconv conflicts -i %s -rel <path> -choice mine|incoming\n", content)
}

func describeSide(s importer.FileSide) string {
	if !s.Exists {
		return "(missing)"
	}
	return fmt.Sprintf("%8d bytes  %s", s.Size, s.Modified)
}

func resolveOne(content, rel, choice, title string, asJSON bool) {
	if rel == "" {
		die("conflicts: -choice needs -rel <path>")
	}
	var warnings []string
	res, err := importer.ResolveConflict(content, rel, choice, importer.ResolveOptions{
		Title: title,
		// The same structural gate the server applies (server/lvnguard.go):
		// a version that the runtime's loader would throw on must not become
		// the shipped one, whichever side offered it.
		Validate: func(rel string, data []byte) error {
			if !strings.EqualFold(filepath.Ext(rel), ".lvn") {
				return nil
			}
			errs, warns := checkLvnBytes(filepath.Join(content, filepath.FromSlash(rel)), data)
			warnings = warns
			if len(errs) > 0 {
				return errors.New(strings.Join(errs, "; "))
			}
			return nil
		},
		Write: writeContentFile(content),
	})
	if err != nil {
		if errors.Is(err, importer.ErrInvalidContent) {
			fmt.Fprintf(os.Stderr, "lvnconv: refusing to resolve %s to %q — that version is not a playable .lvn:\n", rel, choice)
			fmt.Fprintf(os.Stderr, "  %v\n", strings.TrimPrefix(err.Error(), importer.ErrInvalidContent.Error()+": "))
			fmt.Fprintln(os.Stderr, "nothing was changed on disk.")
			os.Exit(1)
		}
		die("conflicts: " + err.Error())
	}
	for _, w := range warnings {
		fmt.Fprintln(os.Stderr, "warning: "+w)
	}
	if asJSON {
		os.Stdout.Write(mustJSON(res))
		return
	}
	switch res.Choice {
	case importer.ChoiceIncoming:
		fmt.Printf("%s ← incoming (%d bytes); the parked version is now the file\n", res.Rel, res.Bytes)
	default:
		fmt.Printf("%s: kept your version (%d bytes); the parked version was discarded\n", res.Rel, res.Bytes)
	}
	if len(res.Baselines) > 0 {
		fmt.Printf("baseline updated for %s — the next import will not re-raise this file\n", strings.Join(res.Baselines, ", "))
	}
	if res.Note != "" {
		fmt.Fprintln(os.Stderr, "note: "+res.Note)
	}
}

// writeContentFile is the CLI's write path: atomic (temp + rename in the same
// directory) so a reader never sees a half file. The server passes its own,
// which additionally snapshots into content/.history under the panel's write
// lock — history is a server-side concept and there is nothing to snapshot into
// on a bare content directory.
func writeContentFile(content string) func(rel string, data []byte) error {
	return func(rel string, data []byte) error {
		dst := filepath.Join(content, filepath.FromSlash(rel))
		if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
			return err
		}
		tmp, err := os.CreateTemp(filepath.Dir(dst), ".tmp-*")
		if err != nil {
			return err
		}
		name := tmp.Name()
		if _, err := tmp.Write(data); err != nil {
			tmp.Close()
			os.Remove(name)
			return err
		}
		if err := tmp.Close(); err != nil {
			os.Remove(name)
			return err
		}
		if err := os.Chmod(name, 0o644); err != nil {
			os.Remove(name)
			return err
		}
		if err := os.Rename(name, dst); err != nil {
			os.Remove(name)
			return err
		}
		return nil
	}
}

// checkLvnBytes is the offline twin of the server's checkLvn (lvnguard.go):
// the runtime's envelope contract first (a JSON object carrying a "script"
// array — laxer parsers accept documents the player's loader throws on), then
// the shared validator, with the project's ext-grammar sidecar resolved from
// the destination path so host ops are judged the same way here as there.
func checkLvnBytes(dst string, data []byte) (errs, warns []string) {
	var root map[string]json.RawMessage
	if err := json.Unmarshal(data, &root); err != nil {
		return []string{fmt.Sprintf("not a .lvn document: %v", err)}, nil
	}
	raw, ok := root["script"]
	if !ok {
		return []string{`not a .lvn document: no "script" array (the runtime's loader rejects it)`}, nil
	}
	var arr []json.RawMessage
	if err := json.Unmarshal(raw, &arr); err != nil {
		return []string{`not a .lvn document: "script" is not an array`}, nil
	}
	doc, err := lvn.Parse(data)
	if err != nil {
		return []string{err.Error()}, nil
	}
	ext, gpath, gerr := lvn.FindExtGrammar(dst)
	if gerr != nil {
		warns = append(warns, fmt.Sprintf("ext-grammar %s is unreadable (%v) — host ops checked against the core grammar only", gpath, gerr))
		ext = nil
	}
	for _, is := range lvn.ValidateExt(doc, ext) {
		if is.Sev == lvn.SevError {
			errs = append(errs, is.String())
		} else {
			warns = append(warns, is.String())
		}
	}
	return errs, warns
}
