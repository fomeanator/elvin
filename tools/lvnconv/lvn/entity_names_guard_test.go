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

// Имя сущности, которое видит игрок, приходит из СЛОВАРЯ.
//
// Названия новелл, подборок, персонажей, нарядов и CG автор пишет в манифесте
// на своём языке. Экран, берущий их напрямую («t.name ?? t.id»), показывает
// авторскую строку всегда — и при переключении на английский реплики
// становились английскими, а «Агентство», «Экспедиции» и имена героинь
// оставались русскими: полстраницы на одном языке, полстраницы на другом.
//
// Правило: имя спрашивается у LvnWords.Name(вид, id, авторское) — он вернёт
// перевод, если тот есть, иначе авторское имя, а на латинице ещё и прочитает
// кириллицу транслитом. Идентификатор игроку не показывают никогда.
//
// Страж смотрит на «.name ??» — конструкцию «авторское имя, иначе id», ровно
// ту, которой обходят словарь.
func TestEntityNamesGoThroughTheDictionary(t *testing.T) {
	root := repoRoot(t)

	// Имена, которые словарю не принадлежат.
	allowed := map[string]string{
		// Провайдер входа (Google/Apple) — не контент новеллы: он зовётся
		// одинаково на всех языках и приходит от платформы, а не от автора.
		"SettingsScreen.Account.cs": "имя провайдера входа приходит от платформы",
	}

	bare := regexp.MustCompile(`\.name\s*\?\?`)
	var found []string

	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell", "com.lvn.engine.services"} {
		dir := filepath.Join(root, "unity", "Packages", pkg, "Runtime")
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			base := filepath.Base(path)
			if _, ok := allowed[base]; ok {
				return nil
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			for i, ln := range strings.Split(string(b), "\n") {
				if bare.MatchString(ln) {
					found = append(found, fmt.Sprintf("%s:%d", base, i+1))
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	sort.Strings(found)
	if len(found) > 0 {
		t.Fatalf("имя сущности берётся мимо словаря: %s\n"+
			"спросите его у дома: LvnWords.Name(\"title\"|\"collection\"|\"actor\"|\"skin\"|\"cg\", id, авторское) — "+
			"иначе при английском интерфейсе останется кириллица, а вместо забытого имени игрок увидит идентификатор",
			strings.Join(found, ", "))
	}
}
