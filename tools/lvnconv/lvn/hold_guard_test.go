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

// ИСТОРИЮ ПРИДЕРЖИВАЕТ ДОМ, А НЕ КАЖДЫЙ ОП САМ.
//
// Обряд «сценарий ждёт, пока идёт работа» состоит из трёх частей: придержать
// (`ctx.Hold`), запустить работу под присмотром журнала, и — что бы ни
// случилось — отпустить (`ctx.Resume`). Он был написан четырнадцать раз
// руками: реклама, комната на двоих, магазин, настройки, гардероб, вход,
// разрешение на уведомления.
//
// Цена забытой третьей части несоразмерна остальным ошибкам интерфейса:
// сценарий остаётся придержанным НАВСЕГДА. Не «эффект не сыграл» и не «кнопка
// не нажалась» — глава просто больше не идёт, и починить это игрок не может
// ничем, кроме перезапуска.
//
// Поэтому обряд живёт в `Lvn.LvnOps.Awaiting`, где его нельзя недописать, а
// прямых `ctx.Hold()` в рантайме не остаётся.
func TestStoryHoldGoesThroughItsHome(t *testing.T) {
	scanned := 0
	root := repoRoot(t)
	hold := regexp.MustCompile(`\bctx\.Hold\(\)`)

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
			scanned++
			// Сам дом и его документация — единственное законное место.
			if filepath.Base(path) == "LvnOps.cs" {
				return nil
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			for i, ln := range strings.Split(string(b), "\n") {
				trimmed := strings.TrimSpace(ln)
				// Строка примера в комментарии — не вызов.
				if strings.HasPrefix(trimmed, "//") || strings.HasPrefix(trimmed, "///") {
					continue
				}
				if hold.MatchString(ln) {
					found = append(found, fmt.Sprintf("%s:%d", filepath.Base(path), i+1))
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	atLeast(t, scanned, 60, "просмотренных файлов")

	if len(found) > 0 {
		sort.Strings(found)
		t.Fatalf("историю придерживают мимо дома: %s\n"+
			"зовите Lvn.LvnOps.Awaiting(ctx, работа, имя) — он придержит, запустит "+
			"под присмотром журнала и отпустит в finally. Забытое отпускание вешает "+
			"главу насмерть: игроку остаётся только перезапуск",
			strings.Join(found, ", "))
	}
}
