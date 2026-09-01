package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// ОБЪЯСНЕНИЕ, ПОТЕРЯВШЕЕ СВОЙ ПРЕДМЕТ.
//
// Два блока `<summary>` подряд означают, что один из них остался без члена:
// способ переписали, перенесли или переименовали, а его объяснение осталось
// лежать — и молча приклеилось к тому, что оказалось следующим.
//
// В этом репозитории объяснения и есть канон. Докблок на чужом члене хуже
// отсутствующего ровно тем же, чем врущая карта домов: он УВЕРЕННО говорит не
// то. Живые примеры на 01.09: «The wardrobe / skin shop» объясняет магазин
// наборов; докблоки «скруглить углы» и «светящаяся кромка» оба висят на
// константе мягкости; «Cut the voice line» подписывает уборку всей главы.
//
// Двух `<summary>` у одного члена не бывает по определению — значит, порог
// здесь не «сколько допустимо», а «сколько ещё не разобрано».
func TestNoExplanationLostItsSubject(t *testing.T) {
	const budget = 36 // только вниз

	root := repoRoot(t)
	var found []string
	for _, dir := range []string{
		"unity/Packages/com.lvn.engine/Runtime",
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine.services/Runtime",
	} {
		for _, f := range csFiles(t, filepath.Join(root, dir)) {
			lines := strings.Split(string(mustRead(t, f)), "\n")
			for i, l := range lines {
				if !strings.Contains(l, "</summary>") {
					continue
				}
				for j := i + 1; j < len(lines) && strings.HasPrefix(strings.TrimSpace(lines[j]), "///"); j++ {
					if strings.Contains(lines[j], "</summary>") {
						break
					}
					if strings.Contains(lines[j], "<summary>") {
						found = append(found, filepath.Base(f)+":"+itoa(i+1))
						break
					}
				}
			}
		}
	}
	atLeast(t, len(found), budget, "объяснений, потерявших свой предмет")
	if len(found) > budget {
		t.Errorf("докблоки без члена (%d при пороге %d): %v\n"+
			"Каждый из них теперь подписывает ЧУЖОЙ член и уверенно говорит не то.",
			len(found), budget, found)
	}
}
