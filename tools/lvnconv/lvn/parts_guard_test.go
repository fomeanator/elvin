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
