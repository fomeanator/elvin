package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ЗАПИСНАЯ КНИЖКА — ОДНА. Страж против расползания хранилища обратно.
//
// До выделения роли `PlayerPrefs` вызывали из 20 файлов: 166 обращений, 42
// фиксации на 66 записей. Фиксацию звали «когда вспомнят», и «нет фиксации»
// читалось одинаково там, где её забыли, и там, где убрали намеренно ради
// кадра. Забытая стоила поведения: одноразовый флаг перезапуска гасился без
// фиксации и после краха воскресал, выстреливая на чужой главе.
//
// Теперь хранилище знает один дом — `Lvn.LvnKeep`, где вопрос фиксации задан
// глаголом (`Put`/`Drop` набело, `Jot` в карандаше, `Batch` пачкой). Этот тест
// держит границу: обращение к `PlayerPrefs` откуда-либо ещё — красный.
//
// Почему в Go, а не в EditMode: страж должен работать без Unity, на любом
// прогоне CI, как и остальные проверки контракта в этом пакете.

var prefsCall = regexp.MustCompile(`(?:UnityEngine\.)?PlayerPrefs\.[A-Za-z]+\s*\(`)

// Единственный дом хранилища — относительный путь от корня репозитория.
const keepHome = "unity/Packages/com.lvn.engine/Runtime/LvnKeep.cs"

var storageRoots = []string{
	filepath.Join("unity", "Packages", "com.lvn.engine", "Runtime"),
	filepath.Join("unity", "Packages", "com.lvn.engine.shell", "Runtime"),
	filepath.Join("unity", "Packages", "com.lvn.engine.services", "Runtime"),
}

func TestDeviceStorageHasOneHome(t *testing.T) {
	root := repoRoot(t)

	if _, err := os.Stat(filepath.Join(root, filepath.FromSlash(keepHome))); err != nil {
		t.Fatalf("%s missing — the device notebook IS the contract; restore it rather than deleting this test", keepHome)
	}

	var strays []string
	for _, rel := range storageRoots {
		dir := filepath.Join(root, rel)
		if _, err := os.Stat(dir); err != nil {
			continue // пакет не установлен в этой раскладке — не повод краснеть
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			if strings.Contains(filepath.ToSlash(path), "/Tests/") {
				return nil // тестам можно: они чистят за собой напрямую
			}
			if strings.HasSuffix(filepath.ToSlash(path), keepHome) {
				return nil // сам дом
			}
			raw, err := os.ReadFile(path)
			if err != nil {
				return nil
			}
			for i, line := range strings.Split(string(raw), "\n") {
				code := line
				if j := strings.Index(code, "//"); j >= 0 {
					code = code[:j] // упоминание в комментарии — это документация, не вызов
				}
				if prefsCall.MatchString(code) {
					rel, _ := filepath.Rel(root, path)
					strays = append(strays, fmt.Sprintf("%s:%d  %s", filepath.ToSlash(rel), i+1, strings.TrimSpace(line)))
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("walk %s: %v", dir, err)
		}
	}

	if len(strays) > 0 {
		t.Fatalf("хранилище мимо записной книжки (%d):\n  %s\n\n"+
			"Ходить в PlayerPrefs напрямую — значит снова решать вопрос фиксации молчанием. "+
			"Возьмите Lvn.LvnKeep: Put/Drop — набело, Jot/JotDrop — в карандаше (фиксируется при уходе "+
			"приложения в фон), Batch() — пачка с одной фиксацией в конце.",
			len(strays), strings.Join(strays, "\n  "))
	}
}
