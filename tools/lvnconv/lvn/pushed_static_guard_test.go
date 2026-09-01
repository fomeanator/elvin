package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ЗНАЧЕНИЕ, КОТОРОЕ КТО-ТО ОБЯЗАН ПРИСВОИТЬ ВОВРЕМЯ.
//
// Движок держит несколько статических полей, которые ЗАПОЛНЯЕТ ОБОЛОЧКА —
// обычно из манифеста, когда тот приедет. Форма опасна тем, что молчалива:
// читатель, случившийся раньше присваивания, получает умолчание и не узнаёт об
// этом. Ни компилятор, ни прогон не спросят «а успели ли сказать?».
//
// Живой случай 01.09 стоил половины загрузки. `DownloadPolicy.PreferredSuffix`
// (бокс качества арта) держал литерал «@2k», а настоящую ступень оболочка
// присваивала при сборке — и комментарий рядом обещал «синхронизируем до
// первой загрузки». Обещание было ложным: прогрев витрины идёт раньше сборки.
// Прогрев грел @2k, показ просил @1440, кода для него не было, и та же
// картинка ехала растром — 864–3877 мс на слой вместо 117.
//
// ЛЕКАРСТВО НЕ В ПОРЯДКЕ ВЫЗОВОВ, А В УМОЛЧАНИИ. Пока «не сказали» неотличимо
// от «сказали вот это», порядок придётся помнить человеку. Если же умолчание
// ПУСТО (null/0/false), читатель сам решает, что делать, — и большинство домов
// движка так и устроено: `LvnCaptions.ChapterWord` пуст и спрашивает Словарь,
// `LvnPlayerName.GuestLabel` пуст и спрашивает его же.
//
// Тест держит список исключений ЯВНЫМ: поле с непустым умолчанием либо
// становится выводимым, либо называется здесь вместе с причиной.
func TestPushedStaticsAreUnsetRepresentable(t *testing.T) {
	known := map[string]string{
		"LvnPlayerName.Var": "имя переменной истории; умолчание «player» — то самое, " +
			"которое движок знал до манифеста, и оно же нужный ответ, пока новелла " +
			"не назвала своё. Запись по этому имени (Vars[Var]) идёт в главе, то есть " +
			"заведомо после манифеста.",
	}

	root := repoRoot(t)
	engine := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime")
	shell := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")

	var shellText strings.Builder
	shellFiles := 0
	_ = filepath.Walk(shell, func(p string, i os.FileInfo, err error) error {
		if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
			return err
		}
		shellFiles++
		shellText.Write(mustRead(t, p))
		return nil
	})
	sawSources(t, shellFiles, 40, "файлов оболочки")
	pushed := shellText.String()

	// ПОЛЕ, а не свойство: `= выражение;` без стрелки. Свойство с `=>` выводит
	// ответ само и потому этой болезнью не болеет.
	// RE2 не умеет заглядывать вперёд, поэтому readonly/const отсеиваются
	// проверкой ниже, а не в самом выражении.
	decl := regexp.MustCompile(`(?m)^\s*(?:public|internal)\s+static\s+([\w<>,\[\]\.\?]+)\s+(\w+)\s*=\s*([^;>][^;]*);`)

	var loud []string
	engineFiles := 0
	_ = filepath.Walk(engine, func(p string, i os.FileInfo, err error) error {
		if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
			return err
		}
		base := filepath.Base(p)
		// Настройки игрока приезжают из ХРАНИЛИЩА, а не от оболочки: там
		// «умолчание» — это последнее, что выбрал игрок, и оно осмысленно.
		if base == "LvnPrefs.cs" {
			return nil
		}
		engineFiles++
		body := stripComments(string(mustRead(t, p)))
		for _, m := range decl.FindAllStringSubmatch(body, -1) {
			typ, name, init := m[1], m[2], strings.TrimSpace(m[3])
			if typ == "readonly" || typ == "const" {
				continue // не поле-переменная: менять его никто не может
			}
			switch init {
			case "null", "0", "0f", "false", `""`, "string.Empty", "default":
				continue // «не сказали» отличимо от «сказали» — читатель разберётся
			}
			if !regexp.MustCompile(`\b` + regexp.QuoteMeta(name) + `\s*=(?:[^=]|$)`).MatchString(pushed) {
				continue // оболочка это не трогает — не наш случай
			}
			key := strings.TrimSuffix(base, ".cs") + "." + name
			if _, ok := known[key]; !ok {
				loud = append(loud, key+" = "+init)
			}
		}
		return nil
	})
	sawSources(t, engineFiles, 80, "файлов движка")

	sort.Strings(loud)
	if len(loud) > 0 {
		t.Errorf("статика движка с НЕПУСТЫМ умолчанием, которую заполняет оболочка (%d):\n  %s\n\n"+
			"Пока «не сказали» неотличимо от «сказали вот это», порядок вызовов "+
			"придётся помнить человеку — и однажды он не вспомнит. Сделайте умолчание "+
			"пустым (читатель выведет сам) или назовите поле в known с причиной.",
			len(loud), strings.Join(loud, "\n  "))
	}
}
