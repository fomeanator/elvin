package lvn

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// ПУТЬ АВТОРА В РЕДАКТОРЕ: ТРИ СВОЙСТВА, БЕЗ КОТОРЫХ ОБЕЩАНИЕ README ЛОЖНО.
//
// README обещает первой строкой: положи .lvns в Assets/ — он скомпилируется
// сам и заиграет. Целиком это проверяется установкой (qa/editor-authoring-
// check.sh): пустой проект, файл в Assets/, глава играется. Тот прогон подымает
// редактор и потому идёт по требованию.
//
// Здесь — свойства, потеря которых сделала бы его красным, за миллисекунды.
// Все три уже однажды отсутствовали или чуть не потерялись, и цена у каждого
// своя:
//
//	расширение    нет регистрации — редактор не заметит файл вообще;
//	CompileFile   сборка ПО ТЕКСТУ вместо ПУТИ ломает include: он резолвится
//	              относительно файла, и без пути строка стала бы репликой;
//	структура     синтаксис может быть безупречен при переходе в никуда —
//	              у игрока это выглядит как «глава просто кончилась».
//
// Страж ищет импортёр ПО ПРИЗНАКУ, а не по имени файла: список имён стареет
// молча, а вопрос «что здесь есть» — нет.
func TestEditorImportKeepsTheAuthorPromise(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity", "Packages", "com.lvn.engine", "Editor")

	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("редакторная сборка ядра не прочитана: %v", err)
	}

	importers := 0
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			t.Fatalf("%s: %v", e.Name(), err)
		}
		body := string(raw)
		if !strings.Contains(body, "ScriptedImporter") || !strings.Contains(body, "OnImportAsset") {
			continue
		}
		importers++

		if !strings.Contains(body, `"lvns"`) {
			t.Errorf("%s: импортёр не объявляет расширение lvns — редактор перестанет "+
				"замечать скрипты автора", e.Name())
		}
		if !strings.Contains(body, "CompileFile(") {
			t.Errorf("%s: импортёр собирает не по пути — include резолвится относительно "+
				"файла, и по тексту он превратился бы в реплику", e.Name())
		}
		if !strings.Contains(body, "LvnsStructureCheck") {
			t.Errorf("%s: импорт перестал проверять структуру — переход в никуда доедет "+
				"до игрока как «глава просто кончилась»", e.Name())
		}
		if !strings.Contains(body, "LogImportError") {
			t.Errorf("%s: импортёр не сообщает об ошибке через LogImportError — "+
				"битый скрипт стал бы тихим отказом", e.Name())
		}
	}

	// Порог на НАЙДЕННОЕ: импортёр ровно один, и ноль означал бы, что признак
	// разъехался с кодом, а не что всё в порядке.
	atLeast(t, importers, 1, "импортёров .lvns в редакторной сборке")
}
