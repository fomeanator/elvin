package textcut

import (
	"strings"
	"testing"
	"unicode/utf8"
)

// Тест переехал сюда вместе с работой: он про РУНЫ против БАЙТОВ, а это и есть
// причина, по которой у обрезки должен быть один дом. Стоял он у выгрузки
// .adpd — там, где копию завели первой, — и две другие копии его не защищали.
func TestRunesKeepsCyrillicIntact(t *testing.T) {
	// 90 Cyrillic runes (180 bytes). A byte-slice at 80 would land mid-character
	// and produce invalid UTF-8; truncateRunes must cut on a rune boundary.
	s := strings.Repeat("я", 90)
	got := Runes(s, 80)
	if !utf8.ValidString(got) {
		t.Fatalf("truncated to invalid UTF-8: %q", got)
	}
	if r := []rune(got); len(r) != 81 || string(r[:80]) != strings.Repeat("я", 80) || r[80] != '…' {
		t.Fatalf("want 80 runes + ellipsis, got %d runes: %q", len(r), got)
	}
	// Short strings pass through untouched (no ellipsis).
	if Runes("привет", 80) != "привет" {
		t.Fatal("short string must be returned unchanged")
	}
}
