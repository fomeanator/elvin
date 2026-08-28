package lvn

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// ВЕСЬ КОНТЕНТ РЕПОЗИТОРИЯ ОБЯЗАН СОБИРАТЬСЯ.
//
// Стражей у языка много, и каждый проверяет ПРАВИЛО. А самого простого вопроса —
// «а наши собственные скрипты вообще компилируются?» — не задавал никто, и
// ответ оказался «семь из них нет». Четыре написаны на 3D-командах
// (`light`, `weather`, `o3d`), которых в компиляторе НЕТ вовсе: язык 3D то ли
// не доехал, то ли уехал, а контент остался и молча лежит несобираемым. Три —
// дуэль с битой меткой и мёртвой петлёй.
//
// Ничего не падало, потому что эти файлы никто не открывает в сборке: демо
// запускают руками, а публикуется совсем другое. Поломку такого рода находят
// не тесты, а автор, который однажды решит показать демо.
//
// Известные долги перечислены поимённо и с причиной. Список — не «разрешение
// не чинить», а зафиксированная граница: пока файл в нём, он не роняет прогон,
// но и не исчезает из виду; новый файл сюда не добавляется молча, потому что
// для этого надо написать причину.
func TestRepositoryScriptsCompile(t *testing.T) {
	root := repoRoot(t)

	known := map[string]string{
		"content/3d-demo/showcase-3d.lvns":               "3D-язык (light/o3d) отсутствует в компиляторе — контент опережает язык",
		"content/flower-field/infinite.lvns":             "то же: команда weather не реализована",
		"content/graveyard/night.lvns":                   "то же: команда weather не реализована",
		"content/hdr-test/scale.lvns":                    "то же: команда weather не реализована",
		"server/content/scripts/duel.lvns":               "дуэль: битая метка «дуэль_рестарт» и мёртвая петля (известный развал пакета)",
		"server/content/packages/lvn-duel/duel.lvns":     "копия дуэли — тот же развал",
		"server/content/knight-roguelike/duel-ch01.lvns": "копия дуэли — тот же развал",
	}

	var broken, healed []string
	for _, dir := range []string{"content", "howto", "server/content"} {
		base := filepath.Join(root, dir)
		if _, err := os.Stat(base); err != nil {
			continue
		}
		err := filepath.Walk(base, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".lvns") {
				return err
			}
			rel, _ := filepath.Rel(root, path)
			rel = filepath.ToSlash(rel)

			src, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			_ = src
			// Через тот же путь, что и CLI: у ConvertFile есть каталог для
			// include, у Convert — нет, и половина скриптов без него не соберётся.
			raw, cErr := lvns.ConvertFile(path)
			ok := cErr == nil
			if ok {
				var doc *Doc
				if b, err := json.Marshal(raw); err == nil {
					ok = json.Unmarshal(b, &doc) == nil
				}
				if ok {
					for _, is := range Validate(doc) {
						if is.Sev == SevError {
							ok = false
							break
						}
					}
				}
			}
			if _, listed := known[rel]; ok && listed {
				healed = append(healed, rel)
			} else if !ok && !listed {
				msg := "ошибок валидации"
				if cErr != nil {
					msg = cErr.Error()
				}
				broken = append(broken, rel+" — "+msg)
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", dir, err)
		}
	}

	sort.Strings(broken)
	sort.Strings(healed)
	if len(broken) > 0 {
		t.Fatalf("скрипты репозитория не собираются:\n  %s\n"+
			"почините их или впишите в known с ПРИЧИНОЙ — молча несобираемый контент "+
			"находит не тест, а автор, решивший показать демо",
			strings.Join(broken, "\n  "))
	}
	if len(healed) > 0 {
		t.Fatalf("эти скрипты уже собираются — уберите их из known:\n  %s",
			strings.Join(healed, "\n  "))
	}
}
