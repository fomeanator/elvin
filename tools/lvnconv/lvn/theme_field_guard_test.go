package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ПОЛЕ ТЕМЫ, КОТОРОЕ ТОЛЬКО ПРИСВАИВАЮТ, — ВТОРОЙ ШАГ ТОЙ ЖЕ ЛЖИ.
//
// Страж полей манифеста ловит настройку, которой не читает НИКТО. Но он
// смотрит на ПЕРВЫЙ шаг: увидел `d.fade_width` в сборщике темы — считает
// живой. А сборщик кладёт значение в поле темы, и если ТО поле не читает
// никто, автор всё равно пишет в манифест и ничего не получает.
//
// Живой случай 01.09: `ui.dialogue.fade_width` («мягкий край проявления»)
// доезжал до `VnTheme.FadeWidth` и умирал там — механизм посимвольного
// затухания давно сменило пословное проявление, а настройка осталась висеть в
// схеме как обещание.
//
// Это ровно та же форма, что и «ступень объявлена не тому»: объявлено — да,
// доехало ли до места, где решает, — не спрашивали. Цепочку надо проверять
// целиком или хотя бы на шаг дальше.
func TestThemeFieldsAreRead(t *testing.T) {
	// Что осталось и почему — поимённо. Пусто значит пусто.
	knownDead := map[string]string{}

	root := repoRoot(t)
	themes := []string{
		"unity/Packages/com.lvn.engine/Runtime/UI/VnTheme.cs",
		"unity/Packages/com.lvn.engine/Runtime/UI/LvnTheme.cs",
	}
	decl := regexp.MustCompile(`(?m)^\s*public\s+(?:readonly\s+)?[\w<>,\[\]\.\?]+\s+([A-Z]\w*)\s*=`)

	fields := map[string]string{} // имя → файл темы
	for _, rel := range themes {
		body := stripComments(string(mustRead(t, filepath.Join(root, rel))))
		for _, m := range decl.FindAllStringSubmatch(body, -1) {
			fields[m[1]] = filepath.Base(rel)
		}
	}
	sawSources(t, len(fields), 30, "полей темы")

	read := map[string]bool{}
	for _, dir := range []string{
		"unity/Packages/com.lvn.engine/Runtime",
		"unity/Packages/com.lvn.engine.shell/Runtime",
	} {
		_ = filepath.Walk(filepath.Join(root, dir), func(p string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			base := filepath.Base(p)
			// Сборщик темы только ПИШЕТ — его чтения не считаются.
			if base == "VnThemeBuilder.cs" || base == "VnTheme.cs" || base == "LvnTheme.cs" {
				return nil
			}
			text := stripComments(string(mustRead(t, p)))
			for f := range fields {
				if read[f] {
					continue
				}
				if strings.Contains(text, "."+f) {
					read[f] = true
				}
			}
			return nil
		})
	}

	var dead []string
	for f := range fields {
		if !read[f] {
			if _, ok := knownDead[f]; !ok {
				dead = append(dead, f+" ("+fields[f]+")")
			}
		}
	}
	sort.Strings(dead)
	if len(dead) > 0 {
		t.Errorf("поля темы, которых не читает никто (%d):\n  %s\n\n"+
			"Настройка доезжает до темы и умирает там: автор пишет в манифест и "+
			"ничего не получает. Либо довести до места применения, либо убрать "+
			"вместе с полем манифеста, либо назвать здесь с причиной.",
			len(dead), strings.Join(dead, "\n  "))
	}
}
