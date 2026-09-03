package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Формат узнают по содержимому, а не по имени.
//
// На проде 191 фон назван .jpg, а внутри PNG — весь импортированный арт.
// basisu смотрит на расширение и отказывается читать такой файл; отказ не
// виден никому (клиент на 404 берёт PNG-путь, картинка на экране есть), и
// сжатие молча пропускало их, а фоны ехали к игроку несжатыми.
func TestРасширениеМожетВрать(t *testing.T) {
	dir := t.TempDir()
	png := []byte("\x89PNG\r\n\x1a\n" + strings.Repeat("x", 40))
	jpg := []byte("\xFF\xD8\xFF" + strings.Repeat("y", 40))

	cases := []struct {
		name, want string
		body       []byte
		relinked   bool
	}{
		{"honest.png", ".png", png, false},
		{"honest.jpg", ".jpg", jpg, false},
		{"liar.jpg", ".png", png, true}, // ровно случай прода
		{"liar.png", ".jpg", jpg, true},
		{"upper.JPG", ".jpg", jpg, false},
	}
	for _, c := range cases {
		p := filepath.Join(dir, c.name)
		if err := os.WriteFile(p, c.body, 0o644); err != nil {
			t.Fatal(err)
		}
		got, undo := honestExtension(p)
		if strings.ToLower(filepath.Ext(got)) != c.want && !(c.want == ".jpg" && strings.ToLower(filepath.Ext(got)) == ".jpeg") {
			t.Errorf("%s: кодировщик получил %q, а внутри %s", c.name, filepath.Ext(got), c.want)
		}
		if c.relinked && got == p {
			t.Errorf("%s: имя врёт о содержимом, а путь не подменён", c.name)
		}
		if !c.relinked && got != p {
			t.Errorf("%s: имя честное — лишней работы быть не должно", c.name)
		}
		if c.relinked {
			if _, err := os.Stat(got); err != nil {
				t.Errorf("%s: ссылка не читается: %v", c.name, err)
			}
		}
		undo()
		if c.relinked {
			if _, err := os.Stat(got); err == nil {
				t.Errorf("%s: временная ссылка пережила уборку", c.name)
			}
		}
	}
}

func TestЧужойФорматОтдаётсяКакЕсть(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "weird.jpg")
	os.WriteFile(p, []byte("GIF89a....."), 0o644)
	got, undo := honestExtension(p)
	defer undo()
	if got != p {
		t.Errorf("не наш формат — пусть кодировщик скажет своё слово, а мы получили %q", got)
	}
}
