package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// Класс, который нигде не зовут, объясняет себя.
//
// В движке нашлись три полностью написанных и покрытых тестами класса, которые
// НЕ ПОДКЛЮЧЕНЫ ни к чему: схлопывающая запись (для автосейвов), расписание
// кадра (для перемотки), текстуры интерфейса. Каждый решает живую задачу, и ни
// один не участвует в игре.
//
// Это не мёртвый код и не забытый: включение каждого меняет что-то крупное —
// облик оболочки, модель сцены, момент записи на диск. Но со стороны «готово и
// ждёт решения» неотличимо от «написали и забыли», а спустя полгода не отличит
// и автор. Поэтому спящий класс обязан сказать о себе словом НЕ ПОДКЛЮЧЁН и
// объяснить, чего ждёт.
//
// Модели данных и конфигурации исключены: их «зовут» через поля экземпляров, и
// по имени типа они не упоминаются никогда.
func TestDormantClassesExplainThemselves(t *testing.T) {
	root := repoRoot(t)
	classRe := regexp.MustCompile(`public (?:sealed |abstract |partial |static )*class (\w+)`)

	sources := map[string]string{}
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell",
		"com.lvn.engine.services", "com.lvn.engine.spine"} {
		dir := filepath.Join(root, "unity", "Packages", pkg, "Runtime")
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			sources[path] = string(b)
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	// Файлы-описания данных: модели манифеста и конфигурация интерфейса.
	isData := func(path string) bool {
		base := filepath.Base(path)
		return base == "LvnManifest.cs" || base == "LvnUiConfig.cs"
	}

	var mute []string
	for home, text := range sources {
		if isData(home) {
			continue
		}
		for _, m := range classRe.FindAllStringSubmatch(text, -1) {
			name := m[1]
			// Классы расширений зовут методами, а не по имени типа.
			if strings.HasSuffix(name, "Extensions") || strings.HasSuffix(name, "Words") {
				continue
			}
			used := false
			word := regexp.MustCompile(`\b` + name + `\b`)
			for path, other := range sources {
				if path != home && word.MatchString(other) {
					used = true
					break
				}
			}
			if used || strings.Contains(text, "НЕ ПОДКЛЮЧЁН") {
				continue
			}
			rel, _ := filepath.Rel(root, home)
			mute = append(mute, fmt.Sprintf("%s (%s)", name, filepath.ToSlash(rel)))
		}
	}
	// Порог охвата: «спящих классов нет» и «обход не нашёл ни одного файла»
	// выглядят одинаково зелёными.
	atLeast(t, len(sources), 150, "просмотренных файлов")

	if len(mute) > 0 {
		t.Fatalf("класс не зовут нигде и он молчит об этом:\n  %s\n\nНапишите в его сводке"+
			" «НЕ ПОДКЛЮЧЁН» и чего он ждёт: «готово и ждёт решения» иначе неотличимо от"+
			" «написали и забыли».", strings.Join(mute, "\n  "))
	}
}
