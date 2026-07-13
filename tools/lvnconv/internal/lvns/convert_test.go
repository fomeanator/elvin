package lvns

import (
	"encoding/json"
	"fmt"
	"reflect"
	"strings"
	"testing"
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
