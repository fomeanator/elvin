package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// ШАБЛОННЫЙ АДРЕС НЕ КАЧАЮТ.
//
// В каталоге спрайтов адреса слоёв — ШАБЛОНЫ: «Cold_Adele_{emotion}.png»,
// «..._clothes_{outfit}.png». Значение оси подставляют в момент показа, когда
// известно, кто какой эмоцией стоит. Файла с фигурными скобками в имени нет и
// быть не должно.
//
// Живой случай 01.09: полосу прогрева каста подключили, а список брал адреса
// как есть — 68 шаблонов из 211 слоёв ушли на сервер и вернулись 404. В логе
// это читается как «сервер сломался»; сломан был СПИСОК.
func TestWarmNeverAsksForTemplateUrls(t *testing.T) {
	root := repoRoot(t)
	f := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content/LvnParts.cs")
	body := stripComments(string(mustRead(t, f)))
	if !strings.Contains(body, "Fetchable(") {
		t.Error("LvnParts: пропала проверка Fetchable — в очередь снова попадут шаблоны " +
			"с {emotion}/{outfit}, и треть прогрева уйдёт в 404")
	}
	if strings.Contains(body, "IsNullOrEmpty(layer?.url)") {
		t.Error("LvnParts: слой снова проверяют только на пустоту — шаблон пустым не бывает")
	}
}
