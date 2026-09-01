package lvn

import (
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ПОВТОР КАДРА ЗНАЕТ НЕ МЕНЬШЕ, ЧЕМ ПОМНИТ РАНТАЙМ.
//
// Кадр моделируют дважды: рантайм на C# (что показать игроку) и здешний повтор
// на Go (что проверить обходом и переиграть). Словарь КОМАНД сверяется давно —
// им заняты `TestOpOwnersCoverKnownOps` и соседи. А вот ПОЛЯ команд не сверял
// никто, и 01.09 это укусило: рантайм читал `off` как «поле есть и не отменено
// словом», повтор — как «ключ присутствует». Проверка сертифицировала не то
// поведение, которое увидит игрок.
//
// Утверждение узкое и потому проверяемое: каждое поле, которое ПОМНИТ рантайм,
// обязан знать и повтор. Обратное неверно — повтор делает больше (обходит
// ветвления, читает варианты выбора), и требовать симметрии значило бы
// требовать от рантайма чужой работы.
func TestReplayKnowsEveryFieldTheRuntimeRemembers(t *testing.T) {
	root := repoRoot(t)
	cs := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/LvnFrame.cs"))))
	gо := stripComments(string(mustRead(t, filepath.Join(root,
		"tools/lvnconv/lvn/frame.go"))))

	field := regexp.MustCompile(`cmd\[\s*"([a-z_][a-z0-9_]*)"\s*\]`)
	var remembers []string
	for _, m := range field.FindAllStringSubmatch(cs, -1) {
		remembers = append(remembers, m[1])
	}
	sawSources(t, len(remembers), 4, "полей, читаемых моделью кадра рантайма")

	var lost []string
	seen := map[string]bool{}
	for _, f := range remembers {
		if seen[f] {
			continue
		}
		seen[f] = true
		if !strings.Contains(gо, `"`+f+`"`) {
			lost = append(lost, f)
		}
	}

	sort.Strings(lost)
	if len(lost) > 0 {
		t.Errorf("рантайм помнит поля, о которых повтор не знает (%d): %s\n\n"+
			"Обход и переигровка будут сертифицировать поведение, которого игрок "+
			"не увидит: расхождение C#↔Go — главный структурный риск движка, и "+
			"ловится оно только такой сверкой.", len(lost), strings.Join(lost, ", "))
	}
}
