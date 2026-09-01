package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// СИД ДОЛЖЕН РЕШАТЬ ВСЁ, ИНАЧЕ ЭТО НЕ СИД.
//
// Соук-бот выбирал варианты по сиду, а броски САМОГО КОНТЕНТА (rand()/chance()
// в выражениях) шли из общего потока движка, который никто не сеял. Один и тот
// же сид давал разные прогоны: упавший тест переигрывался «примерно похожим»
// проходом, и таблица флейков не отличала «иногда падает» от «контент выбросил
// другое число».
//
// Соук, который находит баг и теряет путь к нему, стоит меньше, чем кажется:
// он сообщает, что беда есть, и не даёт её повторить. Про этот разрыв было
// ПРЯМО написано в qa/stability.sh — как о том, чего нет. Написанное вслух
// «этого у нас нет» живёт годами: оно снимает вопрос, не решая его.
func TestSoakSeedsTheContentRollsToo(t *testing.T) {
	root := repoRoot(t)
	body := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Tests/Editor/SoakBotTests.cs"))))
	if !strings.Contains(body, "Lvn.LvnExpression.Random = new Lvn.LvnRandom((ulong)seed)") {
		t.Error("соук снова не сеет броски контента — один сид даст разные " +
			"прогоны, и упавший тест не переиграть")
	}
	if !strings.Contains(body, "finally { Lvn.LvnExpression.Random = contentRng; }") {
		t.Error("соук не возвращает поток случайности на место — следующие тесты " +
			"пойдут с чужим сидом")
	}
}
