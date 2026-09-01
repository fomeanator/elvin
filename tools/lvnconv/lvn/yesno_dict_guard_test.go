package lvn

import (
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// СЛОВАРЬ «ДА-НЕТ» ОДИН НА ДВА ЯЗЫКА.
//
// Автор пишет согласие словом: `no`, `off`, `нет`, `0`. Рантайм на C# и повтор
// на Go разбирают это ПОРОЗНЬ, и разойдись словари — проверка сертифицирует не
// то поведение, которое увидит игрок: обход скажет «грим снят», а на экране он
// останется.
//
// Расхождение было живым 01.09, и завёл его я сам, перенося словарь в Go: в
// зеркале не оказалось «n», а пустая строка считалась отказом. У рантайма
// пустая строка — не слово из словаря: поле просто ЕСТЬ, и признак поднят.
// Один пропущенный вариант написания — и `sfx off=n` снимает грим в проверке и
// оставляет его в игре.
func TestYesNoDictionariesMatch(t *testing.T) {
	root := repoRoot(t)
	cs := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/LvnBool.cs"))))
	gо := stripComments(string(mustRead(t, filepath.Join(root, "tools/lvnconv/lvn/frame.go"))))

	// C#: строка `case "0": case "false": …` перед `return false`.
	csNo := map[string]bool{}
	if m := regexp.MustCompile(`(?s)case "0":(.*?)return false`).FindStringSubmatch(cs); m != nil {
		for _, w := range regexp.MustCompile(`"([^"]*)"`).FindAllStringSubmatch(`"0"`+m[1], -1) {
			csNo[w[1]] = true
		}
	}
	// Go: одна строка `case "0", "false", …:` перед `return false`.
	goNo := map[string]bool{}
	if m := regexp.MustCompile(`(?s)case "0", (.*?):\n\s*return false`).FindStringSubmatch(gо); m != nil {
		for _, w := range regexp.MustCompile(`"([^"]*)"`).FindAllStringSubmatch(`"0", `+m[1], -1) {
			goNo[w[1]] = true
		}
	}

	sawSources(t, len(csNo), 5, "слов отказа у рантайма")
	sawSources(t, len(goNo), 5, "слов отказа у повтора")

	var only []string
	for w := range csNo {
		if !goNo[w] {
			only = append(only, "только у рантайма: "+w)
		}
	}
	for w := range goNo {
		if !csNo[w] {
			only = append(only, "только у повтора: "+w)
		}
	}
	sort.Strings(only)
	if len(only) > 0 {
		t.Errorf("словари согласия разошлись (%d):\n  %s\n\n"+
			"Один пропущенный вариант написания — и `sfx off=n` снимает грим в "+
			"проверке, оставляя его в игре. Держите оба списка слово в слово.",
			len(only), strings.Join(only, "\n  "))
	}
}
