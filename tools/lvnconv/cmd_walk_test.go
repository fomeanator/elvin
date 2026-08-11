package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Фикстуры пишем как .lvn (это JSON) и гоняем через тот же loadForWalk, что и
// боевой путь: тест не должен знать про модель больше, чем знает команда.
func walkFixture(t *testing.T, script string) walkReport {
	t.Helper()
	path := filepath.Join(t.TempDir(), "case.lvn")
	if err := os.WriteFile(path, []byte(`{"scene":"t","script":`+script+`}`), 0o644); err != nil {
		t.Fatal(err)
	}
	rep := walkFile(path, defaultWalkDepth)
	if rep.Err != "" {
		t.Fatalf("фикстура не разобралась: %s", rep.Err)
	}
	return rep
}

func TestWalkLinearScriptIsFullyReached(t *testing.T) {
	rep := walkFixture(t, `[
		{"op":"say","text":"раз"},
		{"op":"say","text":"два"},
		{"op":"say","text":"три"}
	]`)
	if rep.Reached != 3 || len(rep.Blocks) != 0 {
		t.Fatalf("линейный скрипт должен покрываться целиком: %+v", rep)
	}
	if rep.Paths != 1 {
		t.Errorf("путь один, а посчитано %d", rep.Paths)
	}
}

// Условие НЕ вычисляется: обе ветки живые, даже если выражение заведомо ложно.
// Иначе обход отвечал бы «что будет при таких статах» вместо «есть ли путь».
func TestWalkTakesBothSidesOfACondition(t *testing.T) {
	rep := walkFixture(t, `[
		{"op":"if","expr":"1 == 2","then":"a","else":"b"},
		{"op":"label","id":"a"},
		{"op":"say","text":"ветка А"},
		{"op":"goto","label":"end"},
		{"op":"label","id":"b"},
		{"op":"say","text":"ветка Б"},
		{"op":"label","id":"end"},
		{"op":"say","text":"сошлись"}
	]`)
	if len(rep.Blocks) != 0 {
		t.Fatalf("обе ветки условия достижимы, а найдено мёртвое: %+v", rep.Blocks)
	}
	if rep.Reached != rep.Commands {
		t.Errorf("покрыто %d из %d", rep.Reached, rep.Commands)
	}
}

// Тот самый класс находок, ради которого всё писалось: безусловный переход
// перепрыгивает блок, и механика не срабатывает НИКОГДА. В дуэли так потерялась
// аура «отчаяние» — семнадцать команд, которые никто не играл.
func TestWalkFindsBlockSkippedByUnconditionalGoto(t *testing.T) {
	rep := walkFixture(t, `[
		{"op":"say","text":"удар"},
		{"op":"goto","label":"дальше"},
		{"op":"if","expr":"аура == \"отчаяние\"","then":"бонус","else":"дальше"},
		{"op":"label","id":"бонус"},
		{"op":"set","key":"урон","expr":"урон + 5"},
		{"op":"say","text":"Отчаяние добавляет силы!"},
		{"op":"label","id":"дальше"},
		{"op":"say","text":"враг отвечает"}
	]`)
	if len(rep.Blocks) != 1 {
		t.Fatalf("ожидался один мёртвый блок, получено %d: %+v", len(rep.Blocks), rep.Blocks)
	}
	b := rep.Blocks[0]
	if b.Start != 2 || b.End != 5 || b.Len != 4 {
		t.Errorf("границы блока: #%d…#%d (%d)", b.Start, b.End, b.Len)
	}
	if !strings.Contains(b.Sample, "аура") {
		t.Errorf("в отчёте нет лица блока: %q", b.Sample)
	}
	if len(b.Labels) != 1 || b.Labels[0] != "бонус" {
		t.Errorf("метки блока: %v", b.Labels)
	}
}

// Функция, которую никто не вызывает: её тело перепрыгнуто goto (так её и
// компилирует .lvns), поэтому мёртвым оказывается весь блок.
func TestWalkFindsUncalledFunctionBody(t *testing.T) {
	rep := walkFixture(t, `[
		{"op":"goto","label":"__fnskip_f"},
		{"op":"label","id":"__fn_f"},
		{"op":"sfx","id":"искры"},
		{"op":"return"},
		{"op":"label","id":"__fnskip_f"},
		{"op":"say","text":"дальше без искр"}
	]`)
	if len(rep.Blocks) != 1 || rep.Blocks[0].Len != 3 {
		t.Fatalf("тело невызванной функции должно быть мёртвым блоком: %+v", rep.Blocks)
	}
	// А вызванная — живой.
	rep = walkFixture(t, `[
		{"op":"call","label":"__fn_f"},
		{"op":"goto","label":"__fnskip_f"},
		{"op":"label","id":"__fn_f"},
		{"op":"sfx","id":"искры"},
		{"op":"return"},
		{"op":"label","id":"__fnskip_f"},
		{"op":"say","text":"дальше"}
	]`)
	if len(rep.Blocks) != 0 {
		t.Fatalf("вызванная функция мёртвой быть не может: %+v", rep.Blocks)
	}
}

// Все варианты выбора проходятся, включая закрытые гейтом и ветку таймера.
func TestWalkTakesEveryChoiceOptionAndTimeout(t *testing.T) {
	rep := walkFixture(t, `[
		{"op":"choice","timeout":5,"timeout_goto":"молчание","options":[
			{"text":"смело","goto":"смело","expr":"смелость > 100"},
			{"text":"тихо","goto":"тихо"},
			{"text":"через тело","body":[{"op":"set","key":"x","expr":"1"},{"op":"goto","label":"тихо"}]}
		]},
		{"op":"label","id":"смело"},
		{"op":"say","text":"а"},
		{"op":"goto","label":"__end"},
		{"op":"label","id":"тихо"},
		{"op":"say","text":"б"},
		{"op":"goto","label":"__end"},
		{"op":"label","id":"молчание"},
		{"op":"say","text":"в"},
		{"op":"label","id":"__end"}
	]`)
	if len(rep.Blocks) != 0 {
		t.Fatalf("все ветки выбора достижимы: %+v", rep.Blocks)
	}
	if len(rep.DeadOpts) != 0 {
		t.Fatalf("варианты должны быть отмечены пройденными: %v", rep.DeadOpts)
	}
}

// Вечный цикл не вешает обход и не считается находкой: команды в нём живые.
// Останавливает его памятка (во второй оборот входим с меньшим запасом, а из
// этой команды с большим уже ходили), а не предел глубины — поэтому обрубов
// здесь нет, и это правильно: сообщать «покрытие неполное» было бы неправдой.
func TestWalkSurvivesLoops(t *testing.T) {
	rep := walkFixture(t, `[
		{"op":"label","id":"снова"},
		{"op":"say","text":"спросить ещё раз"},
		{"op":"goto","label":"снова"}
	]`)
	if rep.Reached != 3 || len(rep.Blocks) != 0 {
		t.Fatalf("цикл должен быть пройден целиком и без находок: %+v", rep)
	}
	if rep.CutDepth != 0 {
		t.Errorf("цикл остановлен памяткой, обрубов быть не должно: %d", rep.CutDepth)
	}
}

// Служебные швы компиляции (goto __endN сразу после другого goto) мёртвы всегда
// и по делу — они не должны шуметь в списке находок.
func TestWalkCountsCompilerBoilerplateSeparately(t *testing.T) {
	rep := walkFixture(t, `[
		{"op":"if","expr":"x","then":"__then1","else":"__else1"},
		{"op":"label","id":"__then1"},
		{"op":"say","text":"да"},
		{"op":"goto","label":"дальше"},
		{"op":"goto","label":"__end1"},
		{"op":"label","id":"__else1"},
		{"op":"say","text":"нет"},
		{"op":"goto","label":"дальше"},
		{"op":"label","id":"__end1"},
		{"op":"label","id":"дальше"},
		{"op":"say","text":"сошлись"}
	]`)
	if len(rep.Blocks) != 0 {
		t.Fatalf("швы компиляции не находка: %+v", rep.Blocks)
	}
	if rep.Boilerplate != 2 {
		t.Errorf("служебных мёртвых команд должно быть 2, посчитано %d", rep.Boilerplate)
	}
}

// Малая глубина не должна выдавать живой контент за мёртвый МОЛЧА: отчёт обязан
// сказать, что обход не дошёл. Именно на этом первый прогон объявил мёртвыми две
// трети настоящей главы.
func TestWalkReportsTruncationInsteadOfLying(t *testing.T) {
	script := `[
		{"op":"goto","label":"a"},{"op":"label","id":"a"},
		{"op":"goto","label":"b"},{"op":"label","id":"b"},
		{"op":"goto","label":"c"},{"op":"label","id":"c"},
		{"op":"say","text":"дальний конец"}
	]`
	path := filepath.Join(t.TempDir(), "case.lvn")
	if err := os.WriteFile(path, []byte(`{"scene":"t","script":`+script+`}`), 0o644); err != nil {
		t.Fatal(err)
	}
	shallow := walkFile(path, 1)
	if shallow.CutDepth == 0 {
		t.Fatal("обрубленный обход обязан это признать")
	}
	if deep := walkFile(path, 10); deep.Reached != deep.Commands || deep.CutDepth != 0 {
		t.Fatalf("с достаточной глубиной покрытие полное: %+v", deep)
	}
}

// Авторский .lvns — основной вход: обход нужен там, где новеллу пишут.
func TestWalkAcceptsLvnsSource(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "case.lvns")
	src := "scene t\n\nМира: Привет.\n\n- Уйти -> прочь\n- Остаться -> тут\n\n:прочь\nМира: Пока.\n-> __end\n\n:тут\nМира: Ну садись.\n"
	if err := os.WriteFile(path, []byte(src), 0o644); err != nil {
		t.Fatal(err)
	}
	rep := walkFile(path, defaultWalkDepth)
	if rep.Err != "" {
		t.Fatalf(".lvns должен приниматься: %s", rep.Err)
	}
	if rep.Commands == 0 || len(rep.Blocks) != 0 {
		t.Fatalf("обе ветки живые: %+v", rep)
	}
}

// JSON-отчёт — контракт для CI: поля не должны молча переименовываться.
func TestWalkJSONShape(t *testing.T) {
	rep := walkFixture(t, `[{"op":"say","text":"раз"}]`)
	raw, err := json.Marshal(rep)
	if err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{`"file"`, `"commands"`, `"reached"`, `"dead_blocks"`, `"boilerplate_dead"`, `"paths"`, `"cut_by_depth"`} {
		if !strings.Contains(string(raw), key) {
			t.Errorf("в JSON-отчёте нет поля %s", key)
		}
	}
}
