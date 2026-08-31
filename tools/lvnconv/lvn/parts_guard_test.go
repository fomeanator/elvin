package lvn

import (
	"fmt"
	"os"
	"path/filepath"
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
