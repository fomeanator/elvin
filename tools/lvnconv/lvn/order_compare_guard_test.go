package lvn

import (
	"strings"
	"testing"
)

// Порядковое сравнение со строкой — предупреждение, а не тишина.
//
// `<` `>` `<=` `>=` в этом языке ЧИСЛОВЫЕ: оба вычислителя приводят операнды к
// числу, и строка становится нулём. `name < "М"` — это `0 < 0`, всегда один и
// тот же ответ. Отказ тихий вдвойне: выражение выглядит рабочим и в половине
// случаев даёт ровно то, чего автор ждал (false), — а находят такое на живой
// новелле, через недели.
func TestOrderingAStringLiteralWarns(t *testing.T) {
	doc := &Doc{Script: []Cmd{
		{"op": "label", "id": "start"},
		{"op": "if", "expr": `name < "М"`, "then": "start"},
		{"op": "say", "text": "тут"},
	}}
	var got string
	for _, is := range Validate(doc) {
		if is.Sev == SevWarning && strings.Contains(is.Msg, "compare NUMBERS") {
			got = is.Msg
		}
	}
	if got == "" {
		t.Fatal("сравнение строки через `<` прошло молча — автор узнает об этом на живой новелле")
	}
	if !strings.Contains(got, `"М"`) {
		t.Fatalf("предупреждение не называет литерал, а без него автор ищет строку глазами: %s", got)
	}
}

// Числовое сравнение и равенство со строкой предупреждения НЕ вызывают:
// первое законно, второе — единственный правильный способ сравнить строки.
func TestPlainComparisonsStaySilent(t *testing.T) {
	doc := &Doc{Script: []Cmd{
		{"op": "label", "id": "start"},
		{"op": "if", "expr": `hp < 10`, "then": "start"},
		{"op": "if", "expr": `name == "Аня"`, "then": "start"},
		{"op": "say", "text": "тут"},
	}}
	for _, is := range Validate(doc) {
		if strings.Contains(is.Msg, "compare NUMBERS") {
			t.Fatalf("ложная тревога на законном сравнении: %s", is.Msg)
		}
	}
}
