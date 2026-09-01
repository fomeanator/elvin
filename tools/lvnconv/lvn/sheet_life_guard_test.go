package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// НАКЛАДНОЙ ЭКРАН ЗАКРЫВАЕТСЯ ВСЕГДА.
//
// Четыре экрана оболочки живут одним скелетом: «уже открыт — уйти», поднять
// флаг, дождаться затвора, и в `finally` — прибраться и флаг снять. Скелет
// один, а написан четырьмя копиями (`LvnOverlayScreen`, `CgGalleryScreen`,
// `PopupScreen`, `WardrobeSheet`), потому что общего предка у них нет: каждый
// сам себе `VisualElement`.
//
// Опасна в этом скелете ровно одна строка. Снять флаг НАДО в `finally`, а не
// после ожидания: отмена главы, закрытие приложения, исключение внутри — и
// экран остаётся «открытым» навсегда. Следующий вызов упрётся в собственную
// защиту от повторного входа и молча не откроется. Ни ошибки, ни лога:
// гардероб просто перестаёт открываться до перезахода (живой случай был).
//
// Сводить копии в один дом дороже, чем держать: тела у них разные по существу
// (проявление, кошелёк, просмотрщик), а совпадает именно скелет. Поэтому
// сторожим ИНВАРИАНТ, а не форму.
func TestSheetsAlwaysDropTheirOpenFlag(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	// Тело метода от `_open = true` до конца — грубо, но достаточно: нас
	// интересует, встречается ли снятие внутри finally ниже по тексту.
	raise := regexp.MustCompile(`(?m)^\s*_open\s*=\s*true\s*;`)
	drop := regexp.MustCompile(`(?s)finally\s*\{[^}]*_open\s*=\s*false`)

	seen := 0
	var loose []string
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		body := stripComments(string(mustRead(t, filepath.Join(dir, e.Name()))))
		locs := raise.FindAllStringIndex(body, -1)
		if len(locs) == 0 {
			continue
		}
		seen++
		for _, loc := range locs {
			tail := body[loc[1]:]
			if next := raise.FindStringIndex(tail); next != nil {
				tail = tail[:next[0]]
			}
			if !drop.MatchString(tail) {
				loose = append(loose, e.Name())
			}
		}
	}
	sawSources(t, seen, 3, "экранов с флагом открытия")

	sort.Strings(loose)
	if len(loose) > 0 {
		t.Errorf("флаг открытия снимается НЕ в finally: %s\n\n"+
			"Отмена, исключение или закрытие приложения посреди ожидания — и экран "+
			"остаётся «открытым» навсегда: следующий вызов упрётся в защиту от "+
			"повторного входа и молча не откроется.",
			strings.Join(loose, ", "))
	}
}

// «СКРЫТ ЛИ» — ВОПРОС К ДОМУ, А НЕ ПРИВЕДЕНИЕ ТИПА.
//
// Компилятор булевых значений НЕ приводит: `show=no` доезжает до рантайма
// СТРОКОЙ. Разбирает это дом `Lvn.LvnBool`; всякий, кто вместо него пишет
// `(bool?)c["show"]`, видит только настоящий `bool` и молча считает скрытую
// героиню видимой.
//
// Правило было написано СЕМЬ раз: движок (верно), повтор кадра на Go, три
// копии в веб-плеере — и ДВЕ в тестах. Последние опаснее всех: заглушка сцены
// и модель корпуса СЕРТИФИЦИРУЮТ поведение, и приведение заставляло их
// сертифицировать не то, что делает движок. Зелёный отчёт при разъехавшихся
// рантаймах — худшее, что может случиться со стражем.
func TestNobodyCastsShowToBool(t *testing.T) {
	root := repoRoot(t)
	bad := regexp.MustCompile(`\(bool\??\)\s*\w*\[?"?(show|hide|off)"?\]?`)
	seen := 0
	var strays []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			seen++
			if strings.HasSuffix(path, "LvnBool.cs") {
				return nil // дом вправе приводить: он и есть разбор
			}
			for _, m := range bad.FindAllString(stripComments(string(mustRead(t, path))), -1) {
				strays = append(strays, filepath.Base(path)+": "+m)
			}
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	sawSources(t, seen, 300, "файлов .cs")
	sort.Strings(strays)
	if len(strays) > 0 {
		t.Errorf("поле «да-нет» читают приведением, мимо словаря:\n  %s\n\n"+
			"Берите Lvn.LvnBool.Of(поле, умолчание): `show=no` приходит СТРОКОЙ, "+
			"и приведение молча оставляет скрытого на сцене.",
			strings.Join(strays, "\n  "))
	}
}

// КАРТИНКУ ИЗ БАЙТОВ ДЕЛАЕТ ДОМ.
//
// Обряд короткий: завести пустую текстуру и попросить её разобрать байты. Шага
// два, а мест было четыре — и одно из них на неудаче текстуру не уничтожало.
// Битый или неподдерживаемый файл оставлял пустую текстуру в памяти при каждой
// попытке: ошибки нет, лог молчит, память растёт ровно у тех, у кого контент
// побился, — то есть у самых невезучих игроков.
//
// Сторожим не «есть ли Destroy рядом», а сам вызов: `LoadImage` вне дома
// означает, что обряд снова расписали руками, а забыть уборку в нём проще, чем
// вспомнить.
func TestOnlyOneHomeDecodesImages(t *testing.T) {
	root := repoRoot(t)
	home := filepath.Join("Runtime", "Content", "AssetMemory.cs")
	seen := 0
	var strays []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			seen++
			if strings.HasSuffix(path, home) || strings.Contains(path, "/Tests/") {
				return nil
			}
			body := stripComments(string(mustRead(t, path)))
			if strings.Contains(body, ".LoadImage(") {
				strays = append(strays, filepath.Base(path))
			}
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	sawSources(t, seen, 300, "файлов .cs")
	sort.Strings(strays)
	if len(strays) > 0 {
		t.Errorf("картинку из байтов делают мимо дома: %s\n\n"+
			"Берите AssetMemory.Decode(bytes): он возвращает null и убирает за "+
			"собой, а расписанный руками обряд забывает уборку — битый файл "+
			"течёт пустой текстурой при каждой попытке.",
			strings.Join(strays, ", "))
	}
}

// ТИП ЗВУКОВОГО ДЕКОДЕРА БЕРУТ У ДОМА.
//
// Таблица «расширение → декодер Unity» стояла дважды, её свели в
// `DownloadPolicy.AudioTypeOf` — и ровно об этом написано в докблоке дома. А
// сетевой поставщик ходил мимо него ТРЕТЬИМ и слал `AudioType.UNKNOWN`.
//
// UNKNOWN — не «пусть Unity разберётся», а «разбирайся по адресу»: на ссылке
// без расширения или с хвостом версии разбираться не по чему, и клип не
// строится. Наружу это выглядит как «файл скачан, но не звучит» — без ошибки и
// без строки в логе, ровно тот случай, что назван в докблоке дома.
func TestAudioDecoderTypeComesFromTheHome(t *testing.T) {
	root := repoRoot(t)
	seen := 0
	var strays []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			seen++
			if strings.Contains(path, "/Tests/") {
				return nil
			}
			body := stripComments(string(mustRead(t, path)))
			// Вложенная скобка обязательна в шаблоне: сам вызов дома —
			// AudioTypeOf(path) — стоит ВНУТРИ, и «до первой закрывающей»
			// обрывало бы совпадение ровно на правильном коде.
			for _, m := range regexp.MustCompile(`GetAudioClip\((?:[^()]|\([^()]*\))*\)`).FindAllString(body, -1) {
				if !strings.Contains(m, "AudioTypeOf") && !strings.Contains(m, "type") {
					strays = append(strays, filepath.Base(path)+": "+strings.Join(strings.Fields(m), " "))
				}
			}
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	sawSources(t, seen, 300, "файлов .cs")
	sort.Strings(strays)
	if len(strays) > 0 {
		t.Errorf("звук просят без типа из дома:\n  %s\n\n"+
			"Берите DownloadPolicy.AudioTypeOf(url). UNKNOWN на адресе без "+
			"расширения даёт «скачано, но не звучит» — молча.",
			strings.Join(strays, "\n  "))
	}
}

// ПРАВИЛО, ПОЛОВИНЫ КОТОРОГО СВЕРЯЮТСЯ, ЧИТАЕТСЯ ИЗ ОДНОГО МЕСТА.
//
// Два правила сцены устроены одинаково опасно: их спрашивают ДВОЕ и делают
// разное, а ответ обязан совпасть до последнего знака.
//
// Темп строки: печать берёт скорость и печатает, а ОЦЕНКА берёт ту же скорость
// и говорит входящему актёру, когда осесть вместе с текстом. Разойдись они на
// строке с авторской скоростью — герой заканчивает движение в чужом ритме.
//
// Переход видимости: один спрашивает «есть ли зримый переход», другой «какой
// играть». Вопросы разные, выбор один, и живёт он у самой расстановки.
//
// Сторожим не поведение, а ЧИСЛО ЧТЕНИЙ: формула, написанная во второй раз, и
// есть начало расхождения.
func TestPairedRulesAreReadOnce(t *testing.T) {
	root := repoRoot(t)
	cases := []struct {
		file, needle, why string
		limit             int
	}{
		{"unity/Packages/com.lvn.engine/Runtime/UI/DialogueBox.Reveal.cs",
			"_theme.CharsPerSecond",
			"темп темы читают мимо PaceFor: печать и её оценка разойдутся на авторской скорости", 1},
		{"unity/Packages/com.lvn.engine/Runtime/UI/VnStage.Actors.Placement.cs",
			"p.Show ? p.EnterTransition",
			"выбор перехода пишут тернаркой мимо Placement.VisibilityTransition", 0},
	}
	seen := 0
	for _, c := range cases {
		body := stripComments(string(mustRead(t, filepath.Join(root, c.file))))
		seen++
		if n := strings.Count(body, c.needle); n > c.limit {
			t.Errorf("%s: «%s» встречается %d раз при пределе %d — %s",
				filepath.Base(c.file), c.needle, n, c.limit, c.why)
		}
	}
	sawSources(t, seen, 2, "парных правил")
}

// ЗЕРКАЛО ОСТАЁТСЯ ЗЕРКАЛОМ: что навязали при сборке — снимают при отпускании.
//
// Убранство сцены собирают дважды (рождение и смена темы), и обряд сведён в
// пару MakeChrome/DropChrome. Обещание пары записано в её докблоке словами:
// «подписка, которую забыли снять, переживает свой экземпляр». Слова — не
// проверка, а забыть тут легко ровно потому, что видно НИЧЕГО: старый
// экземпляр уже никому не нужен, и лишний обработчик просто тикает в пустоту,
// пока однажды не тикнет по живому.
//
// Сторожим состав: каждое `+=` в сборке обязано иметь парное `-=` в
// отпускании, и наоборот. Это дешевле теста на живой сцене (ей нужны панель и
// документ) и точнее по времени — ловит саму асимметрию, а не её последствие.
func TestChromeUnwiresWhatItWires(t *testing.T) {
	root := repoRoot(t)
	body := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/UI/VnStage.Chrome.cs"))))

	cut := func(name string) string {
		at := strings.Index(body, "private void "+name+"(")
		if at < 0 {
			t.Fatalf("%s пропал — на паре держится сборка убранства", name)
		}
		end := strings.Index(body[at:], "\n        }")
		if end < 0 {
			t.Fatalf("не нашёл конца %s", name)
		}
		return body[at : at+end]
	}
	pick := func(src, sign string) map[string]bool {
		out := map[string]bool{}
		re := regexp.MustCompile(`(_\w+\.\w+)\s*` + regexp.QuoteMeta(sign) + `=\s*(\w+)`)
		for _, m := range re.FindAllStringSubmatch(src, -1) {
			out[m[1]+" → "+m[2]] = true
		}
		return out
	}
	made := pick(cut("MakeChrome"), "+")
	dropped := pick(cut("DropChrome"), "-")
	sawSources(t, len(made), 3, "подписок при сборке убранства")

	var lonely []string
	for k := range made {
		if !dropped[k] {
			lonely = append(lonely, "навязали, не снимают: "+k)
		}
	}
	for k := range dropped {
		if !made[k] {
			lonely = append(lonely, "снимают, не навязывали: "+k)
		}
	}
	sort.Strings(lonely)
	if len(lonely) > 0 {
		t.Errorf("пара сборки и отпускания разошлась (%d):\n  %s\n\n"+
			"Подписка, которую забыли снять, переживает свой экземпляр: старый "+
			"обработчик тикает в пустоту, пока однажды не тикнет по живому.",
			len(lonely), strings.Join(lonely, "\n  "))
	}
}
