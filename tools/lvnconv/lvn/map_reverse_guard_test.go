package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// КАРТА ДОМОВ ЧИТАЕТСЯ В ОБЕ СТОРОНЫ.
//
// `TestEveryHomeInTheMapExists` ловит одно: карта называет то, чего в коде нет.
// Обратное — код держит дом, о котором карта молчит — не ловил никто, а вред у
// него ровно тот, ради которого карта и заведена: НЕВИДИМЫЙ ДОМ ЗАВОДЯТ ЗАНОВО.
// Почти каждая находка этих суток — второй дом рядом с первым, и первый чаще
// всего был просто не назван.
//
// Порог только уменьшается. Вне карты законно живут:
//   - продуктовые службы (у них свой документ, `docs/services.md`);
//   - мосты к чужим тракторам (Spine, WebView, платформенный вход);
//   - демо и разогрев, которые не роль, а сцена.
//
// Всё остальное обязано иметь строку: иначе следующий автор напишет своё.
func TestEveryHomeInCodeIsInTheMap(t *testing.T) {
	const budget = 19 // 01.09: 25 было, шесть самых «дублируемых» вписаны

	root := repoRoot(t)
	canon := string(mustRead(t, filepath.Join(root, "docs", "where-things-live.md")))
	svcPath := filepath.Join(root, "docs", "services.md")
	svc := ""
	if _, err := os.Stat(svcPath); err == nil {
		svc = string(mustRead(t, svcPath))
	}

	decl := regexp.MustCompile(`public static class (Lvn\w+)`)
	var unlisted []string
	scanned := 0
	for _, rel := range storageRoots {
		err := filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			if strings.Contains(filepath.ToSlash(path), "/Tests/") {
				return nil
			}
			scanned++
			for _, m := range decl.FindAllStringSubmatch(string(mustRead(t, path)), -1) {
				if !strings.Contains(canon, m[1]) && !strings.Contains(svc, m[1]) {
					unlisted = append(unlisted, m[1])
				}
			}
			return nil
		})
		if err != nil {
			t.Fatal(err)
		}
	}
	atLeast(t, scanned, 60, "просмотренных файлов")
	sort.Strings(unlisted)
	if len(unlisted) > budget {
		t.Errorf("домов в коде, которых карта не знает: %d при пороге %d\n  %s\n\n"+
			"Невидимый дом заводят заново — это причина почти каждой находки про «второй дом\n"+
			"рядом с первым». Впишите строку в docs/where-things-live.md или объясните здесь,\n"+
			"почему этот дом живёт вне карты.",
			len(unlisted), budget, strings.Join(unlisted, ", "))
	}
}
