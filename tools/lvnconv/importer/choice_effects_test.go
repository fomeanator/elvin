package importer

import (
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
)

// Живой баг партнёра: линеаризованная ветка кончается "goto merge", а превью
// сканировало дальше ПО ФАЙЛУ и читало inc СОСЕДНЕЙ ветки — кнопка обещала
// «+1 Роман», которого этот выбор никогда не начислит. Превью обязано идти
// по потоку: goto — прыжок, choice/if/end — граница.
func TestAnnotateChoiceEffects_FollowsFlowNotFileOrder(t *testing.T) {
	tpl := &Template{}
	tpl.Stats.RelationshipNamespace = "rel"
	tpl.SpeakerNames = map[string]string{"Roman": "Роман"}

	script := []articy.Cmd{
		{"op": "choice", "options": []any{
			articy.Cmd{"text": "Обосновано.", "goto": "a"},
			articy.Cmd{"text": "Ошибочно.", "goto": "b"},
		}},
		{"op": "label", "id": "a"},
		{"op": "say", "text": "…"},
		{"op": "goto", "label": "merge"},
		{"op": "label", "id": "b"},
		{"op": "inc", "key": "rel.Roman", "by": 1},
		{"op": "goto", "label": "merge"},
		{"op": "label", "id": "merge"},
		{"op": "say", "text": "дальше"},
	}
	doc := &articy.Doc{Script: script}
	AnnotateChoiceEffects(doc, tpl)

	opts := doc.Script[0]["options"].([]any)
	a := opts[0].(articy.Cmd)
	b := opts[1].(articy.Cmd)
	if _, has := a["effects"]; has {
		t.Fatalf("ветка «a» стата не трогает — чип обещает чужое: %v", a["effects"])
	}
	effs, has := b["effects"].([]any)
	if !has || len(effs) != 1 {
		t.Fatalf("ветка «b» даёт +1 Роман — превью обязано это показать, got %v", b["effects"])
	}
	eff := effs[0].(map[string]any)
	if eff["label"] != "Роман" || eff["delta"] != 1 {
		t.Fatalf("неверный чип: %v", eff)
	}
}

// Общий merge-хвост после goto ЧЕСТНО исполняется каждым выбором — его inc
// в превью попадает (по потоку, не по файлу).
func TestAnnotateChoiceEffects_MergeTailCountsForEveryOption(t *testing.T) {
	tpl := &Template{}
	tpl.Stats.RelationshipNamespace = "rel"
	tpl.SpeakerNames = map[string]string{"Roman": "Роман"}

	script := []articy.Cmd{
		{"op": "choice", "options": []any{
			articy.Cmd{"text": "x", "goto": "a"},
		}},
		{"op": "label", "id": "a"},
		{"op": "goto", "label": "merge"},
		{"op": "label", "id": "unrelated"},
		{"op": "inc", "key": "rel.Roman", "by": 5}, // чужой код между ветками
		{"op": "label", "id": "merge"},
		{"op": "inc", "key": "rel.Roman", "by": 2},
	}
	doc := &articy.Doc{Script: script}
	AnnotateChoiceEffects(doc, tpl)

	opt := doc.Script[0]["options"].([]any)[0].(articy.Cmd)
	effs, _ := opt["effects"].([]any)
	if len(effs) != 1 {
		t.Fatalf("ожидался ровно merge-хвост, got %v", opt["effects"])
	}
	eff := effs[0].(map[string]any)
	if eff["delta"] != 2 {
		t.Fatalf("merge даёт +2, чужие +5 не считаются: %v", eff)
	}
}
