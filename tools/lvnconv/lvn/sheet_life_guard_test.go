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

// ДИАГНОСТИКА ПОМЕЧАЕТСЯ ОДНИМ СПОСОБОМ.
//
// Тег стоит в самой строке (`[lvn-menu] …`), и по нему фильтруют консоль и
// ОТГРУЖАЕМЫЙ ЛОГ — тот, что приезжает с устройства игрока в админку. Правило
// записано в докблоке дома журнала.
//
// Соглашений при этом было ДВА: 166 строк с `[lvn-*]` и 98 с голым именем —
// `[novelapp]`, `[content]`, `[stage]`, да ещё вперемешку по регистру
// (`[LVN]`, `[LvnFx]`). Фильтр по `lvn-` не видел ТРЕТИ диагностики движка, и
// заметить это можно было только не найдя в поле того, что точно логируется.
func TestEveryDiagnosticTagIsNamespaced(t *testing.T) {
	root := repoRoot(t)
	tag := regexp.MustCompile(`(?:Debug\.Log\w*|LvnLog\.\w+)\(\s*\$?"(\[[a-zA-Z][\w-]*\])`)
	seen := 0
	var strays []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			if strings.Contains(path, "/Tests/") || strings.Contains(path, "Samples~") {
				return nil
			}
			seen++
			for _, m := range tag.FindAllStringSubmatch(string(mustRead(t, path)), -1) {
				if !strings.HasPrefix(m[1], "[lvn") {
					strays = append(strays, filepath.Base(path)+": "+m[1])
				}
			}
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	sawSources(t, seen, 200, "файлов .cs")
	sort.Strings(strays)
	if len(strays) > 0 {
		t.Errorf("теги диагностики мимо соглашения (%d):\n  %s\n\n"+
			"Тег обязан начинаться с «lvn»: по нему фильтруют отгружаемый лог, "+
			"и сообщение с чужим тегом в поле просто не находится.",
			len(strays), strings.Join(strays, "\n  "))
	}
}

// КАРТА КЭША И СЧЁТ БАЙТОВ МЕНЯЮТСЯ ВМЕСТЕ.
//
// Карта отвечает «что у нас есть», счётчик — «сколько это весит», а решение о
// вытеснении принимается по СЧЁТЧИКУ. Забудь вычесть — бюджет считает память
// занятой, и кэш выбрасывает живое; вычти дважды — считает свободной, и растёт
// до отказа. Ни то, ни другое не даёт ошибки: игра просто перезагружает
// картинки или падает по памяти.
//
// Обряд стоял тремя копиями и в разных порядках. Сторожим не порядок (он под
// одним замком безразличен), а само наличие рукописного снятия: `Remove` у
// карты законен только внутри дома.
func TestSpriteCacheDropsThroughOneDoor(t *testing.T) {
	root := repoRoot(t)
	body := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.SpriteCache.cs"))))
	sawSources(t, len(body), 2000, "знаков в доме кэша спрайтов")

	// Внутри самого DropLocked — законно; всё остальное снятие идёт через него.
	at := strings.Index(body, "private void DropLocked(")
	if at < 0 {
		t.Fatal("DropLocked пропал — на нём держится согласие карты и счёта")
	}
	end := strings.Index(body[at:], "\n        }")
	home := body[at : at+end]
	rest := body[:at] + body[at+end:]

	if !strings.Contains(home, "_spriteCache.Remove(") || !strings.Contains(home, "_spriteBytes -=") {
		t.Error("дом больше не делает обе половины разом")
	}
	if n := strings.Count(rest, "_spriteCache.Remove("); n > 0 {
		t.Errorf("снятие из карты мимо дома: %d раз(а)\n\n"+
			"Берите DropLocked: карта и счёт байтов обязаны меняться вместе, "+
			"иначе бюджет вытеснения выбрасывает живое или растёт до отказа.", n)
	}
}

// ПИКСЕЛЬНЫЕ ТЕСТЫ ОБЯЗАНЫ ИМЕТЬ ЧЕМ РИСОВАТЬ.
//
// Пиксельные проверки сами себя пропускают, когда графики нет: на машине без
// неё «нет графики» — законная причина, и сообщение об этом честное. Но если
// графику отнимает САМ ПРОГОН (`-nographics`), пропуск становится вечным:
// девять проверок стекла, створа и переходов не выполнялись НИ РАЗУ, а отчёт
// был зелёный. Зелёное на непроверенном — худший вид зелёного.
//
// Сторожим флаг у PlayMode-запуска. EditMode графику не просит, и там флаг
// уместен — потому сторож смотрит не «есть ли -nographics в файле», а есть ли
// он в наборе доводов PlayMode.
func TestPlayModeRunHasGraphics(t *testing.T) {
	root := repoRoot(t)
	sh := string(mustRead(t, filepath.Join(root, "qa", "run-all.sh")))
	sawSources(t, len(sh), 3000, "знаков в прогоне")

	at := strings.Index(sh, "-testPlatform PlayMode")
	if at < 0 {
		t.Fatal("PlayMode-запуск пропал из прогона — целый класс регрессий виден только там")
	}
	// Набор доводов начинается выше по тексту: ищем ближайший `args=(`.
	start := strings.LastIndex(sh[:at], "args=(")
	if start < 0 {
		t.Fatal("не нашёл набор доводов PlayMode")
	}
	if strings.Contains(sh[start:at], "-nographics") {
		t.Error("PlayMode запускается с -nographics: пиксельные тесты будут " +
			"пропускаться ВСЕГДА, а отчёт останется зелёным")
	}
}

// УЙТИ С ЭКРАНА — ЧЕРЕЗ ОДНУ ДВЕРЬ.
//
// Уход поверхности — это отмена всего, чем показ был обставлен: `display`
// убирает из раскладки, `opacity` и `translate` возвращают на место то, что
// показ двигал и гасил. Правило открывали ТРИЖДЫ и каждый раз наполовину:
// накладной экран помнил смещение, панель истории — прозрачность рамки, бут и
// загрузка — свою прозрачность. Ни один не знал всего набора.
//
// Опасность несимметрична, и в этом всё дело. Забыть `display` видно сразу:
// экран остался на глазах. Забыть прозрачность или смещение не видно НИКОГДА:
// следующий показ ставит `display`, поверхность честно в дереве, ловит тапы,
// ждёт игрока — и невидима.
//
// Поэтому: `style.display = DisplayStyle.None` на СЕБЕ (без получателя) вне
// конструктора — ошибка. Рождение спрятанным разрешено: там уход не отменяют,
// там его ещё не было. Чужой элемент (`_frame.style.display`) не наш случай —
// его прячет тот, кто им владеет.
func TestLeavingTheScreenGoesThroughOneDoor(t *testing.T) {
	root := repoRoot(t)
	dirs := []string{
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine/Runtime/UI",
	}
	selfHide := regexp.MustCompile(`^\s+style\.display = DisplayStyle\.None;`)
	member := regexp.MustCompile(`^\s+(?:public|private|protected|internal)\s`)
	ctor := regexp.MustCompile(`^\s+(?:public|private|protected|internal)\s+\w+\s*\(`)

	seen := 0
	var bypass []string
	for _, d := range dirs {
		entries, err := os.ReadDir(filepath.Join(root, d))
		if err != nil {
			t.Fatal(err)
		}
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
				continue
			}
			lines := strings.Split(string(mustRead(t, filepath.Join(root, d, e.Name()))), "\n")
			for i, l := range lines {
				if !selfHide.MatchString(l) {
					continue
				}
				seen++
				// Ближайшее объявление члена выше: конструктор — рождение,
				// всё остальное — уход.
				j := i
				for j > 0 {
					j--
					if member.MatchString(lines[j]) && strings.Contains(lines[j], "(") {
						break
					}
				}
				if ctor.MatchString(lines[j]) {
					continue
				}
				bypass = append(bypass, e.Name()+":"+itoa(i+1)+" ("+strings.TrimSpace(lines[j])+")")
			}
		}
	}
	sawSources(t, seen, 5, "мест, где поверхность прячет себя")
	sort.Strings(bypass)
	if len(bypass) > 0 {
		t.Errorf("уход с экрана мимо ScreenFx.PutAway (%d):\n  %s\n\n"+
			"PutAway отменяет ВЕСЬ показ: display, opacity, translate. Забытая "+
			"прозрачность или смещение не видны никогда — следующий показ даёт "+
			"поверхность, которая в дереве, ловит тапы, ждёт игрока и невидима.",
			len(bypass), strings.Join(bypass, "\n  "))
	}
}

// СОЗДАННОЕ ТЕСТОМ УБИРАЮТ В TEARDOWN, А НЕ В КОНЦЕ УДАЧНОГО ПУТИ.
//
// Уборка последней строкой теста срабатывает ТОЛЬКО когда все утверждения
// прошли. Упади любое — объект переживает тест: в редакторе его никто не
// сносит, а сцена у тестов общая. Следующий тест находит чужого участника и
// падает не от своей причины; разбор при этом уходит не в тот файл, и это
// самая дорогая форма красноты.
//
// Сторожим форму: `DestroyImmediate` в теле [Test] — признак уборки на удачном
// пути. В `[TearDown]` он законен, там же живёт и дом `Мусор`.
func TestTestsCleanUpInTearDown(t *testing.T) {
	root := repoRoot(t)
	var loud []string
	seen := 0
	for _, rel := range []string{
		"unity/Packages/com.lvn.engine/Tests/Editor",
		"unity/Packages/com.lvn.engine/Tests/Runtime",
	} {
		entries, err := os.ReadDir(filepath.Join(root, rel))
		if err != nil {
			t.Fatal(err)
		}
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
				continue
			}
			seen++
			body := stripComments(string(mustRead(t, filepath.Join(root, rel, e.Name()))))
			for _, block := range strings.Split(body, "[Test]")[1:] {
				// тело до следующего атрибута — грубо, но нам хватает
				if cut := strings.Index(block, "\n        ["); cut > 0 {
					block = block[:cut]
				}
				// Уборка в `finally` — ЗАКОННАЯ: она срабатывает и на
				// упавшем утверждении, то есть делает ровно то, ради чего
				// заведён [TearDown]. Страж, кусающий верное, хуже
				// отсутствующего: его выключают вместе с настоящими находками.
				at := strings.Index(block, "DestroyImmediate(")
				if at < 0 {
					continue
				}
				if fin := strings.Index(block, "finally"); fin >= 0 && fin < at {
					continue
				}
				// СНОС, ПОСЛЕ КОТОРОГО ЕЩЁ ПРОВЕРЯЮТ, — это СЦЕНАРИЙ, а не
				// уборка: «старый слой умер, родились два новых» — ровно то,
				// что тест и воспроизводит. Уборка стоит последней, после неё
				// утверждать уже нечего. Без этой оговорки сторож кусал верное,
				// а такой выключают вместе с настоящими находками.
				if strings.Contains(block[at:], "Assert") {
					continue
				}
				loud = append(loud, e.Name())
				break
			}
		}
	}
	sawSources(t, seen, 40, "файлов тестов")
	sort.Strings(loud)
	if len(loud) > 0 {
		t.Errorf("тестов, убирающих за собой на удачном пути: %d (их не должно быть):\n  %s\n\n"+
			"Берите Мусор + [TearDown]: упавшее утверждение оставляет объект жить, "+
			"и следующий тест падает не от своей причины.",
			len(loud), strings.Join(loud, "\n  "))
	}
}

// ЭКРАНЫ ГАСНУТ В ОДИН ТЕМП.
//
// Прайс-лист длительностей (`LvnMotion`) заведён ровно от этой болезни: «правка
// одного имени меняет ритм всей оболочки разом», а числа на местах вызова дают
// соседние элементы, движущиеся вразнобой. Самое крупное движение оболочки —
// гашение ЦЕЛОГО ЭКРАНА — в список не попало, и пять экранов держали свои
// числа: 0,18 у попапа, 0,25 у галереи и гардероба, 0,3 у входа. Решение «в
// темп актёров» (Илья 25.08) знал только накладной экран, где оно и записано.
//
// Поэтому: длительность в вызове `ScreenFx.Fade*` — не число. Имя (`FadeSeconds`,
// `HandOffSeconds`, `VeilFadeSeconds`), поле автора (`screen_fade`) или
// переменная — что угодно, у чего есть место, где решение записано ОДИН раз.
func TestScreensFadeAtOneTempo(t *testing.T) {
	root := repoRoot(t)
	dirs := []string{
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine/Runtime/UI",
	}
	// Довод длительности: у FadeAsync четвёртый, у FadeAwayAsync второй.
	call := regexp.MustCompile(`ScreenFx\.(FadeAsync|FadeAwayAsync)\(`)
	number := regexp.MustCompile(`^\d+(\.\d+)?f?$`)
	at := map[string]int{"FadeAsync": 3, "FadeAwayAsync": 1}

	seen := 0
	var literals []string
	for _, d := range dirs {
		entries, err := os.ReadDir(filepath.Join(root, d))
		if err != nil {
			t.Fatal(err)
		}
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
				continue
			}
			src := stripComments(string(mustRead(t, filepath.Join(root, d, e.Name()))))
			for _, m := range call.FindAllStringSubmatchIndex(src, -1) {
				which := src[m[2]:m[3]]
				args, ok := topLevelArgs(src, m[1])
				if !ok {
					continue
				}
				seen++
				i := at[which]
				if i >= len(args) {
					continue
				}
				a := strings.TrimSpace(args[i])
				if number.MatchString(a) {
					line := strings.Count(src[:m[0]], "\n") + 1
					literals = append(literals, e.Name()+":"+itoa(line)+" → "+a)
				}
			}
		}
	}
	sawSources(t, seen, 8, "гашений экрана")
	sort.Strings(literals)
	if len(literals) > 0 {
		t.Errorf("длительность гашения задана числом на месте вызова (%d):\n  %s\n\n"+
			"Прайс-лист LvnMotion затем и заведён, чтобы ритм оболочки правился "+
			"одним именем. Общий темп экрана — ScreenFx.FadeSeconds; если этот "+
			"экран правда другой поступок, дайте числу имя с объяснением.",
			len(literals), strings.Join(literals, "\n  "))
	}
}

// Доводы вызова верхнего уровня: запятые внутри вложенных скобок и строк не
// делят. `from` — позиция сразу за открывающей скобкой.
func topLevelArgs(src string, from int) ([]string, bool) {
	depth, start := 1, from
	var args []string
	for i := from; i < len(src); i++ {
		switch src[i] {
		case '(', '[':
			depth++
		case ')', ']':
			depth--
			if depth == 0 {
				return append(args, src[start:i]), true
			}
		case ',':
			if depth == 1 {
				args = append(args, src[start:i])
				start = i + 1
			}
		case '"':
			for i++; i < len(src) && src[i] != '"'; i++ {
				if src[i] == '\\' {
					i++
				}
			}
		}
	}
	return nil, false
}

// НАСТРОЙКА ПРИМЕНЯЕТ СЕБЯ САМА.
//
// `LvnPrefs.Changed` летит на каждую запись, и на него подписаны те, кого она
// касается: панель (масштаб интерфейса), шрифты, темп движения, громкости,
// сцена. Экран настроек знает только «записать» — применение не его дело.
//
// Из 34 присваиваний настроек ручной толчок добавляли ДВА, оба безвредных: они
// звали `LvnPanel.ApplyUiScale()` после записи, которая и так его поднимала.
// Вред был не в этих двух вызовах, а в правиле, которому они учат: следующая
// настройка, применённая руками, обновит ТОТ экран, с которого её меняли, и не
// обновит второй — а экранов настроек два (меню сцены и оболочка).
func TestSettingsApplyThemselves(t *testing.T) {
	root := repoRoot(t)
	dirs := []string{
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine/Runtime/UI",
	}
	call := regexp.MustCompile(`\bApplyUiScale\s*\(`)
	seen := 0
	var outside []string
	for _, d := range dirs {
		entries, err := os.ReadDir(filepath.Join(root, d))
		if err != nil {
			t.Fatal(err)
		}
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
				continue
			}
			// Дом применения — сам LvnPanel: там и вызов при заводе панели, и
			// подписка на событие.
			if e.Name() == "LvnPanel.cs" {
				seen += len(call.FindAllString(string(mustRead(t, filepath.Join(root, d, e.Name()))), -1))
				continue
			}
			src := stripComments(string(mustRead(t, filepath.Join(root, d, e.Name()))))
			for range call.FindAllString(src, -1) {
				seen++
				outside = append(outside, e.Name())
			}
		}
	}
	sawSources(t, seen, 2, "вызовов применения масштаба")
	sort.Strings(outside)
	if len(outside) > 0 {
		t.Errorf("масштаб интерфейса применяют руками мимо подписки: %s\n\n"+
			"LvnPrefs.Changed летит на каждую запись, и панель на него подписана. "+
			"Ручное применение обновит тот экран, с которого меняли, и не обновит "+
			"второй — экранов настроек два.", strings.Join(outside, ", "))
	}
}

// ПРОПУСКОВ НЕ СТАНОВИТСЯ БОЛЬШЕ.
//
// Тест, умеющий пропустить себя, — честный ответ на «среда не даёт проверить»:
// пропуск виден в отчёте, а зелёная проверка, ничего не проверяющая, не видна
// никак. Но у честности есть цена: каждый такой пропуск — дыра в покрытии,
// которую CI не показывает красным.
//
// Замерено 02.09: восемнадцать мест `Assert.Ignore` и один статический
// `[Ignore]`. Причины разные и все названы словом — нет графики, нет шейдера,
// панель UITK в безголовом прогоне не считает раскладку, сервер-смоук не
// собран. Храповик держит число: пропуск добавляется осознанно, а не потому,
// что «тест почему-то красный».
//
// Число может только УМЕНЬШАТЬСЯ. Выросло — значит либо среда стала хуже, либо
// пропуском лечат падение.
func TestSkipsDoNotMultiply(t *testing.T) {
	const budget = 19 // 18 динамических + 1 статический (02.09)

	root := repoRoot(t)
	skip := regexp.MustCompile(`Assert\.Ignore\(|\[\s*(?:UnityTest|Test)\s*,\s*Ignore\(|^\s*\[\s*Ignore\(`)
	seen, files := 0, 0
	where := map[string]int{}
	_ = filepath.Walk(filepath.Join(root, "unity/Packages"), func(p string, i os.FileInfo, err error) error {
		if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") || !strings.Contains(p, "/Tests/") {
			return err
		}
		files++
		n := len(skip.FindAllString(string(mustRead(t, p)), -1))
		if n > 0 {
			seen += n
			where[filepath.Base(p)] = n
		}
		return nil
	})
	sawSources(t, files, 150, "файлов тестов")

	if seen > budget {
		var list []string
		for f, n := range where {
			list = append(list, f+"×"+itoa(n))
		}
		sort.Strings(list)
		t.Errorf("пропусков стало больше: %d при бюджете %d\n  %s\n\n"+
			"Каждый пропуск — дыра, которую CI не показывает красным. Если среда "+
			"правда не даёт проверить — назовите причину словом и поднимите бюджет "+
			"осознанно; если пропуском лечат падение — почините падение.",
			seen, budget, strings.Join(list, "\n  "))
	}
	if seen < budget {
		t.Logf("пропусков стало меньше (%d при бюджете %d) — опустите бюджет", seen, budget)
	}
}

// СЦЕНА, ГОВОРЯЩАЯ САМА С СОБОЙ, НАЗЫВАЕТСЯ.
//
// У команды сцены есть отправитель, и он решает не оформление, а ПАМЯТЬ:
// липкой (наследуемой следующей авторской командой) может быть только команда
// истории. Когда в память попадала команда витрины или гардероба, героиня
// выходила в главу стоящей по-менюшному — «не встраивается в игру, хотя её
// реплика». Ради этого липкость и заведена.
//
// Однорукая перегрузка `ApplyStage(cmd)` подставляет `LvnSender.Story`. Снаружи
// это правильное умолчание — зовущий и есть история. ИЗНУТРИ сцены это ложь:
// сцена не история, она пересылает чужую команду, и назваться историей значит
// подменить память.
//
// Живой случай 02.09: повтор недоехавшей фигуры (`RetryActorSoonAsync`) звал
// одноруко. Поза витрины, доехавшая со второй попытки, оседала в памяти как
// авторская — главный путь эту дыру закрыл, повтор ходил мимо.
func TestStageNamesItselfWhenItTalksToItself(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/UI")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	// Два входа в сцену с одинаковым правилом: и общая дверь, и путь актёра.
	call := regexp.MustCompile(`(?:ApplyStage|ApplyActorAsync)\(`)
	// Объявления перегрузок — не вызовы.
	decl := regexp.MustCompile(`(?:void|Task)\s+(?:ApplyStage|ApplyActorAsync)\(`)

	seen := 0
	var nameless []string
	for _, e := range entries {
		if e.IsDir() || !strings.HasPrefix(e.Name(), "VnStage") || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		src := stripComments(string(mustRead(t, filepath.Join(dir, e.Name()))))
		for _, m := range call.FindAllStringIndex(src, -1) {
			head := src[max0(m[0]-24):m[1]]
			if decl.MatchString(head) {
				continue
			}
			seen++
			args, ok := topLevelArgs(src, m[1])
			if !ok {
				continue
			}
			// Отправитель может ехать именованным доводом (`sender: sender`),
			// значением (`LvnSender.Wardrobe`) или через дом памяти
			// (`RememberedSender(id)`) — считать позиции нельзя: у пути актёра
			// между ними два признака гардероба. Ищем слово, а не место.
			named := false
			for _, a := range args {
				if strings.Contains(strings.ToLower(a), "sender") {
					named = true
					break
				}
			}
			if named {
				continue
			}
			nameless = append(nameless, e.Name()+":"+itoa(strings.Count(src[:m[0]], "\n")+1))
		}
	}
	sawSources(t, seen, 10, "вызовов сцены изнутри неё самой")
	sort.Strings(nameless)
	if len(nameless) > 0 {
		t.Errorf("сцена зовёт себя без отправителя (%d):\n  %s\n\n"+
			"Однорукая перегрузка подставляет LvnSender.Story, то есть ЛИПКИЙ. "+
			"Изнутри сцены это ложь: чужая команда осядет в памяти как авторская, "+
			"и героиня выйдет в главу стоящей по-менюшному.",
			len(nameless), strings.Join(nameless, "\n  "))
	}
}

// ПУБЛИЧНАЯ ДВЕРЬ, В КОТОРУЮ НИКТО НЕ ХОДИТ, ОБЪЯСНЯЕТ СЕБЯ.
//
// У движка-библиотеки два вида таких дверей, и различить их снаружи нельзя:
// ШОВ (её открывает встраивающая игра — привязка аккаунта, приём сообщения от
// хоста, режим бара) и НЕ ПОДКЛЮЧЁННОЕ (написано, но никем не позвано —
// пролёт вкладки, затвор прозрачности). Первое трогать нельзя, второе можно
// выкинуть — и решить это можно только по докблоку.
//
// Конвенция в движке уже была: `LvnMontage.Coalesce` («НЕ ПОДКЛЮЧЁН: ждёт
// второго заказчика»), `LvnIcons.Retarget`, `LvnSpriteFxDriver.ReleaseFade`,
// `LvnGlobalStats.SaveAsync`. Замерено 02.09: из 1203 публичных способов имя
// тринадцати не встречается в репозитории больше НИГДЕ, и семеро из них
// объяснялись, шестеро молчали.
//
// Ищем по имени целиком, а не по вызову со скобками: способ передают и
// группой (`Safe("последний кадр", VnStage.ForgetLastSceneBg)`) — на этом
// сито однажды чуть не объявило мёртвым живой стиратель следа игрока.
func TestUncalledPublicDoorsExplainThemselves(t *testing.T) {
	root := repoRoot(t)
	var files []string
	for _, d := range []string{
		"unity/Packages/com.lvn.engine/Runtime",
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine.services/Runtime",
	} {
		_ = filepath.Walk(filepath.Join(root, d), func(p string, i os.FileInfo, err error) error {
			if err == nil && !i.IsDir() && strings.HasSuffix(p, ".cs") {
				files = append(files, p)
			}
			return err
		})
	}
	// Слова считаем по всему движку и по тестам: позвали из теста — тоже позвали.
	var all []string
	seen := append([]string{}, files...)
	_ = filepath.Walk(filepath.Join(root, "unity/Packages"), func(p string, i os.FileInfo, err error) error {
		if err == nil && !i.IsDir() && strings.HasSuffix(p, ".cs") && strings.Contains(p, "/Tests/") {
			seen = append(seen, p)
		}
		return err
	})
	for _, p := range seen {
		all = append(all, string(mustRead(t, p)))
	}
	blob := strings.Join(all, "\n")
	word := regexp.MustCompile(`\w+`)
	uses := map[string]int{}
	for _, w := range word.FindAllString(blob, -1) {
		uses[w]++
	}

	member := regexp.MustCompile(`(?m)((?:[ \t]*///.*\n)*)[ \t]*public\s+(?:static\s+|async\s+|virtual\s+|sealed\s+|new\s+)*[\w<>\[\],\.\?]+\s+(\w+)\s*\(`)
	mark := regexp.MustCompile(`(?i)НЕ ПОДКЛЮЧ|ШОВ|встраива|хост|host|снаружи|UnitySendMessage`)
	skip := map[string]bool{"Equals": true, "GetHashCode": true, "ToString": true, "Dispose": true}

	doors, mute := 0, []string{}
	for _, p := range files {
		src := string(mustRead(t, p))
		for _, m := range member.FindAllStringSubmatch(src, -1) {
			name := m[2]
			if skip[name] || uses[name] > 1 {
				continue
			}
			doors++
			if !mark.MatchString(m[1]) {
				mute = append(mute, filepath.Base(p)+": "+name)
			}
		}
	}
	sawSources(t, len(files), 150, "файлов движка")
	sort.Strings(mute)
	if len(mute) > 0 {
		t.Errorf("публичные двери без объяснения (%d из %d непозванных):\n  %s\n\n"+
			"Снаружи шов и брошенный код выглядят одинаково. Напишите в докблоке "+
			"«ШОВ: кто открывает» или «НЕ ПОДКЛЮЧЁН: почему и что надо, чтобы ожил».",
			len(mute), doors, strings.Join(mute, "\n  "))
	}
}

// КОД, КОТОРЫЙ НИКТО НЕ КОМПИЛИРУЕТ, НЕ ОТСТАЁТ ОТ ДВИЖКА.
//
// Таких мест в репозитории два вида, и оба опасны одинаково.
//
// ШВЫ под чужие пакеты — `com.lvn.engine.spine` и
// `com.lvn.engine.addressables`. Их asmdef закрыт
// `defineConstraints`, а нужных зависимостей в тестовом хосте нет: **610 строк,
// которые не компилирует НИКТО и никогда**. Опечатка там всплывёт у того, кто
// поставит необязательный пакет, — то есть у чужого человека и не сегодня.
//
// ОБРАЗЦЫ пакетов (`Samples~`) — их Unity не видит вовсе: тильда в имени папки
// выводит её из проекта. Компилятор к ним не притрагивается НИКОГДА, а новый
// встраивающий копирует их ПЕРВЫМИ: сгнивший образец — это первое, что он
// увидит от движка.
//
// Компилировать их нам нечем (нет самих чужих сборок). Но самое вероятное
// гниение — не опечатка, а ПЕРЕИМЕНОВАНИЕ в движке: шов зовёт `LvnXxx.Член`,
// член переезжает, и шов остаётся звать пустоту. Это проверяется текстом.
//
// Сверка грубая (имена, а не типы), поэтому судит только про ОТСУТСТВИЕ имени
// целиком: если слова нет во всём движке — звать нечего.
func TestOptionalPackagesStillFitTheEngine(t *testing.T) {
	root := repoRoot(t)
	word := regexp.MustCompile(`\w+`)
	known := map[string]bool{}
	files := 0
	add := func(d string, declOnly bool) {
		_ = filepath.Walk(filepath.Join(root, d), func(p string, i os.FileInfo, err error) error {
			if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			// ОБРАЗЦЫ ЛЕЖАТ ВНУТРИ ПАКЕТА, и обход движка забирал их слова в
			// словарь известных — образец подтверждал сам себя ровно так же,
			// как до этого шов. Их вклад добавляется отдельно и только
			// объявлениями.
			if !declOnly && strings.Contains(p, "Samples~") {
				return nil
			}
			files++
			src := string(mustRead(t, p))
			// У СВОИХ ФАЙЛОВ ШВА берём только ОБЪЯВЛЕНИЯ. Иначе шов
			// подтверждает сам себя: `LvnSpineBootstrap.TryFitZ` кладёт
			// «TryFitZ» в словарь известных, и проверка на него же и
			// соглашается. Употребления — это всё, что стоит после точки.
			if declOnly {
				src = regexp.MustCompile(`\.\s*\w+`).ReplaceAllString(src, ".")
			}
			for _, w := range word.FindAllString(src, -1) {
				known[w] = true
			}
			return nil
		})
	}
	add("unity/Packages/com.lvn.engine", false)
	add("unity/Packages/com.lvn.engine.services", false)
	add("unity/Packages/com.lvn.engine.spine", true)
	add("unity/Packages/com.lvn.engine.addressables", true)
	// Образцы пакетов Unity игнорирует по тильде в имени папки — их не
	// компилирует вообще ничто, а копирует их новый встраивающий ПЕРВЫМИ.
	add("unity/Packages/com.lvn.engine/Samples~", true)
	add("unity/Packages/com.lvn.engine.services/Samples~", true)
	sawSources(t, files, 200, "файлов движка и швов")

	ref := regexp.MustCompile(`\b(Lvn\w+|VnStage|WorldStage|NovelApp|NovelShell|ILvn\w+)\.(\w+)`)
	seen := 0
	var lost []string
	for _, d := range []string{
		"unity/Packages/com.lvn.engine.spine",
		"unity/Packages/com.lvn.engine.addressables",
		"unity/Packages/com.lvn.engine/Samples~",
		"unity/Packages/com.lvn.engine.services/Samples~",
	} {
		_ = filepath.Walk(filepath.Join(root, d), func(p string, i os.FileInfo, err error) error {
			if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			src := stripComments(string(mustRead(t, p)))
			for _, m := range ref.FindAllStringSubmatch(src, -1) {
				seen++
				if !known[m[1]] {
					lost = append(lost, filepath.Base(p)+": типа "+m[1]+" в движке нет")
				} else if !known[m[2]] {
					lost = append(lost, filepath.Base(p)+": "+m[1]+"."+m[2]+" — члена в движке нет")
				}
			}
			return nil
		})
	}
	sawSources(t, seen, 12, "обращений швов и образцов к движку")
	sort.Strings(lost)
	if len(lost) > 0 {
		t.Errorf("некомпилируемый код зовёт то, чего в движке больше нет (%d):\n  %s\n\n"+
			"Его не компилирует ничто: ошибка всплывёт у того, кто поставит "+
			"spine-unity или Addressables либо скопирует образец, — у чужого "+
			"человека и не сегодня.",
			len(lost), strings.Join(lost, "\n  "))
	}
}
