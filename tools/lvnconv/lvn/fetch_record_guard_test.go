package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// КАК ИДЁТ ОДНА ЗАГРУЗКА — ОДНА ЗАПИСЬ.
//
// Памятей было три, и все под одним замком: сколько байт ждём, сколько
// получили, какая попытка. Замок назван именем ОДНОЙ из них — это и есть
// признак: их три, а факт один.
//
// Разъезд уже стоил вранья на экране. Очистка написана в двух местах и
// очищала РАЗНОЕ, а до того «мусор одиночных закачек въезжал в прогресс
// батча» и давал «Скачано 131 из 135» при пустой очереди — починили
// добавлением ЕЩЁ ОДНОЙ очистки, то есть залатали место, а не форму.
func TestDownloadProgressIsOneRecord(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content")
	scanned := 0
	for _, f := range csFiles(t, dir) {
		scanned++
		body := stripComments(string(mustRead(t, f)))
		for _, old := range []string{"_bytesExpected", "_bytesReceived", "_attempts"} {
			if strings.Contains(body, old) {
				t.Errorf("%s: %s снова живёт отдельной памятью — «как идёт загрузка» "+
					"разложено по словарям, и очистка опять станет списком, который надо не забыть",
					filepath.Base(f), old)
			}
		}
	}
	atLeast(t, scanned, 8, "просмотренных файлов тракта содержимого")

	loader := filepath.Join(dir, "ContentLoader.cs")
	body := stripComments(string(mustRead(t, loader)))
	if !strings.Contains(body, "class Underway") {
		t.Error("ContentLoader.cs: пропала запись Underway")
	}
	if !strings.Contains(body, "Progress(string url)") {
		t.Error("ContentLoader.cs: пропал вход Progress(url) — записи снова заводят по месту")
	}
}
