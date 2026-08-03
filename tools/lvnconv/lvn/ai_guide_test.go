package lvn_test

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// AI.md — единственный документ, который автор отдаёт нейросети целиком.
// Ошибка в нём тиражируется в КАЖДУЮ сгенерированную новеллу и обнаруживается
// у игрока, а не при сборке. Такое уже случалось: метки были описаны как
// `имя:`, тогда как язык понимает `:имя`, и любой пример из файла ломался на
// первом же переходе.
//
// Поэтому примеры из него компилируются здесь, как обычный контент.
func TestAIGuideExamplesCompile(t *testing.T) {
	path := filepath.Join("..", "..", "..", "AI.md")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Skipf("AI.md не найден (%v) — проверять нечего", err)
	}

	blocks := regexp.MustCompile("(?s)```\n(.*?)```").FindAllStringSubmatch(string(data), -1)
	checked := 0
	for i, b := range blocks {
		src := b[1]
		// Цельный сценарий узнаётся по `scene` в первой строке: фрагменты
		// («o3d id=…») сами по себе не компилируются и проверяются глазами.
		if !strings.HasPrefix(strings.TrimSpace(src), "scene ") {
			continue
		}
		checked++
		doc, err := lvns.Convert(src)
		if err != nil {
			t.Errorf("пример №%d из AI.md не компилируется: %v", i+1, err)
			continue
		}
		if len(doc.Script) == 0 {
			t.Errorf("пример №%d из AI.md дал пустой сценарий", i+1)
		}
	}
	if checked == 0 {
		t.Fatal("в AI.md не нашлось ни одного цельного примера — проверка перестала что-либо проверять")
	}
	t.Logf("проверено цельных примеров: %d", checked)
}

// Каждая ОТДЕЛЬНАЯ строка-команда из документа тоже должна быть настоящей.
// Цельные примеры ловят синтаксис, но большая часть файла — фрагменты вида
// «o3d id=… shader=wind», и именно их нейросеть копирует чаще всего. Поле,
// которого нет в языке, отсюда попадёт в каждую сгенерированную сцену.
func TestAIGuideCommandLinesAreReal(t *testing.T) {
	data, err := os.ReadFile(filepath.Join("..", "..", "..", "AI.md"))
	if err != nil {
		t.Skip("AI.md не найден")
	}
	re := regexp.MustCompile(`(?m)^(o3d|bg3d|light|actor|obj|audio|fx|camera|fade|achieve)\s+\S.*$`)
	lines := re.FindAllString(string(data), -1)
	if len(lines) < 10 {
		t.Fatalf("в AI.md нашлось всего %d строк-команд — проверка перестала что-либо проверять", len(lines))
	}
	for _, line := range lines {
		// Строка, разорванная переносом (`\` в конце), — половина примера:
		// проверять её отдельно бессмысленно, целое ловят другие тесты.
		if strings.HasSuffix(strings.TrimSpace(line), "\\") {
			continue
		}
		// `achieve` — конструкция ФАЙЛА, а не текста: она разворачивается на
		// пути ConvertFile. Здесь проверяем то, что видит Convert.
		if strings.HasPrefix(line, "achieve ") {
			continue
		}
		doc, err := lvns.Convert("scene t\n" + line + "\nТекст.\n")
		if err != nil {
			t.Errorf("строка из AI.md не компилируется: %s\n  → %v", line, err)
			continue
		}
		// Команда, которую язык не узнал, становится РЕПЛИКОЙ — молча.
		sawCommand := false
		for _, c := range doc.Script {
			if op, _ := c["op"].(string); op != "say" && op != "label" {
				sawCommand = true
			}
		}
		if !sawCommand {
			t.Errorf("строка из AI.md стала текстом, а не командой: %s", line)
		}
	}
	t.Logf("проверено строк-команд: %d", len(lines))
}

// Отдельно — обещания документа о синтаксисе. Если в языке что-то поменяется,
// а документ забудут, тест назовёт расхождение вслух.
func TestAIGuideSyntaxClaims(t *testing.T) {
	data, err := os.ReadFile(filepath.Join("..", "..", "..", "AI.md"))
	if err != nil {
		t.Skip("AI.md не найден")
	}
	text := string(data)

	// Метка: двоеточие ВПЕРЕДИ. Проверяем и утверждение, и живой пример.
	if !strings.Contains(text, "`:имя`") {
		t.Error("AI.md должен объяснять, что метка пишется как `:имя` — иначе каждая новелла ломается на переходе")
	}
	doc, err := lvns.Convert("scene t\n:метка\nТекст.\n-> метка\n")
	if err != nil {
		t.Fatalf("язык перестал понимать `:метка`: %v", err)
	}
	labels := 0
	for _, c := range doc.Script {
		if op, _ := c["op"].(string); op == "label" {
			labels++
		}
	}
	if labels == 0 {
		t.Error("`:метка` больше не создаёт метку — AI.md надо переписывать")
	}
}
