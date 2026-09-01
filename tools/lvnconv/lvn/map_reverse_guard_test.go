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
// Порог только уменьшается, и теперь он НОЛЬ. Дом, стоящий вне карты домов,
// законен — но обязан быть НАЗВАН в соседнем документе вместе с причиной:
//   - район служб (`docs/services.md`) — его дома знают про сервер и деньги,
//     а ядро их не видит по границам сборок;
//   - мосты к чужим тракторам и демо-сцены — там же, отдельным абзацем.
//
// «Законно вне карты» и «нигде не упомянут» — разные вещи, и раньше страж их
// не различал: тринадцать домов держались порогом, то есть молчаливым
// разрешением не называть их. Ноль означает ровно одно — каждый дом движка
// где-то назван, и следующий автор его НАЙДЁТ, а не напишет свой.
func TestEveryHomeInCodeIsInTheMap(t *testing.T) {
	const budget = 0 // 01.09: было 25 → 13 → 0. Названы ВСЕ, включая двоих, что стоят вне карты по причине (docs/services.md).

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
				// ПО ГРАНИЦАМ СЛОВА, а не по вхождению: `LvnWardrobe` числился
				// в карте только потому, что там есть `LvnWardrobeStage`. Пять
				// имён движка — приставки других (LvnFlow/LvnFlowDistance,
				// LvnNum/LvnNumberFormat, LvnLog/LvnLogShip), и каждое такое
				// совпадение прячет дом ровно от того, кто его ищет.
				word := regexp.MustCompile(`\b` + regexp.QuoteMeta(m[1]) + `\b`)
				if !word.MatchString(canon) && !word.MatchString(svc) {
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
