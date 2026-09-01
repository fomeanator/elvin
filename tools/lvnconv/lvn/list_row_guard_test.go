package lvn

import (
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ЖИВОЙ ДОМ, О КОТОРОМ НЕ ЗНАЮТ.
//
// Дом строки списка (LvnStyler.ListRow) был написан, объяснён и покрыт пятью
// тестами — и звали его ДВА экрана из шести. Остальные четыре собирали ту же
// плитку руками: карточка, вертикальный отступ, скругление.
//
// Хуже: половина из них при этом не знала, что у неё получается мягкая кромка,
// а у соседа нет. Разница между строкой профиля и строкой главы существовала,
// была видна глазом на живом экране — и нигде не была названа.
//
// Это не отсутствие дома. Это незнание о нём, и находится оно только тем, что
// ищут не «где нет дома», а «где его не позвали».
func TestListRowsGoThroughTheirRole(t *testing.T) {
	root := repoRoot(t)
	// Признак ручной сборки: плитка красится и скругляется сразу после ряда.
	hand := regexp.MustCompile(`ScreenUi\.Row\([^)]*\);\s*\n\s*LvnChrome\.(Card|Round)\(`)
	for _, dir := range []string{
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine/Runtime/UI",
	} {
		for _, f := range csFiles(t, filepath.Join(root, dir)) {
			body := stripComments(string(mustRead(t, f)))
			if hand.MatchString(body) {
				t.Errorf("%s собирает строку списка руками — у неё есть роль "+
					"(LvnStyler.ListRow / CardRow), и разница между строкой с "+
					"кромкой и без обязана быть НАЗВАНА, а не получиться",
					filepath.Base(f))
			}
		}
	}

	home := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/UI/LvnStyler.cs"))))
	for _, role := range []string{
		"public static T ListRow<T>(T el, Color? fill = null)",
		"public static T CardRow<T>(T el, Color? fill = null)",
	} {
		if !strings.Contains(home, role) {
			t.Errorf("исчезла роль %q — строки снова будут собираться руками", role)
		}
	}
}
