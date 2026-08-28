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

// Кнопка, которая ждёт, обязана отпустить себя при провале.
//
// Обряд «выключить → дождаться → включить» стоял в девяти местах. Двое обернули
// его в try/finally — там за замок уже платили дефектом. Остальные положились на
// то, что ожидание кончится успехом; вход через провайдера, кнопка «Играть» и
// «Стереть загруженное» ждали сеть и диск без страховки. Одно исключение — и
// кнопка выключена навсегда, подпись осталась «Connecting…», игра цела, лог
// чист, а починка у игрока одна: перезапуск.
//
// Признак: SetEnabled(false) и await в одном блоке. Либо рядом finally (кто-то
// уже платил за этот урок), либо работа идёт через LvnBusy.
func TestWaitingButtonReleasesItself(t *testing.T) {
	root := repoRoot(t)

	// Не кнопка ожидания: гашение ввода на время хореографии, где включение
	// назначает планировщик кадра, а не завершение задачи.
	allowed := map[string]string{
		"LvnBusy.cs":        "сам дом",
		"VnStage.Actors.cs": "гашение выбора на время хореографии: включает планировщик кадра",
	}

	disableRe := regexp.MustCompile(`SetEnabled\(false\)`)
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
			lines := strings.Split(string(b), "\n")
			for i, ln := range lines {
				if !disableRe.MatchString(ln) {
					continue
				}
				// Только выключение ПО НАЖАТИЮ: начальное состояние строки при
				// сборке экрана — не ожидание, а вид.
				from := i - 8
				if from < 0 {
					from = 0
				}
				before := strings.Join(lines[from:i], "\n")
				if !strings.Contains(before, "clicked") && !strings.Contains(before, "Tapped") {
					continue
				}
				// Окно до конца обработчика: ждёт ли он чего-нибудь и есть ли
				// страховка — finally, catch или дом занятости.
				end := i + 80
				if end > len(lines) {
					end = len(lines)
				}
				w := strings.Join(lines[i:end], "\n")
				if !strings.Contains(w, "await") {
					continue
				}
				if strings.Contains(w, "finally") || strings.Contains(w, "catch") ||
					strings.Contains(w, "LvnBusy") {
					continue
				}
				found = append(found, fmt.Sprintf("%s:%d", base, i+1))
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	sort.Strings(found)
	if len(found) > 0 {
		t.Fatalf("кнопка выключена на время ожидания без страховки: %s\n"+
			"возьмите LvnBusy.OnClick/RunAsync — он отпускает кнопку при провале "+
			"и молча отклоняет второй тап",
			strings.Join(found, ", "))
	}
}
