package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"testing"
)

// ЧАСТОТА ОПРОСА НЕ МОЖЕТ БЫТЬ ЧАЩЕ, ЧЕМ СЕРВЕР СПОСОБЕН ОТВЕТИТЬ ИНАЧЕ.
//
// Версия контента считается не на каждый запрос: сервер держит результат обхода
// дерева verCacheTTL секунд, и много опрашивающих делят один обход. Это защита
// от шторма — и одновременно ПОЛ на скорость доставки: раньше, чем протухнет
// запись кэша, правка не появится, сколько её ни спрашивай.
//
// Замерено живым стендом (qa/poll-load-check.sh, 120 клиентов, опрос 500 мс):
//
//	опрос            239 запросов/с, p99 1,8 мс, 87% — 304 без тела
//	цена клиента     ~1,2 КБ в минуту
//	правка видна     медиана 895 мс, ХУДШИЙ СЛУЧАЙ 2020 мс = ровно TTL
//
// Отсюда правило: опустить интервал клиента, не опустив TTL, значит купить
// нули — запросов вчетверо больше, доставка та же. Страж ловит именно эту
// половинчатую правку и говорит, чего не хватает.
func TestClientDoesNotPollFasterThanTheServerCanAnswer(t *testing.T) {
	root := repoRoot(t)

	srv, err := os.ReadFile(filepath.Join(root, "server", "main.go"))
	if err != nil {
		t.Fatalf("сервер не прочитан: %v", err)
	}
	m := regexp.MustCompile(`verCacheTTL\s*=\s*(\d+)\s*\*\s*time\.(Second|Millisecond)`).
		FindStringSubmatch(string(srv))
	if m == nil {
		t.Fatal("verCacheTTL не найден — страж потерял предмет охраны")
	}
	n, err := strconv.Atoi(m[1])
	if err != nil {
		t.Fatalf("verCacheTTL не разобран: %v", err)
	}
	ttlMs := n
	if m[2] == "Second" {
		ttlMs = n * 1000
	}

	cs, err := os.ReadFile(filepath.Join(root, "unity", "Packages", "com.lvn.engine",
		"Runtime", "Content", "ContentSync.cs"))
	if err != nil {
		t.Fatalf("клиентский опрос не прочитан: %v", err)
	}
	cm := regexp.MustCompile(`DefaultIntervalSeconds\s*=\s*([0-9.]+)f`).FindStringSubmatch(string(cs))
	if cm == nil {
		t.Fatal("DefaultIntervalSeconds не найден — страж смотрит не туда")
	}
	sec, err := strconv.ParseFloat(strings.TrimSuffix(cm[1], "."), 64)
	if err != nil {
		t.Fatalf("интервал клиента не разобран: %v", err)
	}
	clientMs := int(sec * 1000)

	if clientMs < ttlMs {
		t.Errorf("клиент опрашивает каждые %d мс, а сервер отвечает по-новому не чаще %d мс "+
			"(verCacheTTL): лишние запросы куплены, скорость доставки прежняя. "+
			"Хотите быстрее — опускайте TTL, и только потом интервал", clientMs, ttlMs)
	}

	// Нижний зажим интервала живёт отдельно от умолчания: он позволяет хозяину
	// выставить частоту, которой сервер всё равно не поддержит. Не отказ, но
	// знать об этом надо — замер стоит рядом, в докблоке.
	if !strings.Contains(string(cs), "Math.Max(250") {
		t.Log("нижний зажим интервала изменился — сверьте его с verCacheTTL")
	}
}
