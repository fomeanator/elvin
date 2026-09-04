package lvn

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// ФОРМА РЕАКЦИИ НА ПРАВКУ: СПРОСИТЬ РАЗНИЦУ, И ЗА КАТАЛОГОМ — ТОЛЬКО ЕСЛИ НАДО.
//
// Опрос дёшев, опасен момент правки: версия меняется у всех разом, и все разом
// идут за ней. Пик здесь не размазан по времени — он совпадает по построению, и
// дрожание интервала его не сгладит.
//
// Замерено живым стендом (qa/reaction-burst-check.sh, 120 клиентов):
//
//	тракт разницы    пик 0,1 МБ/с, трафик 0,1 МБ, ошибок 0
//	прежний тракт    пик 10,9 МБ/с, трафик 14,4 МБ, ошибок 0
//
// Стократная разница сидит в ОДНОМ раннем возврате: каталог не менялся — за ним
// не ходим. Убери его — коды ответов не изменятся, тесты не покраснеют, а
// всплеск вырастет во столько же раз, во сколько весит карта версий.
//
// Порядок тоже часть договора: разницу спрашивают ПРЕЖДЕ каталога. Спроси
// после — и экономить будет уже нечего.
func TestContentChangeAsksTheDeltaFirst(t *testing.T) {
	root := repoRoot(t)
	path := filepath.Join(root, "unity", "Packages", "com.lvn.engine.shell",
		"Runtime", "NovelApp.Boot.cs")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("реакция на смену контента не прочитана: %v", err)
	}
	src := string(raw)

	const handler = "OnContentChangedAsync()"
	start := strings.Index(src, "private async Task "+handler)
	if start < 0 {
		t.Fatal("обработчик смены контента не найден — страж потерял предмет охраны")
	}
	body := src[start:]
	if end := strings.Index(body[len(handler):], "\n        private "); end >= 0 {
		body = body[:len(handler)+end]
	}

	delta := strings.Index(body, "FetchDeltaAsync(")
	manifest := strings.Index(body, "FetchManifestAsync(")
	skip := strings.Index(body, "ManifestChanged")

	if delta < 0 {
		t.Fatal("реакция перестала спрашивать разницу — каждая правка снова стоит всего каталога")
	}
	if manifest < 0 {
		t.Fatal("в реакции нет похода за каталогом — страж смотрит не на тот код")
	}
	if skip < 0 {
		t.Fatal("исчезла проверка «менялся ли каталог» — за ним пойдут всегда")
	}
	if delta > manifest {
		t.Error("каталог забирают ПРЕЖДЕ разницы: экономить после этого уже нечего")
	}
	if skip > manifest {
		t.Error("проверка «менялся ли каталог» стоит ПОСЛЕ похода за ним — она ничего не решает")
	}

	// Ранний возврат — это и есть вся экономия. Без него правка реплики снова
	// потянет каталог у каждого играющего одновременно.
	tail := body[skip:]
	if cut := strings.Index(tail, "FetchManifestAsync("); cut >= 0 {
		tail = tail[:cut]
	}
	if !strings.Contains(tail, "return") {
		t.Error("между проверкой каталога и походом за ним нет раннего возврата — " +
			"неизменившийся каталог всё равно скачают")
	}
}
