package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// НАЛОЖЕНИЕ ПОВЕРХ СЦЕНЫ ЕЁ ЗАВЕШИВАЕТ, А НЕ ЗАМЕНЯЕТ.
//
// Экран знакомства приходит по двум поводам: на первом запуске (под ним
// пусто) и по команде сценария посреди главы (под ним живая сцена). Красил
// он себя одинаково — непрозрачной землёй темы, — и во втором случае стирал
// сцену начисто. Живой случай: «агент просит представиться», игрок вместо
// монастыря видит плоскую заливку (Илья, 01.09).
//
// Страж держит не цвет, а ВОПРОС: экран обязан спросить, идёт ли глава, и
// иметь оба ответа. Пропадёт вопрос — вернётся замена сцены.
func TestAuthScreenVeilsTheSceneItStandsOn(t *testing.T) {
	root := repoRoot(t)
	f := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime/AuthScreen.cs")
	body := stripComments(string(mustRead(t, f)))

	if !strings.Contains(body, "LvnScreenDirector.Current.InChapter") {
		t.Error("AuthScreen.cs: экран не спрашивает, идёт ли глава — значит красит себя одинаково " +
			"и поверх пустоты, и поверх живой сцены; второе стирает сцену")
	}
	if !strings.Contains(body, "LvnTokens.Veil(") {
		t.Error("AuthScreen.cs: нет вуали — поверх сцены экран обязан завешивать, а не заменять " +
			"(так же ведут себя форма ввода, выборы и меню)")
	}
	// Земля ставится в ОДНОМ месте: две заливки во весь экран по разным
	// правилам — ровно тот разъезд, из которого баг и вырос.
	own := 0
	for _, line := range strings.Split(body, "\n") {
		if strings.HasPrefix(strings.TrimSpace(line), "style.backgroundColor = ") {
			own++
		}
	}
	if n := own; n > 2 {
		t.Errorf("AuthScreen.cs: заливок во весь экран %d — правило «чем закрывать» живёт в Ground()", n)
	}
}
