package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ШОВ МЕЖДУ СВОИМИ СБОРКАМИ — СЛЕД НЕВЕРНОГО КВАРТАЛА.
//
// Делегат, который одна сборка движка объявляет, а другая заполняет, бывает
// нужен по-настоящему: сцена не может видеть оболочку, ядро не может видеть
// контент — и тогда инверсия единственный выход. Но он же бывает СЛЕДОМ того,
// что дом поставили не туда, а вместо переезда протянули провод.
//
// Живой случай 01.09: LvnBackend.Reachability — делегат, которым службы
// сообщали о связи, потому что дом признака жил в слое контента. В его же
// документации стояла цена: «не поставлен — службы просто молчат о связи».
// Дом переехал в ядро, шов ушёл вместе с ним.
//
// Порог только уменьшается. Новый шов — это либо настоящая инверсия (и тогда
// в его документации сказано, ПОЧЕМУ иначе нельзя), либо дом не в том
// квартале.
func TestCrossAssemblySeamsDoNotGrow(t *testing.T) {
	// Порог только уменьшается. Три оставшихся шва — настоящие инверсии, и у
	// каждого в документации сказано, почему иначе нельзя:
	//   StageMenu.ExternalSettings — сцена не может видеть оболочку;
	//   LvnPlayer.Log             — точка расширения хоста, пустота безобидна;
	//   LvnPlayer.SpeakerNames    — ядро не видит контент, а имена знает словарь.
	const budget = 3

	root := repoRoot(t)
	// `System.Action` — то же самое, что `Action`, и первая версия шаблона
	// этого не знала: мутация с полным именем прошла молча. Пятый случай за
	// сутки, когда образец закрывает лишь то, что вспомнил его автор.
	decl := regexp.MustCompile(`public static (?:System\.)?(?:Action|Func)(?:<[^>]*>)?\s+(\w+)\s*[;=]`)

	type where struct{ file, asm string }
	homes := map[string]where{}
	var files []struct{ path, asm string }
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell", "com.lvn.engine.services"} {
		dir := filepath.Join(root, "unity", "Packages", pkg, "Runtime")
		_ = filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			slash := filepath.ToSlash(path)
			asm := pkg
			if pkg == "com.lvn.engine" {
				switch {
				case strings.Contains(slash, "/Runtime/Content/"):
					asm = "Lvn.Engine.Content"
				case strings.Contains(slash, "/Runtime/UI/"):
					asm = "Lvn.Engine.UI"
				default:
					asm = "Lvn.Engine"
				}
			}
			files = append(files, struct{ path, asm string }{path, asm})
			src := stripComments(string(mustRead(t, path)))
			base := strings.SplitN(filepath.Base(path), ".", 2)[0]
			for _, m := range decl.FindAllStringSubmatch(src, -1) {
				homes[base+"."+m[1]] = where{filepath.Base(path), asm}
			}
			return nil
		})
	}
	atLeast(t, len(files), 60, "просмотренных файлов")
	atLeast(t, len(homes), 3, "объявленных делегатов")

	var seams []string
	for qualified, h := range homes {
		// Присваивание ИМЕННО этому делегату: с именем класса, иначе одноимённое
		// поле темы («Warn» — цвет) считается швом, чего первая версия и не учла.
		assign := regexp.MustCompile(`\b` + regexp.QuoteMeta(qualified) + `\s*=\s*[^=]`)
		for _, f := range files {
			if filepath.Base(f.path) == h.file || f.asm == h.asm {
				continue
			}
			if assign.MatchString(stripComments(string(mustRead(t, f.path)))) {
				seams = append(seams, fmt.Sprintf("%s (объявлен в %s) ← ставят из %s",
					qualified, h.asm, filepath.Base(f.path)))
				break
			}
		}
	}
	if len(seams) > budget {
		t.Errorf("швов между сборками: %d при пороге %d\n  %s\n\n"+
			"Инверсия законна там, где сборки НЕ МОГУТ видеть друг друга (сцена и оболочка).\n"+
			"Внутри одного направления она значит, что дом поставили не туда, а вместо переезда\n"+
			"протянули провод: забыть проводку легко, и сигнал теряется молча.",
			len(seams), budget, strings.Join(seams, "\n  "))
	}
}
