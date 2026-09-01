package lvn

import (
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ТОКЕН УНИЧТОЖЕНИЯ БЕРУТ ОДИН РАЗ, ПОКА ОБЪЕКТ ЖИВ.
//
// `destroyCancellationToken` — свойство НА КОМПОНЕНТЕ. Пока компонент жив,
// оно отдаёт токен; после Destroy обращение к нему бросает
// MissingReferenceException. В обычном коде это заметно сразу, а в ЦИКЛЕ
// ОЖИДАНИЯ — нет: исключение вылетает в продолжении после await, ловить его
// некому, и оно уходит в лог.
//
// Стоило это дорого. Красный PlayMode держался с 02:44 до 13:30 и был найден
// только бисектом из семи прогонов: падал не тот тест, который сломали, —
// следующий по порядку, у него же в SetUp, на ЧУЖОМ необработанном
// сообщении. Читать код бесполезно: место падения и место ошибки разные.
//
// Правило простое: в условии цикла — КОПИЯ токена, снятая до цикла. Сама
// структура уничтожение переживает и честно отвечает «отменено».
func TestDestroyTokenIsCapturedBeforeAwaitLoops(t *testing.T) {
	root := repoRoot(t)
	shell := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")
	// Чтение свойства ПОСЛЕ первого await в том же методе — вот настоящее
	// правило. Первая версия стража искала только цикл `while (…token…)` и
	// потому не увидела ни `if (token.IsCancellationRequested)` после await,
	// ни `var ct = token;`, стоящее следом за ожиданием. Она была зелёной,
	// пока PlayMode оставался красным.
	inLoop := regexp.MustCompile(`while\s*\([^)]*destroyCancellationToken`)
	scanned := 0
	var bad []string
	for _, f := range csFiles(t, shell) {
		scanned++
		body := stripComments(string(mustRead(t, f)))
		if inLoop.MatchString(body) {
			bad = append(bad, filepath.Base(f))
		}
		// ПОСЛЕ ОЖИДАНИЯ ЧИТАТЬ НЕЛЬЗЯ. Метод режем грубо — по объявлениям
		// верхнего уровня; внутри куска ищем первое `await` и любое
		// упоминание свойства после него.
		for _, part := range strings.Split(body, "\n        private ") {
			a := strings.Index(part, "await ")
			if a < 0 {
				continue
			}
			if strings.Contains(part[a:], "destroyCancellationToken") {
				bad = append(bad, filepath.Base(f))
				break
			}
		}
	}
	atLeast(t, scanned, 40, "просмотренных файлов оболочки")
	if len(bad) > 0 {
		t.Errorf("токен уничтожения читают ПОСЛЕ ожидания (%s) — "+
			"после Destroy это MissingReferenceException в продолжении, которое некому "+
			"поймать; снимите копию токена ДО цикла", strings.Join(bad, ", "))
	}
}
