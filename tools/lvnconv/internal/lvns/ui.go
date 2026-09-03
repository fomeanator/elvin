package lvns

import (
	"fmt"
	"strings"
)

// ── `ui` — дерево интерфейса из сценария ────────────────────────────────────
//
// Разбирает блок вида
//
//	ui бой {
//	  panel at=bottom h=19% pad=2% bg=panel {
//	    row gap=3% { text «{имя}» size=13 }
//	  }
//	}
//
// в ОДНУ команду с вложенным деревом. Почему одной командой, а не потоком
// «создай элемент, создай элемент»: дерево — это описание, а не череда
// действий. Целое описание можно сравнить с предыдущим и обновить точечно;
// поток действий сравнивать не с чем, и любое обновление превращается в
// «снеси и построй заново» — с потерей прокрутки, фокуса и анимаций.
//
// Раскладку считает РАНТАЙМ, а не компилятор. Компилятор не знает ни высоты
// текста после переноса, ни длины списка, ни выреза экрана — всё это меряется
// только на живом экране. Здесь только разбор и проверка имён.

// Элементы. Список закрыт намеренно: неизвестное слово внутри `ui` — это
// опечатка, и молча пропустить её значит нарисовать пустоту без объяснений.
var uiKinds = map[string]bool{
	"panel": true, "row": true, "column": true,
	"text": true, "bar": true, "icon": true, "image": true,
	"button": true, "space": true, "scroll": true,
}

// Поля, общие для всех элементов: раскладка + вид. Имена — как в UI Toolkit,
// чтобы «правильное поведение» не приходилось придумывать: оно уже определено
// там, и три рантайма реализуют одно и то же, а не каждый своё.
var uiFields = map[string]bool{
	// раскладка
	"dir": true, "gap": true, "pad": true, "pad_x": true, "pad_y": true,
	"justify": true, "align": true, "grow": true, "shrink": true, "basis": true,
	"w": true, "h": true, "at": true, "z": true,
	// вид
	"bg": true, "color": true, "radius": true, "edge": true, "opacity": true,
	"size": true, "weight": true, "font": true,
	// смысл конкретных элементов
	"id": true, "value": true, "name": true, "url": true, "on_click": true,
	"hide": true, "appear": true,
}

// Поля самой команды `ui`, а не элемента внутри неё.
//
// layer решает спор за низ экрана: боевой интерфейс обязан уходить ПОД окно
// реплики (иначе кнопки закрывают текст — ровно это и вышло на первой живой
// проверке), а полноэкранное меню — лежать поверх всего.
//
// when — при какой стадии дерево видно: always (по умолчанию), idle (пока не
// идёт реплика и не показан выбор), say, choice. Без этого автор прятал дерево
// вручную в каждой ветке и одну неизбежно забывал — интерфейс оставался
// поверх разговора.
//
// block — про касания. Слой по умолчанию прозрачен для тапа мимо кнопок:
// история должна продолжаться, как продолжалась. Меню — наоборот: тап по
// пустому месту меню не должен листать историю за его спиной.
var uiCmdFields = map[string]bool{"layer": true, "block": true, "when": true, "appear": true}

// parseUiTree читает тело блока и возвращает список узлов. i указывает на
// первую строку тела; возвращается индекс строки ПОСЛЕ закрывающей скобки.
func parseUiTree(lines []string, srcNo []int, i int) ([]any, int, error) {
	var out []any
	for i < len(lines) {
		line := strings.TrimSpace(lines[i])
		if line == "" {
			i++
			continue
		}
		if line == "}" {
			return out, i + 1, nil
		}

		node, hasBlock, err := parseUiNode(line, srcNo[i])
		if err != nil {
			return nil, 0, err
		}
		i++
		// Скобка могла остаться на своей строке — flattenInline выносит её
		// туда для управляющих конструкций, и полагаться на одну форму нельзя.
		if !hasBlock && i < len(lines) && strings.TrimSpace(lines[i]) == "{" {
			hasBlock = true
			i++
		}
		if hasBlock {
			kids, next, kerr := parseUiTree(lines, srcNo, i)
			if kerr != nil {
				return nil, 0, kerr
			}
			if len(kids) > 0 {
				node["children"] = kids
			}
			i = next
		}
		out = append(out, node)
	}
	return nil, 0, fmt.Errorf("ui: блок не закрыт — не хватает }")
}

// parseUiNode разбирает одну строку элемента.
func parseUiNode(line string, srcLine int) (map[string]any, bool, error) {
	hasBlock := false
	if strings.HasSuffix(line, "{") {
		hasBlock = true
		line = strings.TrimSpace(strings.TrimSuffix(line, "{"))
	}

	// Содержимое в «…» — как у реплики. Забираем до разбора полей, иначе
	// пробелы и знаки внутри текста уедут в ключи.
	body := ""
	if a := strings.Index(line, "«"); a >= 0 {
		b := strings.LastIndex(line, "»")
		if b < a {
			return nil, false, fmt.Errorf("строка %d: ui: не закрыт «…»", srcLine)
		}
		body = line[a+len("«") : b]
		line = strings.TrimSpace(line[:a] + " " + line[b+len("»"):])
	}

	sp := strings.IndexAny(line, " \t")
	kind := line
	rest := ""
	if sp > 0 {
		kind, rest = line[:sp], strings.TrimSpace(line[sp:])
	}
	if !uiKinds[kind] {
		return nil, false, fmt.Errorf("строка %d: ui: неизвестный элемент %q", srcLine, kind)
	}

	node := map[string]any{"kind": kind}
	// row/column — сахар над panel: направление это поле, а не отдельный вид.
	if kind == "row" || kind == "column" {
		node["kind"] = "panel"
		node["dir"] = kind
	}
	if body != "" {
		node["text"] = body
	}
	if rest != "" {
		attrs, err := parseKeyValue(rest)
		if err != nil {
			return nil, false, fmt.Errorf("строка %d: ui %s: %w", srcLine, kind, err)
		}
		for k, v := range attrs {
			if !uiFields[k] {
				return nil, false, fmt.Errorf("строка %d: ui %s: неизвестное поле %q", srcLine, kind, k)
			}
			node[k] = v
		}
	}
	return node, hasBlock, nil
}

// parseUiCommand разбирает строку `ui <имя> …` целиком. Возвращает команду и
// индекс следующей строки.
func parseUiCommand(lines []string, srcNo []int, i int) (Cmd, int, error) {
	line := strings.TrimSpace(lines[i])
	rest := strings.TrimSpace(strings.TrimPrefix(line, "ui"))
	if rest == "" {
		return nil, 0, fmt.Errorf("строка %d: ui: нужно имя дерева", srcNo[i])
	}

	open := strings.HasSuffix(rest, "{")
	if open {
		rest = strings.TrimSpace(strings.TrimSuffix(rest, "{"))
	}
	sp := strings.IndexAny(rest, " \t")
	name := rest
	tail := ""
	if sp > 0 {
		name, tail = rest[:sp], strings.TrimSpace(rest[sp:])
	}
	if !isIdentWord(name) {
		return nil, 0, fmt.Errorf("строка %d: ui: %q не годится в имя дерева", srcNo[i], name)
	}
	cmd := Cmd{"op": "ui", "id": name}

	// Короткие формы без тела: спрятать, показать, убрать.
	switch tail {
	case "hide", "show", "drop":
		cmd["action"] = tail
		return cmd, i + 1, nil
	}
	// Поля самой команды — не элемента: где лежит слой и ловит ли он касания.
	if tail != "" {
		attrs, err := parseKeyValue(tail)
		if err != nil {
			return nil, 0, fmt.Errorf("строка %d: ui %s: %w", srcNo[i], name, err)
		}
		for k, v := range attrs {
			if !uiCmdFields[k] {
				return nil, 0, fmt.Errorf("строка %d: ui %s: неизвестное поле %q (ждали hide/show/drop, layer= или block=)", srcNo[i], name, k)
			}
			if k == "layer" {
				sv := fmt.Sprint(v)
				if sv != "hud" && sv != "over" {
					return nil, 0, fmt.Errorf("строка %d: ui %s: layer=%q — бывает hud (под окном диалога) или over (поверх всего)", srcNo[i], name, sv)
				}
			}
			if k == "when" {
				sv := fmt.Sprint(v)
				if sv != "always" && sv != "idle" && sv != "say" && sv != "choice" {
					return nil, 0, fmt.Errorf("строка %d: ui %s: when=%q — бывает always, idle, say или choice", srcNo[i], name, sv)
				}
			}
			cmd[k] = v
		}
	}

	i++
	if !open {
		if i < len(lines) && strings.TrimSpace(lines[i]) == "{" {
			i++
		} else {
			return nil, 0, fmt.Errorf("строка %d: ui %s: ждали блок { … }", srcNo[i-1], name)
		}
	}
	kids, next, err := parseUiTree(lines, srcNo, i)
	if err != nil {
		return nil, 0, err
	}
	// Корень всегда один. Несколько узлов верхнего уровня заворачиваем в
	// панель на весь экран: у дерева обязан быть один владелец места.
	if len(kids) == 1 {
		cmd["tree"] = kids[0]
	} else {
		cmd["tree"] = map[string]any{"kind": "panel", "at": "fill", "children": kids}
	}
	return cmd, next, nil
}

// extractUiBlocks вынимает блоки `ui … { … }` ДО общих проходов компилятора.
//
// Нужно потому, что фигурные скобки в языке уже заняты управляющими
// конструкциями, и flattenInline с expandLoops разберут `ui`-блок как
// незакрытый `if`. Здесь блок целиком заменяется одной строкой-меткой, а сам
// разобранным деревом уезжает в таблицу.
//
// Скобки считаются ТОЛЬКО вне текста и вне значений: `text «{имя}»` и
// `value="{хп / макс}"` полны фигурных скобок, и наивный счёт закрыл бы блок
// на первой же привязке.
func extractUiBlocks(src string) (string, []Cmd, error) {
	lines := strings.Split(src, "\n")
	srcNo := make([]int, len(lines))
	for i := range lines {
		srcNo[i] = i + 1
	}

	var out []string
	var blocks []Cmd
	for i := 0; i < len(lines); {
		t := strings.TrimSpace(stripLineComment(lines[i]))
		if t != "ui" && !strings.HasPrefix(t, "ui ") {
			out = append(out, lines[i])
			i++
			continue
		}
		// Короткие формы (hide/show/drop) блока не открывают — отдаём их
		// обычному разбору, там они станут командой без дерева.
		if !uiOpensBlock(lines, i) {
			out = append(out, lines[i])
			i++
			continue
		}
		end, err := uiBlockEnd(lines, i)
		if err != nil {
			return "", nil, err
		}
		// Чистим тело от комментариев и пустых строк — разбор дерева ждёт
		// только строки элементов.
		body := make([]string, 0, end-i+1)
		nums := make([]int, 0, end-i+1)
		for j := i; j <= end; j++ {
			c := strings.TrimSpace(stripLineComment(lines[j]))
			if c == "" {
				continue
			}
			body = append(body, c)
			nums = append(nums, srcNo[j])
		}
		cmd, _, err := parseUiCommand(body, nums, 0)
		if err != nil {
			return "", nil, err
		}
		blocks = append(blocks, cmd)
		// НОМЕРА СТРОК ПОСЛЕ БЛОКА НЕ ДОЛЖНЫ СЪЕХАТЬ. Блок из N строк
		// заменяется одной меткой, и всё, что ниже, поднималось на N-1 строк:
		// дальше по конвейеру номер строки берётся из позиции в этом списке,
		// и вся диагностика после КАЖДОГО блока `ui` указывала выше нужного
		// места — в IDE подсветка вставала не на ту строку, и чем больше
		// блоков, тем дальше уезжала.
		//
		// Добираем пустыми строками: главный цикл их пропускает, поэтому в
		// разборе они не стоят ничего, а счёт остаётся счётом файла автора.
		out = append(out, fmt.Sprintf("ui#%d", len(blocks)-1))
		for pad := i + 1; pad <= end; pad++ {
			out = append(out, "")
		}
		i = end + 1
	}
	return strings.Join(out, "\n"), blocks, nil
}

// uiOpensBlock: открывает ли эта строка (или следующая непустая) блок.
func uiOpensBlock(lines []string, i int) bool {
	t := strings.TrimSpace(stripLineComment(lines[i]))
	if strings.HasSuffix(t, "{") {
		return true
	}
	for j := i + 1; j < len(lines); j++ {
		n := strings.TrimSpace(stripLineComment(lines[j]))
		if n == "" {
			continue
		}
		return n == "{"
	}
	return false
}

// uiBlockEnd находит строку с закрывающей скобкой блока, считая только те
// скобки, что лежат вне «…» и вне "…".
func uiBlockEnd(lines []string, start int) (int, error) {
	depth := 0
	for i := start; i < len(lines); i++ {
		l := stripLineComment(lines[i])
		inChev, inQuote := false, false
		for k := 0; k < len(l); k++ {
			if strings.HasPrefix(l[k:], "«") {
				inChev = true
				k += len("«") - 1
				continue
			}
			if strings.HasPrefix(l[k:], "»") {
				inChev = false
				k += len("»") - 1
				continue
			}
			if inChev {
				continue
			}
			if l[k] == '"' {
				inQuote = !inQuote
				continue
			}
			if inQuote {
				continue
			}
			if l[k] == '{' {
				depth++
			} else if l[k] == '}' {
				depth--
				if depth == 0 {
					return i, nil
				}
			}
		}
	}
	return 0, fmt.Errorf("строка %d: ui: блок не закрыт", start+1)
}
