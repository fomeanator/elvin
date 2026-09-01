package lvn

import (
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ДВА ПУТИ ИМПОРТА ИМЕНУЮТ ОДИНАКОВО.
//
// Импортёр приводит имена к канону проекта тремя шагами: имя героя вместо
// articy-метки, переопределения подписей из шаблона, спасение опечаток в
// именах переменных. Путей же ДВА — одиночный проект и пакет, — и обряд на них
// расписан порознь: у одиночного это `applyNaming`, у пакетного шаги раскиданы
// по местам, где удобно править файлы.
//
// Расхождение и случилось: пакетный путь звал первые два шага и третий не звал
// вовсе. Комментарий рядом при этом обещал, что «пакетный получает то же
// именование» — оговорка, бывшая правдой ровно до того, как к обряду добавили
// третий шаг.
//
// Цена спящая, но точная: `var_aliases` чинит опечатку в имени показателя.
// Не почини её — показатель пишется в одно имя, читается из другого, и ворота
// по статам молча не срабатывают. Ошибки нет нигде, просто выбор всегда
// закрыт. А пакетный путь — тот, которым едет живой контент.
func TestBothImportPathsNameTheSameWay(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "tools/lvnconv/importer")
	alias := stripComments(string(mustRead(t, filepath.Join(dir, "alias.go"))))
	bundle := stripComments(string(mustRead(t, filepath.Join(dir, "bundle_wire.go"))))

	// Шаги обряда — то, что зовёт applyNaming.
	at := strings.Index(alias, "func applyNaming(")
	if at < 0 {
		t.Fatal("applyNaming пропал — на нём держится обряд именования одиночного пути")
	}
	end := strings.Index(alias[at:], "\n}")
	if end < 0 {
		t.Fatal("не нашёл конца applyNaming")
	}
	steps := map[string]bool{}
	for _, m := range regexp.MustCompile(`\b(apply\w+)\(`).FindAllStringSubmatch(alias[at:at+end], -1) {
		if m[1] != "applyNaming" {
			steps[m[1]] = true
		}
	}
	sawSources(t, len(steps), 3, "шагов приведения имён")

	var missing []string
	for step := range steps {
		if !strings.Contains(bundle, step+"(") {
			missing = append(missing, step)
		}
	}
	sort.Strings(missing)
	if len(missing) > 0 {
		t.Errorf("шаги именования, которых нет на ПАКЕТНОМ пути: %s\n\n"+
			"Одиночный импорт зовёт их через applyNaming, пакетный — своими "+
			"местами. Забытый шаг не даёт ошибки: имена просто расходятся, и "+
			"ворота по статам молча не срабатывают.",
			strings.Join(missing, ", "))
	}
}
