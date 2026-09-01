package lvn

import (
	"path/filepath"
	"strings"
	"testing"
)

// ФИГУРА ВЫХОДИТ ЦЕЛИКОМ ИЛИ НЕ ВЫХОДИТ.
//
// Слой, который не доехал, выбрасывался молча — `if (s != null) layers.Add(s)`
// — и на экран уходило то, что осталось. Живой случай 01.09: на витрине
// стояли одни ВОЛОСЫ, без тела, лица и платья («такое ни при каких
// обстоятельствах нельзя допускать», Илья).
//
// Код неполноту ЗАМЕЧАЛ: строкой ниже считался wholeLook и писал в лог
// «надето 1 из 5 слоёв». Решение принималось только про память об облике, а
// сама фигура всё равно шла на экран — и показ «состоялся», после чего
// чинить уже нечего.
//
// Фигура без тела не бывает частично правильной: это не недогруженная
// картинка, а другой объект на экране.
func TestActorIsShownOnlyWhole(t *testing.T) {
	root := repoRoot(t)
	f := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/UI/VnStage.Actors.cs")
	body := stripComments(string(mustRead(t, f)))

	if !strings.Contains(body, "bool whole =") {
		t.Error("VnStage.Actors.cs: пропала проверка целости фигуры перед показом")
	}
	if !strings.Contains(body, "RetryActorSoonAsync") {
		t.Error("VnStage.Actors.cs: пропал повтор — без него запрет на неполную фигуру " +
			"означает, что героиня не появится вообще")
	}
	// Показ С АРТОМ обязан стоять после проверки. Искать просто
	// «ApplyActor(» нельзя: тем же способом фигуру ПРЯЧУТ (layers = null), и
	// эти вызовы стоят выше по файлу совершенно законно. Первая версия
	// стража на них и споткнулась — шаблон видел вызов, но не видел, чем он
	// зовётся.
	iWhole := strings.Index(body, "bool whole =")
	iApply := strings.Index(body, "ApplyActor(id, layers,")
	if iWhole < 0 || iApply < 0 || iWhole > iApply {
		t.Error("VnStage.Actors.cs: показ фигуры с артом идёт раньше проверки целости")
	}
}
