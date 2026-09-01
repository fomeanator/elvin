package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ПУБЛИКАЦИЯ ПАКЕТОВ НЕ ЗАБУДЕТ НОВЫЙ ПАКЕТ.
//
// Потребители ставят движок из зеркал (клон в несколько мегабайт вместо
// монорепозитория на триста), и раскладывает их по зеркалам workflow, который
// срабатывает ТОЛЬКО ПО ТЕГУ. Пока список пакетов стоял в нём руками, шестой
// пакет пришлось бы вписать — а забыть легко, и узналось бы это при выпуске
// версии: пакет просто не поехал бы, молча.
//
// Список стал самонаходимым (обход unity/Packages/com.lvn.*), а имя зеркала
// выводится из имени пакета: com.lvn.engine.shell → lvn-engine-shell. Этот тест
// держит второе: правило обязано давать ИМЕННО те имена, под которыми зеркала
// уже существуют и на которые ссылаются потребители. Ошибка здесь тише всех
// прочих — она обнаруживается не в сборке, а через полгода, когда чей-то
// проект не найдёт пакет.
func TestPackageMirrorNames(t *testing.T) {
	scanned := 0
	root := repoRoot(t)
	dir := filepath.Join(root, "unity", "Packages")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Skipf("пакеты не установлены в этой раскладке: %v", err)
	}

	// Как выводит имя сам workflow: отбросить «com.», точки заменить дефисами.
	mirrorOf := func(pkg string) string {
		return strings.ReplaceAll(strings.TrimPrefix(pkg, "com."), ".", "-")
	}

	var packages []string
	for _, e := range entries {
		scanned++
		if e.IsDir() && strings.HasPrefix(e.Name(), "com.lvn.") {
			packages = append(packages, e.Name())
		}
	}
	sort.Strings(packages)
	if len(packages) == 0 {
		t.Fatal("не найдено ни одного пакета com.lvn.* — разбор сломался, а не репозиторий")
	}

	// Имена, под которыми зеркала УЖЕ существуют. Менять этот список можно
	// только вместе с реальным переименованием репозитория-зеркала — потому он
	// здесь и записан.
	known := map[string]bool{
		"lvn-engine": true, "lvn-engine-shell": true, "lvn-engine-services": true,
		"lvn-engine-spine": true, "lvn-engine-addressables": true,
	}

	var strays []string
	for _, pkg := range packages {
		m := mirrorOf(pkg)
		if !known[m] {
			strays = append(strays, pkg+" → "+m)
		}
	}
	if len(strays) > 0 {
		t.Errorf("правило дало имя зеркала, которого нет среди известных (%d):\n  %s\n\n"+
			"Либо зеркало ещё не заведено (создайте репозиторий fomeanator/<имя> и впишите его "+
			"в known здесь), либо имя пакета не подходит под правило — тогда публикация уедет "+
			"не туда и узнается это при выпуске версии.",
			len(strays), strings.Join(strays, "\n  "))
	}

	// И обратная сторона: workflow должен ходить по каталогам, а не по списку.
	wf, err := os.ReadFile(filepath.Join(root, ".github", "workflows", "mirror-packages.yml"))
	if err != nil {
		t.Skipf("workflow публикации не найден: %v", err)
	}
	if regexp.MustCompile(`for pair in`).Match(wf) {
		t.Error("публикация UPM-пакетов снова идёт по рукописному списку — новый пакет молча не поедет; " +
			"обходите unity/Packages/com.lvn.*, как это делают пакеты языка ниже в том же файле")
	}
	// Порог пустоты: обход, не нашедший ни одного файла, зеленеет ни о чём.
	atLeast(t, scanned, 3, "проверенных пакетов")

}
