package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// КАРТА ДОМОВ НЕ ОТСТАЁТ ОТ КОДА.
//
// Обратную сторону уже стерегут: `TestEveryHomeInTheMapExists` ловит запись о
// доме, которого больше нет. А вот дом, который ЕСТЬ, но в карту не попал, не
// ловил никто — и карта тихо переставала быть ответом на вопрос «что где
// живёт».
//
// Сверка 01.09: из 217 классов-файлов в карте нет 71. Но карта и не про все
// классы — она про ПОНЯТИЯ, и экраны с моделями в ней не должны быть. Сузив до
// СТАТИЧЕСКИХ домов, которые читают из трёх и более файлов, получаем
// одиннадцать — и первый же (`UiStyle`) оказался одной работой с домом
// картинки, разошедшейся с ним в правиле про углы.
//
// Порог «три читателя» намеренный: дом, к которому ходят из одного места, ещё
// не понятие, а деталь того места.
func TestEveryLivedInHomeIsOnTheMap(t *testing.T) {
	// Окна и служебные дома — поимённо, с причиной.
	known := map[string]string{
		"UiStyle":         "окно в LvnPicture: имя знают четыре опорных компонента",
		"ScreenFx":        "окно в LvnMotion: имя знают девять экранов",
		"LvnEvents":       "служба: очередь событий продукта, не понятие слоя вида",
		"LvnAnalytics":    "служба: отправка аналитики",
		"LvnExperiments":  "служба: A/B в сценарии",
		"LvnDaily":        "служба: ежедневные награды",
		"LvnPlatformAuth": "служба: вход через Google/Apple",
		"LvnWebView":      "служба: показ страницы поверх игры",
	}

	root := repoRoot(t)
	canon := string(mustRead(t, filepath.Join(root, "docs/where-things-live.md")))

	type home struct{ name, path string }
	var homes []home
	files := map[string]string{}
	for _, dir := range []string{
		"unity/Packages/com.lvn.engine/Runtime",
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine.services/Runtime",
	} {
		_ = filepath.Walk(filepath.Join(root, dir), func(p string, i os.FileInfo, err error) error {
			if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			files[p] = stripComments(string(mustRead(t, p)))
			base := filepath.Base(p)
			stem := strings.TrimSuffix(base, ".cs")
			if strings.Contains(stem, ".") {
				return nil // часть разбитого класса — дом называет основной файл
			}
			if regexp.MustCompile(`public\s+static\s+(?:partial\s+)?class\s+` + regexp.QuoteMeta(stem) + `\b`).MatchString(files[p]) {
				homes = append(homes, home{stem, p})
			}
			return nil
		})
	}
	sawSources(t, len(files), 200, "файлов движка и оболочки")

	var lost []string
	for _, h := range homes {
		if strings.Contains(canon, "`"+h.name+"`") {
			continue
		}
		if _, ok := known[h.name]; ok {
			continue
		}
		readers := 0
		use := regexp.MustCompile(`\b` + regexp.QuoteMeta(h.name) + `\.`)
		for p, body := range files {
			if p != h.path && use.MatchString(body) {
				readers++
			}
		}
		if readers >= 3 {
			lost = append(lost, h.name+" (читают из "+itoa(readers)+" файлов)")
		}
	}

	sort.Strings(lost)
	if len(lost) > 0 {
		t.Errorf("дома, до которых карта не дошла (%d):\n  %s\n\n"+
			"К ним ходят из трёх и более мест — значит это понятие, а не деталь "+
			"одного экрана. Впишите строку в docs/where-things-live.md или назовите "+
			"дом в known с причиной (окно, служба).", len(lost), strings.Join(lost, "\n  "))
	}
}
