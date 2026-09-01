package lvn

import (
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ЗЕРКАЛО ОБНОВЛЯЮТ ВМЕСТЕ С ОРИГИНАЛОМ.
//
// Скорость печати живёт в двух записях: настройка игрока (`LvnPrefs.TextSpeed`)
// и множитель часов печати (`TypewriterClock.UserSpeedMultiplier`). Вторая —
// зеркало первой, и это ЗАКОННАЯ инверсия: часы лежат в ядре, настройки в
// интерфейсе, ядро их не видит. Толкать значение приходится сверху.
//
// Но законная инверсия держится на памяти автора: кто заведёт третий путь к
// `_textSpeed` и не толкнёт зеркало, получит настройку, которая «не
// применяется», — а искать будут в часах, в ползунке и в сохранении, потому
// что в самой настройке всё правильно.
//
// Здесь закреплено то, что сегодня верно: у поля два писателя (загрузка и
// установка), и КАЖДЫЙ рядом обновляет зеркало.
func TestTextSpeedMirrorIsPushedEverywhere(t *testing.T) {
	root := repoRoot(t)
	src := stripComments(string(mustRead(t, filepath.Join(root,
		"unity", "Packages", "com.lvn.engine", "Runtime", "UI", "LvnPrefs.cs"))))
	lines := strings.Split(src, "\n")

	write := regexp.MustCompile(`(?:_textSpeed\s*=|Set\(ref _textSpeed)`)
	mirror := regexp.MustCompile(`TypewriterClock\.UserSpeedMultiplier\s*=`)

	// СЧИТАЕМ ЧИСЛОМ, а не соседством. Первая версия искала зеркало в шести
	// строках от записи — и не нашла у загрузки: там пишут поле в начале
	// метода, а зеркало толкают в конце, за двадцатью строками. Требование
	// «рядом» было выдумано мной, а настоящее правило проще: сколько путей
	// пишут настройку, столько раз и толкают зеркало.
	writes, mirrored := 0, 0
	for _, l := range lines {
		if write.MatchString(l) {
			writes++
		}
		if mirror.MatchString(l) {
			mirrored++
		}
	}
	atLeast(t, writes, 2, "мест, где пишут скорость печати")
	if mirrored != writes {
		t.Errorf("скорость печати пишут в %d местах, зеркало толкают %d раз.\n\n"+
			"`TypewriterClock.UserSpeedMultiplier` — зеркало настройки, и ядро само её не видит\n"+
			"(часы в ядре, настройки в интерфейсе). Путь, забывший толкнуть зеркало, даёт\n"+
			"настройку, которая «не применяется», — а искать будут в часах и в ползунке,\n"+
			"потому что в самой настройке всё правильно.", writes, mirrored)
	}
}

// ТЕМП ПЕЧАТИ УХОДИТ ВМЕСТЕ С ГЛАВОЙ.
//
// `text_pace` пишет СТАТИЧЕСКОЕ поле ядра (`TypewriterClock.GlobalCps`), а
// статическое поле переживает и главу, и новеллу. Сбрасывать его было некому:
// медленная драматичная сцена замедляла следующую главу, а через меню — и
// чужую новеллу. Автор второй новеллы искал бы причину у себя и не нашёл: в
// его сценарии про темп не сказано ни слова.
//
// Уборка сцены — единственное место, которое знает про конец главы.
func TestTextPaceIsClearedWithTheChapter(t *testing.T) {
	root := repoRoot(t)
	src := stripComments(string(mustRead(t, filepath.Join(root,
		"unity", "Packages", "com.lvn.engine", "Runtime", "UI", "VnStage.Playback.cs"))))
	if !strings.Contains(src, "private void ResetStage()") {
		t.Fatal("уборки сцены нет — якорь стража промахнулся")
	}
	body := src[strings.Index(src, "private void ResetStage()"):]
	if i := strings.Index(body, "\n        }"); i > 0 {
		body = body[:i]
	}
	if !regexp.MustCompile(`GlobalCps\s*=\s*0`).MatchString(body) {
		t.Error("уборка сцены не сбрасывает темп печати.\n\n" +
			"`text_pace` пишет статическое поле ядра, и оно переживает главу: медленная сцена\n" +
			"замедлит следующую главу, а через меню — и ЧУЖУЮ новеллу. Автор той новеллы будет\n" +
			"искать причину у себя и не найдёт: в его сценарии про темп не сказано ни слова.")
	}
}
