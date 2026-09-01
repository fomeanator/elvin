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
