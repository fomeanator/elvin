package main

import (
	"os"
	"path/filepath"
	"testing"
)

// КОДИРОВЩИК НЕ БЕРЁТ БОЛЬШЕ, ЧЕМ ВЛЕЗАЕТ В ПАМЯТЬ СЛУЖБЫ.
//
// basisu держит картинку и свои буферы целиком: на крупном исходнике сумма
// упирается в потолок cgroup, процесс убивают, и в журнале остаётся только
// «signal: killed». Поломка МОЛЧАЛИВАЯ — коды просто не появляются, а игра
// тихо едет на PNG. За ночь 11.09 так погибли 93 кодирования на проде.
func TestKtx2TakesADownscaleWhenTheSourceIsHuge(t *testing.T) {
	dir := t.TempDir()
	huge := filepath.Join(dir, "wall.png")
	writePNG(t, huge, 3000, 3000)   // 9 Мп — вдвое больше потолка

	if !tooBigToEncode(huge) {
		t.Fatal("крупный исходник признан безопасным — кодировщик снова умрёт по памяти")
	}
	small := filepath.Join(dir, "face.png")
	writePNG(t, small, 512, 512)
	if tooBigToEncode(small) {
		t.Error("маленькую картинку без нужды погнали через уменьшение")
	}
	// Не картинка — решать не нам: пусть кодировщик сам скажет, что это.
	junk := filepath.Join(dir, "notes.txt")
	if err := os.WriteFile(junk, []byte("не картинка"), 0o644); err != nil {
		t.Fatal(err)
	}
	if tooBigToEncode(junk) {
		t.Error("на не-картинке проверка соврала вместо того, чтобы промолчать")
	}
	if tooBigToEncode(filepath.Join(dir, "нет-такого.png")) {
		t.Error("отсутствующий файл признан огромным")
	}
}

// Крупный исходник УЖЕ ИМЕЕТ @2k рядом — берём его, а не делаем второй раз.
func TestKtx2ReusesAnExistingDownscale(t *testing.T) {
	dir := t.TempDir()
	src := filepath.Join(dir, "hall.png")
	writePNG(t, src, 3000, 3000)
	variant := filepath.Join(dir, "hall"+downscaleSuffix+".png")
	writePNG(t, variant, 2048, 2048)

	got := ensureDownscaled(newDownscaler(), src)
	if got != variant {
		t.Errorf("готовый @2k не использован: %q", got)
	}
}
