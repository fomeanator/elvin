package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// КАЖДЫЙ АДРЕС, КОТОРЫЙ ЗОВЁТ ИГРА, ДОЛЖЕН СУЩЕСТВОВАТЬ НА СЕРВЕРЕ.
//
// Клиент обращается к серверу строкой («/v1/wallet/spend»), сервер регистрирует
// обработчик такой же строкой. Совпадать они обязаны буква в букву, а живут в
// разных языках и разных репозиториях сборки.
//
// Замер 06.09: клиент зовёт 24 адреса, сервер регистрирует 82 — расхождений
// ноль, но сверял их только человек глазами. Это четвёртый договор между
// языками, найденный за ночь без стража; предыдущие три — папки крупного арта,
// имена ступеней качества и имена событий аналитики.
//
// Цена ошибки здесь выше, чем у прочих, но и заметнее: переименовали ручку —
// игрок не может потратить валюту или не получает ежедневную награду. Страж
// нужен затем, чтобы это всплывало на прогоне, а не в магазине приложений.
func TestАдресаКлиентаЕстьНаСервере(t *testing.T) {
	root := repoRoot(t)

	// Сервер: mux.HandleFunc("/v1/…", …)
	server := map[string]bool{}
	srvFiles, _ := filepath.Glob(filepath.Join(root, "server", "*.go"))
	reHandle := regexp.MustCompile(`mux\.HandleFunc\("(/[^"]+)"`)
	for _, f := range srvFiles {
		if strings.HasSuffix(f, "_test.go") {
			continue
		}
		b, err := os.ReadFile(f)
		if err != nil {
			continue
		}
		for _, m := range reHandle.FindAllStringSubmatch(string(b), -1) {
			server[m[1]] = true
		}
	}

	// Клиент: любой строковый литерал вида "/v1/…" в рантайме движка.
	client := map[string]bool{}
	reCall := regexp.MustCompile(`"(/v1/[a-z0-9/_.-]+)"`)
	var walk func(dir string)
	walk = func(dir string) {
		entries, err := os.ReadDir(dir)
		if err != nil {
			return
		}
		for _, e := range entries {
			p := filepath.Join(dir, e.Name())
			switch {
			case e.IsDir():
				walk(p)
			case strings.HasSuffix(e.Name(), ".cs") && !strings.Contains(p, "Tests"):
				b, err := os.ReadFile(p)
				if err != nil {
					continue
				}
				for _, m := range reCall.FindAllStringSubmatch(string(b), -1) {
					client[m[1]] = true
				}
			}
		}
	}
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell", "com.lvn.engine.services"} {
		walk(filepath.Join(root, "unity", "Packages", pkg, "Runtime"))
	}

	if len(server) < 20 || len(client) < 10 {
		t.Fatalf("списки не прочитались (сервер %d, клиент %d) — страж потерял опору",
			len(server), len(client))
	}

	// Сервер вправе регистрировать ПРЕФИКС («/v1/leaderboard/»), под который
	// попадает много конкретных адресов, — это не расхождение.
	covered := func(path string) bool {
		if server[path] {
			return true
		}
		for s := range server {
			if strings.HasSuffix(s, "/") && strings.HasPrefix(path, s) {
				return true
			}
		}
		return false
	}

	var orphan []string
	for path := range client {
		if !covered(path) {
			orphan = append(orphan, path)
		}
	}
	sort.Strings(orphan)
	for _, p := range orphan {
		t.Errorf("игра зовёт %s, а такой ручки на сервере нет: игрок получит 404 "+
			"там, где ждёт ответа", p)
	}
	t.Logf("сверено адресов: клиент зовёт %d, сервер регистрирует %d", len(client), len(server))
}
