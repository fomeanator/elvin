package lvns

import "testing"

// ТИХИЕ МИСКОМПИЛЫ — самый дорогой класс ошибок компилятора: автор пишет
// правильно, получает молча не то, и узнаёт об этом от игрока. Здесь заперты
// два, найденные аудитом 03.09.2026, и оба держались на одном и том же —
// один вопрос, на который в компиляторе отвечали двое.

// Ёлочка внутри "…" — ДАННЫЕ, а не синтаксис. Главный цикл это знал
// (chevronDelta), развороты блоков — нет (chevRun), и авторская реплика с
// открывающей ёлочкой уводила ВСЁ ОСТАЛЬНОЕ в многострочную прозу: `if …{` и
// `}` становились репликами, тело блока играло безусловно. Компилятор молчал.
func TestGuillemetInsideQuotesIsDataNotSyntax(t *testing.T) {
	src := `scene s
say text="«Не уходи"
if x > 0 {
Анна: да
}
Анна: конец
`
	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("не собралось: %v", err)
	}
	var ops []string
	for _, c := range doc.Script {
		ops = append(ops, c["op"].(string))
	}
	want := []string{"say", "if", "label", "say", "label", "label", "say"}
	if len(ops) != len(want) {
		t.Fatalf("команды: %v\nждали: %v", ops, want)
	}
	for i := range want {
		if ops[i] != want[i] {
			t.Fatalf("команда %d = %q, ждали %q\nвсё: %v", i, ops[i], want[i], ops)
		}
	}
	// Самое дорогое последствие: реплика ветки обязана остаться ВНУТРИ ветки,
	// а не сыграть безусловно.
	if doc.Script[1]["then"] != "__then_head_1" {
		t.Errorf("ветка потеряла then: %v", doc.Script[1])
	}
	// И текст автора доехал целиком, вместе с его ёлочкой.
	if got := doc.Script[0]["text"]; got != "«Не уходи" {
		t.Errorf("текст автора искажён: %q", got)
	}
}

// Открытая ёлочка ВНЕ кавычек по-прежнему открывает многострочную прозу —
// правило узкое, и сузить его дальше нельзя: на этом держится многострочный
// текст реплик.
func TestBareGuillemetStillOpensMultilineProse(t *testing.T) {
	src := "scene s\nАнна: «первая строка\nвторая строка»\nАнна: следующая\n"
	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("не собралось: %v", err)
	}
	if len(doc.Script) != 2 {
		t.Fatalf("ждали две реплики, вышло %d: %v", len(doc.Script), doc.Script)
	}
	if got := doc.Script[0]["text"]; got != "первая строка\nвторая строка" {
		t.Errorf("многострочная проза склеилась не так: %q", got)
	}
}

// Номер строки в диагностике — номер строки В ФАЙЛЕ АВТОРА. Блок `ui`
// вынимается до общего разбора и заменялся ОДНОЙ меткой: всё, что ниже,
// съезжало на длину блока, и подсветка в IDE вставала не на ту строку.
func TestSourceLinesSurviveUiBlocks(t *testing.T) {
	src := `scene s
ui бой {
  panel
  text «удар»
}
Анна: после блока
ui второй {
  row
}
Анна: и после второго
`
	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("не собралось: %v", err)
	}
	if len(doc.Script) != len(doc.SrcLine) {
		t.Fatalf("SrcLine отстал от Script: %d против %d", len(doc.SrcLine), len(doc.Script))
	}
	want := []struct {
		op   string
		line int
	}{
		{"ui", 2},
		{"say", 6},
		{"ui", 7},
		{"say", 10},
	}
	if len(doc.Script) != len(want) {
		t.Fatalf("команд %d, ждали %d: %v", len(doc.Script), len(want), doc.Script)
	}
	for i, w := range want {
		if op := doc.Script[i]["op"]; op != w.op {
			t.Errorf("команда %d: %v, ждали %s", i, op, w.op)
		}
		if doc.SrcLine[i] != w.line {
			t.Errorf("команда %d (%s): строка %d, ждали %d", i, w.op, doc.SrcLine[i], w.line)
		}
	}
}

// Ошибки разбора называют строку ФАЙЛА АВТОРА, а не позицию в рабочем
// списке: между ними стоят вынутые блоки `ui` и пропущенные пустые строки.
//
// ЧЕГО ЭТОТ ТЕСТ НЕ ПРОВЕРЯЕТ, и это не забывчивость. Развороты (`for`,
// `while`, `func`, однострочные блоки) переписывают текст ЦЕЛИКОМ, вставляя
// свои строки, и карта строк через них не переносится вовсе — после первого
// же цикла номера съезжают вниз на длину разворота. Чинится это не здесь:
// нужно провести номера сквозь четыре прохода (flattenInline, expandLoops,
// expandCalls и сбор функций), а это отдельная работа со своим риском —
// компилятор здесь источник правды для всего продукта. Записано в
// docs/audit-2026-09-03.md как незакрытое.
func TestParseErrorsNameTheAuthorsLine(t *testing.T) {
	src := "scene s\n\n// комментарий\nui бой {\n  panel\n}\n\n:\n"
	_, err := Convert(src)
	if err == nil {
		t.Fatal("пустая метка обязана быть ошибкой")
	}
	if got := err.Error(); got != "line 8: label cannot be empty" {
		t.Errorf("ошибка называет не ту строку: %q", got)
	}
}
