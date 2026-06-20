package ink

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
)

func mustConvert(t *testing.T, src string) *Doc {
	t.Helper()
	d, err := Convert(src)
	if err != nil {
		t.Fatalf("Convert: %v", err)
	}
	return d
}

func mustFail(t *testing.T, src, wantSub string) {
	t.Helper()
	_, err := Convert(src)
	if err == nil {
		t.Fatalf("expected error containing %q, got success", wantSub)
	}
	if !strings.Contains(err.Error(), wantSub) {
		t.Fatalf("error %q does not contain %q", err, wantSub)
	}
}

func js(t *testing.T, v any) string {
	t.Helper()
	b, err := json.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	return string(b)
}

func opAt(t *testing.T, d *Doc, i int) string {
	t.Helper()
	if i >= len(d.Script) {
		t.Fatalf("script has %d commands, want index %d; script=%s", len(d.Script), i, js(t, d.Script))
	}
	op, _ := d.Script[i]["op"].(string)
	return op
}

// ── Basics ──────────────────────────────────────────────────────────────────

func TestSpeakerNarrationAndEmotion(t *testing.T) {
	d := mustConvert(t, `
# actors: Луна=heroine
# scene: intro
Город проснулся под серым небом.
Луна [happy]: Привет, путник.
Внимание: двери закрываются.
`)
	if d.Scene != "intro" {
		t.Fatalf("scene = %q", d.Scene)
	}
	// narration (who=null)
	if d.Script[0]["who"] != nil || d.Script[0]["text"] != "Город проснулся под серым небом." {
		t.Fatalf("narration wrong: %s", js(t, d.Script[0]))
	}
	// emotion swap before say
	if opAt(t, d, 1) != "actor" || d.Script[1]["id"] != "heroine" || d.Script[1]["emotion"] != "happy" {
		t.Fatalf("emotion swap wrong: %s", js(t, d.Script[1]))
	}
	if d.Script[2]["who"] != "Луна" {
		t.Fatalf("speaker wrong: %s", js(t, d.Script[2]))
	}
	// with an actors map declared, unmapped "Внимание:" stays narration
	if d.Script[3]["who"] != nil || !strings.Contains(d.Script[3]["text"].(string), "Внимание") {
		t.Fatalf("unmapped speaker must be narration: %s", js(t, d.Script[3]))
	}
}

func TestKnotStitchDivertResolution(t *testing.T) {
	d := mustConvert(t, `
-> start
== start ==
Привет.
-> finale
= deep
Глубже.
== finale ==
-> deep_ref
== deep_ref ==
-> start.deep
`)
	// top divert resolved to knot
	if opAt(t, d, 0) != "goto" || d.Script[0]["label"] != "start" {
		t.Fatalf("goto wrong: %s", js(t, d.Script[0]))
	}
	found := false
	for _, c := range d.Script {
		if c["op"] == "label" && c["id"] == "start.deep" {
			found = true
		}
	}
	if !found {
		t.Fatalf("stitch label start.deep missing: %s", js(t, d.Script))
	}
}

func TestBareStitchRefInsideKnot(t *testing.T) {
	d := mustConvert(t, `
== start ==
-> deep
= deep
Тут.
-> END
`)
	if d.Script[1]["label"] != "start.deep" {
		t.Fatalf("bare stitch ref not resolved: %s", js(t, d.Script[1]))
	}
	// -> END appends __end label
	last := d.Script[len(d.Script)-1]
	if last["op"] != "label" || last["id"] != "__end" {
		t.Fatalf("__end label missing: %s", js(t, last))
	}
}

func TestUnknownDivertFails(t *testing.T) {
	mustFail(t, "-> nowhere\n", `unknown target "nowhere"`)
}

// ── Logic / vars ────────────────────────────────────────────────────────────

func TestVarAndLogic(t *testing.T) {
	d := mustConvert(t, `
VAR courage = 0
~ met_anya = true
~ courage = courage + 2
~ courage = courage - 1
`)
	want := []string{
		`{"key":"courage","op":"set","value":0}`,
		`{"key":"met_anya","op":"set","value":true}`,
		`{"by":2,"key":"courage","op":"inc"}`,
		`{"by":-1,"key":"courage","op":"inc"}`,
	}
	for i, w := range want {
		if js(t, d.Script[i]) != w {
			t.Fatalf("cmd %d = %s, want %s", i, js(t, d.Script[i]), w)
		}
	}
}

// ── Tags ────────────────────────────────────────────────────────────────────

func TestStagingTags(t *testing.T) {
	d := mustConvert(t, `
# bg: ch1.forest sprite_url=/content/bg/forest.jpg
# actor: heroine show=true position=left sprite_url=/content/actors/h.png
# audio: music play /content/audio/theme.ogg loop=true volume=0.8
# fade: clear 0.6
# dim: 0.4 0.6
# camera: shake amplitude=8 duration=0.25
# particles: rain
# particles: rain off
# preload: /content/bg/a.png /content/audio/b.ogg
# hint: Подойди к двери
# hint: off
# wait: 600
`)
	ops := []string{"bg", "actor", "audio", "fade", "dim", "camera", "particles", "particles", "preload", "hint", "hint", "wait"}
	for i, op := range ops {
		if opAt(t, d, i) != op {
			t.Fatalf("cmd %d op = %q want %q: %s", i, opAt(t, d, i), op, js(t, d.Script[i]))
		}
	}
	if d.Script[0]["sprite_url"] != "/content/bg/forest.jpg" || d.Script[0]["id"] != "ch1.forest" {
		t.Fatalf("bg wrong: %s", js(t, d.Script[0]))
	}
	if d.Script[2]["url"] != "/content/audio/theme.ogg" || d.Script[2]["loop"] != true {
		t.Fatalf("audio wrong: %s", js(t, d.Script[2]))
	}
	pre := d.Script[8]["assets"].([]any)
	if pre[1].(map[string]any)["kind"] != "audio" {
		t.Fatalf("preload kind autodetect wrong: %s", js(t, d.Script[8]))
	}
	if d.Script[10]["show"] != false {
		t.Fatalf("hint off wrong: %s", js(t, d.Script[10]))
	}
}

func TestTagsAttachedToSayLine(t *testing.T) {
	d := mustConvert(t, `
Луна: Тише... # style: whisper # bg: ch1.night sprite_url=/content/bg/night.jpg
`)
	// staging first, then the say with merged style
	if opAt(t, d, 0) != "bg" {
		t.Fatalf("expected bg before say: %s", js(t, d.Script))
	}
	if opAt(t, d, 1) != "say" || d.Script[1]["style"] != "whisper" {
		t.Fatalf("say style not merged: %s", js(t, d.Script[1]))
	}
}

func TestUnknownTagFails(t *testing.T) {
	mustFail(t, "# bgg: oops\n", "unknown tag #bgg")
}

// ── Choices ─────────────────────────────────────────────────────────────────

func TestChoiceSimpleDiverts(t *testing.T) {
	// `+` — sticky options: direct goto wiring without once-gates.
	d := mustConvert(t, `
== scene ==
Выбор?
+ [Согласиться] -> yes
+ [Отказать (50 soft)] -> no
== yes ==
Да.
-> END
== no ==
Нет.
-> END
`)
	var choice Cmd
	for _, c := range d.Script {
		if c["op"] == "choice" {
			choice = c
		}
	}
	opts := choice["options"].([]any)
	o0 := opts[0].(Cmd)
	if o0["text"] != "Согласиться" || o0["goto"] != "yes" {
		t.Fatalf("opt0 wrong: %s", js(t, o0))
	}
	o1 := opts[1].(Cmd)
	cost := o1["cost"].(map[string]any)
	if o1["text"] != "Отказать" || cost["amount"] != int64(50) || cost["currency"] != "soft" {
		t.Fatalf("opt1 cost wrong: %s", js(t, o1))
	}
}

func TestChoiceBodiesAndGather(t *testing.T) {
	// `+` (sticky) options keep the lean wiring — no once-gates.
	d := mustConvert(t, `
== scene ==
+ [Поздороваться]
    Луна: Привет!
    ~ greeted = true
+ [Молчать]
- (after)
Дальше.
-> END
`)
	// option with body diverts to generated label; empty option goes to gather
	var choice Cmd
	for _, c := range d.Script {
		if c["op"] == "choice" {
			choice = c
		}
	}
	opts := choice["options"].([]any)
	bodyLbl := opts[0].(Cmd)["goto"].(string)
	if !strings.HasPrefix(bodyLbl, "__scene_c") {
		t.Fatalf("opt0 should go to generated body label, got %q", bodyLbl)
	}
	// named gathers are knot-scoped, like stitches
	if opts[1].(Cmd)["goto"] != "scene.after" {
		t.Fatalf("opt1 should go to gather: %s", js(t, opts[1]))
	}
	// body ends with goto to the gather
	src := js(t, d.Script)
	if !strings.Contains(src, `"id":"scene.after"`) {
		t.Fatalf("gather label missing: %s", src)
	}
	if !strings.Contains(src, `"greeted"`) {
		t.Fatalf("body lost: %s", src)
	}
}

func TestChoiceConditionStatAndNoDivertFails(t *testing.T) {
	d := mustConvert(t, `
== scene ==
* {met_anya} [Спросить про Аню] -> ask
* [Сила # stat: Смелость 2] -> brave
== ask ==
-> END
== brave ==
-> END
`)
	var choice Cmd
	for _, c := range d.Script {
		if c["op"] == "choice" {
			choice = c
		}
	}
	opts := choice["options"].([]any)
	iff := opts[0].(Cmd)["if"].(map[string]any)
	if iff["key"] != "met_anya" || iff["op"] != "eq" || iff["value"] != true {
		t.Fatalf("opt if wrong: %s", js(t, opts[0]))
	}
	o1 := opts[1].(Cmd)
	if o1["requires_stat"] != "Смелость" || o1["requires_min"] != int64(2) {
		t.Fatalf("stat gate wrong: %s", js(t, o1))
	}

	mustFail(t, `
== scene ==
* [Тупик]
Текст без дайверта.
`, "no divert and no closing gather")
}

// ── Conditionals ────────────────────────────────────────────────────────────

func TestCondDivertsBecomeIf(t *testing.T) {
	d := mustConvert(t, `
== scene ==
{courage >= 4:
    -> good
- else:
    -> bad
}
== good ==
-> END
== bad ==
-> END
`)
	var iff Cmd
	for _, c := range d.Script {
		if c["op"] == "if" {
			iff = c
		}
	}
	cond := iff["cond"].(map[string]any)
	if cond["key"] != "courage" || cond["op"] != "gte" || cond["value"] != int64(4) {
		t.Fatalf("cond wrong: %s", js(t, iff))
	}
	if iff["then"] != "good" || iff["else"] != "bad" {
		t.Fatalf("if branches wrong: %s", js(t, iff))
	}
}

func TestCondWithBodiesDesugars(t *testing.T) {
	d := mustConvert(t, `
== scene ==
{flower_given:
    Луна: Спасибо за цветок!
- else:
    Луна: Ты что-то хотел?
}
Продолжаем.
-> END
`)
	src := js(t, d.Script)
	for _, want := range []string{"__scene_ift", "__scene_ife", "__scene_ifend", "Спасибо за цветок!", "Ты что-то хотел?"} {
		if !strings.Contains(src, want) {
			t.Fatalf("desugared cond missing %q: %s", want, src)
		}
	}
}

func TestInlineCondWithDivertsFails(t *testing.T) {
	mustFail(t, "{x: -> a | -> b}\n", "inline {…} with diverts is not supported")
}

func TestInlineTextAlternatives_PassThroughToSay(t *testing.T) {
	// Engine-side TextAlternatives expands these at say-time; the converter
	// must pass them through verbatim — including blocks at line start.
	d := mustConvert(t, `
Луна: {met: Снова ты|Кто ты}, путник {~один|вдвоём}.
{холодно: Ветер пробирает до костей.|Тепло.}
`)
	if d.Script[0]["text"] != "{met: Снова ты|Кто ты}, путник {~один|вдвоём}." {
		t.Fatalf("alternatives damaged: %s", js(t, d.Script[0]))
	}
	if d.Script[1]["who"] != nil {
		t.Fatalf("line-start alternative must be narration: %s", js(t, d.Script[1]))
	}
}

// ── Misc safety ─────────────────────────────────────────────────────────────

func TestUnsupportedConstructsFail(t *testing.T) {
	mustFail(t, "INCLUDE other.ink\n", "not supported")
	mustFail(t, "== function f ==\n", "functions are not supported")
	mustFail(t, "->-> back\n", "cannot parse divert")
}

// ── Ink-parity: tunnels / expr / once-only / visit counts ───────────────────

func TestTunnels_CallAndReturn(t *testing.T) {
	d := mustConvert(t, `
== main ==
До.
-> scenelet ->
После.
-> END
== scenelet ==
Внутри.
->->
`)
	src := js(t, d.Script)
	if !strings.Contains(src, `{"label":"scenelet","op":"call"}`) {
		t.Fatalf("call missing: %s", src)
	}
	if !strings.Contains(src, `{"op":"return"}`) {
		t.Fatalf("return missing: %s", src)
	}
}

func TestCompoundCondition_BecomesExpr(t *testing.T) {
	d := mustConvert(t, `
== scene ==
{courage >= 2 && lied == false:
    -> brave
- else:
    -> coward
}
== brave ==
-> END
== coward ==
-> END
`)
	var iff Cmd
	for _, c := range d.Script {
		if c["op"] == "if" {
			iff = c
		}
	}
	if iff["expr"] != "courage >= 2 && lied == false" {
		t.Fatalf("compound cond must become expr: %s", js(t, iff))
	}
	if iff["cond"] != nil {
		t.Fatalf("expr and cond must not coexist: %s", js(t, iff))
	}
}

func TestLogicExpression_BecomesSetExpr(t *testing.T) {
	d := mustConvert(t, `
VAR bonus = 1
~ courage = bonus * 2 + 1
~ copy = bonus
`)
	if d.Script[1]["expr"] != "bonus * 2 + 1" || d.Script[1]["key"] != "courage" {
		t.Fatalf("set expr wrong: %s", js(t, d.Script[1]))
	}
	if d.Script[2]["expr"] != "bonus" {
		t.Fatalf("var copy must be expr: %s", js(t, d.Script[2]))
	}
}

func TestOnceOnlyChoices_GetGateAndSetter(t *testing.T) {
	d := mustConvert(t, `
== scene ==
* [Один раз] -> scene
+ [Сколько угодно] -> scene
`)
	var choice Cmd
	for _, c := range d.Script {
		if c["op"] == "choice" {
			choice = c
		}
	}
	opts := choice["options"].([]any)
	gate, _ := opts[0].(Cmd)["expr"].(string)
	if !strings.Contains(gate, "== 0") {
		t.Fatalf("once option must be gated: %s", js(t, opts[0]))
	}
	if _, has := opts[1].(Cmd)["expr"]; has {
		t.Fatalf("sticky option must not be gated: %s", js(t, opts[1]))
	}
	// selecting the once option must set its flag (routed via a body label)
	src := js(t, d.Script)
	if !strings.Contains(src, `once1","op":"set","value":1`) {
		t.Fatalf("once flag setter missing: %s", src)
	}
}

func TestVisitCounts_LabelRefsReadSeenCounters(t *testing.T) {
	d := mustConvert(t, `
== intro ==
Привет.
-> hub
== hub ==
{intro:
    -> seen_it
- else:
    -> END
}
== seen_it ==
{not intro: Странно.|Уже виделись.}
-> END
`)
	src := js(t, d.Script)
	// inc stamped right after the intro label
	if !strings.Contains(src, `{"by":1,"key":"__seen_intro","op":"inc"}`) {
		t.Fatalf("visit-count inc missing: %s", src)
	}
	// bare {intro:} became a counter read
	if !strings.Contains(src, `"key":"__seen_intro","op":"gte"`) {
		t.Fatalf("visit-count cond missing: %s", src)
	}
}

func TestConstSubstitution_InExpr(t *testing.T) {
	d := mustConvert(t, `
CONST CHESS = 2
VAR clue = 0
~ matched = clue == CHESS && clue > 0
`)
	if d.Script[1]["expr"] != "clue == 2 && clue > 0" {
		t.Fatalf("const not inlined in expr: %s", js(t, d.Script[1]))
	}
}

func TestCommentsAndUrlsSurvive(t *testing.T) {
	d := mustConvert(t, `
// шапка-комментарий
# bg: x sprite_url=https://cdn.example/bg.jpg // хвостовой комментарий
Текст. // и тут
`)
	if d.Script[0]["sprite_url"] != "https://cdn.example/bg.jpg" {
		t.Fatalf("URL damaged by comment stripper: %s", js(t, d.Script[0]))
	}
	if d.Script[1]["text"] != "Текст." {
		t.Fatalf("trailing comment not stripped: %s", js(t, d.Script[1]))
	}
}

// ── End-to-end fixtures ─────────────────────────────────────────────────────

func TestSampleInkConvertsWithIntactLabelGraph(t *testing.T) {
	src, err := os.ReadFile("sample.ink")
	if err != nil {
		t.Fatal(err)
	}
	d := mustConvert(t, string(src))

	labels := map[string]bool{}
	for _, c := range d.Script {
		if c["op"] == "label" {
			labels[c["id"].(string)] = true
		}
	}
	check := func(target any, where string) {
		s, _ := target.(string)
		if s != "" && !labels[s] {
			t.Errorf("%s references missing label %q", where, s)
		}
	}
	for _, c := range d.Script {
		switch c["op"] {
		case "goto":
			check(c["label"], "goto")
		case "if":
			check(c["then"], "if.then")
			check(c["else"], "if.else")
		case "choice":
			for _, o := range c["options"].([]any) {
				check(o.(Cmd)["goto"], "option")
			}
		}
	}
	if len(d.Script) < 30 {
		t.Fatalf("sample unexpectedly small: %d commands", len(d.Script))
	}
}

// Full-language inkle stories (downloaded from github.com/inkle/ink-library)
// must be REJECTED with a line-numbered error, never silently mis-converted.
func TestRealWorldStoriesFailGracefully(t *testing.T) {
	for _, f := range []string{"testdata/TheIntercept.ink", "testdata/LD41Emoji.ink"} {
		src, err := os.ReadFile(f)
		if err != nil {
			t.Skipf("fixture %s missing: %v", f, err)
		}
		_, err = Convert(string(src))
		if err == nil {
			t.Errorf("%s: expected unsupported-construct error, got success", f)
			continue
		}
		if !strings.Contains(err.Error(), "line ") {
			t.Errorf("%s: error lacks line number: %v", f, err)
		}
	}
}

func TestTextWithTrailingDivert(t *testing.T) {
	d := mustConvert(t, `
== a ==
Луна: Уходим. -> b
== b ==
-> END
`)
	if opAt(t, d, 1) != "say" || opAt(t, d, 2) != "goto" || d.Script[2]["label"] != "b" {
		t.Fatalf("trailing divert wrong: %s", js(t, d.Script))
	}
}
