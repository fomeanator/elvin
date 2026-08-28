package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Поле команды читается через LvnNum, а не приведением.
//
// Разница не в стиле. Прямое `(float)cmd[key]` БРОСАЕТ на «57%» и на опечатке
// автора — то есть роняет всю команду, а не одно поле, — тогда как дом чисел
// заведён с обещанием «никогда не бросает: одно кривое поле не должно ронять
// главу». И проценты, понятные дереву `ui` с первого дня, мимо дома не
// понимались вовсе.
func TestCommandNumbersGoThroughLvnNum(t *testing.T) {
	root := repoRoot(t)
	bad := []string{"(float)cmd[", "(double)cmd[", "(float)cmd?["}
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
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			for i, line := range strings.Split(string(b), "\n") {
				code := line
				if c := strings.Index(code, "//"); c >= 0 {
					code = code[:c]
				}
				for _, pat := range bad {
					if strings.Contains(code, pat) {
						rel, _ := filepath.Rel(root, path)
						strays = append(strays, fmt.Sprintf("%s:%d: %s", rel, i+1, strings.TrimSpace(line)))
						break
					}
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}
	if len(strays) > 0 {
		t.Fatalf("число из команды берут приведением:\n  %s\n\nЗовите LvnNum.Parse(cmd[key], умолчание):"+
			" приведение бросает на проценте и на опечатке и роняет команду целиком.",
			strings.Join(strays, "\n  "))
	}
}
