package importer

import (
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
)

// applyProtagonistSpeakerRename and applySpeakerNameOverrides used to run
// ONLY inside PostProcessBundle (over an already-compiled .lvn's JSON ops via
// decodeScriptOps) — an ordinary single-project import never got the
// "{player}" rescoping or display-name overrides even with a correctly
// authored Template, because that Template logic never ran outside the
// bundle pipeline. These are now shared, plain-op-level functions Run/
// runMultiChapter call directly on doc.Script — this test proves the logic
// itself is correct independent of which caller feeds it ops.
func TestApplyProtagonistSpeakerRename(t *testing.T) {
	tpl := &Template{Staging: StagingTemplate{
		ProtagonistSpeakerLabels: []string{"Главный герой", "Игрок"},
		PlayerTemplate:           "{player}",
	}}
	ops := cmdsAsOps([]articy.Cmd{
		{"op": "say", "who": "Главный герой", "text": "я"},
		{"op": "say", "who": "Игрок", "text": "выбор"},
		{"op": "say", "who": "Тимур", "text": "не трогать"},
		{"op": "bg", "id": "x"}, // non-say op must be ignored, not panic
	})

	if !applyProtagonistSpeakerRename(ops, tpl) {
		t.Fatal("expected a change")
	}
	if ops[0]["who"] != "{player}" || ops[1]["who"] != "{player}" {
		t.Errorf("protagonist speaker labels not rewritten: %v %v", ops[0], ops[1])
	}
	if ops[2]["who"] != "Тимур" {
		t.Errorf("non-protagonist speaker must be left alone: %v", ops[2])
	}

	if applyProtagonistSpeakerRename(ops, tpl) {
		t.Error("second pass over already-rewritten ops should be a no-op (idempotent)")
	}
}

func TestApplyProtagonistSpeakerRenameDefaultPlayerTemplate(t *testing.T) {
	tpl := &Template{Staging: StagingTemplate{ProtagonistSpeakerLabels: []string{"ГГ"}}}
	ops := cmdsAsOps([]articy.Cmd{{"op": "say", "who": "ГГ", "text": "т"}})
	applyProtagonistSpeakerRename(ops, tpl)
	if ops[0]["who"] != "{player}" {
		t.Errorf("empty PlayerTemplate should fall back to {player}, got %v", ops[0]["who"])
	}
}

func TestApplySpeakerNameOverrides(t *testing.T) {
	tpl := &Template{SpeakerNames: map[string]string{"Bandit": "Бандит"}}
	ops := cmdsAsOps([]articy.Cmd{
		{"op": "say", "who": "Bandit_black_dead", "text": "a"}, // variant-suffix fallback
		{"op": "say", "who": "{player}", "text": "b"},          // already the player template → skip
		{"op": "say", "text": "c"},                             // narration, no who → skip
		{"op": "say", "who": "Unknown", "text": "d"},           // no mapping → unchanged
	})

	if !applySpeakerNameOverrides(ops, tpl) {
		t.Fatal("expected a change")
	}
	if ops[0]["who"] != "Бандит" {
		t.Errorf("variant-suffix name should resolve to the base display name, got %v", ops[0]["who"])
	}
	if ops[1]["who"] != "{player}" {
		t.Errorf("player-template who must not be touched: %v", ops[1])
	}
	if ops[3]["who"] != "Unknown" {
		t.Errorf("unmapped speaker must be left alone: %v", ops[3])
	}
}

func TestApplySpeakerNameOverridesToSprites(t *testing.T) {
	tpl := &Template{SpeakerNames: map[string]string{"Bandit": "Бандит"}}
	sprites := map[string]any{
		"Bandit": map[string]any{"name": "Bandit", "layers": []any{"x.png"}},
		"Tolya":  map[string]any{"name": "Tolya", "layers": []any{"y.png"}},
	}
	applySpeakerNameOverridesToSprites(sprites, tpl)

	if sprites["Bandit"].(map[string]any)["name"] != "Бандит" {
		t.Errorf("sprite display name not overridden: %v", sprites["Bandit"])
	}
	if sprites["Tolya"].(map[string]any)["name"] != "Tolya" {
		t.Errorf("unmapped sprite must be left alone: %v", sprites["Tolya"])
	}
}
