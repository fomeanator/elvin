package lvns

import (
	"fmt"
	"strings"
	"testing"
)

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

// РАЗВОРОТЫ НЕ СДВИГАЮТ НОМЕРА. Каждый из трёх проходов, стоящих между
// автором и разбором, меняет число строк: `for` разворачивается в шесть строк
// вместо одной, `func` — в две, однострочный `if c { x }` — в четыре. Раньше
// номер строки считался ПОСЛЕ них, и каждый разворот выше по файлу уводил всю
// диагностику ниже него на свою длину: компилятор называл место, где ошибки
// нет, а SrcLine в готовом .lvn (по нему IDE ставит курсор) указывал мимо на
// то же расстояние.
//
// Здесь каждый разворот стоит ВЫШЕ ошибки, и каждый — своего рода.
func TestExpansionsDoNotShiftLineNumbers(t *testing.T) {
	cases := []struct {
		name, above string
		grew        string // во что разворачивается: для сообщения о провале
	}{
		{"for", "for i in [1,2] {\n  set key=x value=1\n}", "шесть строк"},
		{"while", "while x < 3 {\n  set key=x expr=\"x + 1\"\n}", "три строки"},
		{"func", "func f() {\n  set key=x value=1\n}", "две строки"},
		{"однострочный if", "if x > 0 { set key=x value=1 }", "четыре строки"},
		{"вызов процедуры", "func f() {\n  set key=x value=1\n}\nf()", "две строки и вызов"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			// Пустая метка на ПОСЛЕДНЕЙ строке: считаем её номер руками.
			body := "scene s\n" + c.above + "\n:\n"
			want := len(strings.Split(strings.TrimSuffix(body, "\n"), "\n"))
			_, err := Convert(body)
			if err == nil {
				t.Fatal("пустая метка обязана быть ошибкой")
			}
			if got, exp := err.Error(), fmt.Sprintf("line %d: label cannot be empty", want); got != exp {
				t.Errorf("%s разворачивается в %s и уводит диагностику: %q, ждали %q",
					c.name, c.grew, got, exp)
			}
		})
	}
}

// SrcLine в готовом документе — та же карта, и по ней работают IDE и редактор.
// Проверяем ПОСЛЕДНЮЮ команду: до неё стоят все виды разворотов сразу.
func TestSrcLineSurvivesExpansions(t *testing.T) {
	src := `scene s
for i in [1,2] {
  set key=a value=1
}
if a > 0 { set key=b value=2 }
func f() {
  set key=c value=3
}
f()
Анна: последняя строка
`
	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("не собралось: %v", err)
	}
	if len(doc.Script) != len(doc.SrcLine) {
		t.Fatalf("карта строк короче сценария: %d против %d", len(doc.SrcLine), len(doc.Script))
	}
	// Реплика — последняя команда, которую породил АВТОР (после неё идут
	// только метки-хвосты разворотов).
	last := -1
	for i, c := range doc.Script {
		if c["op"] == "say" {
			last = i
		}
	}
	if last < 0 {
		t.Fatal("реплики нет в сценарии")
	}
	const want = 10 // строка `Анна: последняя строка`
	if got := doc.SrcLine[last]; got != want {
		t.Errorf("SrcLine реплики = %d, а в файле она на строке %d — "+
			"на столько же промахнётся курсор IDE", got, want)
	}
}
