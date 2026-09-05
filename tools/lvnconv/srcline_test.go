package main

// ОШИБКА НАЗЫВАЕТ СТРОКУ АВТОРА, А НЕ НОМЕР КОМАНДЫ.
//
// Компилятор .lvns знает, какая строка породила какую команду, но в .lvn эта
// карта не едет: она нужна инструментам, а не игре. Проверка её не спрашивала и
// говорила автору «script[3]» — адрес в файле, которого автор не писал и не
// открывает. Замер 05.09: ошибка в седьмой строке главы называлась «script[3]».

import (
	"os"
	"path/filepath"
	"testing"
)

func TestSourceLinesSurviveCompilation(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "глава.lvns")
	// Строки пронумерованы от единицы: 1 — scene, 3 и 4 — реплики,
	// 6 — выбор с висячим переходом.
	src := "scene проба\n" +
		"\n" +
		"Первая реплика.\n" +
		"Вторая реплика.\n" +
		"\n" +
		"- Уйти -> нетакой\n"
	if err := os.WriteFile(path, []byte(src), 0o644); err != nil {
		t.Fatal(err)
	}

	doc, lines, err := loadWithSrcLines(path)
	if err != nil {
		t.Fatalf("глава не скомпилировалась: %v", err)
	}
	if doc == nil || len(doc.Script) == 0 {
		t.Fatal("пустой результат компиляции")
	}
	if len(lines) != len(doc.Script) {
		t.Fatalf("карта строк короче скрипта: %d против %d — часть находок останется без адреса",
			len(lines), len(doc.Script))
	}
	for i, ln := range lines {
		if ln <= 0 {
			t.Errorf("команда %d без строки исходника", i)
		}
	}
	// Первая реплика написана на третьей строке файла.
	if lines[0] != 3 {
		t.Errorf("первая реплика отнесена к строке %d, а написана на 3-й", lines[0])
	}

	// А для .lvn карты нет и быть не может — там адрес остаётся прежним.
	lvnPath := filepath.Join(dir, "глава.lvn")
	if err := os.WriteFile(lvnPath, []byte(`{"scene":"t","script":[{"op":"say","text":"раз"}]}`), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, lines2, err := loadWithSrcLines(lvnPath); err != nil {
		t.Fatal(err)
	} else if lines2 != nil {
		t.Error("для .lvn выдумана карта строк — адрес был бы ложным")
	}
}
