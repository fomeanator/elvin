package figure

import (
	"encoding/json"
	"image"
	"image/color"
	"image/png"
	"os"
	"path/filepath"
	"testing"
)

// writeFrame paints an opaque rectangle inside a transparent canvas — one
// layer variant of a paper doll, padding and all.
func writeFrame(t *testing.T, path string, w, h int, rect image.Rectangle) {
	t.Helper()
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := rect.Min.Y; y < rect.Max.Y; y++ {
		for x := rect.Min.X; x < rect.Max.X; x++ {
			img.Set(x, y, color.NRGBA{R: 200, G: 100, B: 100, A: 255})
		}
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	f, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	if err := png.Encode(f, img); err != nil {
		t.Fatal(err)
	}
}

func TestOpaqueBoxIgnoresPadding(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "body.png")
	writeFrame(t, p, 100, 200, image.Rect(20, 40, 60, 200))

	box, _, ok, err := opaqueBox(p)
	if err != nil || !ok {
		t.Fatalf("opaqueBox: ok=%v err=%v", ok, err)
	}
	if box.X != 0.2 || box.Y != 0.2 || box.W != 0.4 || box.H != 0.8 {
		t.Fatalf("got %+v, want x=0.2 y=0.2 w=0.4 h=0.8", box)
	}
}

func TestFullyTransparentFrameIsNotMeasured(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "empty.png")
	writeFrame(t, p, 40, 40, image.Rect(0, 0, 0, 0))

	if _, _, ok, err := opaqueBox(p); ok || err != nil {
		t.Fatalf("пустой кадр не должен давать габарит: ok=%v err=%v", ok, err)
	}
}

// The whole point of the union: a wide dress must not shrink the character
// when she takes it off, and a tall hairdo must not raise the floor.
func TestScanUnionsEveryVariantAndSkipsPlainSprites(t *testing.T) {
	dir := t.TempDir()
	art := filepath.Join(dir, "sprites", "doll")
	writeFrame(t, filepath.Join(art, "body_a.png"), 100, 100, image.Rect(40, 30, 60, 100))     // узкое тело
	writeFrame(t, filepath.Join(art, "dress_wide.png"), 100, 100, image.Rect(20, 50, 80, 100)) // широкое платье
	writeFrame(t, filepath.Join(art, "hair_tall.png"), 100, 100, image.Rect(45, 10, 55, 40))   // высокая причёска
	// Производный кадр с теми же пикселями — он не должен считаться дважды и
	// вообще не должен участвовать.
	writeFrame(t, filepath.Join(art, "body_a@mini.png"), 10, 10, image.Rect(0, 0, 10, 10))
	writeFrame(t, filepath.Join(dir, "sprites", "sign.png"), 100, 100, image.Rect(0, 0, 30, 30))

	man := map[string]any{"sprites": map[string]any{
		"doll": map[string]any{
			"aspect": 1.0,
			"layers": []any{
				map[string]any{"url": "/content/sprites/doll/body_{pose}.png"},
				map[string]any{"url": "/content/sprites/doll/dress_{outfit}.png"},
				map[string]any{"url": "/content/sprites/doll/hair_{hair}.png"},
			},
		},
		"sign": map[string]any{ // одиночная картинка — её композицию не трогаем
			"layers": []any{map[string]any{"url": "/content/sprites/sign.png"}},
		},
	}}
	blob, _ := json.MarshalIndent(man, "", " ")
	if err := os.WriteFile(filepath.Join(dir, "manifest.json"), blob, 0o644); err != nil {
		t.Fatal(err)
	}

	results, err := Scan(dir)
	if err != nil {
		t.Fatal(err)
	}
	byName := map[string]Result{}
	for _, r := range results {
		byName[r.Entity] = r
	}
	doll := byName["doll"]
	if doll.Files != 3 {
		t.Fatalf("измерено %d кадров, ждали 3 (производный @mini не в счёт)", doll.Files)
	}
	// Объединение: x 0.20…0.80, y 0.10…1.00
	near := func(a, b float64) bool { return a-b < 1e-6 && b-a < 1e-6 }
	if !near(doll.Box.X, 0.2) || !near(doll.Box.W, 0.6) || !near(doll.Box.Y, 0.1) || !near(doll.Box.H, 0.9) {
		t.Fatalf("габарит %+v, ждали x=0.2 w=0.6 y=0.1 h=0.9", doll.Box)
	}
	if byName["sign"].Skipped == "" {
		t.Fatalf("одиночный спрайт должен быть пропущен, а он измерен: %+v", byName["sign"])
	}

	// …и запись кладёт результат в манифест, ничего больше не трогая.
	written, err := Apply(dir, results)
	if err != nil || written != 1 {
		t.Fatalf("Apply: written=%d err=%v", written, err)
	}
	back, _ := os.ReadFile(filepath.Join(dir, "manifest.json"))
	var reread map[string]any
	if err := json.Unmarshal(back, &reread); err != nil {
		t.Fatal(err)
	}
	sprites := reread["sprites"].(map[string]any)
	got := sprites["doll"].(map[string]any)["content"].(map[string]any)
	if got["w"].(float64) != 0.6 || got["h"].(float64) != 0.9 {
		t.Fatalf("в манифесте %v", got)
	}
	if _, exists := sprites["sign"].(map[string]any)["content"]; exists {
		t.Fatal("одиночному спрайту габарит писать нельзя")
	}

	// Витринная иконка того же слоя (decor_rose_icon.png) — картинка во весь
	// свой маленький кадр. Попав в объединение, она объявила бы, что фигура
	// занимает холст целиком, и обнулила бы всё измерение.
	writeFrame(t, filepath.Join(art, "dress_rose_icon.png"), 40, 40, image.Rect(0, 0, 40, 40))
	withIcon, err := Scan(dir)
	if err != nil {
		t.Fatal(err)
	}
	for _, r := range withIcon {
		if r.Entity != "doll" {
			continue
		}
		if r.Foreign != 1 {
			t.Fatalf("иконку не отсеяли: foreign=%d", r.Foreign)
		}
		if !near(r.Box.W, 0.6) {
			t.Fatalf("иконка раздула габарит до %+v", r.Box)
		}
	}

	// Повторный прогон уже ничего не меняет — команда идемпотентна.
	again, err := Scan(dir)
	if err != nil {
		t.Fatal(err)
	}
	for _, r := range again {
		if r.Changed() {
			t.Fatalf("повторный прогон хочет переписать %s", r.Entity)
		}
	}
}
