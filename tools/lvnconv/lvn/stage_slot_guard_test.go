package lvn

import (
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// КТО НА СЦЕНЕ — ОДНА ЗАПИСЬ, А НЕ ПЯТЬ ПАМЯТЕЙ ПО ОДНОМУ КЛЮЧУ.
//
// Памятей было пять: сам объект, его группа прозрачности, базовая
// непрозрачность, явный порядок слоя и возраст рождения. «Поставить актёра»
// значило пять записей, «убрать» — пять удалений, причём В РАЗНЫЕ ГРУППЫ:
// три переживают уборку у героини, две обязаны обнулиться (иначе она войдёт
// в новую сцену с чужим z из катсцены или окажется старше всех и потому за
// спинами у всех).
//
// Ломается такой список не сегодня, а на ШЕСТОЙ памяти: её заведут, забудут
// внести в уборку, и сцена начнёт протекать между главами. Страж считает
// поля-словари по ключу-строке: одна запись про актёра плюс очередь
// отложенных эффектов — и всё.
func TestStageKeepsOneRecordPerActor(t *testing.T) {
	const budget = 2 // _slots (кто на сцене) и _pendingSfx (эффект, заказанный до рождения)

	root := repoRoot(t)
	f := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/UI/World/WorldStage.cs")
	body := stripComments(string(mustRead(t, f)))
	field := regexp.MustCompile(`(?m)^\s*private readonly Dictionary<string,[^>]*>\s+_\w+`)
	n := len(field.FindAllString(body, -1))
	if n == 0 {
		t.Fatal("в WorldStage не осталось ни одного словаря по ключу-строке — страж устарел")
	}
	if n > budget {
		t.Errorf("памятей по ключу актёра: %d при пороге %d — одна работа («кто на сцене») "+
			"разложена по нескольким словарям, и уборка снова становится списком, "+
			"который надо не забыть", n, budget)
	}
	if !strings.Contains(body, "ForgetStaging") {
		t.Error("пропало ForgetStaging: «забыть мизансцену, оставив живой объект» — " +
			"это отдельное правило, и оно обязано быть названо, а не разложено по Clear")
	}
}

// ПОДПИСЬ НА КАДРЕ — тоже одна запись.
//
// Памятей было две: сам элемент и живой шаблон с подстановками, который
// пересчитывают каждый тик. «Убрать подпись» — два удаления, сброс сцены —
// две очистки, причём написанные в РАЗНЫХ ФАЙЛАХ (сброс кадра и сброс всей
// сцены). Разъехавшись, пара даёт подпись-призрак: элемент снят, а шаблон
// остался и считается каждый тик — или надпись висит и больше не
// обновляется.
func TestHudLabelIsOneRecord(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/UI")
	for _, f := range csFiles(t, dir) {
		body := stripComments(string(mustRead(t, f)))
		if strings.Contains(body, "_labelTmpl") {
			t.Errorf("%s: шаблон подписи снова живёт отдельной памятью — "+
				"элемент и его живой текст обязаны уходить вместе", filepath.Base(f))
		}
	}
	vn := filepath.Join(dir, "VnStage.cs")
	if !strings.Contains(stripComments(string(mustRead(t, vn))), "class HudLabel") {
		t.Error("VnStage.cs: пропала запись HudLabel — подпись снова разложена по памятям")
	}
}
