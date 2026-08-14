package main

import (
	"bytes"
	"encoding/json"
	"image"
	"image/color"
	"image/png"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

// writePNG кладёт картинку заданного размера — единственный способ проверить
// проверку разрешения честно, без подделки замера.
func writePNG(t *testing.T, path string, w, h int) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	img := image.NewRGBA(image.Rect(0, 0, w, h))
	img.Set(0, 0, color.RGBA{1, 2, 3, 255})
	var buf bytes.Buffer
	if err := png.Encode(&buf, img); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, buf.Bytes(), 0o644); err != nil {
		t.Fatal(err)
	}
}

// Мыло — это дефект, который компилируется. Скрипт верный, файл на месте,
// ошибок нет, а на телефоне у персонажа рвётся лицо: живой случай Time
// Romance, где на портрете 209x229 во весь рост бросали игру чаще всего.
func TestGuardWarnsAboutTinyArt(t *testing.T) {
	dir := t.TempDir()
	writePNG(t, filepath.Join(dir, "art", "мирон.png"), 209, 229)
	writePNG(t, filepath.Join(dir, "art", "герой.png"), 900, 1600)
	writePNG(t, filepath.Join(dir, "bg", "лагерь.png"), 1920, 1080)

	s := &server{content: dir}
	doc, err := lvn.Parse([]byte(`{"script":[
		{"op":"actor","id":"miron","sprite_url":"/content/art/мирон.png"},
		{"op":"actor","id":"hero","sprite_url":"/content/art/герой.png"},
		{"op":"bg","sprite_url":"/content/bg/лагерь.png"}
	]}`))
	if err != nil {
		t.Fatal(err)
	}
	warns := strings.Join(s.missingAssets(doc), "\n")
	if !strings.Contains(warns, "мирон.png") || !strings.Contains(warns, "209x229") {
		t.Errorf("портрет-аватарка во весь рост обязан попасть в предупреждения:\n%s", warns)
	}
	if strings.Contains(warns, "герой.png") || strings.Contains(warns, "лагерь.png") {
		t.Errorf("нормальный арт не должен ругаться:\n%s", warns)
	}
}

// Фон и персонаж тянутся по высоте по-разному, поэтому и пороги разные:
// фон 1200x800 для персонажа сойдёт, а для фона — нет.
func TestGuardThresholdDependsOnKind(t *testing.T) {
	dir := t.TempDir()
	writePNG(t, filepath.Join(dir, "a.png"), 1200, 800)
	s := &server{content: dir}
	for _, tc := range []struct {
		op   string
		want bool
	}{{"bg", true}, {"actor", false}} {
		doc, err := lvn.Parse([]byte(`{"script":[{"op":"` + tc.op + `","sprite_url":"/content/a.png"}]}`))
		if err != nil {
			t.Fatal(err)
		}
		got := len(s.missingAssets(doc)) > 0
		if got != tc.want {
			t.Errorf("%s 1200x800: ругань=%v, ожидалось %v", tc.op, got, tc.want)
		}
	}
}

// Отчёт заказывают художнику, поэтому важнее всего порядок: сверху то, что
// игрок видит чаще всего, а не то, что мельче всех.
func TestArtQualityRanksByShows(t *testing.T) {
	dir := t.TempDir()
	writePNG(t, filepath.Join(dir, "art", "часто.png"), 200, 200)
	writePNG(t, filepath.Join(dir, "art", "редко.png"), 100, 100)
	script := `{"script":[
		{"op":"actor","id":"a","sprite_url":"/content/art/часто.png"},
		{"op":"actor","id":"a","sprite_url":"/content/art/часто.png"},
		{"op":"actor","id":"a","sprite_url":"/content/art/часто.png"},
		{"op":"actor","id":"b","sprite_url":"/content/art/редко.png"},
		{"op":"bg","sprite_url":"/content/bg/нету.png"}
	]}`
	if err := os.MkdirAll(filepath.Join(dir, "cold"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "cold", "ch01.lvn"), []byte(script), 0o644); err != nil {
		t.Fatal(err)
	}

	s := &server{content: dir, adminToken: "t"}
	req := httptest.NewRequest(http.MethodGet, "/v1/admin/art-quality", nil)
	req.Header.Set("Authorization", "Bearer t")
	rec := httptest.NewRecorder()
	s.handleArtQuality(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("код %d: %s", rec.Code, rec.Body.String())
	}
	var rep artQualityReport
	if err := json.Unmarshal(rec.Body.Bytes(), &rep); err != nil {
		t.Fatal(err)
	}
	if rep.BadFiles != 2 || rep.BadShows != 4 {
		t.Errorf("ожидалось 2 мелких файла и 4 показа, получено %d/%d", rep.BadFiles, rep.BadShows)
	}
	if rep.Missing != 1 {
		t.Errorf("ссылка на несуществующий фон обязана считаться: %d", rep.Missing)
	}
	if len(rep.Offenders) == 0 || !strings.Contains(rep.Offenders[0].URL, "часто.png") {
		t.Errorf("сверху должен быть самый показываемый: %+v", rep.Offenders)
	}
}
