package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// СОДЕРЖИМОЕ МАНИФЕСТА РАСКЛАДЫВАЕТ ОДИН ДОМ.
//
// Работа была написана дважды — на старте и на живом обновлении, — и списки
// расходились при следующем добавленном поле: автор дописывает поле, оно
// попадает в тот список, где его писали, и живое обновление молча оставляет
// старое значение. Отладить нельзя: на старте всё правильно.
//
// Страж считает не «сколько раз позвали дом», а сколько раз написали САМУ
// работу. Каждое присваивание из манифеста обязано стоять ровно один раз на
// всю оболочку.
func TestManifestContentIsAppliedInOnePlace(t *testing.T) {
	root := repoRoot(t)
	shell := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")
	files := csFiles(t, shell)

	// Каждая строка — одна работа по манифесту, которую нельзя писать дважды.
	jobs := []string{
		"TeachHousesFrom(manifest)",
		"ApplyMenuStaging(manifest)",
		"Set3DSetCatalog(manifest.sets3d)",
		"Stage.NameInput = manifest",
		"Stage.Catalog = new SpriteCatalog(manifest",
		"_globalUi = manifest.ui",
	}
	for _, job := range jobs {
		n, where := 0, ""
		for _, f := range files {
			body := stripComments(string(mustRead(t, f)))
			c := strings.Count(body, job)
			if c > 0 {
				n += c
				where += " " + filepath.Base(f)
			}
		}
		if n == 0 {
			t.Errorf("работа %q исчезла — страж устарел или дом переписан", job)
		}
		if n > 1 {
			t.Errorf("работа %q написана %d раза (%s) — она принадлежит дому ApplyManifest "+
				"(NovelApp.Manifest.cs); два списка одного факта расходятся при следующем поле",
				job, n, strings.TrimSpace(where))
		}
	}
}

// ТЕМА СОБИРАЕТСЯ НАЧИСТО. Собирать поверх ДЕЙСТВУЮЩЕЙ темы значит оставлять
// на экране поля, которых в новом манифесте уже нет: автор убрал скругление —
// оно осталось. Убранное поле неотличимо от ненаписанного, и правильно
// отвечает на это только чистая основа.
func TestStageThemeIsBuiltFromScratch(t *testing.T) {
	root := repoRoot(t)
	shell := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")
	for _, f := range csFiles(t, shell) {
		body := stripComments(string(mustRead(t, f)))
		if strings.Contains(body, "VnThemeBuilder.From(manifest.ui, Stage.Theme)") {
			t.Errorf("%s: тема игры собирается ПОВЕРХ действующей — убранное автором поле "+
				"останется на экране; основа обязана быть чистой (new VnTheme())", filepath.Base(f))
		}
	}
}
