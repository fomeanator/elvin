package lvn

import (
	"reflect"
	"testing"
)

func docFrom(t *testing.T, src string) *Doc {
	t.Helper()
	d, err := Parse([]byte(src))
	if err != nil {
		t.Fatal(err)
	}
	return d
}

func TestExtractStrings(t *testing.T) {
	d := docFrom(t, `{"script":[
		{"op":"say","who":"Hero","text":"Hello {name}!"},
		{"op":"say","text":"Narration."},
		{"op":"choice","options":[
			{"text":"Yes","goto":"a"},
			{"text":"No","body":[{"op":"say","who":"Hero","text":"Nested line."}]}
		]},
		{"op":"input","var":"name","prompt":"Who are you?"},
		{"op":"text","id":"hud","text":"HP: {hp}"},
		{"op":"say","who":"Hero","text":"Hello {name}!"},
		{"op":"say","text_id":"guid-1","text":"source rendering"},
		{"op":"bg","sprite_url":"bg.png"}
	]}`)
	got := ExtractStrings(d)
	want := []string{
		"Hero", "Hello {name}!", "Narration.", "Yes", "No", "Nested line.",
		"Who are you?", "HP: {hp}", "guid-1",
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("ExtractStrings:\n got %#v\nwant %#v", got, want)
	}
}

func TestExtractStringsDedupsAndOrders(t *testing.T) {
	d := docFrom(t, `{"script":[
		{"op":"say","text":"B"},
		{"op":"say","text":"A"},
		{"op":"say","text":"B"}
	]}`)
	got := ExtractStrings(d)
	want := []string{"B", "A"} // first-appearance order, deduped
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %#v want %#v", got, want)
	}
}

func TestMergeCatalog(t *testing.T) {
	keys := []string{"Hello", "New line", "Kept"}
	existing := map[string]string{
		"Hello": "Привет", // translated — kept verbatim
		"Kept":  "Kept",   // untranslated (value == key)
		"Gone":  "Ушло",   // stale
	}
	merged, st := MergeCatalog(existing, keys, false)
	if merged["Hello"] != "Привет" {
		t.Fatalf("existing translation lost: %q", merged["Hello"])
	}
	if merged["New line"] != "New line" {
		t.Fatalf("new key must prefill with source, got %q", merged["New line"])
	}
	if merged["Gone"] != "Ушло" {
		t.Fatal("stale key dropped without -prune")
	}
	if st.Total != 3 || st.Untranslated != 1 ||
		!reflect.DeepEqual(st.Missing, []string{"New line"}) ||
		!reflect.DeepEqual(st.Stale, []string{"Gone"}) {
		t.Fatalf("stats: %+v", st)
	}

	pruned, _ := MergeCatalog(existing, keys, true)
	if _, ok := pruned["Gone"]; ok {
		t.Fatal("-prune must drop stale keys")
	}
}
