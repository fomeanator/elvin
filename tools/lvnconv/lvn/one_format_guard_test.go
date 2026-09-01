package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ФОРМАТ АРТА ОДИН.
//
// Форматов было два: сырой .astc приехал первым, слёг 06.07 на невыровненных
// размерах блока и с тех пор стоял с выключателем `AstcEnabled = false`.
// Живым остался KTX2. Полгода мёртвый занимал 171 строку клиента и 205
// сервера — и, что хуже размера, отвечал на вопрос «а почему быстрый формат
// не работает?» раньше, чем этот вопрос успевали задать: два кандидата на
// одну работу выглядят как выбор, а не как поломка одного из них.
//
// Второй формат, если он однажды понадобится, — это не «ещё одна развилка в
// DecodeSpriteAsync», а замена. Развилка возвращает ровно то состояние, в
// котором никто не замечал, что показ идёт медленным путём.
func TestOneGpuFormatForStoryArt(t *testing.T) {
	root := repoRoot(t)
	src := string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.Sprites.cs")))
	body := stripComments(src)

	forks := regexp.MustCompile(`TryDecode(\w+)Async`).FindAllStringSubmatch(body, -1)
	seen := map[string]bool{}
	for _, f := range forks {
		seen[f[1]] = true
	}
	if len(seen) != 1 || !seen["Ktx2"] {
		t.Fatalf("развилок формата в DecodeSpriteAsync: %v — а должна быть одна (Ktx2). "+
			"Второй формат — это ЗАМЕНА, а не ещё одна ветка: пока их два, "+
			"поломка одного читается как «работает другой»", seen)
	}
}

// ОБЩЕЕ НЕ ЖИВЁТ В ДОМЕ ОДНОГО ИЗ ПОТРЕБИТЕЛЕЙ.
//
// «Какие расширения считать исходником», «есть ли файл», «куда на диске
// ложится путь запроса» — работа всех производных файлов сразу: уменьшенных
// вариантов, кодов, и того же .astc. Жила она в astc.go, потому что он
// приехал первым. Пока формат был жив, это выглядело безобидно; в день, когда
// мёртвый сняли, сборка сервера встала — общий механизм уехал вместе с одним
// из троих.
//
// Дом, названный по потребителю, — дом не про то. Признак виден заранее:
// файл, в который ходят двое, кто в его имени не упомянут.
func TestDerivedHelpersLiveInTheirOwnHome(t *testing.T) {
	root := repoRoot(t)
	home := string(mustRead(t, filepath.Join(root, "server/derived.go")))
	for _, decl := range []string{"var sourceExts", "func fileExists", "func (s *server) contentPath"} {
		if !strings.Contains(home, decl) {
			t.Errorf("server/derived.go не объявляет %q — общее для производных файлов "+
				"снова разъехалось по домам форматов", decl)
		}
	}
	for _, file := range []string{"server/ktx2.go", "server/downscale.go"} {
		body := string(mustRead(t, filepath.Join(root, file)))
		for _, decl := range []string{"var sourceExts", "func fileExists", "func (s *server) contentPath"} {
			if strings.Contains(body, decl) {
				t.Errorf("%s объявляет %q — это работа ВСЕХ производных файлов, "+
					"её дом server/derived.go", file, decl)
			}
		}
	}
}

// МЁРТВОГО ФОРМАТА БОЛЬШЕ НЕТ НИГДЕ.
//
// Снятый формат оставляет хвосты в неожиданных местах: список расширений в
// кэше клиента, суффикс в наборе производных, раздел в README. Каждый хвост
// поодиночке безобиден и ровно поэтому переживает уборку.
func TestDeadFormatLeavesNoTails(t *testing.T) {
	root := repoRoot(t)
	for _, gone := range []string{
		"server/astc.go",
		"server/astc_test.go",
		"unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.Astc.cs",
	} {
		if _, err := os.Stat(filepath.Join(root, gone)); err == nil {
			t.Errorf("%s снова на месте — мёртвый формат вернулся", gone)
		}
	}
	for _, file := range []string{
		"unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.cs",
		"unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.Sprites.cs",
	} {
		body := stripComments(string(mustRead(t, filepath.Join(root, file))))
		if strings.Contains(strings.ToLower(body), ".astc") {
			t.Errorf("%s всё ещё разбирает \".astc\" — клиент таких адресов "+
				"не строит, значит эта ветка не выполняется никогда", file)
		}
	}
}
