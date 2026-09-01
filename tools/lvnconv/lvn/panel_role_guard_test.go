package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// ЛИСТ ОДЕВАЕТ РОЛЬ, А НЕ ЭКРАН.
//
// Положение всплывающего листа давно общее (LvnChrome.Sheet). А ОДЕВАЛСЯ он в
// каждом экране заново, четырьмя строками подряд: заливка, отступ вбок, отступ
// вверх, скругление. Четыре панели — четыре набора чисел, и ни одно из различий
// нигде не объяснено.
//
// Сводить их — вопрос художника, и они не сведены: пиксели не изменились ни у
// одной. Изменилось другое — различия видны в самом вызове, а форма названа
// один раз. Расхождение, которое нельзя увидеть, не обсуждается и потому
// растёт.
func TestSheetsAreDressedByTheRole(t *testing.T) {
	root := repoRoot(t)
	users := map[string]string{
		"unity/Packages/com.lvn.engine.shell/Runtime/AuthScreen.cs":         "знакомство",
		"unity/Packages/com.lvn.engine.shell/Runtime/ServerSelectScreen.cs": "выбор сервера",
		"unity/Packages/com.lvn.engine.shell/Runtime/StatsPanel.cs":         "окно статов",
		"unity/Packages/com.lvn.engine/Runtime/UI/StageMenu.cs":             "меню сцены",
	}
	for file, what := range users {
		body := stripComments(string(mustRead(t, filepath.Join(root, file))))
		if !strings.Contains(body, "LvnStyler.Panel(") {
			t.Errorf("%s (%s) снова одевает лист сам — четыре строки, которые "+
				"в пятый раз напишут иначе", filepath.Base(file), what)
		}
	}

	home := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/UI/LvnStyler.cs"))))
	if !strings.Contains(home, "public static T Panel<T>(T el, Color fill, float radius = -1f,") {
		t.Fatal("исчезла роль панели — одевать лист снова будет каждый экран")
	}
}
