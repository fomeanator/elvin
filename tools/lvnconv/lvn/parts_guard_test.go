package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ИЗ ЧЕГО СОСТОИТ ГЛАВА — ОДНА ОПИСЬ, А НЕ СЕМЬ.
//
// Знание «глава = скрипт + фон загрузки + объявленные ассеты» было записано
// СЕМЬ раз, разными глаголами: греем всё, планируем скачивание по главам,
// ставим главу в очередь, считаем «глава целиком на диске», убираем диск,
// оцениваем «докачать текущую», тянем следующую вперёд. Одно добавленное поле
// главы означало бы шесть мест, которые о нём не узнают.
//
// Расхождение уже случилось и стоило игроку ожидания: арт карточки хаба один
// обход брал как `card.image ?? cover_url`, соседний — только `card.image`, а
// третий не брал вовсе. Новелла без своей карточки выпадала из набора «не
// выгружать» — то есть могла быть стёрта с диска, пока витрина её рисует.
//
// Признак ручной описи — упоминание СКРИПТА и ФОНА главы рядом: так пишут
// только тогда, когда перечисляют состав. Частичные наборы (скрипт и ассеты
// для готовности к офлайну) законны и здесь не ловятся: они отвечают на другой
// вопрос — «можно ли играть», а не «из чего состоит».
func TestChapterPartsHaveOneInventory(t *testing.T) {
	root := repoRoot(t)
	const home = "LvnParts.cs"
	const window = 8

	var found []string
	scanned := 0
	for _, rel := range storageRoots {
		err := filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			slash := filepath.ToSlash(path)
			if strings.HasSuffix(slash, home) || strings.Contains(slash, "/Tests/") {
				return nil
			}
			scanned++
			lines := strings.Split(stripComments(string(mustRead(t, path))), "\n")
			for i, l := range lines {
				if !strings.Contains(l, ".script_url") {
					continue
				}
				lo, hi := i-window, i+window
				if lo < 0 {
					lo = 0
				}
				if hi > len(lines) {
					hi = len(lines)
				}
				if strings.Contains(strings.Join(lines[lo:hi], "\n"), ".bg_url") {
					found = append(found, fmt.Sprintf("%s:%d", filepath.Base(path), i+1))
					break
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", rel, err)
		}
	}
	if scanned < 100 {
		t.Fatalf("просмотрено всего %d файлов — обход промахнулся, страж проверил бы пустоту", scanned)
	}
	if len(found) > 0 {
		t.Errorf("состав главы перечислен вручную (%d):\n  %s\n\n"+
			"Скрипт и фон рядом — это опись. Она живёт в %s (LvnParts.OfChapter): "+
			"там «что», здесь только глагол.",
			len(found), strings.Join(found, "\n  "), home)
	}
}

func mustRead(t *testing.T, path string) []byte {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("%s: %v", path, err)
	}
	return b
}

// УДАЧНЫЙ ОТВЕТ СЕРВЕРА — ОДНО ПРАВИЛО.
//
// `LvnBackend.Ok` называет удачей весь второй разряд кодов, и продуктовые
// службы (кошелёк, награды, реклама, отзывы, ящик) спрашивают именно его. А
// сам дом правила сверялся с «ровно 200» в шести местах — регистрация, имя,
// вход, привязка, удаление аккаунта, список провайдеров. Сегодня сервер
// отвечает двумястами везде, поэтому расхождение ничего не ломало; завтра
// достаточно 201 или 204 (или прокси, нормализующего ответ), чтобы удача
// прочиталась как отказ — а на привязке аккаунта «уже привязан» превратилось
// бы для игрока в «не вышло».
//
// Проверяется только пакет служб: у загрузчика контента сравнение с 200
// означает совсем другое (сервер проигнорировал Range и прислал файл целиком,
// а не кусок) — и там оно на своём месте.
func TestHttpSuccessHasOneRule(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, filepath.FromSlash("unity/Packages/com.lvn.engine.services/Runtime"))

	var found []string
	scanned := 0
	err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
			return nil
		}
		scanned++
		for i, l := range strings.Split(stripComments(string(mustRead(t, path))), "\n") {
			if strings.Contains(l, "== 200") || strings.Contains(l, "!= 200") {
				found = append(found, fmt.Sprintf("%s:%d", filepath.Base(path), i+1))
			}
		}
		return nil
	})
	if err != nil {
		t.Fatalf("обход служб: %v", err)
	}
	if scanned < 10 {
		t.Fatalf("просмотрено всего %d файлов служб — обход промахнулся", scanned)
	}
	if len(found) > 0 {
		t.Errorf("«ровно 200» вместо правила (%d):\n  %s\n\n"+
			"Удачу называет LvnBackend.Ok — второй разряд целиком.",
			len(found), strings.Join(found, "\n  "))
	}
}

// НАСТРОЙКА ЖИВЁТ В КАТАЛОГЕ, А НЕ НА ЭКРАНЕ.
//
// Набор настроек чтения и звука был записан ДВАЖДЫ — в меню сцены и на экране
// оболочки. Пределы совпадали чудом (их сверяли руками), а имена уже
// разошлись: прозрачность окна звалась `settings.box_opacity` в оболочке и
// `window_opacity` в сцене, «пропускать прочитанное» — `settings.skip_read` и
// `skip_read_only`, эффекты — «Effects» и «Sound FX». Переводчик переводил
// одно из двух, и игрок видел половину настроек по-русски, а половину
// по-английски — смотря откуда открыл.
//
// Признак возврата: экран настроек САМ пишет настройку. Значит, он снова знает
// про неё то, чего не знает второй экран.
func TestSettingsLiveInTheCatalog(t *testing.T) {
	root := repoRoot(t)
	const home = "LvnSettingsCatalog.cs"
	screens := []string{
		"unity/Packages/com.lvn.engine.shell/Runtime/SettingsScreen.cs",
		"unity/Packages/com.lvn.engine/Runtime/UI/StageMenu.Settings.cs",
	}
	prefs := []string{"TextSpeed", "AutoDelayScale", "DialogOpacity",
		"SkipReadOnly", "ReduceMotion", "VolMusic", "VolSfx", "VolAmbient", "VolVoice"}

	var found []string
	for _, rel := range screens {
		path := filepath.Join(root, filepath.FromSlash(rel))
		body, err := os.ReadFile(path)
		if err != nil {
			t.Fatalf("%s: %v — экран переименовали, а страж об этом не знает", rel, err)
		}
		text := stripComments(string(body))
		for _, p := range prefs {
			if strings.Contains(text, "LvnPrefs."+p+" =") {
				found = append(found, fmt.Sprintf("%s: LvnPrefs.%s", filepath.Base(path), p))
			}
		}
	}
	if len(found) > 0 {
		t.Errorf("экран настроек пишет настройку сам (%d):\n  %s\n\n"+
			"Состав настроек — в %s: экран берёт определение и решает только, "+
			"как его показать. Иначе второй экран о настройке не узнает.",
			len(found), strings.Join(found, "\n  "), home)
	}
}

// ПСЕВДОНИМ СЛОВА ОБЯЗАН УКАЗЫВАТЬ НА НАСТОЯЩИЕ ИМЕНА.
//
// Таблица `LvnWordAliases` чинит расхождение двух пространств ключей: меню
// сцены спрашивало голые имена (`close`, `window_opacity`), экраны оболочки —
// с приставкой (`common.close`, `settings.box_opacity`), а перевод автора лежал
// под первыми. Семнадцать подписей живого манифеста Time Romance оживают
// именно ею.
//
// Цена ошибки в такой таблице — тихая подмена слова: опечатка в паре либо не
// сработает вовсе, либо (хуже) свяжет два РАЗНЫХ слова. Поэтому обе стороны
// каждой пары должны существовать в коде: канон кто-то спрашивает, прежнее имя
// кто-то спрашивал.
func TestWordAliasesPointAtRealKeys(t *testing.T) {
	root := repoRoot(t)
	home := filepath.Join(root, filepath.FromSlash(
		"unity/Packages/com.lvn.engine/Runtime/Content/LvnWordAliases.cs"))
	src, err := os.ReadFile(home)
	if err != nil {
		t.Fatalf("LvnWordAliases.cs: %v", err)
	}
	pair := regexp.MustCompile(`\["([a-z_.]+)"\]\s*=\s*"([a-z_.]+)"`)
	pairs := pair.FindAllStringSubmatch(stripComments(string(src)), -1)
	atLeast(t, len(pairs), 15, "пар в таблице псевдонимов")

	// Всё, что где-либо спрашивают словом: LvnWords.Of/Pick("…"), L("…") сцены
	// и определения каталога (`Key = "settings.…"`) — там ключ не вызов, а
	// поле, но спрашивают его точно так же.
	asked := map[string]bool{}
	askRe := regexp.MustCompile(`(?:LvnWords\.(?:Of|Pick)|\bL)\(\s*"([a-z_.]+)"|Key\s*=\s*"([a-z_.]+)"`)
	scanned := 0
	for _, rel := range storageRoots {
		_ = filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			if strings.HasSuffix(path, "LvnWordAliases.cs") {
				return nil
			}
			scanned++
			for _, m := range askRe.FindAllStringSubmatch(stripComments(string(mustRead(t, path))), -1) {
				for _, g := range m[1:] {
					if g != "" {
						asked[g] = true
					}
				}
			}
			return nil
		})
	}
	atLeast(t, scanned, 100, "просмотренных файлов")
	atLeast(t, len(asked), 100, "имён, которые где-то спрашивают")

	// Проверяется КАНОН: прежнее имя код уже не спрашивает — в том и смысл
	// пары, что код ушёл вперёд, а словарь автора остался. Зато канон обязан
	// быть живым: пара к имени, которого никто не показывает, — опечатка.
	var orphan []string
	seenLegacy := map[string]string{}
	for _, p := range pairs {
		canon, legacy := p[1], p[2] // p[0] — вся строка совпадения
		if !asked[canon] {
			orphan = append(orphan, fmt.Sprintf("%s (канон никто не спрашивает)", canon))
		}
		if was, dup := seenLegacy[legacy]; dup {
			orphan = append(orphan, fmt.Sprintf("%s: прежнее имя занято каноном %s", legacy, was))
		}
		seenLegacy[legacy] = canon
	}
	sort.Strings(orphan)
	if len(orphan) > 0 {
		t.Errorf("псевдонимы указывают в пустоту (%d):\n  %s\n\n"+
			"Пара должна связывать два ЖИВЫХ имени: иначе она либо не сработает, "+
			"либо однажды свяжет два разных слова.",
			len(orphan), strings.Join(orphan, "\n  "))
	}
}

// ПОЛЕ-ПОДПИСЬ ИДЁТ ЧЕРЕЗ СЛОВАРЬ, А НЕ НАПРЯМУЮ.
//
// Автор задаёт подписи полями секций (`gate_title`, `menu_label`,
// `regen_ready_text`). Прочитанное НАПРЯМУЮ (`cfg.gate_title ?? "…"`) такое
// поле нельзя ни перевести каталогом языка, ни переопределить словарём: слово
// автора становится последним, и переключатель языка на нём молча
// останавливается. Попап «не хватает энергии» так и жил — четыре подписи мимо
// словаря, — а пункт меню настроек не переводился ВООБЩЕ ничем.
//
// Правило: `LvnWords.Pick(ключ, поле, английское)` — перевод сильнее поля,
// поле сильнее умолчания.
func TestAuthoredCaptionsGoThroughWords(t *testing.T) {
	root := repoRoot(t)
	raw := regexp.MustCompile(`\.(\w*(?:_label|_text|_title))\s*\?\?\s*"`)

	var found []string
	scanned := 0
	for _, rel := range storageRoots {
		_ = filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			slash := filepath.ToSlash(path)
			if strings.Contains(slash, "/Tests/") || strings.HasSuffix(slash, "LvnWords.cs") ||
				strings.HasSuffix(slash, "LvnAuthoredWords.cs") {
				return nil
			}
			scanned++
			for i, line := range strings.Split(stripComments(string(mustRead(t, path))), "\n") {
				if m := raw.FindStringSubmatch(line); m != nil {
					found = append(found, fmt.Sprintf("%s:%d — %s", filepath.Base(path), i+1, m[1]))
				}
			}
			return nil
		})
	}
	atLeast(t, scanned, 100, "просмотренных файлов")
	if len(found) > 0 {
		t.Errorf("подпись автора читается мимо словаря (%d):\n  %s\n\n"+
			"Возьмите LvnWords.Pick(ключ, поле, английское): иначе слово автора "+
			"нельзя перевести каталогом языка, и переключатель на нём остановится.",
			len(found), strings.Join(found, "\n  "))
	}
}

// ЗАКРЫТИЕ ТАПОМ МИМО ЛИСТА — ОДНО ПРАВИЛО НА ВСЕХ.
//
// «Тап мимо панели закрывает» было написано семью экранами и двумя разными
// событиями: четыре по клику (нажал и отпустил на затемнении), два по нажатию
// — то есть в момент касания. Разница видна пальцем: с нажатием случайный
// промах при перетаскивании закрывает панель, не дав отвести палец обратно.
//
// Признак возврата: экран сам сравнивает цель события с собой и закрывается.
func TestDismissByTapHasOneRule(t *testing.T) {
	root := repoRoot(t)
	const home = "LvnChrome.cs"
	dismiss := regexp.MustCompile(`\.target\s*==\s*\w+.*\b(Close|Hide|Dismiss)\s*\(`)

	var found []string
	scanned := 0
	for _, rel := range storageRoots {
		_ = filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			slash := filepath.ToSlash(path)
			if strings.HasSuffix(slash, home) || strings.Contains(slash, "/Tests/") {
				return nil
			}
			scanned++
			for i, line := range strings.Split(stripComments(string(mustRead(t, path))), "\n") {
				if dismiss.MatchString(line) {
					found = append(found, fmt.Sprintf("%s:%d", filepath.Base(path), i+1))
				}
			}
			return nil
		})
	}
	atLeast(t, scanned, 100, "просмотренных файлов")
	if len(found) > 0 {
		t.Errorf("экран закрывает себя сам по тапу мимо (%d):\n  %s\n\n"+
			"Правило живёт в LvnChrome.Scrim(корень, закрыть): иначе у экранов "+
			"снова разойдётся событие — клик против нажатия.",
			len(found), strings.Join(found, "\n  "))
	}
}

// ХРАПОВИК РАДИУСА: числу скругления, вписанному на месте, позволено только
// убывать.
//
// Соседний страж (TestFullRoundingGoesThroughChrome) добился, чтобы скругление
// шло через Рамочника, и там же сказано зачем: «скругление у панели, кнопки и
// карточки обязано меняться ОДНИМ движением — темой; место, вписавшее 12
// руками, из темы выпадает молча». Через дом оно теперь идёт — а ЧИСЛО
// по-прежнему вписывают: пятьдесят два места и четырнадцать разных значений
// (3, 4, 5, 6, 8, 10, 12, 14, 16, 18, 22, 28…), между которыми глазом нет
// разницы, а темой их не подвинуть.
//
// 01.09 сведено: Илья попросил единый вид, и все пятьдесят мест взяли ступень
// темы. Лестница получила две недостающие — RadiusXs=6 (засечка, дорожка
// шкалы) и RadiusLg=28 (лист, диалоговая плашка, как в visual-standards), — а
// «таблетка» (999) названа RadiusPill. Дальше правило простое: берёшь ступень;
// нужна новая — заводи её в теме, и она появится у всех сразу.
func TestRadiusComesFromTheThemeOrShrinks(t *testing.T) {
	const budget = 1 // 01.09: все скругления у ступеней темы; остаётся один
	//                  «без скругления» (Round(el, 0) — не ступень, а её отсутствие)

	root := repoRoot(t)
	// ТОЛЬКО СКРУГЛЕНИЕ. `Math.Round(seconds, 2)` — округление числа, а не
	// угла: страж уже принял его за радиус и потребовал взять ступень темы
	// (правка прошла компиляцию как «float вместо decimal»).
	arithmetic := regexp.MustCompile(`\b(?:Math|Mathf)\.Round\(`)
	numeric := regexp.MustCompile(`Round\(\s*[\w.]+\s*,\s*\d`)
	// УМОЛЧАНИЕ ЗА `??` — то же число, просто с другой стороны. Страж его не
	// видел, и мимо шкалы жили 18, 26 и два 12: `Round(b, cfg.x ?? 26f)` не
	// подходит под правило выше — после запятой стоит не цифра, а имя поля.
	// Ищем ИМЕННО скругление: вызов .Round( (арифметика к этому месту уже
	// заменена на ARITH() выше) и поля вида *_radius. Мировой радиус портала и
	// доля высоты HUD — не углы, и попадать сюда им незачем.
	defaulted := regexp.MustCompile(`(?:\.Round\(|\w*_radius\b)[^;]*\?\?[^;]*\b\d+(?:\.\d+)?f?\b`)

	count, scanned := 0, 0
	for _, rel := range storageRoots {
		_ = filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			if strings.Contains(filepath.ToSlash(path), "/Tests/") {
				return nil
			}
			scanned++
			text := arithmetic.ReplaceAllString(stripComments(string(mustRead(t, path))), "ARITH(")
			count += len(numeric.FindAllString(text, -1))
			count += len(defaulted.FindAllString(text, -1))
			return nil
		})
	}
	atLeast(t, scanned, 100, "просмотренных файлов")
	if count > budget {
		t.Errorf("радиусов, вписанных числом, стало %d при пороге %d.\n\n"+
			"Возьмите LvnTokens.Radius / RadiusSm — или добавьте ступень в тему "+
			"осознанно, но не заводите ещё одно число «на глаз»: темой его потом не подвинуть.",
			count, budget)
	}
}

// ЗВУК УХОДИТ ВМЕСТЕ С ГЛАВОЙ.
//
// «Выходишь из главы — музыка дублируется» (живой репорт 01.09). Диагноз был
// не про музыку, а про список: обряд «снять всё, что принадлежит уходящей
// главе» (VnStage.EndChapterFrame) снимал эпоху работ, печать, корутины,
// хотспоты и вуаль — а музыку с эмбиентом не снимал. Трек главы продолжал
// играть, и поверх него отпускалась музыка витрины.
//
// Дом был правильный, неполон был перечень. Страж держит именно перечень: в
// обряде завершения обязано быть глушение звука.
func TestChapterEndTakesItsSound(t *testing.T) {
	root := repoRoot(t)
	path := filepath.Join(root, filepath.FromSlash(
		"unity/Packages/com.lvn.engine/Runtime/UI/VnStage.Playback.cs"))
	body := methodBody(t, path, "private void EndChapterFrame()")
	if !strings.Contains(body, "SilenceChapter") {
		t.Errorf("обряд завершения главы не глушит её звук.\n\n" +
			"Музыка и эмбиент переживут главу и зазвучат в меню поверх витринного трека — " +
			"это уже случалось. Зовите StageAudio.SilenceChapter в EndChapterFrame.")
	}
}

// РЯД СОБИРАЕТ ДОМ, А НЕ ЭКРАН.
//
// «Горизонтально, по центру» — три строки стиля, написанные тридцать четыре
// раза: шапка экрана, строка значения, полоса кнопок, чип, карусель. Ни одна
// не выглядит нарушением («просто стиль»), и ровно поэтому из них набирается
// разнобой: где-то забыли выравнивание, где-то поставили другое, и одинаковые
// на вид ряды ведут себя по-разному.
//
// Признак возврата: рядом стоящие flexDirection = Row и alignItems = Center.
func TestRowsComeFromScreenUi(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, filepath.FromSlash("unity/Packages/com.lvn.engine.shell/Runtime"))
	const budget = 2 // 01.09: остались две строки с двумя присваиваниями в одной; только вниз

	count, scanned := 0, 0
	err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
			return nil
		}
		if strings.HasSuffix(filepath.ToSlash(path), "ScreenUi.cs") {
			return nil
		}
		scanned++
		lines := strings.Split(stripComments(string(mustRead(t, path))), "\n")
		for i, l := range lines {
			if !strings.Contains(l, "flexDirection = FlexDirection.Row") {
				continue
			}
			lo, hi := i-2, i+3
			if lo < 0 {
				lo = 0
			}
			if hi > len(lines) {
				hi = len(lines)
			}
			if strings.Contains(strings.Join(lines[lo:hi], "\n"), "alignItems = Align.Center") {
				count++
			}
		}
		return nil
	})
	if err != nil {
		t.Fatalf("обход оболочки: %v", err)
	}
	atLeast(t, scanned, 30, "просмотренных файлов оболочки")
	if count > budget {
		t.Errorf("рядов, собранных вручную, стало %d при пороге %d.\n\n"+
			"Возьмите ScreenUi.Row(spread) — иначе одинаковые на вид ряды снова "+
			"разойдутся выравниванием.", count, budget)
	}
}

// КЛЮЧ, ПРИВЯЗАННЫЙ К НОВЕЛЛЕ, СТРОИТ ЗАПИСНАЯ КНИЖКА.
//
// Хранилищ на новеллу несколько — сейвы, галерея, прочитанное, статы, — и
// каждое строило ключ само. Приставки у них разные и такими и останутся
// (сменить приставку значит потерять чужие сохранения), а вот «а если новеллы
// нет» имело ТРИ ответа: «default», пустая строка и ключ с точкой на конце.
// Из-за последнего пустое имя и отсутствующее уезжали в РАЗНЫЕ ящики — одно и
// то же «нет новеллы», записанное дважды.
//
// Признак возврата: склейка приставки с именем новеллы на месте.
func TestTitleKeysComeFromKeep(t *testing.T) {
	root := repoRoot(t)
	// «lvn_что-то_» + titleId или интерполяция $"lvn.что-то.{titleId…}"
	hand := regexp.MustCompile(`"lvn[._][\w.]*"\s*\+\s*\(?\s*(?:string\.IsNullOrEmpty\()?title|\$"lvn[._][\w.]*\{title`)

	var found []string
	scanned := 0
	for _, rel := range storageRoots {
		_ = filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			if strings.Contains(filepath.ToSlash(path), "/Tests/") {
				return nil
			}
			scanned++
			for i, l := range strings.Split(stripComments(string(mustRead(t, path))), "\n") {
				if hand.MatchString(l) {
					found = append(found, fmt.Sprintf("%s:%d", filepath.Base(path), i+1))
				}
			}
			return nil
		})
	}
	atLeast(t, scanned, 100, "просмотренных файлов")
	if len(found) > 0 {
		t.Errorf("ключ новеллы склеен на месте (%d):\n  %s\n\n"+
			"Возьмите LvnKeep.Scoped(приставка, id): иначе «нет новеллы» снова получит "+
			"несколько ответов, и одна и та же пустота ляжет в разные ящики.",
			len(found), strings.Join(found, "\n  "))
	}
}

// ВИТРИНА ОТВЕЧАЕТ ОДНИМ СПОСОБОМ.
//
// Витрин у движка две — карусель и хаб, — и вопрос к ним один: какую новеллу
// выбрал игрок. Пока ответов было два, цикл оболочки знал устройство карусели
// (событие, защёлка, отписка, номер карточки), а запрос «открыть новеллу по id»
// уходил в карусель даже тогда, когда на экране был хаб: ссылка молчала и
// рапортовала об успехе.
//
// Страж держит границу с двух сторон: цикл не зовёт витрины по именам, и обе
// витрины остаются витринами.
func TestBrowseAnswersOneWay(t *testing.T) {
	root := repoRoot(t)
	shell := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")

	flow := stripComments(string(mustRead(t, filepath.Join(shell, "NovelShell.Flow.cs"))))
	byName := regexp.MustCompile(`\b(Carousel|Hub)\s*[.?]`)
	var found []string
	for i, l := range strings.Split(flow, "\n") {
		if byName.MatchString(l) {
			found = append(found, fmt.Sprintf("NovelShell.Flow.cs:%d: %s", i+1, strings.TrimSpace(l)))
		}
	}
	if len(found) > 0 {
		t.Errorf("цикл оболочки зовёт витрину по имени (%d):\n  %s\n\n"+
			"Спрашивайте ILvnBrowse: у вопроса «какую новеллу выбрал игрок» один ответ, "+
			"а как витрина его получит — карточкой, каруселью, защёлкнутой ссылкой — её дело.",
			len(found), strings.Join(found, "\n  "))
	}

	for _, name := range []string{"TitleCarousel.cs", "BrowseHub.cs"} {
		src := string(mustRead(t, filepath.Join(shell, name)))
		if !regexp.MustCompile(`class\s+\w+\s*:[^{]*\bILvnBrowse\b`).MatchString(src) {
			t.Errorf("%s больше не витрина (нет ILvnBrowse) — значит, у оболочки снова "+
				"появился экран выбора новеллы со своим личным способом отвечать", name)
		}
	}
}

// ЧТО ЭТО ЗА ФАЙЛ — ОДИН ОТВЕТ.
//
// «Это скрипт?» было написано ТРИЖДЫ: в политике загрузок, в планировщике и в
// офлайн-политике — причём третья копия чистила адрес от запроса своим
// способом. Род файла определяли ДВОЕ, и на незнакомом расширении они
// расходились: планировщик считал такой файл картинкой и грел его как картинку.
//
// Расширение файла само по себе не запрещено — по нему выбирают декодер звука
// и разбирают атлас Spine. Запрещено ВТОРОЕ МНЕНИЕ о роде: список расширений
// содержимого живёт в DownloadPolicy, остальные спрашивают его.
func TestFileKindHasOneAnswer(t *testing.T) {
	root := repoRoot(t)
	// Явные исключения — они отвечают на ДРУГОЙ вопрос, а не на «что это за файл».
	allowed := map[string]string{
		"DownloadPolicy.cs": "сам определитель",
		"DirectoryAssets.cs": "каким декодером читать звук (AudioType), а не какого файл рода",
		"VnStage.Spine.cs":   "разбор атласа Spine: страницы перечислены внутри файла",
		"LvnSpineBootstrap.cs": "то же — страницы атласа",
	}
	sniff := regexp.MustCompile(`EndsWith\("\.(lvn|png|jpg|jpeg|webp|ogg|wav|mp3)"`)

	var found []string
	scanned := 0
	for _, rel := range []string{"unity/Packages"} {
		_ = filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			p := filepath.ToSlash(path)
			if strings.Contains(p, "/Tests/") || strings.Contains(p, "/Editor/") {
				return nil
			}
			if _, ok := allowed[filepath.Base(path)]; ok {
				return nil
			}
			scanned++
			for i, l := range strings.Split(stripComments(string(mustRead(t, path))), "\n") {
				if sniff.MatchString(l) {
					found = append(found, fmt.Sprintf("%s:%d: %s", filepath.Base(path), i+1, strings.TrimSpace(l)))
				}
			}
			return nil
		})
	}
	atLeast(t, scanned, 100, "просмотренных файлов")
	if len(found) > 0 {
		t.Errorf("род файла определяют по расширению мимо DownloadPolicy (%d):\n  %s\n\n"+
			"Спросите DownloadPolicy.Kind/IsScript/IsImage/IsAudio. Второе мнение о роде "+
			"расходится с первым молча: список расширений в одной копии пополняют, в другой забывают.",
			len(found), strings.Join(found, "\n  "))
	}
}

// КРИВАЯ ДВИЖЕНИЯ ПИШЕТСЯ ИМЕНЕМ, А НЕ ФОРМУЛОЙ.
//
// «Тормозит у цели» было написано девятью одинаковыми строками в шести файлах,
// а в доме движения этой кривой не было вовсе — соседний дом появления так и
// отметил: «своей Ease нет». Пока у кривой нет имени, «как выглядит движение» —
// не решение дома, а привычка автора файла: поправить его разом негде.
func TestMotionCurvesHaveNames(t *testing.T) {
	root := repoRoot(t)
	formula := regexp.MustCompile(`1f\s*-\s*Mathf\.Pow\(1f\s*-\s*\w+,\s*3f\)`)

	var found []string
	scanned := 0
	_ = filepath.Walk(filepath.Join(root, "unity/Packages"), func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
			return nil
		}
		if filepath.Base(path) == "LvnMotion.cs" { // сам дом кривых
			return nil
		}
		scanned++
		for i, l := range strings.Split(stripComments(string(mustRead(t, path))), "\n") {
			if formula.MatchString(l) {
				found = append(found, fmt.Sprintf("%s:%d", filepath.Base(path), i+1))
			}
		}
		return nil
	})
	atLeast(t, scanned, 100, "просмотренных файлов")
	if len(found) > 0 {
		t.Errorf("кривая движения записана формулой (%d):\n  %s\n\n"+
			"Возьмите LvnMotion.Settle (приход тормозит у цели) или LvnMotion.Leave "+
			"(уход разгоняется прочь): у движения оболочки должен быть один почерк.",
			len(found), strings.Join(found, "\n  "))
	}
}

// СВЕЖИЙ КОНТЕНТ ДОХОДИТ ДО ВСЕХ, КТО НА НЁМ ДЕРЖИТСЯ.
//
// Манифест развозили по экранам ПОИМЁННО, и список держался на памяти
// пишущего. Он уже подводил: «без этой строки вкладка гардероба одна
// оставалась на прежнем содержимом, пока соседние экраны показывали новое».
// Забыть строку легко, а увидеть последствие — нет: экран не падает, он просто
// показывает вчерашнее.
//
// Теперь кто живёт манифестом — свойство ЭКРАНА (ILvnContentAware), а развозит
// набор. Страж держит границу: оболочка не раздаёт контент по именам.
func TestFreshContentReachesEveryone(t *testing.T) {
	root := repoRoot(t)
	shell := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime/NovelShell.cs")
	src := stripComments(string(mustRead(t, shell)))

	// «Экран.ЧтоТо = manifest…» или «Экран?.SetXxx(manifest)» — раздача по имени.
	byName := regexp.MustCompile(`\b[A-Z]\w*\s*\??\.\s*\w+\s*(?:=[^=]|\()\s*[^;]*\bmanifest\b`)
	var found []string
	for i, l := range strings.Split(src, "\n") {
		if strings.Contains(l, "_screens.SetContent") {
			continue
		}
		if byName.MatchString(l) {
			found = append(found, fmt.Sprintf("NovelShell.cs:%d: %s", i+1, strings.TrimSpace(l)))
		}
	}
	if len(found) > 0 {
		t.Errorf("оболочка раздаёт контент по именам экранов (%d):\n  %s\n\n"+
			"Пометьте экран ILvnContentAware — набор развезёт сам. Перечень по именам "+
			"держится на памяти пишущего, а забытый экран не падает: он показывает вчерашнее.",
			len(found), strings.Join(found, "\n  "))
	}
}

// «ИСТОЧНИК ДОСТУПЕН» И «СКОЛЬКО ЖДАТЬ ПЕРЕД ПОВТОРОМ» — по одному ответу.
//
// Офлайн-признак говорит про СЕТЬ, а файлы в сборке никуда не деваются:
// локальный источник доступен всегда. Оговорку эту писали от руки и
// по-разному — где-то `IsLocal || !IsOffline`, где-то один `!IsOffline`, — и
// локальная сборка считалась офлайновой ровно там, где про неё забыли.
//
// Пауза перед повтором — та же история: у движка есть LvnBackoff, а рядом
// заводилась своя лесенка «700 × попытка». «Сколько ждать» не может зависеть
// от того, какой файл споткнулся.
func TestReachabilityAndBackoffHaveOneAnswer(t *testing.T) {
	root := repoRoot(t)
	handRolled := regexp.MustCompile(`IsLocal\s*\|\|\s*!\s*\w*\.?LvnNetworkStatus\.IsOffline`)
	handPause := regexp.MustCompile(`Task\.Delay\(\s*\d+\s*\*\s*attempt`)

	var found []string
	scanned := 0
	_ = filepath.Walk(filepath.Join(root, "unity/Packages"), func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
			return nil
		}
		p := filepath.ToSlash(path)
		if strings.Contains(p, "/Tests/") || strings.HasSuffix(p, "ContentLoader.Disk.cs") {
			return nil // сам дом ответа
		}
		scanned++
		for i, l := range strings.Split(stripComments(string(mustRead(t, path))), "\n") {
			if handRolled.MatchString(l) {
				found = append(found, fmt.Sprintf("%s:%d — доступность вручную", filepath.Base(path), i+1))
			}
			if handPause.MatchString(l) {
				found = append(found, fmt.Sprintf("%s:%d — своя лесенка пауз", filepath.Base(path), i+1))
			}
		}
		return nil
	})
	atLeast(t, scanned, 100, "просмотренных файлов")
	if len(found) > 0 {
		t.Errorf("сетевое правило написано на месте (%d):\n  %s\n\n"+
			"Спросите loader.Reachable и LvnBackoff.DelaySeconds.",
			len(found), strings.Join(found, "\n  "))
	}
}

// СЧЁТ ЗАПИНОК ЗАБИРАЮТ, А НЕ ПОДСМАТРИВАЮТ.
//
// Плавность мерится за ГЛАВУ: сколько раз кадр вставал и насколько худший.
// Конец главы ЗАБИРАЕТ счёт (Take — с обнулением), а уход из середины читал те
// же счётчики напрямую и не сбрасывал: запинки брошенной главы утекали в
// следующую и портили её число — ту самую величину, ради которой счёт заведён.
//
// Правило простое: в отчёте о конце главы (любом) стоит Take.
func TestHitchCountIsTakenNotPeeked(t *testing.T) {
	root := repoRoot(t)
	peek := regexp.MustCompile(`\("(?:hitches|worst_ms)"\s*,\s*[\w.]*LvnFrameWatch\.(?:Hitches|WorstMs)`)

	var found []string
	scanned := 0
	_ = filepath.Walk(filepath.Join(root, "unity/Packages"), func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
			return nil
		}
		if strings.Contains(filepath.ToSlash(path), "/Tests/") {
			return nil
		}
		scanned++
		for i, l := range strings.Split(stripComments(string(mustRead(t, path))), "\n") {
			if peek.MatchString(l) {
				found = append(found, fmt.Sprintf("%s:%d", filepath.Base(path), i+1))
			}
		}
		return nil
	})
	atLeast(t, scanned, 100, "просмотренных файлов")
	if len(found) > 0 {
		t.Errorf("счёт запинок отправлен подсматриванием (%d):\n  %s\n\n"+
			"Возьмите LvnFrameWatch.Take(): глава, ушедшая без сброса, дарит свои "+
			"запинки следующей — и обе цифры перестают что-либо значить.",
			len(found), strings.Join(found, "\n  "))
	}
}
