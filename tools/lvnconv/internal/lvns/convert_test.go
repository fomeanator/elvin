package lvns

import (
	"encoding/json"
	"reflect"
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
		{"op": "say", "who": "Mara", "text": "You came back."},
		{"op": "actor", "id": "mara_custom", "emotion": "smile"},
		{"op": "say", "who": "Mara", "text": "Then come in out of the rain."},
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
