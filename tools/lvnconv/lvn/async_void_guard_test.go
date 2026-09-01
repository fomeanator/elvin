package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Фоновая работа запускается через LvnAsync.Fire, а не `async void`.
//
// У `async void` то же скверное свойство, ради которого завели Fire: упавшая
// задача исчезает бесследно — ни строки в логе, ни следа на устройстве. Хуже
// того, вместе с ней исчезает и всё, что стояло после await. Живой случай:
// оборванная сеть посреди покупки оставляла замок экрана поднятым НАВСЕГДА, и
// магазин мертвел молча.
//
// Исключения — те, где `async void` неизбежен: точка входа Unity и обработчик,
// который сам ловит своё исключение и восстанавливается. Оба обязаны нести
// внутри try.
func TestBackgroundWorkGoesThroughFire(t *testing.T) {
	scanned := 0
	root := repoRoot(t)
	allowed := map[string]string{
		"unity/Packages/com.lvn.engine.shell/Runtime/NovelApp.Boot.cs": "Start",           // точка входа Unity
		"unity/Packages/com.lvn.engine/Runtime/UI/VnStage.SaveLoad.cs": "RestoreSnapshot", // ловит и продолжает
	}
	var strays []string
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
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			rel, _ := filepath.Rel(root, path)
			rel = filepath.ToSlash(rel)
			for i, line := range strings.Split(string(b), "\n") {
				code := line
				if c := strings.Index(code, "//"); c >= 0 {
					code = code[:c]
				}
				if !strings.Contains(code, "async void") {
					continue
				}
				if name, ok := allowed[rel]; ok && strings.Contains(code, name+"(") {
					if !strings.Contains(string(b), "try") {
						strays = append(strays, fmt.Sprintf("%s:%d: %s — разрешён, но не ловит своё исключение", rel, i+1, strings.TrimSpace(line)))
					}
					continue
				}
				strays = append(strays, fmt.Sprintf("%s:%d: %s", rel, i+1, strings.TrimSpace(line)))
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}
	atLeast(t, scanned, 60, "просмотренных файлов")

	if len(strays) > 0 {
		t.Fatalf("фоновая работа запущена мимо Fire:\n  %s\n\nСделайте тело `async Task` и зовите"+
			" LvnAsync.Fire(ЧтоТоAsync(), \"имя\"): упавший `async void` уносит с собой и лог, и всё,"+
			" что стояло после await.", strings.Join(strays, "\n  "))
	}
}
