package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// ОТКЛИК НА НАЖАТИЕ ВКЛЮЧАЮТ НА КАЖДОМ КОРНЕ.
//
// Механизм заведён общим и висит на КОРНЕ, а не на каждой кнопке, — ровно
// затем, чтобы про него не пришлось помнить в каждом новом месте. И всё же
// про него забыли там, где игрок проводит больше всего времени: на корне
// СЦЕНЫ. Оболочка отвечала на палец, а окно диалога, выборы и форма ввода
// имени молчали — хотя кнопку формы даже пометили нажимаемой
// (LvnMotion.Tappable в VnStage.Input). Пометка была, слушателя не было.
//
// Признак общий: механизм «включается один раз на дерево» защищает от
// забывчивости внутри дерева, но САМ список деревьев остаётся ручным. Страж
// держит именно его.
func TestEveryPanelRootAnswersTheFinger(t *testing.T) {
	root := repoRoot(t)
	// Файлы, которые берут корень панели себе, обязаны включить отклик.
	owners := []string{
		"unity/Packages/com.lvn.engine/Runtime/UI/VnStage.cs",
		"unity/Packages/com.lvn.engine.shell/Runtime/NovelShell.cs",
		"unity/Packages/com.lvn.engine/Runtime/UI/LvnUiLayer.cs",
		"unity/Packages/com.lvn.engine.shell/Runtime/BrowseHub.cs",
	}
	for _, rel := range owners {
		body := stripComments(string(mustRead(t, filepath.Join(root, rel))))
		if !strings.Contains(body, "EnableTapFeedback(") {
			t.Errorf("%s: корень без отклика на нажатие — палец опускается и ничего "+
				"не происходит, экран читается как картинка", filepath.Base(rel))
		}
	}
}
