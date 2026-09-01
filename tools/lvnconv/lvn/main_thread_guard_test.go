package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ГЛАВНЫЙ ПОТОК НЕ СПРАШИВАЮТ С РАБОЧЕГО.
//
// `Screen`, `SystemInfo` и часть `Application` Unity отдаёт только главному
// потоку — с рабочего они бросают исключение. Поймать это на разработке почти
// нельзя: путь исполняется редко, а падение выглядит случайным.
//
// Замер 01.09 сначала напугал: 91 обращение в 33 файлах. Но продолжений
// `await` Unity возвращает на главный поток сам (свой контекст синхронизации),
// поэтому опасны РОВНО тела `Task.Run` — единственное место, где код заведомо
// уходит с главного. Их одиннадцать, и обращений внутри ноль.
//
// Страж держит этот ноль. Форма «безопасно по порядку, а не по устройству»
// встретилась за день трижды; здесь она закрыта по устройству.
func TestNoMainThreadApiInsideWorkers(t *testing.T) {
	root := repoRoot(t)
	// isMobilePlatform/platform/isEditor — по сути константы сборки и читаются
	// откуда угодно; опасны те, что спрашивают ЖИВОЕ состояние.
	api := regexp.MustCompile(`\b(?:Screen|SystemInfo)\.\w+|\bApplication\.(?:persistentDataPath|version|productName|lowMemory|isPlaying|targetFrameRate)`)

	var loud []string
	blocks, scanned := 0, 0
	for _, dir := range []string{
		"unity/Packages/com.lvn.engine/Runtime",
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine.services/Runtime",
	} {
		_ = filepath.Walk(filepath.Join(root, dir), func(p string, i os.FileInfo, err error) error {
			if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			scanned++
			body := stripComments(string(mustRead(t, p)))
			for _, m := range regexp.MustCompile(`Task\.Run\(`).FindAllStringIndex(body, -1) {
				open := strings.IndexByte(body[m[1]-1:], '(')
				if open < 0 {
					continue
				}
				start := m[1] - 1 + open
				depth, end := 0, start
				for j := start; j < len(body) && j < start+6000; j++ {
					if body[j] == '(' {
						depth++
					} else if body[j] == ')' {
						depth--
						if depth == 0 {
							end = j
							break
						}
					}
				}
				blocks++
				for _, a := range api.FindAllString(body[start:end], -1) {
					loud = append(loud, filepath.Base(p)+": "+a)
				}
			}
			return nil
		})
	}
	sawSources(t, scanned, 200, "файлов движка и оболочки")
	if blocks < 8 {
		t.Fatalf("тел Task.Run найдено всего %d — разбор промахнулся, и «ноль нарушений» "+
			"означало бы пустоту, а не порядок", blocks)
	}

	sort.Strings(loud)
	if len(loud) > 0 {
		t.Errorf("главный поток спрашивают с рабочего (%d):\n  %s\n\n"+
			"Внутри Task.Run эти свойства бросят исключение. Снимите значение ДО "+
			"ухода в поток или запомните его один раз с главного (см. "+
			"LvnDeviceProfile.PrimeAdvice).", len(loud), strings.Join(loud, "\n  "))
	}
}
