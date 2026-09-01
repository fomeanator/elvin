package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// ДЕРЖАТЬ АРТ УМЕЕТ ТОЛЬКО ДОСКА.
//
// Держать набор спрайтов под ключом умели три места по трём разным правилам:
// сцена (ключ — слот, отпускание с задержкой), скелеты (ключ — актёр,
// отпускание сразу) и картинка в панели (ключ — сам элемент). Работа
// одинаковая, разными были только ключ и задержка.
//
// Там, где работу писали заново, теряли правило порядка. Сцена прикрепляла
// новый набор ДО того, как отпустить прежний, и объясняла зачем: наборы
// пересекаются, а отпустив первым, доводишь счётчик общего спрайта до нуля —
// и окно вправе забрать текстуру ровно в этот миг. Скелеты делали наоборот, и
// у них наборы пересекаются ЧАЩЕ: страницы атласа у перестроенного скелета
// обычно те же самые.
//
// Прямой вызов счётчика мимо доски — это четвёртое правило, которое опять
// будет написано по памяти.
func TestOnlyThePinBoardCountsHolders(t *testing.T) {
	root := repoRoot(t)
	allowed := map[string]bool{
		"unity/Packages/com.lvn.engine/Runtime/UI/LvnPinBoard.cs":                    true,
		"unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.SpriteCache.cs": true,
	}
	for _, dir := range []string{
		"unity/Packages/com.lvn.engine/Runtime",
		"unity/Packages/com.lvn.engine.shell/Runtime",
	} {
		for _, f := range csFiles(t, filepath.Join(root, dir)) {
			rel := filepath.ToSlash(strings.TrimPrefix(f, root+"/"))
			if allowed[rel] {
				continue
			}
			if strings.Contains(stripComments(string(mustRead(t, f))), "PinSprite(") {
				t.Errorf("%s держит арт мимо доски (PinSprite напрямую). "+
					"Порядок «прикрепить раньше, чем отпустить» живёт в "+
					"LvnPinBoard — написанный по памяти четвёртый раз он "+
					"обязательно окажется обратным", rel)
			}
		}
	}
}

// ПРИКРЕПИТЬ РАНЬШЕ, ЧЕМ ОТПУСТИТЬ.
//
// Всё, ради чего дом заведён, — три строки в одном порядке. Порядок невидим
// глазом и проявляется белым прямоугольником раз в сто показов, поэтому его
// стережёт не только тест поведения, но и текст.
func TestPinBoardHoldsBeforeItLets(t *testing.T) {
	root := repoRoot(t)
	body := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/UI/LvnPinBoard.cs"))))
	pin := strings.Index(body, "ledger.PinSprite(s, true)")
	let := strings.Index(body, "if (_held.TryGetValue(key, out var prev)) Let(prev)")
	if pin < 0 || let < 0 {
		t.Fatal("доска перестала прикреплять или отпускать узнаваемым образом — " +
			"перечитайте LvnPinBoard: страж больше не проверяет порядок")
	}
	if pin > let {
		t.Error("доска отпускает прежний набор РАНЬШЕ, чем прикрепляет новый: " +
			"общий спрайт на мгновение остаётся без держателей, и стриминговое " +
			"окно вправе забрать текстуру именно там")
	}
	if !strings.Contains(body, "held.Ledger.PinSprite(s, false)") {
		t.Error("отпускает не тот, кто держал: доска обязана помнить счётчик " +
			"вместе с набором — иначе прежний держит текстуру вечно, а новому " +
			"приходит минус за то, чего он не давал")
	}
}
