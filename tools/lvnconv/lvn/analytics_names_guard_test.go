package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ИМЯ СОБЫТИЯ — ДОГОВОР ДВУХ СТОРОН. Страж против молчаливого расхождения.
//
// Клиент пишет событие, сервер по имени сворачивает воронку. Пока имена стояли
// голыми литералами в шестнадцати местах C# и константами в Go, договор держался
// на памяти автора — и разошёлся: сервер годами носил пометки «not sent yet» у
// трёх событий, которые клиент давно слал, а отчёт воронки безусловно утверждал
// «клиент не шлёт chapter_abandon» и вёл читателя к выводам о несуществующем
// мире. Никто не солгал — просто никто не сверял.
//
// Сверяем в одну сторону: КАЖДОЕ имя, которое сервер сворачивает специально,
// обязано существовать у клиента. Обратное неверно и не проверяется — клиент
// вправе слать больше (сервер считает такие события общим счётчиком), а имена
// авторских меток конверсии (`track "имя"` в .lvns) вообще приходят из новеллы
// и договором движка не являются.

var (
	goEvConst = regexp.MustCompile(`(?m)^\s*(ev[A-Za-z]+)\s*=\s*"([a-z_]+)"`)
	csConst   = regexp.MustCompile(`(?m)public\s+const\s+string\s+\w+\s*=\s*"([a-z_]+)"\s*;`)
)

// Имена, которые сервер знает намеренно без клиента — с причиной.
var serverOnlyEvents = map[string]string{
	"track": "авторская метка конверсии: имя приходит строкой из .lvns, а не из движка",
}

func TestAnalyticsNamesMatchTheServer(t *testing.T) {
	root := repoRoot(t)

	rollup, err := os.ReadFile(filepath.Join(root, "server", "analytics_rollup.go"))
	if err != nil {
		t.Fatalf("server/analytics_rollup.go: %v", err)
	}
	events, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(
		"unity/Packages/com.lvn.engine.services/Runtime/LvnEvents.cs")))
	if err != nil {
		t.Fatalf("LvnEvents.cs: %v — перечень имён И ЕСТЬ клиентская сторона договора", err)
	}

	client := map[string]bool{}
	for _, m := range csConst.FindAllStringSubmatch(string(events), -1) {
		client[m[1]] = true
	}
	if len(client) == 0 {
		t.Fatal("в LvnEvents.cs не нашлось ни одного имени — сверять нечего")
	}

	var missing []string
	for _, m := range goEvConst.FindAllStringSubmatch(string(rollup), -1) {
		name := m[2]
		if client[name] || serverOnlyEvents[name] != "" {
			continue
		}
		missing = append(missing, m[1]+" = \""+name+"\"")
	}
	sort.Strings(missing)

	if len(missing) > 0 {
		t.Fatalf("сервер сворачивает события, которых у клиента нет (%d):\n  %s\n\n"+
			"Либо клиент перестал их слать — и тогда метрика в отчёте молча пуста, "+
			"либо имя разошлось. Добавьте константу в LvnEvents.cs или, если событие "+
			"серверное намеренно, впишите его в serverOnlyEvents с причиной.",
			len(missing), strings.Join(missing, "\n  "))
	}
}
