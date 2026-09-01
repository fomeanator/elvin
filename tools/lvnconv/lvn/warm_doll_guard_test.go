package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// ГЕРОИНЯ ВИТРИНЫ ГРЕЕТСЯ ВМЕСТЕ С ПОЛОТНОМ.
//
// Полотно витрины грели заранее — «одна известная картинка, прогретая
// заранее, снимает это целиком». Куклу не грели: её слои начинали качаться и
// распаковываться в тот миг, когда витрина уже открыта.
//
// Живой запуск 01.09 показал цену, и она не в сети. Три места в декодере
// занимали полотно (2000×1500, 2,5 с) и ядро створа (1440×1440, 1,85 с), а
// пять слоёв героини стояли к ним в очередь: face 1250 мс, decor и clothes по
// 1827 мс, hair — 4998 мс при собственной распаковке в 551 мс. Итог — пять
// секунд пустого места («работало же отлично», Илья).
//
// Кукла — не «про запас»: она первый кадр витрины наравне с фоном.
func TestMenuHeroineIsWarmedWithTheCanvas(t *testing.T) {
	root := repoRoot(t)
	menu := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime/NovelApp.Menu.cs")
	body := stripComments(string(mustRead(t, menu)))
	if !strings.Contains(body, "WarmActorAsync(") {
		t.Error("NovelApp.Menu.cs: героиню витрины больше не греют на старте — " +
			"её слои снова встанут в очередь за полотном и створом")
	}
	// Оба прогрева в одном месте: разведёшь их — и снова окажется, что один
	// сделали, а про второй забыли.
	iCanvas := strings.Index(body, "WarmMenuCanvasAsync(")
	iDoll := strings.Index(body, "WarmActorAsync(")
	if iCanvas < 0 || iDoll < 0 {
		t.Fatal("NovelApp.Menu.cs: пропал один из прогревов витрины")
	}
	between := body[min2(iCanvas, iDoll):max2(iCanvas, iDoll)]
	if strings.Count(between, "private ") > 0 {
		t.Error("NovelApp.Menu.cs: прогрев полотна и прогрев героини разъехались по разным способам — " +
			"это снова два списка одного факта «что нужно витрине к первому кадру»")
	}

	stage := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/UI/VnStage.Pins.cs")
	if !strings.Contains(stripComments(string(mustRead(t, stage))), "public async Task WarmActorAsync") {
		t.Error("VnStage.Pins.cs: пропал прогрев актёра")
	}
}

func min2(a, b int) int {
	if a < b {
		return a
	}
	return b
}

func max2(a, b int) int {
	if a > b {
		return a
	}
	return b
}
