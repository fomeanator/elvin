package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ИМЕНА СОБЫТИЙ — ДОГОВОР, ПО КОТОРОМУ СЧИТАЮТСЯ ОТЧЁТЫ.
//
// Клиент шлёт событие строкой («chapter_finish»), сервер по этой же строке
// считает дочитывания, воронку по слайдам и сравнение вариантов в A/B. Имена
// объявлены врозь: у клиента константами LvnEvents, у сервера — ev*-константами
// свода.
//
// Замер 06.09: договоров-констант с общим значением между языками семнадцать, и
// девять из них не сверял никто — включая ВСЕ имена событий, по которым строится
// отчёт.
//
// Цена расхождения не «ошибка», а хуже: клиент шлёт chapter_finish, сервер ждёт
// chapter_finished — и отчёт показывает НОЛЬ дочитываний. Никто не падает,
// решение принимается по пустой таблице, а причина не видна ни в логах, ни у
// игрока.
//
// Сверяются только имена, которые ЕСТЬ У ОБОИХ: клиент вправе слать событие,
// которого свод пока не считает (их семнадцать против одиннадцати), и это не
// поломка. Поломка — когда одно и то же событие названо по-разному.
func TestИменаСобытийСовпадаютУКлиентаИСервера(t *testing.T) {
	root := repoRoot(t)
	read := func(rel string) string {
		b, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(rel)))
		if err != nil {
			t.Fatalf("не читается %s: %v", rel, err)
		}
		return string(b)
	}

	cs := read("unity/Packages/com.lvn.engine.services/Runtime/LvnEvents.cs")
	go_ := read("server/analytics_rollup.go")

	// Клиент: public const string ChapterFinish = "chapter_finish";
	client := map[string]string{} // значение → имя константы
	for _, m := range regexp.MustCompile(
		`const\s+string\s+(\w+)\s*=\s*"([a-z_]+)"`).FindAllStringSubmatch(cs, -1) {
		client[m[2]] = m[1]
	}
	// Сервер: evChapterFinish = "chapter_finish"
	server := map[string]string{}
	for _, m := range regexp.MustCompile(
		`\b(ev\w+)\s*=\s*"([a-z_]+)"`).FindAllStringSubmatch(go_, -1) {
		server[m[2]] = m[1]
	}

	if len(client) == 0 || len(server) == 0 {
		t.Fatalf("списки не прочитались (клиент %d, сервер %d) — страж потерял опору",
			len(client), len(server))
	}

	// Ключ сверки — ИМЯ КОНСТАНТЫ без приставки: ChapterFinish ↔ evChapterFinish.
	// Так расхождение ЗНАЧЕНИЙ становится видно даже при одинаковых именах.
	byName := map[string]string{} // ChapterFinish → значение у сервера
	for val, name := range server {
		byName[strings.TrimPrefix(name, "ev")] = val
	}

	var mismatched, checked []string
	for val, name := range client {
		srvVal, ok := byName[name]
		if !ok {
			continue // событие, которого свод пока не считает — это законно
		}
		checked = append(checked, name)
		if srvVal != val {
			mismatched = append(mismatched, name+": клиент шлёт "+val+", сервер ждёт "+srvVal)
		}
	}
	sort.Strings(checked)
	sort.Strings(mismatched)

	if len(checked) < 5 {
		t.Fatalf("сверено всего %d имён (%v) — страж читает не то место", len(checked), checked)
	}
	for _, m := range mismatched {
		t.Errorf("имя события разошлось — %s.\n"+
			"Отчёт покажет ноль по этому событию: никто не упадёт, а решение примут по пустой таблице.", m)
	}
	t.Logf("сверено имён событий: %d (%s)", len(checked), strings.Join(checked, ", "))
}
