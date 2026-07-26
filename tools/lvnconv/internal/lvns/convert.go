package lvns

import (
	"fmt"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"unicode"
)

// Cmd is one .lvn command object.
type Cmd map[string]any

// Doc is the .lvn document shape ({scene?, script}).
type Doc struct {
	Scene  string `json:"scene,omitempty"`
	Script []Cmd  `json:"script"`

	// SrcLine[i] is the 1-based source line that produced Script[i]. Authoring
	// metadata for editor diagnostics — never serialized into the .lvn.
	SrcLine []int `json:"-"`
}

var KnownOps = map[string]bool{
	"say": true, "choice": true, "bg": true, "actor": true, "obj": true,
	// A bare `clear` on its own line takes the whole cast off stage. It needs no
	// parse branch of its own — the generic fieldless path below turns a known
	// word with nothing after it into a command of that name. It DOES need to be
	// in this map: an unknown word falls into the narration branch and would
	// print the word itself to the player as a line of dialogue.
	"clear": true,
	"fade":  true, "dim": true, "flash": true, "tint": true, "blur": true,
	"camera": true, "particles": true,
	"audio": true, "wait": true, "input": true, "preload": true, "text_pace": true,
	"voice": true,               // compile-time prefix: voices the NEXT say line
	"text":  true,               // reactive HUD/stat label
	"save":  true, "load": true, // snapshot save/load (func is lowered away by expandLoops)
	"label": true, "goto": true, "if": true,
	"set": true, "inc": true, "hint": true,
	"call": true, "return": true,
	// Script-driven animation: `anim` tweens any prop of an entity/layer over
	// time; `move` is sugar for a screen-space path. Both compile to an "anim"
	// command carrying an LvnAnim payload (see buildAnimCmd). `defanim` names a
	// reusable animation and `play` stamps it onto an entity — pure compile-time
	// expansion, the runtime only ever sees "anim".
	"anim": true, "move": true, "defanim": true, "play": true,
	// `ext <op> k=v …` compiles a HOST-DEFINED op ({op:"<op>", …}) — the game's
	// C# handles it via LvnOps.Register (see the embedding guide).
	"ext": true,
	// wardrobe_show opens the in-story wardrobe for `char` — emitted by the
	// bundle importer's wardrobe-scene substitution (bundle_wire.go), handled
	// by NovelApp/WardrobeSheet at runtime. Round-trip needs it recognized
	// here too, or a decompiled chapter that opens the wardrobe fails to
	// recompile ("unknown command") the moment an author re-saves it.
	"wardrobe_show": true,
}

var reDialogue = regexp.MustCompile(`(?s)^([^:=\n]+?)(?:\s*\[([^\]]+)\])?\s*:\s*(.*)$`)

// Convert parses lvns source and returns the .lvn document.
func Convert(src string) (*Doc, error) { return convertWith(src, nil) }

// nestCtx — то, что вложенная компиляция обязана унаследовать от объемлющего
// документа. Блок опции это НЕ отдельный файл: он часть той же главы, и всё,
// что глава успела объявить выше по тексту, для него так же в силе.
type nestCtx struct {
	names     *synthNamer       // чтобы минтованные метки не столкнулись
	actorMaps map[string]string // чтобы реплика в блоке не потеряла who_id
}

// convertWith is Convert with one extra input: the label namespace of an
// ENCLOSING document. A woven option block is compiled by calling back into the
// compiler, and with a fresh namer each nesting level restarts at seq 1 — an
// inner weave and an outer one both minted `__weave_head_1`, which is a
// duplicate label and a chapter the validator refuses. Inheriting the namer
// keeps every minted name unique across nesting AND keeps it derived from the
// nearest author label, so it still does not move when the chapter is edited.
func convertWith(src string, outer *nestCtx) (*Doc, error) {
	// Lower the sugar before the line parser runs (core language stays tiny):
	//  1. flattenInline: put every control-flow `{`/`}` on its own line,
	//  2. collectFuncs: read the func signatures AND classify each one (expression
	//     function vs procedure) — classification needs the flattened BODY, which
	//     is why it runs after step 1 and not on the raw source,
	//  3. expression-function definitions leave no ops at all (their calls are
	//     inlined in step 6), so blank them out here,
	//  4. expandLoops: lower for/while/if and procedure bodies to label/goto,
	//  5. expandCalls: rewrite procedure call sites + `return <expr>`,
	//  6. inlineFuncs (after the doc is built): substitute expression-function
	//     calls into every expression the runtime will evaluate.
	// Директива, дожившая до разбора, — это вызов Convert без файла. Молча
	// пропустить нельзя: строка ушла бы в наррацию и напечаталась игроку.
	if err := strayInclude(strings.Split(src, "\n")); err != nil {
		return nil, err
	}
	flat := flattenInline(src)
	funcs, err := collectFuncs(flat)
	if err != nil {
		return nil, err
	}
	blankExprFuncDefs(flat, funcs)
	expanded, err := expandLoops(flat)
	if err != nil {
		return nil, err
	}
	src, err = expandCalls(expanded, funcs)
	if err != nil {
		return nil, err
	}

	doc := &Doc{Script: []Cmd{}}
	actorMaps := make(map[string]string)
	defAnims := make(map[string]map[string]any) // defanim <name> … → params, expanded by `play`
	// Fall-through labels for the single-branch `if … -> …` are minted the same
	// derived, edit-stable way the block lowering uses (see synthNamer): they are
	// save anchors, so a name must not depend on how many ifs precede it.
	var nfNames *synthNamer
	var pendingChoice map[string]any // `choice timeout=…` attrs awaiting the next `- option` block
	pendingVoice := ""               // `voice <url>` awaiting the next say line

	// Pre-process and clean lines. `srcNo` keeps each cleaned line's original
	// 1-based source line number, so commands can map back to the editor.
	var lines []string
	var srcNo []int
	rawLines := strings.Split(src, "\n")

	chevDepth := 0 // >0 while inside an unclosed «…» (multi-line string)
	var cbuf strings.Builder
	cbufSrc := 0
	for idx, raw := range rawLines {
		if chevDepth > 0 {
			// Inside a multi-line «…»: keep the raw line verbatim (no comment strip,
			// no blank-skip) and join with a real newline, until the » closes it.
			cbuf.WriteString("\n")
			cbuf.WriteString(raw)
			chevDepth += chevronDelta(raw)
			if chevDepth <= 0 {
				chevDepth = 0
				lines = append(lines, strings.TrimSpace(cbuf.String()))
				srcNo = append(srcNo, cbufSrc)
				cbuf.Reset()
			}
			continue
		}

		// Strip inline // comments — quote-, «…»- and URL-aware, so a // inside
		// a "string", a «…» text or after :// in a url is kept (a bare Index
		// would truncate `hint text="press A // or B"` mid-value and, worse, eat
		// the closing » of `Анна: «Пауза // тишина»` and swallow the rest).
		line := strings.TrimSpace(stripLineComment(raw))

		// Skip comments and empty lines
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}

		// Does this line OPEN an unclosed «…»? If so, start buffering continuation
		// lines so a multi-line label/say/choice text stays one logical line.
		d := chevronDelta(line)
		if d > 0 {
			chevDepth = d
			cbuf.Reset()
			cbuf.WriteString(line)
			cbufSrc = idx + 1
			continue
		}

		lines = append(lines, line)
		srcNo = append(srcNo, idx+1)
	}
	if cbuf.Len() > 0 {
		// An unterminated «…» has swallowed everything to EOF. Emitting "what
		// we have" would silently compile the rest of the chapter into one
		// giant say — fail loudly at the opening line instead.
		return nil, fmt.Errorf("line %d: unclosed «…» — the opening « never finds its »; the rest of the file would be swallowed into this string", cbufSrc)
	}

	// emit appends a command and records the source line it came from.
	emit := func(c Cmd, line int) {
		doc.Script = append(doc.Script, c)
		doc.SrcLine = append(doc.SrcLine, line)
	}

	if outer != nil {
		nfNames = outer.names
		nfNames.absorb(lines) // the block's own author labels must not be shadowed
		// Speaker→actor mapping: without it a line inside a woven block lost its
		// who_id, so the stage highlighted nobody for exactly those lines — and
		// the round-trip guard saw the field appear out of nowhere on recompile.
		for k, v := range outer.actorMaps {
			actorMaps[k] = v
		}
	} else {
		nfNames = newSynthNamer(lines)
	}

	defs := make(map[string]string) // def <name> <template…> → line-prefix macros

	// expandDefs applies the `def` line-prefix macros to one line, repeatedly
	// (a preset may expand into another). Shared by the statement loop below and
	// by choice option bodies, so a preset works the same in both places.
	expandDefs := func(line string, srcLine int) (string, error) {
		for hops := 0; len(defs) > 0; hops++ {
			w := firstField(line)
			tmpl, ok := defs[w]
			if !ok {
				break
			}
			if hops >= 16 {
				return "", fmt.Errorf("line %d: def: expansion loop via %q", srcLine, w)
			}
			line = strings.TrimSpace(tmpl + " " + strings.TrimSpace(line[len(w):]))
		}
		return line, nil
	}

	for i := 0; i < len(lines); {
		line := lines[i]

		// 0. Presets: `def <name> <op …>` names a line prefix; a later line
		// starting with <name> expands to "<template> <rest>" and reparses.
		// Pure compile-time text — the runtime never sees a def. The tour's
		// `text code x=… y=… size=… color=…` boilerplate becomes one word.
		if strings.HasPrefix(line, "def ") {
			rest := strings.TrimSpace(line[4:])
			sp := strings.IndexAny(rest, " \t")
			if sp <= 0 {
				return nil, fmt.Errorf("line %d: def: usage: def <name> <op …>", srcNo[i])
			}
			name := rest[:sp]
			if !isIdentWord(name) {
				return nil, fmt.Errorf("line %d: def: %q is not a valid preset name", srcNo[i], name)
			}
			if KnownOps[name] {
				return nil, fmt.Errorf("line %d: def: %q shadows a built-in op", srcNo[i], name)
			}
			defs[name] = strings.TrimSpace(rest[sp:])
			i++
			continue
		}
		if len(defs) > 0 {
			expanded, derr := expandDefs(line, srcNo[i])
			if derr != nil {
				return nil, derr
			}
			if expanded != line {
				line, lines[i] = expanded, expanded
			}
		}

		// 1. Directives: scene
		if strings.HasPrefix(line, "scene ") {
			doc.Scene = strings.TrimSpace(line[6:])
			i++
			continue
		}
		if strings.HasPrefix(line, "scene:") {
			doc.Scene = strings.TrimSpace(line[6:])
			i++
			continue
		}

		// 2. Directives: actor_map
		if strings.HasPrefix(line, "actor_map ") {
			mapping := strings.TrimSpace(line[10:])
			parts := strings.SplitN(mapping, "=", 2)
			if len(parts) == 2 {
				actorMaps[strings.TrimSpace(parts[0])] = strings.TrimSpace(parts[1])
			}
			i++
			continue
		}

		// 3. Label: :label_name
		if strings.HasPrefix(line, ":") {
			labelID := strings.TrimPrefix(line, ":")
			labelID = strings.TrimSpace(labelID)
			if labelID == "" {
				return nil, fmt.Errorf("line %d: label cannot be empty", i+1)
			}
			emit(Cmd{"op": "label", "id": labelID}, srcNo[i])
			nfNames.enter(line) // this label scopes the fall-through names below it
			i++
			continue
		}

		// 4. Choice: consecutive lines starting with `-` (but not `->`, which is goto)
		if strings.HasPrefix(line, "-") && !strings.HasPrefix(line, "->") {
			var options []any
			var weaves []weaveBlock // blocks too rich for a body — lowered below
			j := i
			for j < len(lines) {
				curr := lines[j]
				if strings.HasPrefix(curr, "-") && !strings.HasPrefix(curr, "->") {
					opt, err := parseChoiceOption(curr)
					if err != nil {
						return nil, fmt.Errorf("line %d: %w", j+1, err)
					}
					j++
					// `- text -> label … {` … `}` — the option's BODY: the command
					// list LvnPlayer.Choose executes on pick (a `_once_` flag, a
					// stat bump, a stage tweak) before it jumps. Without this form
					// the body had no source spelling at all and every re-save
					// through the panel silently dropped it (audit O3: the
					// "ask once" question pools never emptied).
					if isOptionBlockOpen(curr) {
						var body []string
						closed := false
						// Depth-counted, so an option block may contain another
						// choice with its own blocks: a flat scan would end the
						// outer block at the INNER `}` and silently reparent the
						// rest of it as top-level script.
						depth := 1
						blockLine := srcNo[j-1]
						for ; j < len(lines); j++ {
							t := strings.TrimSpace(lines[j])
							if t == "}" {
								depth--
								if depth == 0 {
									closed = true
									j++
									break
								}
							} else if isOptionBlockOpen(t) {
								depth++
							}
							bl, derr := expandDefs(lines[j], srcNo[j])
							if derr != nil {
								return nil, derr
							}
							body = append(body, bl)
						}
						if !closed {
							return nil, fmt.Errorf("line %d: unclosed choice option body (missing '}')", srcNo[j-1])
						}
						cmds, berr := parseBlockCommands(body, &nestCtx{names: nfNames, actorMaps: actorMaps})
						if berr != nil {
							return nil, fmt.Errorf("line %d: %w", srcNo[i], berr)
						}
						target, _ := opt["goto"].(string)
						if needsWeaving(cmds) {
							// WEAVE. The block holds prose or flow, which a runtime
							// `body` cannot carry: LvnPlayer.Choose dispatches only
							// set/inc/goto there, and a body command has no index in
							// the script, so anything else would vanish on the first
							// save/restore. Same syntax, different mechanism — the
							// block is lowered to ordinary script behind a minted
							// label, exactly as the author would have written it by
							// hand, minus the naming.
							lbl := nfNames.name("weave", nfNames.site())
							weaves = append(weaves, weaveBlock{
								label: lbl, cmds: cmds, target: target, line: blockLine,
							})
							delete(opt, "goto")
							opt["goto"] = lbl
						} else {
							// The header's `-> label` is the jump the body ends with —
							// keeping it on the option line is what makes the target
							// still readable (and greppable) at a glance.
							if target != "" {
								cmds = append(cmds, Cmd{"op": "goto", "label": target})
								delete(opt, "goto")
							}
							bodyAny := make([]any, len(cmds))
							for k, c := range cmds {
								bodyAny[k] = map[string]any(c)
							}
							opt["body"] = bodyAny
						}
					}
					options = append(options, opt)
				} else {
					break
				}
			}
			cc := Cmd{"op": "choice", "options": options}
			for k, v := range pendingChoice { // a preceding `choice timeout=…` line
				cc[k] = v
			}
			pendingChoice = nil
			emit(cc, srcNo[i])
			emitWeaves(emit, nfNames, weaves, srcNo[i])
			i = j
			continue
		}

		// 4b. Arrow goto: `-> label`
		if strings.HasPrefix(line, "->") {
			target := strings.TrimSpace(line[2:])
			if target == "" {
				return nil, fmt.Errorf("line %d: '->' needs a label", srcNo[i])
			}
			emit(Cmd{"op": "goto", "label": target}, srcNo[i])
			i++
			continue
		}

		// 4c. Single-branch if: `if <cond> -> <label>` (falls through when false).
		// Block `if <cond> { … } else { … }` is expanded earlier into the canonical
		// `if expr= then= else=` form, so here only the arrow form reaches us.
		if strings.HasPrefix(line, "if ") && strings.Contains(line, "->") {
			rest := strings.TrimSpace(line[3:])
			ai := strings.Index(rest, "->")
			cond := strings.TrimSpace(rest[:ai])
			target := strings.TrimSpace(rest[ai+2:])
			if cond == "" || target == "" {
				return nil, fmt.Errorf("line %d: expected 'if <cond> -> <label>'", srcNo[i])
			}
			fall := nfNames.name("nf", nfNames.site())
			emit(Cmd{"op": "if", "expr": cond, "then": target, "else": fall}, srcNo[i])
			emit(Cmd{"op": "label", "id": fall}, srcNo[i])
			i++
			continue
		}

		// 4d. Variable assignment: `name = expr` (init and mutation alike)
		if key, expr, ok := parseAssign(line); ok && !KnownOps[key] {
			emit(Cmd{"op": "set", "key": key, "expr": expr}, srcNo[i])
			i++
			continue
		}

		// 5. Commands and Dialogue
		words := strings.Fields(line)
		firstWord := ""
		if len(words) > 0 {
			firstWord = words[0]
		}

		isCommand := false
		var cmd Cmd

		if KnownOps[firstWord] {
			if firstWord == "ext" {
				// ext minigame kind="lockpick" → {"op":"minigame","kind":"lockpick"}
				rest := strings.TrimSpace(line[len("ext"):])
				toks := strings.Fields(rest)
				if len(toks) < 1 || strings.Contains(toks[0], "=") {
					return nil, fmt.Errorf("line %d: ext: usage: ext <op> key=value …", srcNo[i])
				}
				opName := toks[0]
				params, perr := parseKeyValue(strings.TrimSpace(strings.TrimPrefix(rest, opName)))
				if perr != nil {
					return nil, fmt.Errorf("line %d: ext %s: %w", srcNo[i], opName, perr)
				}
				ec := Cmd{"op": opName}
				for k, v := range params {
					ec[k] = v
				}
				isCommand = true
				cmd = ec
			} else if firstWord == "defanim" {
				// defanim <name> prop=… keys=… [loop=… ease=… dur=…] — stored,
				// no runtime command; `play` stamps it later.
				rest := strings.TrimSpace(line[len("defanim"):])
				toks := strings.Fields(rest)
				if len(toks) < 2 || strings.Contains(toks[0], "=") {
					return nil, fmt.Errorf("line %d: defanim: usage: defanim <name> prop=… keys=…", srcNo[i])
				}
				name := toks[0]
				params, perr := parseKeyValue(strings.TrimSpace(strings.TrimPrefix(rest, name)))
				if perr != nil {
					return nil, fmt.Errorf("line %d: defanim %s: %w", srcNo[i], name, perr)
				}
				if _, hasProp := params["prop"]; !hasProp {
					return nil, fmt.Errorf("line %d: defanim %s: prop required", srcNo[i], name)
				}
				defAnims[name] = params
				i++ // the line loop advances manually — a bare continue would spin forever
				continue
			}
			if firstWord == "play" {
				// play id=<entity> anim=<name> [overrides] — or terse: play <id> <name>
				rest := strings.TrimSpace(line[len("play"):])
				toks := strings.Fields(rest)
				var params map[string]any
				var perr error
				if len(toks) >= 2 && !strings.Contains(toks[0], "=") && !strings.Contains(toks[1], "=") {
					params, perr = parseKeyValue(strings.TrimSpace(strings.Join(toks[2:], " ")))
					if perr == nil {
						params["id"] = toks[0]
						params["anim"] = toks[1]
					}
				} else {
					params, perr = parseKeyValue(rest)
				}
				if perr != nil {
					return nil, fmt.Errorf("line %d: play: %w", srcNo[i], perr)
				}
				name, _ := params["anim"].(string)
				def, ok := defAnims[name]
				if !ok {
					return nil, fmt.Errorf("line %d: play: unknown animation %q (defanim it first)", srcNo[i], name)
				}
				merged := make(map[string]any, len(def)+len(params))
				for k, v := range def {
					merged[k] = v
				}
				for k, v := range params { // play's own params override the definition
					if k != "anim" {
						merged[k] = v
					}
				}
				ac, aerr := buildAnimCmd("anim", merged)
				if aerr != nil {
					return nil, fmt.Errorf("line %d: play %s: %w", srcNo[i], name, aerr)
				}
				isCommand = true
				cmd = ac
			} else if firstWord == "anim" || firstWord == "move" {
				// Surface malformed anim/move as a real compile error instead of
				// silently letting the line fall through to narration (`say`).
				rest := strings.TrimSpace(line[len(firstWord):])
				toks := strings.Fields(rest)
				var params map[string]any
				var err error
				if len(toks) > 0 && !strings.Contains(toks[0], "=") {
					params, err = parseAnimPositional(firstWord, rest) // terse: anim goblin2 scale [1 1.03 1] 2s yoyo
				} else {
					params, err = parseKeyValue(rest) // legacy: anim id=… prop=… keys=…
				}
				if err != nil {
					return nil, fmt.Errorf("line %d: %s: %w", srcNo[i], firstWord, err)
				}
				ac, err := buildAnimCmd(firstWord, params)
				if err != nil {
					return nil, fmt.Errorf("line %d: %w", srcNo[i], err)
				}
				isCommand = true
				cmd = ac
			} else if firstWord == "actor" {
				rest := strings.TrimSpace(line[len("actor"):])
				toks := strings.Fields(rest)
				if len(toks) > 0 && !strings.Contains(toks[0], "=") {
					// Terse: actor <id> [pos|emotion|hide|show] [w= h= x= y= scale= anchor= …]
					ac := Cmd{"op": "actor", "id": toks[0], "show": true}
					for _, t := range toks[1:] {
						if strings.Contains(t, "=") {
							kv := strings.SplitN(t, "=", 2)
							k := kv[0]
							switch k {
							case "w":
								k = "width"
							case "h":
								k = "height"
							}
							ac[k] = scalarVal(kv[1])
						} else {
							switch t {
							case "hide":
								ac["show"] = false
							case "show":
								ac["show"] = true
							case "left", "right", "center", "far_left", "far_right", "offscreen_left", "offscreen_right":
								ac["position"] = t
							default:
								ac["emotion"] = t // pose / emotion axis value
							}
						}
					}
					isCommand = true
					cmd = ac
				} else if params, err := parseKeyValue(rest); err == nil {
					// Legacy: actor id=… show=true position=…
					isCommand = true
					cmd = Cmd{"op": "actor"}
					for k, v := range params {
						cmd[k] = v
					}
				}
			} else if firstWord == "bg" {
				rest := strings.TrimSpace(line[len("bg"):])
				if rest != "" && !strings.Contains(rest, "=") {
					// Terse: bg <url>  (id derived from the file name)
					c := Cmd{"op": "bg", "sprite_url": stripQuotes(rest)}
					base := rest
					if sl := strings.LastIndexAny(base, "/\\"); sl >= 0 {
						base = base[sl+1:]
					}
					if dot := strings.LastIndex(base, "."); dot >= 0 {
						base = base[:dot]
					}
					if base != "" {
						c["id"] = base
					}
					isCommand = true
					cmd = c
				} else if params, err := parseKeyValue(rest); err == nil {
					// Legacy: bg id=… sprite_url=…
					isCommand = true
					cmd = Cmd{"op": "bg"}
					for k, v := range params {
						cmd[k] = v
					}
				}
			} else if firstWord == "text" {
				// Reactive label: text <id> [x= y= anchor= size= color= font=] «{expr}…»
				// or `text <id> hide`. Leading whitespace-tokens are id + k=v params;
				// the rest (which may span newlines inside «…») is the template.
				rem := strings.TrimSpace(line[len("text"):])
				id, after := nextWord(rem)
				if id != "" {
					c := Cmd{"op": "text", "id": id}
					rem = after
					for {
						w, next := nextWord(rem)
						if w == "" {
							break
						}
						if w == "hide" && strings.TrimSpace(next) == "" {
							c["hide"] = true
							rem = ""
							break
						}
						if strings.Contains(w, "=") {
							kv := strings.SplitN(w, "=", 2)
							c[kv[0]] = scalarVal(kv[1])
							rem = next
							continue
						}
						break // w begins the template — stop consuming params
					}
					tmpl := strings.TrimSpace(rem)
					// A reactive label's template is quoted («…»/"…") or references
					// a variable ({expr}). A bare unquoted run of words is almost
					// certainly prose that happens to start with "text" ("text me
					// when you arrive.") — let it fall through to narration rather
					// than silently minting a label with id "me".
					quoted := strings.HasPrefix(tmpl, "«") || strings.HasPrefix(tmpl, "\"")
					if tmpl != "" && !quoted && !strings.Contains(tmpl, "{") {
						// not a command — drop to dialogue handling below
					} else {
						if tmpl != "" {
							c["text"] = stripQuotes(tmpl)
						}
						isCommand = true
						cmd = c
					}
				}
			} else if firstWord == "voice" {
				// `voice "/content/voice/x.ogg"` — the NEXT say line speaks it.
				rest := strings.TrimSpace(line[len("voice"):])
				if strings.HasPrefix(rest, "url=") {
					rest = strings.TrimSpace(rest[len("url="):])
				}
				// A voice url is one token (or quoted). An unquoted value with
				// spaces ("voice of reason spoke first.") is prose starting with
				// the word "voice" — don't swallow it and mis-voice the next say.
				quoted := strings.HasPrefix(rest, "«") || strings.HasPrefix(rest, "\"")
				if !quoted && strings.ContainsAny(rest, " \t") {
					// fall through to narration below
				} else {
					pendingVoice = stripQuotes(rest)
					if pendingVoice == "" {
						return nil, fmt.Errorf("line %d: voice: usage: voice <url>", srcNo[i])
					}
					i++
					continue
				}
			} else if firstWord == "choice" {
				// `choice timeout=10 timeout_goto=late` — attributes for the
				// NEXT `- option` block (a timed choice). No command by itself.
				params, perr := parseKeyValue(strings.TrimSpace(line[len("choice"):]))
				if perr != nil {
					return nil, fmt.Errorf("line %d: choice: %w", srcNo[i], perr)
				}
				pendingChoice = params
				i++
				continue
			} else if firstWord == "return" && len(words) == 1 {
				isCommand = true
				cmd = Cmd{"op": "return"}
			} else if firstWord == "goto" || firstWord == "call" {
				// Structural keywords: never prose. A label with spaces or a
				// missing target is a mistake — fail loudly instead of silently
				// rendering "goto my label" as an on-screen dialogue line.
				if len(words) == 2 {
					isCommand = true
					cmd = Cmd{"op": firstWord, "label": words[1]}
				} else {
					return nil, fmt.Errorf("line %d: %s needs exactly one label with no spaces (got %q) — labels can't contain spaces", srcNo[i], firstWord, strings.TrimSpace(line[len(firstWord):]))
				}
			} else if firstWord != "return" {
				rest := strings.TrimSpace(line[len(firstWord):])
				if rest == "" {
					isCommand = true
					cmd = Cmd{"op": firstWord}
				} else {
					params, err := parseKeyValue(rest)
					if err == nil {
						isCommand = true
						cmd = Cmd{"op": firstWord}
						for k, v := range params {
							cmd[k] = v
						}
					} else if looksLikeCommand(words) {
						// Shaped exactly like a command (op + key=value tokens) but
						// the params didn't parse — a syntax slip in a real command,
						// not prose. Don't let it fall through to a dialogue line.
						return nil, fmt.Errorf("line %d: %s: %v", srcNo[i], firstWord, err)
					}
					// else: a known word starting genuine prose ("wait here," she
					// said) — fall through to narration as before.
				}
			}
		}

		if isCommand {
			emit(cmd, srcNo[i])
			i++
			continue
		}

		// A line shaped exactly like a command (`word key=value …`) whose op
		// isn't known is almost certainly a typo — `actro id=hill` compiling
		// into the on-screen line "actro id=hill" is the silent-failure mode
		// authors lose hours to. Real prose never matches this shape.
		if looksLikeCommand(words) && !KnownOps[firstWord] {
			if hint := nearestOp(firstWord); hint != "" {
				return nil, fmt.Errorf("line %d: unknown command %q — did you mean %q?", srcNo[i], firstWord, hint)
			}
			return nil, fmt.Errorf("line %d: unknown command %q (write it as dialogue with «…» quoting if this is prose)", srcNo[i], firstWord)
		}

		// Dialogue: Name [emotion]: Text or Narration
		if m := reDialogue.FindStringSubmatch(line); m != nil {
			speaker := strings.TrimSpace(m[1])
			emotion := strings.TrimSpace(m[2])
			text := strings.TrimSpace(m[3])

			text = stripQuotes(text)

			if emotion != "" {
				actorID, ok := actorMaps[speaker]
				if !ok {
					actorID = strings.ToLower(strings.ReplaceAll(speaker, " ", "_"))
				}
				emit(Cmd{"op": "actor", "id": actorID, "emotion": emotion}, srcNo[i])
			}

			sc := Cmd{"op": "say", "who": speaker, "text": text}
			// An explicit actor_map means the display name and the actor id
			// disagree ("Ash" speaking through the "hill" sprite) — carry the
			// id so the stage can highlight/lip-sync the right slot. Unmapped
			// speakers keep the loose name↔id match and need no extra field.
			if actorID, ok := actorMaps[speaker]; ok {
				sc["who_id"] = actorID
			}
			if pendingVoice != "" {
				sc["voice"] = pendingVoice
				pendingVoice = ""
			}
			emit(sc, srcNo[i])
		} else {
			// Narration
			text := stripQuotes(line)
			sc := Cmd{"op": "say", "text": text}
			if pendingVoice != "" {
				sc["voice"] = pendingVoice
				pendingVoice = ""
			}
			emit(sc, srcNo[i])
		}

		i++
	}

	// Expression functions are inlined here, on the finished document: this pass
	// sees EVERY expression the runtime will evaluate (`expr` fields plus the {…}
	// interpolations inside any string), including the ones a `def` preset or a
	// block lowering produced, and it never touches prose outside {…}.
	if err := inlineFuncs(doc, funcs); err != nil {
		return nil, err
	}

	return doc, nil
}

// flattenInline puts every control-flow brace on its own line (`if c { … }`,
// `} else { … }`, `for/while/func c { … }`), the form the macro passes expect.
// Lines inside a multi-line «…» pass through verbatim — their `{`/`}` are prose
// or interpolation, not control flow.
func flattenInline(src string) []string {
	var out []string
	depth := 0
	for _, raw := range strings.Split(src, "\n") {
		if depth > 0 || chevRun(0, raw) > 0 {
			out = append(out, raw)
			depth = chevRun(depth, raw)
			continue
		}
		out = append(out, splitInline(raw)...)
	}
	return out
}

// splitInline turns a one-line control block into the own-line brace form, so
// authors can write `if c { x }`, `if c { x } else { y }`, `} else { y }`,
// `for i in xs { x }` on a single line. Brace matching is string-/«»-aware and
// depth-counted, so interpolation ({hp}) and map literals ({a:1}) in the body
// survive intact. A non-control line, or a control line already in own-line form
// (ends with `{`, or is bare `}` / `} else {`), passes through unchanged.
func splitInline(line string) []string {
	t := stripLineComment(strings.TrimSpace(line))
	det := strings.TrimSpace(t)
	if det == "" {
		return []string{line}
	}
	isCtl := strings.HasPrefix(det, "if ") || strings.HasPrefix(det, "for ") ||
		strings.HasPrefix(det, "while ") || strings.HasPrefix(det, "func ") ||
		strings.HasPrefix(det, "}")
	if !isCtl || strings.HasSuffix(det, "{") || det == "}" ||
		strings.ReplaceAll(det, " ", "") == "}else{" {
		return []string{line} // not inline, or already own-line form
	}

	rs := []rune(det)
	open := firstBlockBrace(rs)
	if open < 0 {
		return []string{line} // e.g. `if c -> label` (handled elsewhere)
	}
	close := matchBrace(rs, open)
	if close < 0 {
		return []string{line}
	}

	var out []string
	if strings.HasPrefix(det, "}") {
		out = append(out, "} else {") // shape: } else { BODY }
	} else {
		out = append(out, strings.TrimSpace(string(rs[:open]))+" {")
	}
	body := strings.TrimSpace(string(rs[open+1 : close]))
	if body != "" {
		out = append(out, splitInline(body)...)
	}
	tail := strings.TrimSpace(string(rs[close+1:]))
	switch {
	case tail == "":
		out = append(out, "}")
	case strings.HasPrefix(tail, "else"):
		out = append(out, splitInline("} "+tail)...) // BODY } else { BODY2 }
	default:
		out = append(out, "}")
		out = append(out, splitInline(tail)...)
	}
	return out
}

// firstBlockBrace returns the index of the first '{' that is not inside a string
// or «…» (so a quoted condition or chevron text doesn't fool it), or -1.
func firstBlockBrace(rs []rune) int {
	var inStr rune
	chev := 0
	for i := 0; i < len(rs); i++ {
		c := rs[i]
		if inStr != 0 {
			if c == inStr {
				inStr = 0
			}
			continue
		}
		switch {
		case c == '«':
			chev++
		case c == '»':
			if chev > 0 {
				chev--
			}
		case chev > 0:
			// inside chevrons
		case c == '"' || c == '\'':
			inStr = c
		case c == '{':
			return i
		}
	}
	return -1
}

// matchBrace returns the index of the '}' matching the '{' at open (depth-counted,
// ignoring braces inside strings/«…»), or -1.
func matchBrace(rs []rune, open int) int {
	var inStr rune
	chev, depth := 0, 0
	for i := open; i < len(rs); i++ {
		c := rs[i]
		if inStr != 0 {
			if c == inStr {
				inStr = 0
			}
			continue
		}
		switch {
		case c == '«':
			chev++
		case c == '»':
			if chev > 0 {
				chev--
			}
		case chev > 0:
			// inside chevrons
		case c == '"' || c == '\'':
			inStr = c
		case c == '{':
			depth++
		case c == '}':
			depth--
			if depth == 0 {
				return i
			}
		}
	}
	return -1
}

// stripLineComment removes a trailing // comment that is not inside a string,
// «…» or a URL (://). Used only for inline-block detection/splitting.
func stripLineComment(s string) string {
	rs := []rune(s)
	var inStr rune
	chev := 0
	for i := 0; i < len(rs); i++ {
		c := rs[i]
		if inStr != 0 {
			if c == inStr {
				inStr = 0
			}
			continue
		}
		switch {
		case c == '«':
			chev++
		case c == '»':
			if chev > 0 {
				chev--
			}
		case chev > 0:
			// inside chevrons
		case c == '"' || c == '\'':
			inStr = c
		case c == '/' && i+1 < len(rs) && rs[i+1] == '/':
			if i > 0 && rs[i-1] == ':' {
				continue // part of :// in a URL
			}
			return string(rs[:i])
		}
	}
	return s
}

var reFuncDef = regexp.MustCompile(`^\s*func\s+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*\{\s*$`)
var reCall = regexp.MustCompile(`^\s*(?:([A-Za-z_]\w*)\s*=\s*)?([A-Za-z_]\w*)\s*\((.*)\)\s*$`)

var reReturnExpr = regexp.MustCompile(`^return\s+(.+)$`)

// exprBuiltins are the evaluator's own functions, which a `func` may not shadow:
// the redefinition would silently change the meaning of every existing call in the
// file. Mirrors lvn.ExprFuncs — TestExprBuiltinsMatchValidator keeps the two
// lists from drifting.
var exprBuiltins = map[string]bool{
	"rand": true, "chance": true, "min": true, "max": true, "abs": true,
	"floor": true, "round": true,
	"len": true, "has": true, "get": true, "indexof": true, "count": true,
	"sum": true, "first": true, "last": true, "keys": true, "vals": true,
	"list": true, "push": true, "pop": true, "removeat": true, "remove": true,
	"slice": true, "concat": true, "put": true, "del": true,
}

// funcDef is one `func …` declaration. The same syntax carries TWO different
// things, told apart by the body — and they lower in completely different ways:
//
//	expression function — the body is a single `return <expr>`. The declaration
//	    emits NO commands; every call site is replaced by the expression itself
//	    at compile time, so it works wherever an expression works (`x = add(2,3)`,
//	    `{add(2,3)}` in a line, an if-condition) and every runtime gets it for
//	    free without learning a new op or a user-function table. Recursion is
//	    impossible by construction and is reported as an error.
//	procedure — the body is commands. Lowered to `label __fn_<name>` + `call` +
//	    `return`, and called as a STATEMENT (`show_hero()`); parameters are bound
//	    to plain variables before the call (no frames, so no recursion).
type funcDef struct {
	name   string
	params []string
	expr   string // expression function: the returned expression (empty ⇒ procedure)
	line   int    // 1-based line of the declaration, for diagnostics
	from   int    // index of the `func …{` line in the flattened source
	to     int    // index of its closing `}`
}

// collectFuncs records each `func name(p1, p2) { … }` declaration and classifies
// it. Takes the FLATTENED lines (see flattenInline) because the classification
// looks at the body, and an author's one-liner `func f(x){ return x+1 }` only has
// a separable body after flattening — reading the raw source instead is exactly
// how the one-liner form used to be missed entirely (its call sites then survived
// as unknown expression functions, i.e. a silent 0 at runtime).
func collectFuncs(lines []string) (map[string]*funcDef, error) {
	funcs := map[string]*funcDef{}
	depth := 0
	for i := 0; i < len(lines); i++ {
		if depth > 0 || chevRun(0, lines[i]) > 0 { // a `func …` line inside «…» is prose
			depth = chevRun(depth, lines[i])
			continue
		}
		mm := reFuncDef.FindStringSubmatch(lines[i])
		if mm == nil {
			continue
		}
		name := mm[1]
		if prev, dup := funcs[name]; dup {
			return nil, fmt.Errorf("line %d: func %s: already declared on line %d", i+1, name, prev.line)
		}
		if exprBuiltins[name] {
			return nil, fmt.Errorf("line %d: func %s: %s() is a built-in expression function — pick another name", i+1, name, name)
		}
		var ps []string
		for _, p := range strings.Split(mm[2], ",") {
			if p = strings.TrimSpace(p); p != "" {
				ps = append(ps, p)
			}
		}
		body, end, err := funcBody(lines, i)
		if err != nil {
			return nil, err
		}
		d := &funcDef{name: name, params: ps, line: i + 1, from: i, to: end}
		// A single `return <expr>` body is an expression function; anything else
		// (commands, several statements, a bare `return`) is a procedure.
		if len(body) == 1 {
			if rm := reReturnExpr.FindStringSubmatch(body[0]); rm != nil {
				d.expr = strings.TrimSpace(rm[1])
			}
		}
		funcs[name] = d
	}
	if err := resolveFuncBodies(funcs); err != nil {
		return nil, err
	}
	return funcs, nil
}

// funcBody returns the meaningful body statements of the declaration opening at
// lines[open] (blank and comment-only lines dropped, «…» prose kept verbatim) plus
// the index of its closing `}`.
func funcBody(lines []string, open int) ([]string, int, error) {
	var body []string
	depth, chev := 1, 0
	for j := open + 1; j < len(lines); j++ {
		if chev > 0 || chevRun(0, lines[j]) > 0 {
			chev = chevRun(chev, lines[j])
			body = append(body, strings.TrimSpace(lines[j]))
			continue
		}
		t := strings.TrimSpace(stripLineComment(lines[j]))
		switch {
		case t == "":
			continue
		case strings.HasPrefix(t, "}") && strings.HasSuffix(t, "{"): // `} else {`
		case t == "}":
			if depth--; depth == 0 {
				return body, j, nil
			}
		case strings.HasSuffix(t, "{"):
			depth++
		}
		body = append(body, t)
	}
	return nil, 0, fmt.Errorf("line %d: func: missing closing '}'", open+1)
}

// resolveFuncBodies inlines expression-function calls that appear inside other
// expression-function bodies, so a call site only ever needs one substitution.
// A cycle here IS recursion, which compile-time inlining cannot express — it is
// reported instead of silently expanding forever.
func resolveFuncBodies(funcs map[string]*funcDef) error {
	const (
		busy = 1
		done = 2
	)
	state := map[string]int{}
	names := make([]string, 0, len(funcs))
	for n := range funcs {
		names = append(names, n)
	}
	sort.Strings(names) // stable error reporting
	var visit func(name string) error
	visit = func(name string) error {
		d := funcs[name]
		switch state[name] {
		case done:
			return nil
		case busy:
			return fmt.Errorf("line %d: func %s: recursive functions are not supported — a `func` that returns an expression is inlined at compile time; rewrite it as a `while` loop, or use `call`/`return`", d.line, name)
		}
		state[name] = busy
		if d.expr != "" {
			for _, dep := range calledFuncs(d.expr, funcs) {
				if err := visit(dep); err != nil {
					return err
				}
			}
			expr, err := inlineExpr(d.expr, funcs, d.line)
			if err != nil {
				return err
			}
			d.expr = expr
		}
		state[name] = done
		return nil
	}
	for _, n := range names {
		if err := visit(n); err != nil {
			return err
		}
	}
	return nil
}

// blankExprFuncDefs erases expression-function declarations from the flattened
// source: they contribute no commands at all. Lines are blanked rather than
// removed so every later line keeps its number (diagnostics stay honest).
func blankExprFuncDefs(lines []string, funcs map[string]*funcDef) {
	for _, d := range funcs {
		if d.expr == "" {
			continue
		}
		for i := d.from; i <= d.to && i < len(lines); i++ {
			lines[i] = ""
		}
	}
}

// chevRun advances a running «…» nesting depth across one physical line.
// Used by the macro passes to leave the INSIDE of a multi-line string alone —
// a `return`/`if`/`for` that appears as prose within «…» must not be lowered
// into a real command (it would inject control flow into a dialogue line).
func chevRun(depth int, s string) int {
	for _, r := range s {
		if r == '«' {
			depth++
		} else if r == '»' && depth > 0 {
			depth--
		}
	}
	return depth
}

// expandCalls rewrites PROCEDURE call statements and `return <expr>` into core
// primitives, once blocks have been flattened to own-lines. A call `name(a, b)`
// becomes `<param1> = a` / `<param2> = b` / `call __fn_name`; `r = name(a)` adds
// `r = __ret`. Expression functions never reach this pass as calls — they are
// inlined into the expression itself (see inlineFuncs) — so the two kinds of
// `func` stay strictly apart: a statement call here, an expression there.
func expandCalls(src string, funcs map[string]*funcDef) (string, error) {
	var out []string
	depth := 0
	for n, line := range strings.Split(src, "\n") {
		// Inside (or opening) a multi-line «…»: pass the line through untouched
		// so prose like `return home, she thought.` never becomes a `return` op.
		if depth > 0 || chevRun(0, line) > 0 {
			out = append(out, line)
			depth = chevRun(depth, line)
			continue
		}
		t := strings.TrimSpace(line)

		// `return <expr>` → stash the value, then return.
		if strings.HasPrefix(t, "return ") {
			if expr := strings.TrimSpace(t[len("return "):]); expr != "" {
				out = append(out, "__ret = "+expr, "return")
				continue
			}
		}

		if mm := reCall.FindStringSubmatch(stripLineComment(t)); mm != nil {
			lhs, fname, argstr := mm[1], mm[2], mm[3]
			if d, ok := funcs[fname]; ok {
				// An expression function has no body to jump into. `x = add(1,2)` is
				// left for the assignment parser (inlineFuncs expands it afterwards);
				// alone on a line its value would just be dropped — and before this
				// check that line fell through and became on-screen TEXT.
				if d.expr != "" {
					if lhs == "" {
						return "", fmt.Errorf("line %d: %s() returns a value — use it inside an expression (`x = %s(…)`, `{%s(…)}`), not as a statement", n+1, fname, fname, fname)
					}
					out = append(out, line)
					continue
				}
				args := splitArgs(argstr)
				if len(args) != len(d.params) {
					return "", fmt.Errorf("line %d: %s() takes %d argument(s), got %d", n+1, fname, len(d.params), len(args))
				}
				for i, p := range d.params {
					out = append(out, p+" = "+args[i]) // bind param (assignment sugar)
				}
				out = append(out, "call __fn_"+fname)
				if lhs != "" {
					out = append(out, lhs+" = __ret")
				}
				continue
			}
		}
		out = append(out, line)
	}
	return strings.Join(out, "\n"), nil
}

// splitArgs splits a call's argument list on top-level commas, respecting
// nested (), [], {}, quotes and «…».
func splitArgs(s string) []string {
	var args []string
	rs := []rune(s)
	var inStr rune
	chev, depth, start := 0, 0, 0
	for i := 0; i < len(rs); i++ {
		c := rs[i]
		if inStr != 0 {
			if c == inStr {
				inStr = 0
			}
			continue
		}
		switch {
		case c == '«':
			chev++
		case c == '»':
			if chev > 0 {
				chev--
			}
		case chev > 0:
		case c == '"' || c == '\'':
			inStr = c
		case c == '(' || c == '[' || c == '{':
			depth++
		case c == ')' || c == ']' || c == '}':
			depth--
		case c == ',' && depth == 0:
			args = append(args, strings.TrimSpace(string(rs[start:i])))
			start = i + 1
		}
	}
	if last := strings.TrimSpace(string(rs[start:])); last != "" {
		args = append(args, last)
	}
	return args
}

// ── expression functions: compile-time inlining ─────────────────────────────
//
// An expression function is sugar with no runtime footprint: `x = add(2,3)`
// compiles to `set key=x expr="((2) + (3))"`. Every runtime (C#, JS, and any
// future one) therefore supports `func` without knowing it exists, and the op
// dictionary does not grow. The price is that recursion is impossible — reported
// as an error in resolveFuncBodies rather than silently mis-expanded.

// inlineFuncs substitutes expression-function calls throughout the finished
// document: `expr` fields whole (that is the one field name every runtime hands
// to the evaluator — `if`, `set`, choice options) and the {…} interpolations
// inside every other string.
func inlineFuncs(doc *Doc, funcs map[string]*funcDef) error {
	if len(funcs) == 0 {
		return nil
	}
	for i, c := range doc.Script {
		line := 0
		if i < len(doc.SrcLine) {
			line = doc.SrcLine[i]
		}
		if err := inlineInMap(c, funcs, line); err != nil {
			return err
		}
	}
	return nil
}

func inlineInMap(m map[string]any, funcs map[string]*funcDef, line int) error {
	for k, v := range m {
		switch tv := v.(type) {
		case string:
			var s string
			var err error
			if k == "expr" {
				s, err = inlineExpr(tv, funcs, line)
			} else {
				s, err = inlineInterp(tv, funcs, line)
			}
			if err != nil {
				return err
			}
			if s != tv {
				m[k] = s
			}
		case Cmd:
			if err := inlineInMap(tv, funcs, line); err != nil {
				return err
			}
		case map[string]any:
			if err := inlineInMap(tv, funcs, line); err != nil {
				return err
			}
		case []any:
			for _, e := range tv {
				switch te := e.(type) {
				case Cmd:
					if err := inlineInMap(te, funcs, line); err != nil {
						return err
					}
				case map[string]any:
					if err := inlineInMap(te, funcs, line); err != nil {
						return err
					}
				}
			}
		}
	}
	return nil
}

// inlineExpr expands every declared-function call in one expression. Built-in
// calls (floor/get/rand/…) and names nobody declared pass through untouched —
// the validator is what flags an unknown function.
func inlineExpr(s string, funcs map[string]*funcDef, line int) (string, error) {
	if !strings.Contains(s, "(") {
		return s, nil
	}
	for round := 0; ; round++ {
		if round > 64 {
			return "", fmt.Errorf("line %d: func: call expansion does not settle — nested calls too deep", line)
		}
		rs := []rune(s)
		name, at := "", -1
		var d *funcDef
		for _, t := range scanIdents(rs) {
			if !t.call || t.member {
				continue
			}
			if def, ok := funcs[string(rs[t.start:t.end])]; ok {
				name, at, d = string(rs[t.start:t.end]), t.start, def
				break
			}
		}
		if at < 0 {
			return collapseParens(s), nil
		}
		// A procedure has no value to substitute: its body is commands, and it can
		// only be called as a statement. Saying so here is the difference between a
		// clear compile error and a variable that silently reads 0 at runtime.
		if d.expr == "" {
			return "", fmt.Errorf("line %d: %s() is a procedure (its body runs commands) — it cannot be used inside an expression; call it on its own line, or make its body a single `return <expr>`", line, name)
		}
		open := at + len([]rune(name))
		for open < len(rs) && rs[open] != '(' {
			open++
		}
		end := matchParen(rs, open)
		if end < 0 {
			return "", fmt.Errorf("line %d: %s(: unbalanced parentheses in %q", line, name, s)
		}
		args := splitArgs(string(rs[open+1 : end]))
		if len(args) != len(d.params) {
			return "", fmt.Errorf("line %d: %s() takes %d argument(s), got %d", line, name, len(d.params), len(args))
		}
		s = string(rs[:at]) + "(" + substituteParams(d.expr, d.params, args) + ")" + string(rs[end+1:])
	}
}

// inlineInterp expands calls inside the {…} spans of a text field only, so a call
// in «you earn {offer(base,rep)} coins» is inlined while prose that merely looks
// like a call is left alone. `{{`/`}}` are literal braces. A span containing `|`
// is an Ink-style alternative ({a|b|c}, {cond: yes|no}) whose branches are TEXT —
// only the condition head before `:` is an expression there.
func inlineInterp(s string, funcs map[string]*funcDef, line int) (string, error) {
	if !strings.Contains(s, "{") {
		return s, nil
	}
	var b strings.Builder
	for i := 0; i < len(s); i++ {
		if i+1 < len(s) && (s[i] == '{' && s[i+1] == '{' || s[i] == '}' && s[i+1] == '}') {
			b.WriteString(s[i : i+2])
			i++
			continue
		}
		if s[i] != '{' {
			b.WriteByte(s[i])
			continue
		}
		end := strings.IndexByte(s[i+1:], '}')
		if end < 0 {
			b.WriteString(s[i:]) // unterminated span: the runtime prints it verbatim
			break
		}
		end += i + 1
		span, err := inlineSpan(s[i+1:end], funcs, line)
		if err != nil {
			return "", err
		}
		b.WriteString("{" + span + "}")
		i = end
	}
	return b.String(), nil
}

func inlineSpan(span string, funcs map[string]*funcDef, line int) (string, error) {
	bar := strings.IndexByte(span, '|')
	if bar < 0 {
		return inlineExpr(span, funcs, line)
	}
	if colon := strings.IndexByte(span, ':'); colon > 0 && colon < bar {
		head, err := inlineExpr(span[:colon], funcs, line)
		if err != nil {
			return "", err
		}
		return head + span[colon:], nil
	}
	return span, nil // pure sequence/cycle/shuffle alternative — all branches are text
}

// substituteParams replaces the parameter identifiers in a function body with the
// argument expressions, each parenthesized so the caller's precedence survives
// (`upkeep(d+1)` with body `d/3` must not become `d+1/3`). Names inside string
// literals, member suffixes (`a.b`) and call names are left alone.
func substituteParams(expr string, params, args []string) string {
	if len(params) == 0 {
		return expr
	}
	pos := make(map[string]int, len(params))
	for i, p := range params {
		pos[p] = i
	}
	rs := []rune(expr)
	var b strings.Builder
	prev := 0
	for _, t := range scanIdents(rs) {
		if t.call || t.member {
			continue
		}
		i, ok := pos[string(rs[t.start:t.end])]
		if !ok {
			continue
		}
		b.WriteString(string(rs[prev:t.start]))
		if reAtomicArg.MatchString(args[i]) {
			b.WriteString(args[i]) // a bare name/number/string needs no bracket
		} else {
			b.WriteString("(" + args[i] + ")")
		}
		prev = t.end
	}
	b.WriteString(string(rs[prev:]))
	return b.String()
}

// An argument that cannot bind tighter than what surrounds it: a variable (dotted
// paths included), an unsigned number, a quoted string. Everything else is
// bracketed so the caller's precedence survives.
var reAtomicArg = regexp.MustCompile(`^(?:[\p{L}_][\p{L}\p{N}_.]*|[0-9]+(?:\.[0-9]+)?|"[^"]*"|'[^']*')$`)

// collapseParens drops doubled brackets — `((x))` → `(x)` — that inlining a chain
// of functions piles up. Cosmetic, but the .lvn is read by people (the IDE, the
// decompiler), so the sugar should compile to what a human would have written. A
// bracket that belongs to a CALL (`floor(…)`) is never touched.
func collapseParens(s string) string {
	for {
		rs := []rune(s)
		cut := -1
		for i := 0; i+1 < len(rs); i++ {
			if rs[i] != '(' || rs[i+1] != '(' {
				continue
			}
			if i > 0 && (rs[i-1] == '_' || rs[i-1] == '.' || unicode.IsLetter(rs[i-1]) || unicode.IsDigit(rs[i-1])) {
				continue // `floor(` — the bracket is part of the call
			}
			if inner, outer := matchParen(rs, i+1), matchParen(rs, i); inner > 0 && inner+1 == outer {
				cut = i
				break
			}
		}
		if cut < 0 {
			return s
		}
		outer := matchParen(rs, cut)
		out := make([]rune, 0, len(rs)-2)
		out = append(out, rs[:cut]...)
		out = append(out, rs[cut+1:outer]...)
		out = append(out, rs[outer+1:]...)
		s = string(out)
	}
}

// calledFuncs lists the declared functions an expression calls (in source order).
func calledFuncs(expr string, funcs map[string]*funcDef) []string {
	var out []string
	rs := []rune(expr)
	for _, t := range scanIdents(rs) {
		if !t.call || t.member {
			continue
		}
		if n := string(rs[t.start:t.end]); funcs[n] != nil {
			out = append(out, n)
		}
	}
	return out
}

// identTok is one identifier of an expression: its rune span, whether a '(' follows
// (a call) and whether a '.' precedes it (a member of a dotted path like global.rep).
type identTok struct {
	start, end   int
	call, member bool
}

// scanIdents walks an expression's identifiers, skipping the insides of string
// literals and «…» so a name mentioned in a literal is never rewritten.
func scanIdents(rs []rune) []identTok {
	var out []identTok
	var inStr rune
	chev := 0
	for i := 0; i < len(rs); i++ {
		c := rs[i]
		if inStr != 0 {
			if c == inStr {
				inStr = 0
			}
			continue
		}
		if chev > 0 {
			if c == '«' {
				chev++
			} else if c == '»' {
				chev--
			}
			continue
		}
		switch {
		case c == '"' || c == '\'':
			inStr = c
			continue
		case c == '«':
			chev++
			continue
		}
		if !(c == '_' || unicode.IsLetter(c)) {
			continue
		}
		j := i + 1
		for j < len(rs) && (rs[j] == '_' || unicode.IsLetter(rs[j]) || unicode.IsDigit(rs[j])) {
			j++
		}
		k := j
		for k < len(rs) && (rs[k] == ' ' || rs[k] == '\t') {
			k++
		}
		out = append(out, identTok{
			start:  i,
			end:    j,
			call:   k < len(rs) && rs[k] == '(',
			member: i > 0 && rs[i-1] == '.',
		})
		i = j - 1
	}
	return out
}

// matchParen returns the index of the ')' closing the '(' at open, string- and
// «…»-aware (the brace-matching twin of matchBrace).
func matchParen(rs []rune, open int) int {
	var inStr rune
	chev, depth := 0, 0
	for i := open; i < len(rs); i++ {
		c := rs[i]
		if inStr != 0 {
			if c == inStr {
				inStr = 0
			}
			continue
		}
		switch {
		case c == '«':
			chev++
		case c == '»':
			if chev > 0 {
				chev--
			}
		case chev > 0:
			// inside chevrons
		case c == '"' || c == '\'':
			inStr = c
		case c == '(':
			depth++
		case c == ')':
			depth--
			if depth == 0 {
				return i
			}
		}
	}
	return -1
}

// expandLoops rewrites block iteration into the flat primitives the line parser
// already understands. Two forms (the brace must end the opening line; `}` stands
// alone):
//
//	for <var> in <expr> { … }     while <expr> { … }
//
// A `for` desugars to: stash the collection, walk an index with len()+[], bind
// <var> each pass. A `while` desugars to a guarded label loop. Labels are unique
// per loop and nest via a stack, so loops can contain loops. Input is the
// flattened line list (see flattenInline).
func expandLoops(srcLines []string) (string, error) {
	type frame struct {
		kind            string // "for" | "while" | "if" | "func" | "opt"
		loopLbl, endLbl string
		idxVar          string // for-only
		elseLbl         string // if-only
		sawElse         bool   // if-only
	}
	var stack []frame
	var out []string
	names := newSynthNamer(srcLines)
	lastStmt := "" // last plain statement emitted, for the trailing-`return` check

	cdepth := 0
	for _, raw := range srcLines {
		// Inside/opening a «…»: emit verbatim, never interpret as a block.
		if cdepth > 0 || chevRun(0, raw) > 0 {
			out = append(out, raw)
			cdepth = chevRun(cdepth, raw)
			lastStmt = "" // prose, never a control statement
			continue
		}
		det := strings.TrimSpace(raw)
		if ci := strings.Index(det, "//"); ci >= 0 { // ignore trailing comments for detection
			det = strings.TrimSpace(det[:ci])
		}

		// A choice option's `{ … }` body is NOT control flow: it is a literal
		// command list carried inside the option (LvnPlayer.Choose runs it on
		// pick). Nothing here may lower it — the lines pass through untouched so
		// the choice scanner in Convert can pick them up verbatim.
		if len(stack) > 0 && stack[len(stack)-1].kind == "opt" {
			if det == "}" {
				stack = stack[:len(stack)-1]
				out = append(out, raw)
				lastStmt = ""
				continue
			}
			// A nested OPTION block is legal: a woven branch may hold another
			// choice, and its options carry blocks of their own (weave.go).
			// Push a frame so the matching `}` closes the inner one — a flat
			// scan would close the OUTER block on the inner brace and silently
			// reparent the rest of the branch as top-level script.
			if isOptionBlockOpen(det) {
				out = append(out, raw)
				stack = append(stack, frame{kind: "opt"})
				lastStmt = ""
				continue
			}
			if strings.HasSuffix(det, "{") {
				return "", fmt.Errorf("choice option body: nested blocks are not allowed (%q) — "+
					"the body is a flat command list; move branching to a label and lead there with '-> label'", det)
			}
			out = append(out, raw)
			if det != "" {
				lastStmt = det
			}
			continue
		}

		names.enter(det) // a `:label` line opens the next naming scope

		switch {
		case isOptionBlockOpen(det):
			// `- text -> label … {` opens an option body; see the guard above.
			out = append(out, raw)
			stack = append(stack, frame{kind: "opt"})
			lastStmt = ""
			continue

		case strings.HasPrefix(det, "for ") && strings.HasSuffix(det, "{"):
			inner := strings.TrimSpace(strings.TrimSuffix(det[4:], "{"))
			pos := strings.Index(inner, " in ")
			if pos < 0 {
				return "", fmt.Errorf("for: expected 'for <var> in <expr> {', got %q", det)
			}
			itemVar := strings.TrimSpace(inner[:pos])
			expr := strings.TrimSpace(inner[pos+4:])
			if itemVar == "" || expr == "" {
				return "", fmt.Errorf("for: empty variable or collection in %q", det)
			}
			tag := names.site()
			idx := names.name("i", tag)
			sv := names.name("src", tag)
			loop := names.name("loop", tag)
			body := names.name("body", tag)
			end := names.name("end", tag)
			out = append(out,
				fmt.Sprintf("set key=%s expr=%q", sv, expr),
				fmt.Sprintf("set key=%s value=0", idx),
				":"+loop,
				fmt.Sprintf("if expr=%q then=%s else=%s", fmt.Sprintf("%s < len(%s)", idx, sv), body, end),
				":"+body,
				fmt.Sprintf("set key=%s expr=%q", itemVar, fmt.Sprintf("%s[%s]", sv, idx)),
			)
			stack = append(stack, frame{kind: "for", loopLbl: loop, endLbl: end, idxVar: idx})

		case strings.HasPrefix(det, "while ") && strings.HasSuffix(det, "{"):
			expr := strings.TrimSpace(strings.TrimSuffix(det[6:], "{"))
			if expr == "" {
				return "", fmt.Errorf("while: empty condition in %q", det)
			}
			tag := names.site()
			loop := names.name("loop", tag)
			body := names.name("body", tag)
			end := names.name("end", tag)
			out = append(out,
				":"+loop,
				fmt.Sprintf("if expr=%q then=%s else=%s", expr, body, end),
				":"+body,
			)
			stack = append(stack, frame{kind: "while", loopLbl: loop, endLbl: end})

		case strings.HasPrefix(det, "func ") && strings.HasSuffix(det, "{"):
			inner := strings.TrimSpace(strings.TrimSuffix(strings.TrimPrefix(det, "func "), "{"))
			name := inner
			if p := strings.Index(inner, "("); p >= 0 {
				name = strings.TrimSpace(inner[:p])
			}
			if name == "" {
				return "", fmt.Errorf("func: missing name in %q", det)
			}
			// A procedure's skip label is derived from the function NAME, which
			// is as stable as a name gets — a re-save never renames it.
			skip := names.name("fnskip", name)
			// A PROCEDURE definition (expression functions are blanked out before
			// this pass): jump over the body in linear flow; the body is a
			// `call`-only routine.
			out = append(out, "goto "+skip, ":__fn_"+name)
			stack = append(stack, frame{kind: "func", endLbl: skip})

		case strings.HasPrefix(det, "if ") && strings.HasSuffix(det, "{"):
			cond := strings.TrimSpace(strings.TrimSuffix(det[3:], "{"))
			if cond == "" {
				return "", fmt.Errorf("if: empty condition in %q", det)
			}
			tag := names.site()
			thenL := names.name("then", tag)
			elseL := names.name("else", tag)
			endL := names.name("end", tag)
			out = append(out,
				fmt.Sprintf("if expr=%q then=%s else=%s", cond, thenL, elseL),
				":"+thenL,
			)
			stack = append(stack, frame{kind: "if", endLbl: endL, elseLbl: elseL})

		case strings.ReplaceAll(det, " ", "") == "}else{":
			if len(stack) == 0 || stack[len(stack)-1].kind != "if" {
				return "", fmt.Errorf("'} else {' without a matching 'if … {'")
			}
			f := &stack[len(stack)-1]
			out = append(out, "goto "+f.endLbl, ":"+f.elseLbl) // end of then-branch; else-branch follows
			f.sawElse = true

		case det == "}":
			if len(stack) == 0 {
				return "", fmt.Errorf("unmatched '}' (no open for/while/if block)")
			}
			f := stack[len(stack)-1]
			stack = stack[:len(stack)-1]
			switch f.kind {
			case "for":
				out = append(out, fmt.Sprintf("set key=%s expr=%q", f.idxVar, fmt.Sprintf("%s + 1", f.idxVar)), "goto "+f.loopLbl, ":"+f.endLbl)
			case "while":
				out = append(out, "goto "+f.loopLbl, ":"+f.endLbl)
			case "func":
				// Safety return only when the body does not already end in one —
				// otherwise the lowering emitted `return` twice (harmless at runtime,
				// but the second one is unreachable code the validator then reports).
				if lastStmt != "return" && !strings.HasPrefix(lastStmt, "return ") {
					out = append(out, "return")
				}
				out = append(out, ":"+f.endLbl) // skip-over label
			case "if":
				if f.sawElse {
					out = append(out, ":"+f.endLbl) // else-branch falls into end
				} else {
					out = append(out, ":"+f.elseLbl, ":"+f.endLbl) // no else: else target == end
				}
			}

		default:
			out = append(out, raw)
			if det != "" { // blank/comment-only lines are not statements
				lastStmt = det
			}
			continue
		}
		lastStmt = "" // a control-flow line; what got emitted is a label or a goto
	}

	if len(stack) > 0 {
		if stack[len(stack)-1].kind == "opt" {
			return "", fmt.Errorf("unclosed choice option body (missing '}')")
		}
		return "", fmt.Errorf("unclosed for/while block (missing '}')")
	}
	return strings.Join(out, "\n"), nil
}

// ── synthetic label names ────────────────────────────────────────────────────
//
// The names the lowering mints (`__then…`, `__else…`, `__end…`, `__nf…`) are not
// private to the compiler: they end up as `label` ops in the .lvn, and a SAVE is
// anchored on the id of the nearest preceding label (LvnPlayer.AnchorOf). A name
// that moves when an unrelated part of the chapter is edited therefore moves
// every player's bookmark — audit O16, where one re-save renamed 837 labels
// (`n37_000000` → `__nf1`) and the anchor of every save inside them silently
// resolved somewhere else.
//
// So a name is derived, not counted: nearest preceding AUTHOR label + the
// ordinal of the lowering site inside that label's scope. Editing scene 5 cannot
// renumber scene 1, and re-saving an unchanged chapter is a no-op. Names are
// checked against the labels the script already defines, so a lowering can never
// shadow an author's label (or another lowering's).
type synthNamer struct {
	scope string
	seq   map[string]int
	taken map[string]bool
}

func newSynthNamer(lines []string) *synthNamer {
	n := &synthNamer{scope: "head", seq: map[string]int{}, taken: map[string]bool{}}
	for _, l := range lines {
		if id, ok := sourceLabelID(l); ok {
			n.taken[id] = true
		}
	}
	return n
}

// enter opens a new naming scope when the line is a label the AUTHOR wrote (a
// `__`-prefixed one is itself a lowering artifact and would reintroduce the
// drift it exists to avoid).
func (n *synthNamer) enter(line string) {
	if id, ok := sourceLabelID(line); ok && !strings.HasPrefix(id, "__") {
		n.scope = id
	}
}

// site returns the tag shared by every label one lowering site needs (an `if`
// mints then/else/end off a single tag, so they read as one unit).
func (n *synthNamer) site() string {
	n.seq[n.scope]++
	return fmt.Sprintf("%s_%d", n.scope, n.seq[n.scope])
}

// name mints one collision-free label for a site tag.
func (n *synthNamer) name(kind, tag string) string {
	base := "__" + kind + "_" + tag
	name := base
	for i := 2; n.taken[name]; i++ {
		name = fmt.Sprintf("%s_%d", base, i)
	}
	n.taken[name] = true
	return name
}

// sourceLabelID reads a `:label` source line.
func sourceLabelID(line string) (string, bool) {
	t := strings.TrimSpace(stripLineComment(line))
	if !strings.HasPrefix(t, ":") {
		return "", false
	}
	id := strings.TrimSpace(t[1:])
	return id, id != ""
}

// isOptionBlockOpen reports whether a line opens a choice option's `{ … }` body:
// an option line (`- text …`, never the `->` goto) whose LAST character is the
// brace. Requiring the brace to end the line is what keeps option text free to
// contain `{expr}` interpolation — `- Осталось {gold} монет -> shop` is not a
// block, and a text ending in a bare `{` is not a thing an author writes.
func isOptionBlockOpen(det string) bool {
	return strings.HasPrefix(det, "-") && !strings.HasPrefix(det, "->") && strings.HasSuffix(det, "{")
}

func parseChoiceOption(line string) (map[string]any, error) {
	text := strings.TrimSpace(line[1:]) // strip '-'
	// A trailing `{` opens the option's body block — the caller collects it; the
	// brace is not part of the option line itself.
	hasBody := isOptionBlockOpen(strings.TrimSpace(line))
	if hasBody {
		text = strings.TrimSpace(strings.TrimSuffix(text, "{"))
	}

	var optText, targetLabel, paramsStr string
	if arrowIdx := strings.Index(text, "->"); arrowIdx >= 0 {
		optText = strings.TrimSpace(text[:arrowIdx])
		rest := strings.TrimSpace(text[arrowIdx+2:])
		if rest == "" {
			return nil, fmt.Errorf("choice option must specify a target label after '->'")
		}
		if spaceIdx := strings.IndexAny(rest, " \t"); spaceIdx == -1 {
			targetLabel = rest
		} else {
			targetLabel = rest[:spaceIdx]
			paramsStr = strings.TrimSpace(rest[spaceIdx+1:])
		}
	} else {
		if !hasBody {
			return nil, fmt.Errorf("choice option must have a target label (use '-> label')")
		}
		// Body-only option: the body IS the whole action and the flow falls
		// through past the choice once it has run, so there is no target to name.
		optText, paramsStr = splitOptionParams(text)
	}
	if optText == "" {
		return nil, fmt.Errorf("choice option text cannot be empty")
	}

	opt := map[string]any{
		"text": stripQuotes(optText),
	}
	if targetLabel != "" {
		opt["goto"] = targetLabel
	}

	if paramsStr != "" {
		params, err := parseKeyValue(paramsStr)
		if err != nil {
			return nil, fmt.Errorf("invalid choice option parameters: %w", err)
		}
		for k, v := range params {
			opt[k] = v
		}
	}
	// wallet_cost="20 crystals" is the REAL price LvnPlayer.Choose charges
	// (distinct from the narrative `cost` caption) — reconstruct the
	// {amount,currency} shape the runtime expects, the same as ToLvns wrote it.
	if wc, ok := opt["wallet_cost"].(string); ok {
		if m := reWalletCost.FindStringSubmatch(wc); m != nil {
			amt, _ := strconv.ParseFloat(m[1], 64)
			opt["wallet_cost"] = map[string]any{"amount": amt, "currency": m[2]}
		}
	}
	// effects="Роман:+2,Мирон:+1" is the cosmetic "+2 Роман" choice-preview
	// hint (AnnotateChoiceEffects) — reconstruct the [{label,delta}] shape.
	if es, ok := opt["effects"].(string); ok {
		var effects []any
		for _, part := range strings.Split(es, ",") {
			m := reEffect.FindStringSubmatch(strings.TrimSpace(part))
			if m == nil {
				continue
			}
			delta, err := strconv.Atoi(m[2])
			if err != nil {
				continue
			}
			effects = append(effects, map[string]any{"label": m[1], "delta": delta})
		}
		if len(effects) > 0 {
			opt["effects"] = effects
		} else {
			delete(opt, "effects")
		}
	}
	return opt, nil
}

var reWalletCost = regexp.MustCompile(`^([0-9]+(?:\.[0-9]+)?)\s+(\S+)$`)
var reEffect = regexp.MustCompile(`^(.+):([+-][0-9]+)$`)
var reOptParam = regexp.MustCompile(`(^|[ \t])[a-z_][a-z0-9_]*=`)

// splitOptionParams separates a body-only option's text from its trailing
// `key=value …` attributes. Reachable only when there is no `-> label` to split
// on, so the split has to be found in the text: the first token that both LOOKS
// like an attribute AND parses as one opens the parameter run — prose that
// merely contains an `=` stays prose.
func splitOptionParams(s string) (text, params string) {
	for _, loc := range reOptParam.FindAllStringIndex(s, -1) {
		at := loc[0]
		for at < len(s) && (s[at] == ' ' || s[at] == '\t') {
			at++
		}
		cand := strings.TrimSpace(s[at:])
		if _, err := parseKeyValue(cand); err == nil {
			return strings.TrimSpace(s[:at]), cand
		}
	}
	return strings.TrimSpace(s), ""
}

// parseOptionBody compiles a choice option's `{ … }` block into the command list
// the runtime runs on pick. The body is FLAT: LvnPlayer.Choose walks it once
// (set/inc apply data, a goto jumps and stops the walk, anything else goes to
// the stage), so control flow has no meaning there and is rejected at compile
// time instead of vanishing silently at runtime.
//
// Staging inside a body is NOT rejected here, on purpose. It runs — it just is
// not replayed after a load (the resume trace is a list of script indices and a
// body command has no index), so lvn.Validate warns about it. Refusing it at
// compile time would make an existing .lvn carrying one impossible to decompile
// and recompile, and round-trip totality is the stronger invariant: a chapter
// nobody can re-save is a chapter whose author loses work.
// parseBlockCommands compiles an option's `{ … }` block. It does NOT judge what
// is in there — the caller does, via needsWeaving: a block of set/inc/goto rides
// along as a runtime `body`, anything richer is lowered into script (weave.go).
func parseBlockCommands(lines []string, outer *nestCtx) ([]Cmd, error) {
	doc, err := convertWith(strings.Join(lines, "\n"), outer)
	if err != nil {
		return nil, fmt.Errorf("choice option body: %w", err)
	}
	return doc.Script, nil
}

// optionBodyDenied are the ops LvnPlayer.Choose does NOT dispatch inside a body:
// everything the player handles in its own loop (conformance/ops-owners.json,
// csharp "player"/"player+stage") except set/inc/goto, which Choose implements
// explicitly. Handed to a body they would be forwarded to the stage and disappear
// without a trace.
//
// This list used to make such a block an ERROR. It is now the weave fork instead
// (weave.go): the same block is lowered into ordinary script behind a minted
// label, which is what the author would have hand-written anyway.
var optionBodyDenied = map[string]bool{
	"say": true, "choice": true, "label": true, "if": true,
	"call": true, "return": true, "wait": true, "input": true,
	"preload": true, "load": true,
}

func parseKeyValue(s string) (map[string]any, error) {
	res := make(map[string]any)
	s = strings.TrimSpace(s)
	for len(s) > 0 {
		eqIdx := strings.Index(s, "=")
		if eqIdx == -1 {
			return nil, fmt.Errorf("expected '=' in key-value pair at %q", s)
		}
		key := strings.TrimSpace(s[:eqIdx])
		if !isValidKey(key) {
			return nil, fmt.Errorf("invalid key name %q", key)
		}
		s = s[eqIdx+1:]
		s = strings.TrimSpace(s)
		if len(s) == 0 {
			return nil, fmt.Errorf("missing value for key %q", key)
		}

		var val string
		if s[0] == '"' || s[0] == '\'' {
			quote := s[0]
			end := -1
			for i := 1; i < len(s); i++ {
				if s[i] == quote {
					// count consecutive preceding backslashes — an even count
					// means this quote is NOT escaped (handles a trailing "\\").
					nb := 0
					for j := i - 1; j >= 1 && s[j] == '\\'; j-- {
						nb++
					}
					if nb%2 == 0 {
						end = i
						break
					}
				}
			}
			if end == -1 {
				return nil, fmt.Errorf("unclosed quote for key %q", key)
			}
			val = s[1:end]
			val = strings.ReplaceAll(val, "\\\"", "\"")
			val = strings.ReplaceAll(val, "\\'", "'")
			s = s[end+1:]
		} else {
			spaceIdx := strings.IndexAny(s, " \t")
			if spaceIdx == -1 {
				val = s
				s = ""
			} else {
				val = s[:spaceIdx]
				s = s[spaceIdx+1:]
			}
		}

		if val == "true" {
			res[key] = true
		} else if val == "false" {
			res[key] = false
		} else if val == "null" {
			res[key] = nil
		} else if n, err := strconv.ParseFloat(val, 64); err == nil {
			if !strings.Contains(val, ".") {
				if valInt, err := strconv.ParseInt(val, 10, 64); err == nil {
					res[key] = valInt
				} else {
					res[key] = n
				}
			} else {
				res[key] = n
			}
		} else {
			res[key] = val
		}
		s = strings.TrimSpace(s)
	}
	return res, nil
}

func isValidKey(k string) bool {
	if len(k) == 0 {
		return false
	}
	for _, r := range k {
		// Any unicode letter: authors write Russian variable names
		// (`set здоровье=10`) as naturally as English ones.
		if !(unicode.IsLetter(r) || unicode.IsDigit(r) || r == '_' || r == '.') {
			return false
		}
	}
	return true
}

// looksLikeCommand: an ASCII-lowercase first word followed ONLY by key=value
// tokens — the exact shape of every .lvns command and of no natural sentence.
func looksLikeCommand(words []string) bool {
	if len(words) < 2 {
		return false
	}
	for _, r := range words[0] {
		if !((r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '_') {
			return false
		}
	}
	for _, w := range words[1:] {
		eq := strings.IndexByte(w, '=')
		if eq <= 0 || !isValidKey(w[:eq]) {
			return false
		}
	}
	return true
}

// nearestOp: the known op within edit distance 2 of s, or "".
func nearestOp(s string) string {
	best, bestD := "", 3
	for op := range KnownOps {
		if d := editDistance(s, op); d < bestD {
			best, bestD = op, d
		}
	}
	return best
}

func editDistance(a, b string) int {
	ra, rb := []rune(a), []rune(b)
	prev := make([]int, len(rb)+1)
	cur := make([]int, len(rb)+1)
	for j := range prev {
		prev[j] = j
	}
	for i := 1; i <= len(ra); i++ {
		cur[0] = i
		for j := 1; j <= len(rb); j++ {
			cost := 1
			if ra[i-1] == rb[j-1] {
				cost = 0
			}
			cur[j] = min3(prev[j]+1, cur[j-1]+1, prev[j-1]+cost)
		}
		prev, cur = cur, prev
	}
	return prev[len(rb)]
}

func min3(a, b, c int) int {
	if b < a {
		a = b
	}
	if c < a {
		a = c
	}
	return a
}

// chevronDelta counts the net «/» balance of a line, IGNORING guillemets that
// sit inside a "…" double-quoted string — a generic-form command like
// `say text="«Не уходи"` carries an author's unbalanced guillemet as DATA,
// and treating it as syntax made the scanner swallow the rest of the file
// into one giant multi-line string (live-hit: soviet.lvn's «Союз нерушимый…»
// verse split across lines either hard-failed the whole chapter's recompile
// or silently glued 4 lines into one say). The count can go negative (a bare
// » before any «) — callers clamp as needed.
func chevronDelta(s string) int {
	d := 0
	inQuote := false
	prev := rune(0)
	for _, r := range s {
		switch {
		case r == '"' && prev != '\\':
			inQuote = !inQuote
		case inQuote:
			// data, not syntax
		case r == '«':
			d++
		case r == '»':
			d--
		}
		prev = r
	}
	return d
}

func stripQuotes(s string) string {
	s = strings.TrimSpace(s)
	if len(s) >= 2 {
		// Strip a WRAPPING quote pair only: if the same quote also appears
		// inside, the outer ones are part of the prose («"Да" — и "нет"»),
		// not delimiters, and cutting them would corrupt the line.
		if (s[0] == '"' && s[len(s)-1] == '"') || (s[0] == '\'' && s[len(s)-1] == '\'') {
			if !strings.ContainsRune(s[1:len(s)-1], rune(s[0])) {
				return s[1 : len(s)-1]
			}
		}
	}
	// French quotes «…» (the multi-line/dialogue delimiter), trimmed as a unit.
	if strings.HasPrefix(s, "«") && strings.HasSuffix(s, "»") {
		return strings.TrimSpace(strings.TrimSuffix(strings.TrimPrefix(s, "«"), "»"))
	}
	return s
}

// numParam coerces a parsed key-value number (int64 or float64) to a float.
func numParam(v any) (float64, bool) {
	switch n := v.(type) {
	case float64:
		return n, true
	case int64:
		return float64(n), true
	case int:
		return float64(n), true
	}
	return 0, false
}

// parseAnimKeys turns "t:v t:v …" into [[t,v],…] keyframes and the max time.
func parseAnimKeys(s string) ([]any, float64, error) {
	var keys []any
	var maxT float64
	for _, tok := range strings.Fields(s) {
		parts := strings.SplitN(tok, ":", 2)
		if len(parts) != 2 {
			return nil, 0, fmt.Errorf("bad keyframe %q (want t:v)", tok)
		}
		t, err := strconv.ParseFloat(strings.TrimSpace(parts[0]), 64)
		if err != nil {
			return nil, 0, fmt.Errorf("bad time in %q", tok)
		}
		v, err := strconv.ParseFloat(strings.TrimSpace(parts[1]), 64)
		if err != nil {
			return nil, 0, fmt.Errorf("bad value in %q", tok)
		}
		keys = append(keys, []any{t, v})
		if t > maxT {
			maxT = t
		}
	}
	if len(keys) == 0 {
		return nil, 0, fmt.Errorf("no keyframes")
	}
	return keys, maxT, nil
}

// parseAssign recognises a bare `name = expr` line (variable init/mutation). It
// rejects comparison operators (==, !=, >=, <=) and any left side that isn't a
// plain identifier, so `if a == b`, `actor g center y=.5` etc. are left alone.
func parseAssign(line string) (key, expr string, ok bool) {
	eq := -1
	for idx := 0; idx < len(line); idx++ {
		if line[idx] != '=' {
			continue
		}
		var prev, next byte
		if idx > 0 {
			prev = line[idx-1]
		}
		if idx+1 < len(line) {
			next = line[idx+1]
		}
		if prev == '!' || prev == '<' || prev == '>' || prev == '=' || next == '=' {
			continue // part of == != >= <=
		}
		eq = idx
		break
	}
	if eq < 0 {
		return "", "", false
	}
	key = strings.TrimSpace(line[:eq])
	expr = strings.TrimSpace(line[eq+1:])
	if expr == "" || !isValidKey(key) {
		return "", "", false
	}
	return key, expr, true
}

// nextWord returns the first whitespace-delimited token of s and the remainder
// starting at the delimiter (so a following multi-line «…» template keeps its
// newlines). Empty token when s has no token.
func nextWord(s string) (word, rest string) {
	s = strings.TrimLeft(s, " \t")
	if s == "" {
		return "", ""
	}
	if i := strings.IndexAny(s, " \t\n"); i >= 0 {
		return s[:i], s[i:]
	}
	return s, ""
}

// scalarVal types a bare value: a number stays numeric, anything else is a
// (quote-stripped) string. Used by the terse positional actor/anim forms.
// firstField is the first whitespace-delimited token of a line ("" if none).
func firstField(line string) string {
	for i := 0; i < len(line); i++ {
		if line[i] == ' ' || line[i] == '\t' {
			return line[:i]
		}
	}
	return line
}

// isIdentWord reports whether s is a plain identifier ([A-Za-z_][A-Za-z0-9_]*).
func isIdentWord(s string) bool {
	if s == "" {
		return false
	}
	for i := 0; i < len(s); i++ {
		c := s[i]
		alpha := c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
		if !alpha && (i == 0 || c < '0' || c > '9') {
			return false
		}
	}
	return true
}

func scalarVal(v string) any {
	v = strings.TrimSpace(v)
	if n, err := strconv.ParseFloat(v, 64); err == nil {
		return n
	}
	return stripQuotes(v)
}

func isDur(t string) bool { // "2s", "0.2s", ".5s"
	if !strings.HasSuffix(t, "s") || len(t) < 2 {
		return false
	}
	_, err := strconv.ParseFloat(strings.TrimSuffix(t, "s"), 64)
	return err == nil
}

func isAnimWord(t string) bool {
	return t == "yoyo" || t == "loop" || t == "pingpong" || t == "stop" || isDur(t)
}

// parseAnimPositional reads the terse anim/move form into the param map that
// buildAnimCmd expects:
//
//	anim <id> <prop> [v v v] <dur>s [yoyo|loop] [ease= …]   // bracket list spread over dur
//	anim <id> <prop> 0:1 .5:1.1 1:1 …                       // explicit t:v keyframes
//	move <id> 0.2,0.5 0.8,0.5 1s                            // path points
func parseAnimPositional(op, rest string) (map[string]any, error) {
	p := map[string]any{}
	var bracket []string
	if lb := strings.Index(rest, "["); lb >= 0 {
		rel := strings.Index(rest[lb:], "]")
		if rel < 0 {
			return nil, fmt.Errorf("unclosed '[' in keys")
		}
		bracket = strings.Fields(strings.TrimSpace(rest[lb+1 : lb+rel]))
		rest = strings.TrimSpace(rest[:lb] + " " + rest[lb+rel+1:])
	}
	toks := strings.Fields(rest)
	if len(toks) == 0 {
		return nil, fmt.Errorf("need an id")
	}
	p["id"] = toks[0]
	idx := 1
	if op == "anim" && idx < len(toks) && !strings.Contains(toks[idx], "=") && !isAnimWord(toks[idx]) && !strings.Contains(toks[idx], ":") {
		p["prop"] = toks[idx]
		idx++
	}
	var inlineKeys []string
	for _, t := range toks[idx:] {
		switch {
		case strings.Contains(t, "="):
			kv := strings.SplitN(t, "=", 2)
			p[kv[0]] = scalarVal(kv[1])
		case isDur(t):
			d, _ := strconv.ParseFloat(strings.TrimSuffix(t, "s"), 64)
			p["dur"] = d
		case t == "yoyo" || t == "loop" || t == "pingpong":
			p["loop"] = t
		case t == "stop":
			p["stop"] = true
		case strings.Contains(t, ":"):
			inlineKeys = append(inlineKeys, t)
		case op == "move":
			if cur, ok := p["path"].(string); ok {
				p["path"] = cur + " " + t
			} else {
				p["path"] = t
			}
		}
	}
	if len(inlineKeys) > 0 {
		p["keys"] = strings.Join(inlineKeys, " ")
	} else if len(bracket) > 0 {
		d := 1.0
		if dv, ok := numParam(p["dur"]); ok && dv > 0 {
			d = dv
		}
		n := len(bracket)
		parts := make([]string, n)
		for i, v := range bracket {
			t := 0.0
			if n > 1 {
				t = float64(i) / float64(n-1) * d
			}
			parts[i] = fmt.Sprintf("%g:%s", t, v)
		}
		p["keys"] = strings.Join(parts, " ")
	}
	return p, nil
}

// parsePathPoints turns "x,y x,y …" into a list of 2D control points.
func parsePathPoints(s string) ([][2]float64, error) {
	var pts [][2]float64
	for _, tok := range strings.Fields(s) {
		parts := strings.SplitN(tok, ",", 2)
		if len(parts) != 2 {
			return nil, fmt.Errorf("bad point %q (want x,y)", tok)
		}
		x, err := strconv.ParseFloat(strings.TrimSpace(parts[0]), 64)
		if err != nil {
			return nil, fmt.Errorf("bad x in %q", tok)
		}
		y, err := strconv.ParseFloat(strings.TrimSpace(parts[1]), 64)
		if err != nil {
			return nil, fmt.Errorf("bad y in %q", tok)
		}
		pts = append(pts, [2]float64{x, y})
	}
	if len(pts) < 2 {
		return nil, fmt.Errorf("path needs at least 2 points")
	}
	return pts, nil
}

// propIdentity is a property's rest value — the start a `to=` one-liner tweens
// FROM (transforms rest at 0; scale/alpha rest at 1).
func propIdentity(prop string) float64 {
	switch prop {
	case "scale", "scalex", "scaley", "alpha":
		return 1
	default:
		return 0
	}
}

// parseLoop reads the loop param, which is either a bool (true/false) or a word
// (once/restart/yoyo). Returns (loop, yoyo).
func parseLoop(v any) (bool, bool) {
	switch n := v.(type) {
	case bool:
		return n, false
	case string:
		switch n {
		case "yoyo", "pingpong":
			return true, true
		case "true", "restart", "loop":
			return true, false
		}
	}
	return false, false
}

// buildAnimCmd compiles an `anim`/`move` source line into a runtime "anim"
// command carrying an LvnAnim payload (loop/duration/tracks). `move` is sugar:
// a screen-space path becomes synced screen_x/screen_y tracks. Keeping both as
// one runtime op means the engine only learns a single new verb.
func buildAnimCmd(op string, p map[string]any) (Cmd, error) {
	id, _ := p["id"].(string)
	if id == "" {
		return nil, fmt.Errorf("%s: id required", op)
	}

	// Stop form: `anim id=x stop=all` (every script lane) or `stop=<channel/prop>`.
	// `stop=false` (bool) is NOT a stop — fall through to a normal animate.
	if sv, ok := p["stop"]; ok {
		if b, isBool := sv.(bool); !isBool || b {
			target := "all"
			if s, isStr := sv.(string); isStr && s != "" && s != "true" {
				target = s
			}
			return Cmd{"op": "anim", "id": id, "stop": target}, nil
		}
	}

	// `channel` is optional: when omitted the runtime derives one per animated
	// property (so rotation/scale/move run at once and compose, while re-animating
	// the same property replaces it). An explicit channel lets you group/override.
	channel, _ := p["channel"].(string)
	mode, _ := p["mode"].(string)
	loop, yoyo := parseLoop(p["loop"])
	ease, _ := p["ease"].(string)
	interp, _ := p["interp"].(string)
	switch interp {
	case "", "linear", "spline", "step":
	default:
		// The runtime treats unknown interp as linear — surface the typo here
		// instead of silently flattening the author's curve.
		return nil, fmt.Errorf("%s: interp=%q is not linear|spline|step", op, interp)
	}
	dur, durSet := numParam(p["dur"])

	withShaping := func(tr map[string]any) map[string]any {
		if ease != "" {
			tr["ease"] = ease
		}
		if interp != "" {
			tr["interp"] = interp
		}
		return tr
	}

	var tracks []any
	var duration float64

	if op == "move" {
		d := dur
		if !durSet || d <= 0 {
			d = 1
		}
		var xs, ys []any
		if to, ok := p["to"].(string); ok && to != "" {
			// one-liner: glide from the current spot to a single point
			pt, err := parsePathPoints(to + " " + to) // reuse parser; take first
			if err != nil {
				return nil, fmt.Errorf("move: bad to=%q (want x,y)", to)
			}
			xs = []any{[]any{0.0, 0.0}, []any{d, pt[0][0]}}
			ys = []any{[]any{0.0, 0.0}, []any{d, pt[0][1]}}
		} else {
			pathStr, _ := p["path"].(string)
			pts, err := parsePathPoints(pathStr)
			if err != nil {
				return nil, fmt.Errorf("move: %w", err)
			}
			n := len(pts)
			for i, pt := range pts {
				t := 0.0
				if n > 1 {
					t = float64(i) / float64(n-1) * d
				}
				xs = append(xs, []any{t, pt[0]})
				ys = append(ys, []any{t, pt[1]})
			}
		}
		tracks = []any{
			withShaping(map[string]any{"prop": "screen_x", "keys": xs}),
			withShaping(map[string]any{"prop": "screen_y", "keys": ys}),
		}
		duration = d
		if orient, ok := p["orient"].(bool); ok && orient {
			// runtime reads this to rotate along the path tangent (phase 2)
			tracks[0].(map[string]any)["orient"] = true
		}
	} else { // anim
		prop, _ := p["prop"].(string)
		if prop == "" {
			return nil, fmt.Errorf("anim: prop required")
		}
		tr := map[string]any{"prop": prop}
		if to, hasTo := numParam(p["to"]); hasTo {
			// one-liner: tween from the property's rest value to the target
			d := dur
			if !durSet || d <= 0 {
				d = 1
			}
			tr["keys"] = []any{[]any{0.0, propIdentity(prop)}, []any{d, to}}
			duration = d
		} else {
			keysStr, _ := p["keys"].(string)
			keys, maxT, err := parseAnimKeys(keysStr)
			if err != nil {
				return nil, fmt.Errorf("anim: %w", err)
			}
			tr["keys"] = keys
			duration = maxT
			if durSet && dur > 0 {
				duration = dur
			}
		}
		if layer, _ := p["layer"].(string); layer != "" {
			tr["layer"] = layer
		}
		tracks = []any{withShaping(tr)}
	}

	anim := map[string]any{"loop": loop, "duration": duration, "tracks": tracks}
	if yoyo {
		anim["yoyo"] = true
	}
	cmd := Cmd{"op": "anim", "id": id, "anim": anim}
	if channel != "" {
		cmd["channel"] = channel
	}
	if mode != "" {
		cmd["mode"] = mode
	}
	return cmd, nil
}
