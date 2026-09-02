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

// СПРЯТАННЫЙ ЭКРАН УМЕЕТ ВЕРНУТЬСЯ.
//
// Экраны оболочки прячутся по-разному, и это законно: у каждого своё
// состояние, которое надо унести. Но одно сочетание делает экран невидимым
// НАВСЕГДА: спрятать, выставив прозрачность в ноль, и не ставить её при
// показе. Следующий показ выставит `display = Flex` поверх нулевой
// прозрачности — экран «открыт», не показавшись. Ни ошибки, ни строки в логе,
// только тишина в ответ на нажатие.
//
// Сегодня так не делает никто: у всех, кто прячет в ноль, показ идёт
// проявлением. Правило при этом нигде не было записано — девять реализаций
// решали каждая сама.
func TestHiddenScreensCanComeBack(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	hide := regexp.MustCompile(`(?s)public void Hide\(\).*?\n        \}`)
	seen := 0
	var blind []string
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		body := stripComments(string(mustRead(t, filepath.Join(dir, e.Name()))))
		m := hide.FindString(body)
		if m == "" {
			continue
		}
		seen++
		if !strings.Contains(m, "opacity = 0f") {
			continue
		}
		rest := strings.Replace(body, m, "", 1)
		if !strings.Contains(rest, "style.opacity") && !strings.Contains(rest, "FadeAsync") {
			blind = append(blind, e.Name())
		}
	}
	sawSources(t, seen, 5, "экранов с сокрытием")
	sort.Strings(blind)
	if len(blind) > 0 {
		t.Errorf("экраны прячутся в ноль и не ставят прозрачность при показе: %s\n\n"+
			"Следующий показ выставит display = Flex поверх нулевой прозрачности: "+
			"экран «открыт», не показавшись, и в ответ на нажатие — тишина.",
			strings.Join(blind, ", "))
	}
}
