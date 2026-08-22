//go:build js && wasm

// WASM build of the lvnconv pipeline — the SAME lvns→.lvn converter and .lvn
// validator the CLI uses, exposed to the browser so the authoring website
// compiles with one source of truth (no hand-ported JS that drifts). Builds to
// server/website/lvns.wasm; the page calls window.lvnsCompile(src).
package main

import (
	"encoding/json"
	"fmt"
	"regexp"
	"strings"
	"syscall/js"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

var reQuoted = regexp.MustCompile(`"([^"]+)"`)

// resolveDocLine maps a document-level finding (no command index, so no source
// line from the converter) back to a likely source line by scanning the text —
// so "no scene" underlines the scene line and a dead label underlines its
// definition, instead of having no marker at all.
func resolveDocLine(src string, op, msg string) int {
	lines := strings.Split(src, "\n")
	if op == "scene" {
		for i, l := range lines {
			if strings.HasPrefix(strings.TrimSpace(l), "scene") {
				return i + 1
			}
		}
		return 1
	}
	if op == "label" {
		if m := reQuoted.FindStringSubmatch(msg); m != nil {
			target := ":" + m[1]
			for i, l := range lines {
				if strings.TrimSpace(l) == target {
					return i + 1
				}
			}
		}
	}
	return 0
}

// lvnsCompile(src[, extGrammarJSON][, files][, selfName]) -> { ok, json, errors, warnings }
//
// Второй аргумент — содержимое ext-grammar.json проекта: объявленные хост-опы
// после этого валидируются как встроенные (тот же контракт, что у CLI-флага
// -ext-grammar). Битое объявление само является ошибкой компиляции.
//
// Третий — ОСТАЛЬНЫЕ ОТКРЫТЫЕ ФАЙЛЫ новеллы, объект {"имя.lvns": "текст"}, и
// четвёртый — имя компилируемого файла. Без них `include` в студии не работал
// вовсе: браузер отдаёт компилятору текст, а путь в include резолвится
// относительно файла. Именно эта ошибка висела в IDE на строке с include, хотя
// та же глава прекрасно собиралась через CLI. Имя своего файла нужно, чтобы
// цикл, идущий через него самого, опознавался как цикл.
func compile(this js.Value, args []js.Value) any {
	src := ""
	if len(args) > 0 {
		src = args[0].String()
	}
	var ext *lvn.ExtGrammar
	if len(args) > 1 && args[1].Type() == js.TypeString && args[1].String() != "" {
		g, gerr := lvn.ParseExtGrammar([]byte(args[1].String()))
		if gerr != nil {
			return map[string]any{"ok": false, "json": "", "errors": "ext-grammar: " + gerr.Error(), "warnings": ""}
		}
		ext = g
	}

	files := map[string]string{}
	if len(args) > 2 && args[2].Type() == js.TypeObject {
		if keys := js.Global().Get("Object").Call("keys", args[2]); keys.Type() == js.TypeObject {
			for i := 0; i < keys.Length(); i++ {
				k := keys.Index(i).String()
				if v := args[2].Get(k); v.Type() == js.TypeString {
					files[k] = v.String()
				}
			}
		}
	}
	self := ""
	if len(args) > 3 && args[3].Type() == js.TypeString {
		self = args[3].String()
	}

	var doc *lvns.Doc
	var err error
	if len(files) > 0 || self != "" {
		doc, err = lvns.ConvertFiles(src, self, files)
	} else {
		doc, err = lvns.Convert(src)
	}
	if err != nil {
		return map[string]any{"ok": false, "json": "", "errors": err.Error(), "warnings": ""}
	}

	data, _ := json.MarshalIndent(doc, "", "  ")

	// Run the real .lvn validator so the editor surfaces dangling jumps,
	// unknown ops, duplicate labels, etc. — the same checks a build must pass.
	var errs, warns []string
	var diags []any
	if ld, perr := lvn.Parse(data); perr == nil {
		for _, is := range lvn.ValidateExt(ld, ext) {
			line := 0
			if is.Index >= 0 && is.Index < len(doc.SrcLine) {
				line = doc.SrcLine[is.Index]
			} else if is.Index < 0 {
				line = resolveDocLine(src, is.Op, is.Msg)
			}
			diags = append(diags, map[string]any{
				"sev":  is.Sev.String(),
				"line": line,
				"op":   is.Op,
				"msg":  is.Msg,
			})
			// Human string keyed by SOURCE line, not the opaque command index.
			loc := ""
			if line > 0 {
				loc = fmt.Sprintf("line %d", line)
			}
			if is.Op != "" {
				if loc != "" {
					loc += " "
				}
				loc += is.Op
			}
			s := is.Msg
			if loc != "" {
				s = loc + ": " + is.Msg
			}
			if is.Sev == lvn.SevError {
				errs = append(errs, s)
			} else {
				warns = append(warns, s)
			}
		}
	}

	return map[string]any{
		"ok":       len(errs) == 0,
		"json":     string(data),
		"errors":   strings.Join(errs, "\n"),
		"warnings": strings.Join(warns, "\n"),
		"diags":    diags,
	}
}

func main() {
	js.Global().Set("lvnsCompile", js.FuncOf(compile))
	<-make(chan struct{}) // keep the runtime alive
}
