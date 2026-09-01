package lvn

import (
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ДОРОЖКУ УБИРАЕТ ДОМ ДОРОЖЕК.
//
// Дорожка живёт в ДВУХ памятях: то, что играет сейчас, и то, что ждёт
// очереди. «Убрать дорожку» значит тронуть обе — и это писали по месту
// трижды: остановить дорожку, остановить всё, остановить цель. Последнее
// стояло четырьмя строками подряд, потому что цель ищется под двумя именами
// (своим и «script:цель»).
//
// Дубля тут не видно поиском: три места пишут РАЗНЫЕ строки про одно
// правило. Видно только вопрос — «а если памятей станет три».
func TestLaneRemovalGoesThroughTheHome(t *testing.T) {
	root := repoRoot(t)
	f := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/UI/World/WorldActor.cs")
	body := stripComments(string(mustRead(t, f)))

	// Снятие дорожки руками: обе памяти в одной строке или подряд.
	pair := regexp.MustCompile(`_channels\.(?:Remove|Clear)\([^\n]*\n?[^\n]*_queue\.(?:Remove|Clear)\(` +
		`|_queue\.(?:Remove|Clear)\([^\n]*\n?[^\n]*_channels\.(?:Remove|Clear)\(`)
	if m := pair.FindString(body); m != "" {
		t.Errorf("WorldActor: дорожку снимают руками из обеих памятей — %q. "+
			"Это работа дома (AnimLanes.Drop / DropTarget / DropAll): пока правило написано "+
			"по месту, каждое написание обязано помнить про вторую память",
			strings.TrimSpace(strings.ReplaceAll(m, "\n", " ")))
	}
	if !strings.Contains(body, "AnimLanes.Drop(") || !strings.Contains(body, "AnimLanes.DropTarget(") {
		t.Error("WorldActor больше не зовёт дом дорожек — правило вернулось по месту")
	}
}
