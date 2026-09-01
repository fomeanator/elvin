package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// «ЖИВУ МАНИФЕСТОМ» — ОДНА ПОМЕТКА И ОДНО ИМЯ.
//
// Свежее содержимое развозит НАБОР по пометке ILvnContentAware, а не список
// по именам: забытый экран не падает, он просто показывает вчерашнее.
//
// Одна дыра в этом правиле держалась не на забывчивости, а на ИМЕНИ: у
// сюжетного гардероба тот же метод назывался своим словом (SetManifest), и
// под общую пометку лист не подходил. Одна работа под двумя именами не
// выглядит дублем — она выглядит двумя разными работами, и общее правило её
// не видит.
func TestContentDeliveryHasOneName(t *testing.T) {
	root := repoRoot(t)
	shell := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")
	engine := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime")

	for _, dir := range []string{shell, engine} {
		for _, f := range csFiles(t, dir) {
			body := stripComments(string(mustRead(t, f)))
			if strings.Contains(body, "SetManifest(") {
				t.Errorf("%s: «приехал свежий манифест» назван вторым именем (SetManifest) — "+
					"под пометку ILvnContentAware такой экран не подходит, и содержимое ему "+
					"придётся вручать по имени", filepath.Base(f))
			}
		}
	}
}

// ВРУЧЕНИЕ СОДЕРЖИМОГО СТОИТ В ОДНОМ МЕСТЕ.
//
// Сюжетный гардероб в набор оболочки не входит — его создаёт приложение и
// показывает поверх сцены. Значит вручает ему приложение, и ровно один раз:
// вторая строка в обработчике живого обновления — это снова список по
// именам, только короткий.
func TestStorySheetGetsContentInOnePlace(t *testing.T) {
	root := repoRoot(t)
	shell := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")
	n, where := 0, ""
	for _, f := range csFiles(t, shell) {
		body := stripComments(string(mustRead(t, f)))
		c := strings.Count(body, "_storySheet as ILvnContentAware") +
			strings.Count(body, "_storySheet.SetContent(") +
			strings.Count(body, "_storySheet?.SetContent(")
		if c > 0 {
			n += c
			where += " " + filepath.Base(f)
		}
	}
	// Два: вручение в доме ApplyManifest и первичное — при создании листа.
	if n != 2 {
		t.Errorf("вручений содержимого сюжетному листу %d (%s), ожидалось 2 — "+
			"дом ApplyManifest и создание листа", n, strings.TrimSpace(where))
	}
}
