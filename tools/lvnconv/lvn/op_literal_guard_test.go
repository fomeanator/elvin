package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ВИД КОМАНДЫ РАЗБИРАЕТСЯ В ОДНОМ ДОМЕ.
//
// `LvnOpKind` отвечает на вопросы «про кого эта команда» и «есть ли за ней
// картинка». Пока рядом стоят литеральные сравнения — `op == "bg"`,
// `op == "actor"` — тот же вопрос решается вторым правилом, и правила
// расходятся молча: заведут новый вид команды, впишут в дом, а литерал в
// стороне о нём не узнает.
//
// Замер 01.09: тройка `"bg" || "actor" || "obj"` стояла ДВАЖДЫ в одном файле
// предзагрузчика. Впиши новый вид в одно место — предзагрузка заработает через
// раз: первый проход увидит, второй нет. Выглядит это не ошибкой в новой
// команде, а «иногда подтормаживает».
//
// Список известных мест — явный. Дом сам себя разбирает по литералам, и это
// правильно: он для того и заведён.
func TestOpKindLiteralsStayHome(t *testing.T) {
	known := map[string]string{
		"LvnPlayer.Replay.cs": "ключ трассы реплея — НАРОЧНО другое деление: каждая вуаль " +
			"отвечает за себя, потому что схлопывание трассы обязано совпадать с тем, как её переигрывают",
		"LvnFrame.cs":          "кадр помнит своё; список сверяется с домом отдельным стражем",
		"VnStage.Commands.cs":  "разбор входящей команды по имени — это и есть её распаковка, а не вопрос о виде",
		"LvnFlowDistance.cs":   "мера расстояния по флоу articy, чужой словарь",
		"StageMenu.Gallery.cs": "галерея сама решает, что показывать, по своим правилам",
		"VnStage.Reads.cs":     "чтение полей команды, а не классификация",
		"VnStage.Playback.cs":  "проигрывание конкретной команды",
		"LvnPlayer.cs":         "исполнение команд — разбор по имени неизбежен",
	}

	root := repoRoot(t)
	// ЛОВИМ НАБОР, А НЕ ИМЯ. Сравнить `op` с ОДНИМ именем — законная
	// диспетчеризация: так разбирают конкретную команду, и делают это всюду.
	// Болезнь начинается там, где из двух и более имён складывают КЛАСС: это
	// вопрос «про кого команда», и на него отвечает дом.
	lit := regexp.MustCompile(
		`\bop\s*(?:==|!=)\s*"(?:actor|obj|bg|bg3d|sfx)"\s*(?:\|\||&&)\s*op\s*(?:==|!=)\s*"(?:actor|obj|bg|bg3d|sfx)"`)

	var loud []string
	scanned := 0
	for _, dir := range []string{
		"unity/Packages/com.lvn.engine/Runtime",
		"unity/Packages/com.lvn.engine.shell/Runtime",
	} {
		_ = filepath.Walk(filepath.Join(root, dir), func(p string, i os.FileInfo, err error) error {
			if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			base := filepath.Base(p)
			if base == "LvnOpKind.cs" {
				return nil // дом
			}
			if _, ok := known[base]; ok {
				return nil
			}
			scanned++
			if m := lit.FindAllString(stripComments(string(mustRead(t, p))), -1); len(m) > 0 {
				loud = append(loud, base+": "+strings.Join(m, ", "))
			}
			return nil
		})
	}
	sawSources(t, scanned, 100, "файлов движка и оболочки")

	sort.Strings(loud)
	if len(loud) > 0 {
		t.Errorf("вид команды разбирается литералом мимо дома (%d):\n  %s\n\n"+
			"Спросите LvnOpKind.Of или LvnOpKind.CarriesArt. Если вопрос ДРУГОЙ — "+
			"назовите его в доме рядом с остальными и внесите файл в known с причиной.",
			len(loud), strings.Join(loud, "\n  "))
	}
}
