package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
	"unicode"
)

// СЛОВА НА ЭКРАНЕ ПРИНАДЛЕЖАТ АВТОРУ. Страж против возвращения зашитых подписей.
//
// Движок лежит в открытом репозитории и служит любым играм, а его оболочка
// говорила по-русски: пятьдесят подписей стояли прямо в коде — «Загрузки»,
// «Удалить аккаунт», «Пока нет сохранений». Автору-неносителю их было не
// перевести никак: ключа нет, значит и слова в манифесте не переопределить.
// Крайним случаем был логотип — буква «Т» в кружке, инициал ОДНОЙ конкретной
// новеллы, зашитый в движок для всех.
//
// Правило: подпись на экране идёт через `LvnWords.Of(ключ, английское
// умолчание)`. Английское — потому что это системный слой (docs/language-policy),
// а родное слово автор кладёт в манифест (`ui.words`).
//
// Что тест НЕ ловит: русский в комментариях, в логах и в диагностике. Там он
// уместен и полезен — это язык, на котором думает команда.

var (
	uiText   = regexp.MustCompile(`new\s+(?:[\w.]+\.)?(?:Label|Button)\s*\(|\.text\s*=`)
	strLit   = regexp.MustCompile(`"([^"\\]|\\.)*"`)
	noiseLn  = regexp.MustCompile(`LvnLog|Debug\.|Trace\(|\[lvn-|nameof`)
	shellDir = filepath.Join("unity", "Packages", "com.lvn.engine.shell", "Runtime")
)

func hasCyrillic(s string) bool {
	for _, r := range s {
		if unicode.Is(unicode.Cyrillic, r) {
			return true
		}
	}
	return false
}

func TestShellSaysNothingInAHardcodedLanguage(t *testing.T) {
	scanned := 0
	root := repoRoot(t)
	dir := filepath.Join(root, shellDir)
	if _, err := os.Stat(dir); err != nil {
		t.Skipf("оболочка не установлена в этой раскладке: %v", err)
	}

	var strays []string
	err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
			return nil
		}
		scanned++
		raw, err := os.ReadFile(path)
		if err != nil {
			return nil
		}
		for i, line := range strings.Split(string(raw), "\n") {
			code := line
			if j := strings.Index(code, "//"); j >= 0 {
				code = code[:j] // комментарии — язык команды, не игрока
			}
			if !uiText.MatchString(code) || noiseLn.MatchString(code) {
				continue
			}
			if strings.Contains(code, "LvnWords") {
				continue // слово уже принадлежит автору
			}
			for _, lit := range strLit.FindAllString(code, -1) {
				if hasCyrillic(lit) {
					rel, _ := filepath.Rel(root, path)
					strays = append(strays, fmt.Sprintf("%s:%d  %s", filepath.ToSlash(rel), i+1, strings.TrimSpace(line)))
					break
				}
			}
		}
		return nil
	})
	if err != nil {
		t.Fatalf("walk %s: %v", dir, err)
	}

	atLeast(t, scanned, 60, "просмотренных файлов")

	if len(strays) > 0 {
		t.Fatalf("подписи на экране зашиты одним языком (%d):\n  %s\n\n"+
			"Экран принадлежит автору игры, а не движку. Оберните: "+
			"LvnWords.Of(\"ключ\", \"English default\") — умолчание английское (системный слой), "+
			"родное слово автор кладёт в манифест, в ui.words.",
			len(strays), strings.Join(strays, "\n  "))
	}
}
