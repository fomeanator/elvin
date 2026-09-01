package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// ОБРЯД ЗАБВЕНИЯ ИМЕЕТ ЖИВОГО ЗОВУЩЕГО.
//
// LvnForget.All умеет стереть игрока целиком: сейвы, галерею, прочитанное,
// переменные новелл, гардероб, кросс-новелльные статы, метки, имя и флаги
// вступления. Написан он давно и подробно — и до 01.09 его не звал в игре
// НИКТО: обряд работал только в тестах.
//
// Это отдельный род тихой поломки: дом жив, покрыт тестами, зелёный — и
// бесполезен, потому что двери к нему нет. Тесты доказывают, что он работает
// правильно; они не могут доказать, что он кому-то доступен.
func TestForgetRitualIsReachableFromTheGame(t *testing.T) {
	root := repoRoot(t)
	shell := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")
	scanned, callers := 0, 0
	for _, f := range csFiles(t, shell) {
		scanned++
		if strings.Contains(stripComments(string(mustRead(t, f))), "LvnForget.All(") {
			callers++
		}
	}
	atLeast(t, scanned, 40, "просмотренных файлов оболочки")
	if callers == 0 {
		t.Error("LvnForget.All не зовут из игры — обряд забвения снова доступен только тестам; " +
			"игроку «начать заново» нечем")
	}

	// И сама строка настроек: без неё шов есть, а нажать нечего.
	set := filepath.Join(shell, "SettingsScreen.Account.cs")
	if !strings.Contains(stripComments(string(mustRead(t, set))), "ResetRow()") {
		t.Error("SettingsScreen.Account.cs: пропала строка сброса аккаунта")
	}
}
