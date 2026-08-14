package lvns

import (
	"golang.org/x/text/unicode/norm"

	"encoding/json"
	"fmt"
	"reflect"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

func TestConvert(t *testing.T) {
	src := `
scene test_chapter
actor_map Mara=mara_custom

// A background change
bg sprite_url="/content/bg/room.jpg"

:start
Rain ticked on the porch roof.
Mara: You came back.
Mara [smile]: Then come in out of the rain.

- I did. -> warmth_choice min=2 requires_stat="courage"
- I can't stay. -> leave cost="5 coins"

:warmth_choice
goto start

:leave
return
`

	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}

	if doc.Scene != "test_chapter" {
		t.Errorf("expected scene to be 'test_chapter', got %q", doc.Scene)
	}

	expectedScript := []Cmd{
		{"op": "bg", "sprite_url": "/content/bg/room.jpg"},
		{"op": "label", "id": "start"},
		{"op": "say", "text": "Rain ticked on the porch roof."},
		{"op": "say", "who": "Mara", "who_id": "mara_custom", "text": "You came back."},
		{"op": "actor", "id": "mara_custom", "emotion": "smile"},
		{"op": "say", "who": "Mara", "who_id": "mara_custom", "text": "Then come in out of the rain."},
		{
			"op": "choice",
			"options": []any{
				map[string]any{"text": "I did.", "goto": "warmth_choice", "min": int64(2), "requires_stat": "courage"},
				map[string]any{"text": "I can't stay.", "goto": "leave", "cost": "5 coins"},
			},
		},
		{"op": "label", "id": "warmth_choice"},
		{"op": "goto", "label": "start"},
		{"op": "label", "id": "leave"},
		{"op": "return"},
	}

	if len(doc.Script) != len(expectedScript) {
		t.Fatalf("expected script length %d, got %d", len(expectedScript), len(doc.Script))
	}

	for i, cmd := range doc.Script {
		expected := expectedScript[i]
		// Marshal and unmarshal to normalize types for comparison (e.g. nested slices/maps)
		cmdJSON, _ := json.Marshal(cmd)
		expectedJSON, _ := json.Marshal(expected)
		var normCmd, normExpected map[string]any
		json.Unmarshal(cmdJSON, &normCmd)
		json.Unmarshal(expectedJSON, &normExpected)

		if !reflect.DeepEqual(normCmd, normExpected) {
			t.Errorf("at index %d:\nexpected: %+v\ngot:      %+v", i, normExpected, normCmd)
		}
	}
}

func TestConvertAnimAndMove(t *testing.T) {
	src := `
scene anim_test
anim id=hero prop=y keys="0:0 1:0.5" loop=true ease=inOutSine
anim id=hero layer=face prop=rotation keys="0:0 2:8" interp=spline
move id=hero path="0,0 1,1" dur=2 ease=outCubic
`
	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}

	expected := []Cmd{
		{"op": "anim", "id": "hero", "anim": map[string]any{
			"loop": true, "duration": 1.0,
			"tracks": []any{map[string]any{
				"prop": "y", "ease": "inOutSine",
				"keys": []any{[]any{0.0, 0.0}, []any{1.0, 0.5}},
			}},
		}},
		{"op": "anim", "id": "hero", "anim": map[string]any{
			"loop": false, "duration": 2.0,
			"tracks": []any{map[string]any{
				"prop": "rotation", "layer": "face", "interp": "spline",
				"keys": []any{[]any{0.0, 0.0}, []any{2.0, 8.0}},
			}},
		}},
		{"op": "anim", "id": "hero", "anim": map[string]any{
			"loop": false, "duration": 2.0,
			"tracks": []any{
				map[string]any{"prop": "screen_x", "ease": "outCubic", "keys": []any{[]any{0.0, 0.0}, []any{2.0, 1.0}}},
				map[string]any{"prop": "screen_y", "ease": "outCubic", "keys": []any{[]any{0.0, 0.0}, []any{2.0, 1.0}}},
			},
		}},
	}

	if len(doc.Script) != len(expected) {
		t.Fatalf("expected %d commands, got %d", len(expected), len(doc.Script))
	}
	for i, cmd := range doc.Script {
		cmdJSON, _ := json.Marshal(cmd)
		expJSON, _ := json.Marshal(expected[i])
		var normCmd, normExp map[string]any
		json.Unmarshal(cmdJSON, &normCmd)
		json.Unmarshal(expJSON, &normExp)
		if !reflect.DeepEqual(normCmd, normExp) {
			t.Errorf("at index %d:\nexpected: %s\ngot:      %s", i, expJSON, cmdJSON)
		}
	}
}

// A typo'd interp must fail the compile: the runtime falls back to linear for
// unknown values, which would silently flatten the author's curve.
func TestConvertAnimRejectsUnknownInterp(t *testing.T) {
	_, err := Convert(`
scene t
anim id=h prop=y keys="0:0 1:1" interp=spilne
`)
	if err == nil || !strings.Contains(err.Error(), "interp") {
		t.Fatalf("expected an interp error, got %v", err)
	}
}

func TestConvertAnimOneLinerYoyoStop(t *testing.T) {
	src := `
scene t
anim id=h prop=scale to=1.15 dur=0.4 ease=outBack
anim id=h prop=y keys="0:0 1:-0.05 2:0" loop=yoyo
move id=h to=0.2,-0.05 dur=1
anim id=h stop=all
`
	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}
	expected := []Cmd{
		{"op": "anim", "id": "h", "anim": map[string]any{
			"loop": false, "duration": 0.4,
			"tracks": []any{map[string]any{"prop": "scale", "ease": "outBack",
				"keys": []any{[]any{0.0, 1.0}, []any{0.4, 1.15}}}},
		}},
		{"op": "anim", "id": "h", "anim": map[string]any{
			"loop": true, "yoyo": true, "duration": 2.0,
			"tracks": []any{map[string]any{"prop": "y",
				"keys": []any{[]any{0.0, 0.0}, []any{1.0, -0.05}, []any{2.0, 0.0}}}},
		}},
		{"op": "anim", "id": "h", "anim": map[string]any{
			"loop": false, "duration": 1.0,
			"tracks": []any{
				map[string]any{"prop": "screen_x", "keys": []any{[]any{0.0, 0.0}, []any{1.0, 0.2}}},
				map[string]any{"prop": "screen_y", "keys": []any{[]any{0.0, 0.0}, []any{1.0, -0.05}}},
			},
		}},
		{"op": "anim", "id": "h", "stop": "all"},
	}
	if len(doc.Script) != len(expected) {
		t.Fatalf("expected %d commands, got %d", len(expected), len(doc.Script))
	}
	for i, cmd := range doc.Script {
		c, _ := json.Marshal(cmd)
		e, _ := json.Marshal(expected[i])
		var nc, ne map[string]any
		json.Unmarshal(c, &nc)
		json.Unmarshal(e, &ne)
		if !reflect.DeepEqual(nc, ne) {
			t.Errorf("at %d:\nexpected: %s\ngot:      %s", i, e, c)
		}
	}
}

// defanim/play: named animations expand at compile time — the runtime only
// ever sees plain "anim" commands; play's own params override the definition.
func TestConvertDefanimPlayExpansion(t *testing.T) {
	doc, err := Convert(`
scene t
defanim shake prop=x keys="0:0 0.1:0.02 0.2:0"
play id=codel anim=shake
play guard shake
play id=codel anim=shake mode=queue
`)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}
	if len(doc.Script) != 3 {
		t.Fatalf("want 3 anim commands (defanim emits none), got %d", len(doc.Script))
	}
	for i, c := range doc.Script {
		if c["op"] != "anim" {
			t.Fatalf("cmd %d: op = %v, want anim", i, c["op"])
		}
	}
	if doc.Script[0]["id"] != "codel" || doc.Script[1]["id"] != "guard" {
		t.Fatalf("ids: %v / %v", doc.Script[0]["id"], doc.Script[1]["id"])
	}
	if doc.Script[2]["mode"] != "queue" {
		t.Fatalf("play params must override/extend the definition, mode = %v", doc.Script[2]["mode"])
	}
}

// An unknown name is a compile error, not silent narration.
func TestConvertPlayUnknownNameFails(t *testing.T) {
	_, err := Convert(`
scene t
play id=x anim=nope
`)
	if err == nil || !strings.Contains(err.Error(), "unknown animation") {
		t.Fatalf("expected unknown-animation error, got %v", err)
	}
}

// `input var=… prompt=…` compiles as a plain command, and a `choice timeout=…`
// prefix line folds its attributes into the option block that follows.
func TestConvertInputAndTimedChoice(t *testing.T) {
	src := `
scene ti
input var=name prompt="Кто ты?" default="Гость" max=24
choice timeout=10 timeout_goto=late
- Да -> yes
- Нет -> no
`
	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}
	if len(doc.Script) != 2 {
		t.Fatalf("expected 2 commands, got %d", len(doc.Script))
	}
	in := doc.Script[0]
	if in["op"] != "input" || in["var"] != "name" || in["prompt"] != "Кто ты?" {
		t.Errorf("input mis-compiled: %v", in)
	}
	ch := doc.Script[1]
	if ch["op"] != "choice" {
		t.Fatalf("expected the option block, got %v", ch)
	}
	if fmt.Sprint(ch["timeout"]) != "10" {
		t.Errorf("timeout not folded into the choice: %v (%T)", ch["timeout"], ch["timeout"])
	}
	if ch["timeout_goto"] != "late" {
		t.Errorf("timeout_goto not folded: %v", ch["timeout_goto"])
	}
	if opts, _ := ch["options"].([]any); len(opts) != 2 {
		t.Errorf("options lost while folding: %v", ch["options"])
	}
}

// A `voice <url>` prefix line voices exactly the NEXT say (dialogue or
// narration) and never leaks onto the ones after it.
func TestConvertVoicePrefix(t *testing.T) {
	src := `
scene v
voice "/content/voice/a1.ogg"
Мара: Привет!
Без озвучки.
`
	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}
	if doc.Script[0]["voice"] != "/content/voice/a1.ogg" {
		t.Errorf("voiced line lost its url: %v", doc.Script[0])
	}
	if _, has := doc.Script[1]["voice"]; has {
		t.Errorf("voice leaked onto the next line: %v", doc.Script[1])
	}
}

// `def <name> <op …>` is a compile-time line-prefix macro: usage lines expand
// to "<template> <rest>" (later k=v args win) and the runtime never sees it.
func TestConvertDefPresetExpansion(t *testing.T) {
	doc, err := Convert(`
scene t
def code text code x=3 y=12.5 size=50 color=#9fe8a8
def enter actor hill left idle x=.24
code «actor hill left idle»
enter
enter hair=red
`)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}
	if len(doc.Script) != 3 {
		t.Fatalf("want 3 commands (def emits none), got %d", len(doc.Script))
	}
	if doc.Script[0]["op"] != "text" || doc.Script[0]["id"] != "code" || doc.Script[0]["text"] != "actor hill left idle" {
		t.Fatalf("label expansion wrong: %v", doc.Script[0])
	}
	if doc.Script[1]["op"] != "actor" || doc.Script[1]["id"] != "hill" || doc.Script[1]["x"] != 0.24 {
		t.Fatalf("actor expansion wrong: %v", doc.Script[1])
	}
	if doc.Script[2]["hair"] != "red" {
		t.Fatalf("usage args must extend the template: %v", doc.Script[2])
	}
}

// A def may not shadow a built-in op, and runaway recursion is an error.
func TestConvertDefPresetGuards(t *testing.T) {
	if _, err := Convert("scene t\ndef actor actor hill left\n"); err == nil || !strings.Contains(err.Error(), "shadows") {
		t.Fatalf("expected shadow error, got %v", err)
	}
	if _, err := Convert("scene t\ndef a b 1\ndef b a 1\na\n"); err == nil || !strings.Contains(err.Error(), "expansion loop") {
		t.Fatalf("expected expansion-loop error, got %v", err)
	}
}

// An expression function (`func f(a) { return <expr> }`) leaves NO commands
// behind: every call site is inlined into the expression itself, in all three
// places a runtime evaluates one — a `set`, an `if` and a {…} interpolation —
// plus a choice option's filter. Before this, the definition lowered to
// call/return and the CALL stayed an expression no evaluator knew, so the
// variable silently read 0.
func TestConvertExprFuncInlining(t *testing.T) {
	doc, err := Convert(`
scene t
func add(a, b) { return a + b }
func scaled(n) {
  return floor(n * 3 / 2)
}
func both(x) { return add(x, scaled(x)) }
gold = 1
gold = gold + add(2, 3)
big = scaled(gold + 1)
chain = both(4)
Total {add(gold, 1)}.
if add(gold, 0) > 3 -> ok
:ok
- pick -> ok2 expr="scaled(gold) > 1"
- skip -> ok2
:ok2
end
`)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}
	for i, c := range doc.Script {
		if c["op"] == "call" || c["op"] == "return" {
			t.Fatalf("expression function must emit no call/return, got %v at %d", c, i)
		}
		if id, _ := c["id"].(string); strings.HasPrefix(id, "__fn_") {
			t.Fatalf("expression function must emit no body label, got %v at %d", c, i)
		}
	}
	want := map[int]string{
		1: "gold + (2 + 3)",              // nested in a bigger expression
		2: "(floor((gold + 1) * 3 / 2))", // a non-atomic argument keeps its brackets
		3: "(4 + (floor(4 * 3 / 2)))",    // func calling funcs: whole chain inlined
	}
	for i, expr := range want {
		if got := doc.Script[i]["expr"]; got != expr {
			t.Fatalf("script[%d] expr = %q, want %q", i, got, expr)
		}
	}
	if got := doc.Script[4]["text"]; got != "Total {(gold + 1)}." {
		t.Fatalf("interpolation not inlined: %q", got)
	}
	if got := doc.Script[5]["expr"]; got != "(gold + 0) > 3" {
		t.Fatalf("if-condition not inlined: %q", got)
	}
	opts := doc.Script[8]["options"].([]any)
	if got := opts[0].(map[string]any)["expr"]; got != "(floor(gold * 3 / 2)) > 1" {
		t.Fatalf("choice option filter not inlined: %q", got)
	}
}

// A procedure (`func p() { <commands> }`) keeps the call/return lowering and is
// invoked as a statement. The body's own trailing `return` must not be doubled by
// the safety return the closing brace adds.
func TestConvertProcedureLowering(t *testing.T) {
	doc, err := Convert(`
scene t
func greet(who) {
  Hello, {who}.
}
func early(flag) {
  if flag > 0 { return }
  Not skipped.
  return
}
greet("Mara")
early(1)
`)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}
	var ops []string
	for _, c := range doc.Script {
		ops = append(ops, fmt.Sprint(c["op"]))
	}
	joined := strings.Join(ops, ",")
	if strings.Contains(joined, "return,return") {
		t.Fatalf("duplicated safety return in %v", joined)
	}
	calls := 0
	for _, c := range doc.Script {
		if c["op"] == "call" {
			calls++
			if !strings.HasPrefix(c["label"].(string), "__fn_") {
				t.Fatalf("call must target the func body label: %v", c)
			}
		}
	}
	if calls != 2 {
		t.Fatalf("want 2 procedure calls, got %d (%v)", calls, joined)
	}
	// Each argument binds to a plain variable right before its jump.
	n := len(doc.Script)
	if doc.Script[n-4]["key"] != "who" || doc.Script[n-2]["key"] != "flag" {
		t.Fatalf("procedure arguments not bound: %v", doc.Script[n-4:])
	}
}

// Every way of getting `func` wrong is a compile error with a sentence that says
// what to do — never a silent 0 at runtime.
func TestConvertFuncErrors(t *testing.T) {
	cases := []struct{ name, src, want string }{
		{"recursion", "scene t\nfunc f(n) { return f(n - 1) }\nx = f(3)\n", "recursive"},
		{"mutual recursion", "scene t\nfunc a(n) { return b(n) }\nfunc b(n) { return a(n) }\nx = a(1)\n", "recursive"},
		{"too few args", "scene t\nfunc add(a, b) { return a + b }\nx = add(1)\n", "takes 2 argument(s), got 1"},
		{"too many args", "scene t\nfunc inc1(a) { return a + 1 }\nx = inc1(1, 2)\n", "takes 1 argument(s), got 2"},
		{"procedure args", "scene t\nfunc p(a) {\n  Hi {a}.\n}\np()\n", "takes 1 argument(s), got 0"},
		{"procedure in expression", "scene t\nfunc p(a) {\n  Hi {a}.\n}\nx = p(1) + 1\n", "is a procedure"},
		{"expression func as statement", "scene t\nfunc add(a, b) { return a + b }\nadd(1, 2)\n", "returns a value"},
		{"duplicate declaration", "scene t\nfunc f(a) { return a }\nfunc f(b) { return b }\nx = f(1)\n", "already declared"},
		{"unclosed body", "scene t\nfunc f(a) {\n  return a\n", "missing closing"},
	}
	for _, c := range cases {
		_, err := Convert(c.src)
		if err == nil {
			t.Fatalf("%s: expected an error", c.name)
		}
		if !strings.Contains(err.Error(), c.want) {
			t.Fatalf("%s: error %q must mention %q", c.name, err, c.want)
		}
	}
}

// A func name mentioned in prose (outside a {…} span) is text, not a call — the
// inliner only rewrites expressions and interpolations.
func TestConvertFuncLeavesProseAlone(t *testing.T) {
	doc, err := Convert(`
scene t
func offer(base) { return base * 2 }
price = offer(10)
The trader will offer(a hand) if you ask.
«A long line — offer(nothing) is still prose, but {offer(price)} is not.»
`)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}
	if got := doc.Script[1]["text"]; got != "The trader will offer(a hand) if you ask." {
		t.Fatalf("prose was rewritten: %q", got)
	}
	if got := doc.Script[2]["text"]; got != "A long line — offer(nothing) is still prose, but {(price * 2)} is not." {
		t.Fatalf("chevron line wrong: %q", got)
	}
}

// Ink-style text alternatives (mostly an ink-import artefact) share the {…}
// syntax but their branches are prose, not expressions — only the condition head
// before `:` may be inlined, and a bare sequence is left completely alone.
func TestConvertFuncInTextAlternatives(t *testing.T) {
	doc, err := Convert(`
scene t
func rich(g) { return g > 10 }
gold = 12
Keeper: The purse is {rich(gold): heavy|light}, the mood {calm|tense|calm}.
`)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}
	want := "The purse is {(gold > 10): heavy|light}, the mood {calm|tense|calm}."
	if got := doc.Script[1]["text"]; got != want {
		t.Fatalf("alternatives handling wrong:\n got %q\nwant %q", got, want)
	}
}

// The compiler's "don't shadow a built-in" list and the validator's "these are the
// only functions" list are the same set, in two packages that don't share code.
// Bind them, or the next built-in added on one side becomes a silent gap on the
// other (the T2 drift pattern: one dictionary, several implementations).
func TestExprBuiltinsMatchValidator(t *testing.T) {
	for name := range lvn.ExprFuncs {
		if !exprBuiltins[name] {
			t.Errorf("lvn.ExprFuncs has %q, lvns.exprBuiltins does not", name)
		}
	}
	for name := range exprBuiltins {
		if !lvn.ExprFuncs[name] {
			t.Errorf("lvns.exprBuiltins has %q, lvn.ExprFuncs does not", name)
		}
	}
}

// Shadowing a built-in would quietly re-point every existing call in the file.
func TestConvertFuncCannotShadowBuiltin(t *testing.T) {
	_, err := Convert("scene t\nfunc floor(x) { return x }\ny = floor(1.5)\n")
	if err == nil || !strings.Contains(err.Error(), "built-in expression function") {
		t.Fatalf("expected a shadowing error, got %v", err)
	}
}

// ── choice option bodies (`- text -> label { … }`) ──────────────────────────
//
// The body is the command list LvnPlayer.Choose runs on pick. Until the block
// form existed it had no source spelling at all, so every "ask this once"
// option lost the flag that retires it on the first re-save (audit O3).

func TestConvertChoiceOptionBody(t *testing.T) {
	doc, err := Convert("scene t\n- Спросить -> q1 expr=\"!_once_q1\" {\n    _once_q1 = true\n    hint text=\"ok\"\n}\n- Уйти -> __end\n:q1\nответ\n")
	if err != nil {
		t.Fatal(err)
	}
	opts, _ := doc.Script[0]["options"].([]any)
	if len(opts) != 2 {
		t.Fatalf("options: %v", doc.Script[0])
	}
	opt := opts[0].(map[string]any)
	if opt["expr"] != "!_once_q1" {
		t.Fatalf("the option's own params were eaten by the block: %v", opt)
	}
	if _, has := opt["goto"]; has {
		t.Fatalf("a body option must carry its jump IN the body (the runtime ignores goto then): %v", opt)
	}
	body, _ := opt["body"].([]any)
	if len(body) != 3 {
		t.Fatalf("body: %v", body)
	}
	if b := body[0].(map[string]any); b["op"] != "set" || b["key"] != "_once_q1" {
		t.Fatalf("first body command: %v", b)
	}
	if b := body[1].(map[string]any); b["op"] != "hint" {
		t.Fatalf("staging command lost from the body: %v", b)
	}
	if b := body[2].(map[string]any); b["op"] != "goto" || b["label"] != "q1" {
		t.Fatalf("the header's target must close the body: %v", b)
	}
	// The plain option next to it is untouched.
	if opts[1].(map[string]any)["goto"] != "__end" {
		t.Fatalf("plain option changed shape: %v", opts[1])
	}
}

// A body without a jump falls through past the choice — the arrow-less header.
func TestConvertChoiceOptionBodyFallsThrough(t *testing.T) {
	doc, err := Convert("scene t\n- Закрыть expr=\"menu\" {\n    menu = false\n}\nдальше\n")
	if err != nil {
		t.Fatal(err)
	}
	opt := doc.Script[0]["options"].([]any)[0].(map[string]any)
	if opt["text"] != "Закрыть" || opt["expr"] != "menu" {
		t.Fatalf("arrow-less header parsed wrong: %v", opt)
	}
	if _, has := opt["goto"]; has {
		t.Fatalf("fall-through option gained a target: %v", opt)
	}
	if body, _ := opt["body"].([]any); len(body) != 1 {
		t.Fatalf("body: %v", opt)
	}
}

// `{gold}` in option text is interpolation, not a block: only a brace that ENDS
// the line opens one. Getting this wrong would shred every priced option.
func TestConvertChoiceOptionInterpolationIsNotABlock(t *testing.T) {
	doc, err := Convert("scene t\n- Осталось {gold} монет -> shop\n:shop\nдальше\n")
	if err != nil {
		t.Fatal(err)
	}
	opt := doc.Script[0]["options"].([]any)[0].(map[string]any)
	if opt["text"] != "Осталось {gold} монет" || opt["goto"] != "shop" {
		t.Fatalf("interpolated option mangled: %v", opt)
	}
}

// What a block still CANNOT be. A block holding prose or flow is no longer an
// error — it is woven into script (see weave_test.go); these two are malformed
// source, which is a different thing and must stay loud.
func TestConvertChoiceOptionBodyRejectsControlFlow(t *testing.T) {
	for _, tc := range []struct{ name, src, want string }{
		{"nested block", "scene t\n- A -> x {\n    if gold > 1 {\n        y = 1\n    }\n}\n:x\nz\n", "nested blocks are not allowed"},
		{"unclosed", "scene t\n- A -> x {\n    y = 1\n:x\nz\n", "unclosed choice option body"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			_, err := Convert(tc.src)
			if err == nil || !strings.Contains(err.Error(), tc.want) {
				t.Fatalf("want %q, got %v", tc.want, err)
			}
		})
	}
}

// ── synthetic label stability (the save anchor) ──────────────────────────────
//
// A save is anchored on the id of the nearest preceding label, so the names the
// lowering mints ARE part of the save format. When they were a global counter,
// inserting one `if` at the top of a chapter renumbered every synthetic label
// below it (audit O16: 837 renames in one re-save) and every save anchored under
// one of them silently resumed somewhere else.

func labelIDs(t *testing.T, src string) []string {
	t.Helper()
	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("compile: %v", err)
	}
	var out []string
	for _, c := range doc.Script {
		if c["op"] == "label" {
			out = append(out, c["id"].(string))
		}
	}
	return out
}

func TestSyntheticLabelsSurviveAnEditElsewhere(t *testing.T) {
	base := "scene t\n:one\nif gold > 1 {\n  A\n}\n:two\nif hp > 1 {\n  B\n} else {\n  C\n}\n:three\nif x -> two\nD\n"
	// An edit in the FIRST scene: a new line, and a whole new branch.
	edited := "scene t\n:one\nprologue\nif mood > 0 {\n  M\n}\nif gold > 1 {\n  A\n}\n:two\nif hp > 1 {\n  B\n} else {\n  C\n}\n:three\nif x -> two\nD\n"

	before, after := labelIDs(t, base), labelIDs(t, edited)
	// Every label that belongs to a scene the edit did not touch must be
	// byte-identical — those are the anchors old saves are holding.
	inScope := func(ids []string, scope string) []string {
		var out []string
		for _, id := range ids {
			if strings.Contains(id, "_"+scope+"_") {
				out = append(out, id)
			}
		}
		return out
	}
	for _, scope := range []string{"two", "three"} {
		b, a := inScope(before, scope), inScope(after, scope)
		if len(b) == 0 {
			t.Fatalf("scope %q has no synthetic labels — the test proves nothing: %v", scope, before)
		}
		if strings.Join(b, ",") != strings.Join(a, ",") {
			t.Errorf("editing scene :one renamed scene :%s's labels — every save anchored there moves\n"+
				"before: %v\nafter:  %v", scope, b, a)
		}
	}
	// And re-compiling the very same source is of course a no-op.
	if again := labelIDs(t, base); strings.Join(again, ",") != strings.Join(before, ",") {
		t.Errorf("compilation is not deterministic:\n%v\n%v", before, again)
	}
}

// A lowering must never mint a name the script already uses: it would merge two
// different places into one jump target (and one save anchor).
func TestSyntheticLabelsDoNotShadowAnAuthorLabel(t *testing.T) {
	ids := labelIDs(t, "scene t\n:one\n:__then_one_1\nA\nif gold > 1 {\n  B\n}\nC\n")
	seen := map[string]bool{}
	for _, id := range ids {
		if seen[id] {
			t.Fatalf("duplicate label %q in %v", id, ids)
		}
		seen[id] = true
	}
	if !seen["__then_one_1"] {
		t.Fatalf("the author's own label vanished: %v", ids)
	}
}

// «Ё» существует в двух видах: одним символом (NFC) и «Е» с комбинирующим
// знаком (NFD). macOS отдаёт файлы в NFD, редакторы — по-разному, и две внешне
// одинаковые строки при этом не равны: каталог перевода молча не находит
// реплику, `if имя == "Ёжик"` не срабатывает. Компилятор приводит вход к NFC,
// чтобы дальше по конвейеру формы не смешивались.
func TestConvertNormalizesUnicodeToNFC(t *testing.T) {
	const nfc = "Отлично! Ёжик, ёлка, объём."
	nfd := norm.NFD.String(nfc)
	if nfd == nfc {
		t.Fatal("фикстура бессмысленна: NFD-форма совпала с NFC")
	}

	doc, err := Convert("scene t\n\nКатя: " + nfd + "\n")
	if err != nil {
		t.Fatalf("компиляция NFD-исходника: %v", err)
	}
	var got string
	for _, c := range doc.Script {
		if c["op"] == "say" {
			got, _ = c["text"].(string)
		}
	}
	if got != nfc {
		t.Errorf("реплика осталась не в NFC:\n получили %q\n ожидали %q", got, nfc)
	}
}
