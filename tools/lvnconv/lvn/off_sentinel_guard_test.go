package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// «ВЫКЛЮЧИТЬ» СПРАШИВАЕТСЯ ОДНИМ ВОПРОСОМ.
//
// Сказать «убрать трёхмерный набор» автор может двумя способами: полем
// `off: true` или именем-признаком `id: "off"`. Применение знало оба,
// предзагрузчик — только второй.
//
// Цена расхождения не косметическая. На команде `{op:"bg3d", off:true,
// id:"castle"}` предзагрузчик выкачивал ЦЕЛЫЙ трёхмерный набор, а применение
// тут же его выбрасывало: трафик и место на диске за сцену, которую никто не
// покажет. В логе это выглядит обычной загрузкой — ошибки нет нигде.
//
// Ответ живёт в `VnStage.Reads.Turns3DOff`. Страж держит, чтобы сравнение с
// именем-признаком не расползлось обратно.
func TestOffSentinelAskedInOnePlace(t *testing.T) {
	root := repoRoot(t)
	// Сравнение ИМЕНИ команды с признаком «off» — то самое полузнание.
	lit := regexp.MustCompile(`\bid\s*(?:==|!=)\s*"off"|\(string\)\s*\w+\["id"\]\s*(?:==|!=)\s*"off"`)

	var loud []string
	scanned := 0
	_ = filepath.Walk(filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime"),
		func(p string, i os.FileInfo, err error) error {
			if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			base := filepath.Base(p)
			if base == "VnStage.Reads.cs" {
				return nil // дом ответа
			}
			scanned++
			if m := lit.FindAllString(stripComments(string(mustRead(t, p))), -1); len(m) > 0 {
				loud = append(loud, base+": "+strings.Join(m, ", "))
			}
			return nil
		})
	sawSources(t, scanned, 80, "файлов движка")

	sort.Strings(loud)
	if len(loud) > 0 {
		t.Errorf("«выключено» проверяется своим правилом (%d):\n  %s\n\n"+
			"Спросите VnStage.Turns3DOff: сказать «убрать» можно и полем off, и "+
			"именем-признаком, и знать надо оба — иначе выкачивается набор, который "+
			"тут же выбросят.", len(loud), strings.Join(loud, "\n  "))
	}
}
