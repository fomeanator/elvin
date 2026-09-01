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

// ЧТО НАПИСАЛА ГЛАВА — С ГЛАВОЙ И УХОДИТ.
//
// У команды автора область действия — глава. У статического поля движка —
// «пока игра не закрыта». Свести их можно только руками, и место одно: уборка
// сцены. Забыть это легко, а последствие видит не тот, кто забыл: `text_pace`
// из драматичной сцены замедлял СЛЕДУЮЩУЮ главу, а через меню — и чужую
// новеллу, где про темп не сказано ни слова.
//
// Здесь проверяется общее правило, а не один случай: всякое статическое поле
// ядра, которому сцена присваивает значение, обязано сбрасываться уборкой.
// Настройки игрока сюда не попадают — их пишет не сцена, а экран настроек.
func TestChapterStateDoesNotOutliveTheChapter(t *testing.T) {
	root := repoRoot(t)
	coreDir := filepath.Join(root, "unity", "Packages", "com.lvn.engine", "Runtime")

	// Статические изменяемые поля домов ядра (сам корень Runtime, без UI/Content).
	classRe := regexp.MustCompile(`public static class (\w+)`)
	// RE2 не знает отрицательного просмотра вперёд — отсев в коде, не в
	// шаблоне. Та же ловушка, что с обратными ссылками: язык шаблонов здесь
	// беднее привычного, и притворяться иначе значит уронить страж.
	fieldRe := regexp.MustCompile(`public static ([\w<>\[\],.?]+) (\w+)\s*[;=]`)
	type key struct{ cls, fld string }
	core := map[key]bool{}
	entries, err := os.ReadDir(coreDir)
	if err != nil {
		t.Fatal(err)
	}
	files := 0
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		files++
		src := string(mustRead(t, filepath.Join(coreDir, e.Name())))
		cls := ""
		if m := classRe.FindStringSubmatch(src); m != nil {
			cls = m[1]
		}
		if cls == "" {
			continue
		}
		for _, m := range fieldRe.FindAllStringSubmatch(src, -1) {
			switch m[1] {
			case "readonly", "class", "event":
				continue
			}
			core[key{cls, m[2]}] = true
		}
	}
	atLeast(t, files, 15, "файлов ядра")
	atLeast(t, len(core), 5, "статических полей ядра")

	uiDir := filepath.Join(root, "unity", "Packages", "com.lvn.engine", "Runtime", "UI")
	stage := map[key]bool{}
	_ = filepath.Walk(uiDir, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasPrefix(filepath.Base(path), "VnStage") {
			return nil
		}
		src := stripComments(string(mustRead(t, path)))
		for k := range core {
			if regexp.MustCompile(`\b` + k.cls + `\.` + k.fld + `\s*=[^=]`).MatchString(src) {
				stage[k] = true
			}
		}
		return nil
	})

	reset := stripComments(string(mustRead(t, filepath.Join(uiDir, "VnStage.Playback.cs"))))
	i := strings.Index(reset, "private void ResetStage()")
	if i < 0 {
		t.Fatal("уборки сцены нет — якорь стража промахнулся")
	}
	body := reset[i:]
	if j := strings.Index(body, "\n        }"); j > 0 {
		body = body[:j]
	}

	var leaking []string
	for k := range stage {
		if !regexp.MustCompile(`\b` + k.cls + `\.` + k.fld + `\s*=`).MatchString(body) {
			leaking = append(leaking, fmt.Sprintf("%s.%s", k.cls, k.fld))
		}
	}
	sort.Strings(leaking)
	if len(leaking) > 0 {
		t.Errorf("сцена пишет в статику ядра, а уборка её не сбрасывает (%d):\n  %s\n\n"+
			"У команды автора область действия — глава, у статического поля — «пока игра не\n"+
			"закрыта». Последствие видит не тот, кто забыл: настройка одной новеллы всплывает\n"+
			"в другой, и её автор ищет причину у себя.",
			len(leaking), strings.Join(leaking, ", "))
	}
}
