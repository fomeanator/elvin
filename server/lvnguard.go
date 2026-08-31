package main

// lvnguard.go — the structural gate every compiled script passes on its way
// into content/.
//
// Why it exists: the server is the ONE chokepoint all content goes through.
// Studio's "Save to app", a raw `PUT /v1/admin/assets/scripts/x.lvn`, and both
// importers all end at the same write, and content/ is served to players
// immediately (Cache-Control: no-store). Until this file, none of those paths
// ran the validator that already existed in the lvnconv `lvn` package — a
// chapter with a jump to a label that no longer exists, or a body that isn't
// even a .lvn document, shipped to players the second it was saved and failed
// only on the device, mid-scene.
//
// Severity is the whole design. The product decision is that the COMPILER owns
// unknown ops and the runtime only logs them, so:
//
//   - ERRORS block the write (422, nothing touches the disk or .history):
//     malformed JSON, a document the runtime's own LvnDocument.Parse would
//     throw on, duplicate labels, jumps to labels that don't exist. These are
//     "the chapter cannot play", not style.
//   - WARNINGS never block: an unknown op is a legal host op (LvnOps.Register)
//     until the project declares otherwise, so refusing it would break every
//     embedding game. They ride back in the response so Studio can show them
//     to the author, and go to the server log so an operator sees them too.
//
// A project that DOES want its host ops checked declares them in
// ext-grammar.json beside (or one level above) its scripts — then an
// undeclared op stops being noise and starts meaning something.

import (
	"encoding/json"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

// lvnFindings is one script's verdict, already split by what the caller must
// do about it. Errors gate the write; warnings are reported and pass.
type lvnFindings struct {
	Errors   []string `json:"errors,omitempty"`
	Warnings []string `json:"warnings,omitempty"`
}

func (f lvnFindings) blocked() bool { return len(f.Errors) > 0 }

// orEmpty is a nil-free view of a findings slice: JSON `null` in a response
// clients iterate is a needless footgun, an empty array is not.
func orEmpty(v []string) []string {
	if v == nil {
		return []string{}
	}
	return v
}

// prefixed renders the findings as flat, self-describing lines for an import
// report — "lvn <rel>: <issue>" — so a machine-generated script's problems
// read the same as a hand-saved one's.
func (f lvnFindings) prefixed(rel string) []string {
	out := make([]string, 0, len(f.Errors)+len(f.Warnings))
	for _, e := range f.Errors {
		out = append(out, fmt.Sprintf("lvn %s: ERROR %s", rel, e))
	}
	for _, w := range f.Warnings {
		out = append(out, fmt.Sprintf("lvn %s: warning %s", rel, w))
	}
	return out
}

// isLvnPath reports whether a content-relative path is a COMPILED script.
// Deliberately only ".lvn": the ".lvns" sidecar is editable source, compiled
// to .lvn by the panel before it is saved, and an author mid-edit must be able
// to save a half-written source file.
func isLvnPath(rel string) bool {
	return strings.EqualFold(filepath.Ext(rel), ".lvn")
}

func isManifestPath(rel string) bool {
	return filepath.ToSlash(rel) == "manifest.json"
}

// checkManifest — тот же гейт, но для МАНИФЕСТА.
//
// Скрипты проходили структурную проверку, манифест — нет: его писали на диск
// после разбора JSON и всё. А это весь облик приложения: темы, цвета, экраны,
// подборки. Опечатка молча давала умолчание, и автор видел не ошибку, а
// «почему-то не так», причём УЖЕ у игрока.
//
// Только предупреждения, и намеренно: схемы манифеста здесь нет (она в
// C#-DTO), проверка идёт по конвенции имён, а хост вправе класть в манифест
// своё. Отказывать на неполном знании нельзя — молчать тоже.
func (s *server) checkManifest(data []byte) lvnFindings {
	var f lvnFindings
	for _, iss := range lvn.ValidateManifest(data) {
		if iss.Sev == lvn.SevError {
			f.Errors = append(f.Errors, iss.Msg)
		} else {
			f.Warnings = append(f.Warnings, iss.Msg)
		}
	}
	return f
}

// checkLvn parses and validates one compiled script's bytes. rel is the
// content-relative destination: it is used ONLY to locate the project's
// ext-grammar sidecar, so an unwritten file (an importer's output) checks
// exactly like a saved one.
func (s *server) checkLvn(rel string, data []byte) lvnFindings {
	var f lvnFindings

	// The engine's own loader (LvnDocument.Parse) demands a JSON OBJECT with a
	// "script" array and throws otherwise — lvn.Parse is laxer (a document
	// without "script" unmarshals into an empty Doc and only warns). Check the
	// runtime's contract first so "it saved fine but the chapter won't open"
	// can't happen.
	if err := checkLvnEnvelope(data); err != nil {
		f.Errors = append(f.Errors, err.Error())
		return f
	}
	doc, err := lvn.Parse(data)
	if err != nil {
		f.Errors = append(f.Errors, err.Error())
		return f
	}

	ext, gpath, gerr := lvn.FindExtGrammar(filepath.Join(s.content, filepath.FromSlash(rel)))
	if gerr != nil {
		// A broken sidecar must not block content: the author's way out of a
		// bad ext-grammar.json is to save a new one, and gating scripts on it
		// would strand them. Say it loudly and fall back to the core grammar.
		f.Warnings = append(f.Warnings, fmt.Sprintf("ext-grammar %s is unreadable (%v) — host ops checked against the core grammar only", gpath, gerr))
		ext = nil
	}
	for _, is := range lvn.ValidateExt(doc, ext) {
		if is.Sev == lvn.SevError {
			f.Errors = append(f.Errors, is.String())
		} else {
			f.Warnings = append(f.Warnings, is.String())
		}
	}

	// Ссылка есть, файла нет. Компилятору это не видно — он не знает, что
	// лежит на диске, — а игроку видно сразу: пустой экран вместо фона,
	// беззвучная сцена. Опечатка в имени файла ничем не отличается от
	// «арт зальют завтра», поэтому предупреждение, а не отказ: порядок «сначала
	// текст, потом картинки» — обычная работа, и запрещать его нельзя.
	for _, miss := range s.missingAssets(doc) {
		f.Warnings = append(f.Warnings, miss)
	}
	return f
}

// missingAssets — ссылки на контент, которого нет на диске.
//
// Смотрим ровно те поля, по которым движок идёт за файлом: фон и спрайты
// (sprite_url у bg/actor/obj), звук (url у audio) и предзагрузку. Внешние
// адреса (http) не наше дело, шаблоны с подстановкой ({стихия}) не проверяем:
// их значение известно только в игре.
func (s *server) missingAssets(doc *lvn.Doc) []string {
	seen := map[string]bool{}
	var out []string
	check := func(i int, op, url string) {
		if url == "" || seen[url] || strings.HasPrefix(url, "http") || strings.ContainsAny(url, "{}") {
			return
		}
		seen[url] = true
		rel := strings.TrimPrefix(url, "/content/")
		if rel == url && !strings.HasPrefix(url, "/") {
			rel = url // относительный путь внутри контента
		} else if rel == url {
			return // абсолютный путь не в /content/ — не наш
		}
		clean := filepath.Clean("/" + filepath.FromSlash(rel))[1:]
		abs := filepath.Join(s.content, clean)
		st, err := os.Stat(abs)
		if err != nil {
			out = append(out, fmt.Sprintf("script[%d] %s: файла нет — %s (ссылка есть, а на диске пусто: игрок увидит пустоту)", i, op, url))
			return
		}
		if op == "audio" || op == "preload" || st.IsDir() {
			return
		}
		if w, h, ok := imageSize(abs); ok {
			if min := minArtHeight(op); h < min {
				out = append(out, fmt.Sprintf("script[%d] %s: картинка %dx%d — мельче %dpx по высоте (%s); на телефоне растянется в мыло — %s",
					i, op, w, h, min, artKindName(op), url))
			}
		}
	}
	for i, c := range doc.Script {
		switch c.Op() {
		case "bg", "actor", "obj":
			check(i, c.Op(), c.Str("sprite_url"))
		case "audio":
			check(i, "audio", c.Str("url"))
		case "preload":
			check(i, "preload", c.Str("url"))
		}
	}
	return out
}

// checkImportedScripts runs the SAME gate over every compiled script an import
// produced. Machine-generated .lvn skipped this check entirely: an importer
// bug (a wardrobe swap that drops a label other branches still jump to was a
// live one) wrote a chapter straight into content/ that no validator ever saw.
//
// Deliberately reporting, not blocking: an import also writes art, sprites and
// the manifest, so failing the request after the fact would leave a half-built
// title and no diagnosis. The findings go into the response (and the log) so
// the author sees them at import time instead of in playtesting — and the same
// scripts are re-gated for real the moment anyone saves one from Studio.
func (s *server) checkImportedScripts(res *importer.Result) []string {
	if res == nil {
		return nil
	}
	var out []string
	check := func(rel string, data []byte) {
		if !isLvnPath(rel) || len(data) == 0 {
			return
		}
		f := s.checkLvn(rel, data)
		if f.blocked() {
			log.Printf("import produced an invalid script %s: %s", rel, strings.Join(f.Errors, "; "))
		}
		out = append(out, f.prefixed(rel)...)
	}
	for _, sf := range res.Scripts {
		check(sf.Rel, sf.Data)
	}
	check(res.ScriptRel, res.Lvn) // single-chapter form lives outside res.Scripts
	return out
}

// checkLvnEnvelope enforces the document shape the RUNTIME requires, which is
// stricter than the Go struct's: a top-level object carrying a "script" array.
// A bare op array or a stray `{}` unmarshals happily in Go and then throws
// FormatException on the player's device.
func checkLvnEnvelope(data []byte) error {
	var root map[string]json.RawMessage
	if err := json.Unmarshal(data, &root); err != nil {
		return fmt.Errorf("not a .lvn document: %v — expected a JSON object like {\"scene\":…,\"script\":[…]}", err)
	}
	raw, ok := root["script"]
	if !ok {
		return fmt.Errorf(`not a .lvn document: no "script" array (the runtime's loader rejects it)`)
	}
	var arr []json.RawMessage
	if err := json.Unmarshal(raw, &arr); err != nil {
		return fmt.Errorf(`not a .lvn document: "script" is not an array`)
	}
	return nil
}
