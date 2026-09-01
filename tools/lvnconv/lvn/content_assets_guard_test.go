package lvn

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

// АРТ, НА КОТОРЫЙ ССЫЛАЕТСЯ СКРИПТ, ОБЯЗАН ЛЕЖАТЬ НА МЕСТЕ.
//
// Битая ссылка не роняет ни компилятор, ни валидатор: у неё правильная форма,
// и узнаёт о ней игрок — серым прямоугольником вместо кадра или тишиной вместо
// музыки. Проверка дешёвая (существует ли файл), а не делал её никто.
//
// Нашлось три: расширение перепутано (`Andrey_Axe.png` при лежащем рядом
// `.jpg`), музыка и звук по путям, которых в дереве нет вовсе
// (`/content/music/…`, `/content/sfx/…` — правильные `/content/audio/…`).
//
// ДВЕ ТОНКОСТИ, без которых страж врёт:
//
//   - `?v=tight2` — версия кэша, а не часть пути. Без её отсечения «битыми»
//     оказываются восемь живых файлов.
//   - Главы `cold-*` ссылаются на партнёрский арт, которого в ОТКРЫТОМ
//     репозитории нет и не будет: 430 ссылок, все законные. Их пропускаем
//     явно — иначе страж утонет в шуме и будет выключен целиком.
func TestScriptAssetsExist(t *testing.T) {
	scanned := 0
	root := repoRoot(t)
	base := filepath.Join(root, "server", "content")
	if _, err := os.Stat(base); err != nil {
		t.Skip("нет server/content")
	}

	local := func(u string) string {
		if u == "" || strings.HasPrefix(u, "http") || strings.ContainsAny(u, "{}") {
			return ""
		}
		if i := strings.IndexByte(u, '?'); i >= 0 {
			u = u[:i] // версия кэша — не часть пути
		}
		u = strings.TrimPrefix(u, "/")
		u = strings.TrimPrefix(u, "content/")
		return filepath.Join(base, filepath.FromSlash(u))
	}

	var missing []string
	err := filepath.Walk(base, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasSuffix(path, ".lvn") {
			return err
		}
		if strings.HasPrefix(filepath.Base(path), "cold-") {
			return nil // партнёрский арт вне открытого репозитория
		}
		raw, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		scanned++
		var doc struct {
			Script []map[string]any `json:"script"`
		}
		if json.Unmarshal(raw, &doc) != nil {
			return nil // разбор документа — забота другого стража
		}
		rel, _ := filepath.Rel(base, path)
		for _, c := range doc.Script {
			for _, key := range []string{"sprite_url", "url", "body_url", "voice"} {
				s, _ := c[key].(string)
				p := local(s)
				if p == "" {
					continue
				}
				if _, err := os.Stat(p); err != nil {
					missing = append(missing, filepath.ToSlash(rel)+" → "+s)
				}
			}
		}
		return nil
	})
	if err != nil {
		t.Fatalf("обход контента: %v", err)
	}

	sort.Strings(missing)
	if len(missing) > 0 {
		t.Fatalf("скрипты ссылаются на отсутствующий арт:\n  %s\n"+
			"о битой ссылке узнаёт игрок — серым кадром или тишиной; ни компилятор, "+
			"ни валидатор её не видят, потому что форма правильная",
			strings.Join(missing, "\n  "))
	}
	// Порог пустоты: обход, не нашедший ни одного файла, зеленеет ни о чём.
	atLeast(t, scanned, 5, "проверенных скриптов")

}
