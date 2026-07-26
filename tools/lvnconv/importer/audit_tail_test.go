package importer

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// roundTrip decompiles a script and recompiles it the way the panel's WASM
// does on "Save to app" — the only fidelity that matters for a sidecar.
func roundTrip(t *testing.T, script []articy.Cmd) ([]map[string]any, string) {
	t.Helper()
	src := ToLvns(&articy.Doc{Script: script})
	doc, err := lvns.Convert(string(src))
	if err != nil {
		t.Fatalf("sidecar does not recompile: %v\n%s", err, src)
	}
	out := make([]map[string]any, len(doc.Script))
	for i, c := range doc.Script {
		out[i] = map[string]any(c)
	}
	return out, string(src)
}

// O13: a one-chapter bundle import must get the same wiring a chaptered one
// gets. Every pass below PostProcessBundle iterates res.Scripts, which the
// single-chapter output shape left empty — so the whole bundle layer (wardrobe,
// outfit stamping, speaker names, real backgrounds, audio cues) silently did
// nothing at all.
func TestSingleChapterBundleIsWiredLikeAChapteredOne(t *testing.T) {
	res := &Result{
		ScriptRel: "scripts/x.lvn",
		Lvn: []byte(`{"script":[{"op":"say","who":"Man","text":"hi"},
		 {"op":"set","key":"Music.House","value":true},{"op":"say","text":"bye"}]}`),
		LvnsRel: "scripts/x.lvns", Lvns: []byte("STALE\n"),
		Sprites: map[string]any{},
	}
	normalizeSingleChapterResult(res)
	if len(res.Scripts) != 2 {
		t.Fatalf("payload not folded into res.Scripts: %d entries", len(res.Scripts))
	}
	if len(res.Lvn) != 0 || res.LvnsRel != "" {
		t.Fatal("the payload must MOVE — a copy left behind makes WriteToContentDir truncate the file")
	}
	if res.ScriptRel != "scripts/x.lvn" {
		t.Fatal("ScriptRel is the title's script path and must survive")
	}

	tpl := DefaultTemplate()
	tpl.SpeakerNames = map[string]string{"Man": "Мужчина"}
	PostProcessBundle(res, XlsxData{}, "", tpl)

	var doc struct {
		Script []map[string]any `json:"script"`
	}
	for _, sf := range res.Scripts {
		if strings.HasSuffix(sf.Rel, ".lvn") {
			if err := json.Unmarshal(sf.Data, &doc); err != nil {
				t.Fatal(err)
			}
		}
	}
	named, audio := false, false
	for _, c := range doc.Script {
		if c["op"] == "say" && c["who"] == "Мужчина" {
			named = true
		}
		if c["op"] == "audio" {
			audio = true
		}
	}
	if !named {
		t.Error("speaker_names never reached a one-chapter bundle import")
	}
	if !audio {
		t.Error("a Music.* cue never became an audio op in a one-chapter bundle import")
	}
}

// O18: the variant-suffix fallback exists for state variants of a character
// the catalog holds ("Matvey_neardeath" → «Матвей»). Applied to any id that
// merely STARTS with a known name, it captions strangers — another novel's
// "Ivan_Petrov" became «Иван» from the default template's 56 names.
func TestSpriteDisplayNamesOnlyFollowRealVariants(t *testing.T) {
	tpl := &Template{SpeakerNames: map[string]string{"Matvey": "Матвей", "Ivan": "Иван"}}
	sprites := map[string]any{
		"Matvey":           map[string]any{"name": "Matvey"},
		"Matvey_neardeath": map[string]any{"name": "Matvey_neardeath"}, // variant of a present entity
		"Ivan_Petrov":      map[string]any{"name": "Ivan_Petrov"},      // a DIFFERENT person
	}
	applySpeakerNameOverridesToSprites(sprites, tpl)
	want := map[string]string{
		"Matvey":           "Матвей",
		"Matvey_neardeath": "Матвей",
		"Ivan_Petrov":      "Ivan_Petrov",
	}
	for id, exp := range want {
		got, _ := sprites[id].(map[string]any)["name"].(string)
		if got != exp {
			t.Errorf("%s: name = %q, want %q", id, got, exp)
		}
	}
}

// O21: a reactive label has a positional grammar. The flat k=v form came back
// as an on-screen dialogue line (or with every value shifted by one field).
func TestTextOpRoundTripsThroughItsPositionalForm(t *testing.T) {
	in := []articy.Cmd{
		{"op": "text", "id": "code", "text": "Just a plain line.", "x": 3.0, "y": 12.5, "color": "#9fe8a8", "size": 50.0},
		{"op": "text", "id": "hud", "text": "Sparks: {{sparks}}."},
		{"op": "text", "id": "hud", "hide": true},
		{"op": "say", "text": "after"},
	}
	out, src := roundTrip(t, in)
	if len(out) != len(in) {
		t.Fatalf("op count changed %d→%d\n%s", len(in), len(out), src)
	}
	for i, c := range out {
		if c["op"] != in[i]["op"] {
			t.Fatalf("op %d became %q (was %q)\n%s", i, c["op"], in[i]["op"], src)
		}
	}
	if out[0]["id"] != "code" || out[0]["text"] != "Just a plain line." || out[0]["color"] != "#9fe8a8" {
		t.Fatalf("label fields drifted: %v\n%s", out[0], src)
	}
	if out[2]["hide"] != true {
		t.Fatalf("`text <id> hide` lost its hide: %v\n%s", out[2], src)
	}
}

// O22: a price that recompiles into a bare STRING is never charged
// (LvnPlayer.Choose spends only {currency, amount}) — the premium option
// silently turns free. A string amount now survives; a multi-word currency
// still cannot be expressed, so the guard must SAY so instead of passing.
func TestWalletCostShapeIsGuarded(t *testing.T) {
	ok := []articy.Cmd{
		{"op": "choice", "options": []any{
			map[string]any{"text": "buy", "goto": "a", "wallet_cost": map[string]any{"currency": "crystals", "amount": "20"}},
		}},
		{"op": "label", "id": "a"},
		{"op": "say", "text": "end"},
	}
	out, src := roundTrip(t, ok)
	wc, _ := out[0]["options"].([]any)[0].(map[string]any)["wallet_cost"].(map[string]any)
	if wc == nil || wc["currency"] != "crystals" {
		t.Fatalf("a string amount must still round-trip as an object, got %v\n%s", out[0], src)
	}

	unrepresentable := []articy.Cmd{
		{"op": "choice", "options": []any{
			map[string]any{"text": "buy", "goto": "a", "wallet_cost": map[string]any{"currency": "soft coins", "amount": 5.0}},
		}},
		{"op": "label", "id": "a"},
		{"op": "say", "text": "end"},
	}
	warnings := VerifyLvnsRoundTrip(unrepresentable, ToLvns(&articy.Doc{Script: unrepresentable}))
	if len(warnings) == 0 || !strings.Contains(strings.Join(warnings, "|"), "CHARGE") {
		t.Fatalf("a price that recompiles to a string must be reported, got %v", warnings)
	}
}

// O23: the effects hint is joined with "," — a label containing one came back
// truncated («Иван, брат» → «брат»). Cosmetic, so the unrepresentable entry is
// dropped; the others must be untouched.
func TestEffectsHintNeverShowsATruncatedName(t *testing.T) {
	in := []articy.Cmd{
		{"op": "choice", "options": []any{
			map[string]any{"text": "pick", "goto": "a", "effects": []any{
				map[string]any{"label": "Иван, брат", "delta": 1.0},
				map[string]any{"label": "Матвей", "delta": 2.0},
			}},
		}},
		{"op": "label", "id": "a"},
		{"op": "say", "text": "end"},
	}
	out, src := roundTrip(t, in)
	effs, _ := out[0]["options"].([]any)[0].(map[string]any)["effects"].([]any)
	for _, e := range effs {
		if l, _ := e.(map[string]any)["label"].(string); l == "брат" {
			t.Fatalf("a comma'd label survived as a truncated name\n%s", src)
		}
	}
	if len(effs) != 1 {
		t.Fatalf("the representable effect was lost too: %v\n%s", effs, src)
	}
}
