package lvns

import (
	"encoding/json"
	"strings"
	"testing"
)

func uiCmd(t *testing.T, src string) map[string]any {
	t.Helper()
	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}
	for _, c := range doc.Script {
		raw, _ := json.Marshal(c)
		var m map[string]any
		json.Unmarshal(raw, &m)
		if m["op"] == "ui" {
			return m
		}
	}
	t.Fatalf("команда ui не найдена: %+v", doc.Script)
	return nil
}

// Дерево приходит ОДНОЙ командой, а не потоком «создай элемент». Целое
// описание можно сравнить с предыдущим и обновить точечно; поток сравнивать не
// с чем, и любое обновление стало бы «снеси и построй заново» — с потерей
// прокрутки, фокуса и анимаций.
func TestUiParsesNestedTree(t *testing.T) {
	m := uiCmd(t, `
scene t
:s
ui бой {
  panel at=bottom h=19% {
    row gap=3% {
      text «ТЫ» size=13
      bar value="{хп / макс}"
    }
  }
}
`)
	if m["id"] != "бой" {
		t.Fatalf("имя дерева потеряно: %v", m["id"])
	}
	root := m["tree"].(map[string]any)
	if root["kind"] != "panel" || root["at"] != "bottom" {
		t.Fatalf("корень не тот: %v", root)
	}
	row := root["children"].([]any)[0].(map[string]any)
	// row — сахар над panel: направление это поле, а не отдельный вид, иначе
	// рантайму пришлось бы знать два элемента вместо одного.
	if row["kind"] != "panel" || row["dir"] != "row" {
		t.Fatalf("row должен разворачиваться в panel dir=row: %v", row)
	}
	kids := row["children"].([]any)
	if len(kids) != 2 {
		t.Fatalf("ожидались два ребёнка, получено %d", len(kids))
	}
	if kids[0].(map[string]any)["text"] != "ТЫ" {
		t.Fatalf("текст элемента потерян: %v", kids[0])
	}
}

// ГЛАВНАЯ ЛОВУШКА РАЗБОРА: фигурные скобки есть и в тексте, и в значениях.
// Наивный счёт закрыл бы блок на первой же привязке.
func TestUiBracesInsideTextAndValuesDoNotCloseBlock(t *testing.T) {
	m := uiCmd(t, `
scene t
:s
ui хп {
  panel {
    text «{имя} — {хп} из {макс}»
    bar value="{хп / макс}"
  }
}
`)
	root := m["tree"].(map[string]any)
	kids := root["children"].([]any)
	if len(kids) != 2 {
		t.Fatalf("привязки съели структуру: %v", root)
	}
	if kids[0].(map[string]any)["text"] != "{имя} — {хп} из {макс}" {
		t.Fatalf("текст с выражениями искажён: %v", kids[0])
	}
	if kids[1].(map[string]any)["value"] != "{хп / макс}" {
		t.Fatalf("выражение в значении искажено: %v", kids[1])
	}
}

// Опечатка в имени элемента или поля — ошибка компиляции. Молча пропустить
// значит нарисовать пустоту и не объяснить почему.
func TestUiRejectsUnknownKindAndField(t *testing.T) {
	if _, err := Convert("scene t\n:s\nui a {\n  panelll {\n  }\n}\n"); err == nil {
		t.Fatal("неизвестный элемент прошёл")
	} else if !strings.Contains(err.Error(), "panelll") {
		t.Fatalf("ошибка не называет виновника: %v", err)
	}
	if _, err := Convert("scene t\n:s\nui a {\n  panel wdith=10 {\n  }\n}\n"); err == nil {
		t.Fatal("неизвестное поле прошло")
	}
}

// Короткие формы блока не открывают.
func TestUiShortForms(t *testing.T) {
	for _, act := range []string{"hide", "show", "drop"} {
		m := uiCmd(t, "scene t\n:s\nui бой "+act+"\n")
		if m["action"] != act || m["id"] != "бой" {
			t.Fatalf("%s: %v", act, m)
		}
		if _, has := m["tree"]; has {
			t.Fatalf("%s не должен нести дерево: %v", act, m)
		}
	}
}

// Несколько узлов верхнего уровня заворачиваются в панель на весь экран: у
// дерева обязан быть один владелец места.
func TestUiWrapsMultipleRoots(t *testing.T) {
	m := uiCmd(t, "scene t\n:s\nui a {\n  text «раз»\n  text «два»\n}\n")
	root := m["tree"].(map[string]any)
	if root["kind"] != "panel" || root["at"] != "fill" {
		t.Fatalf("корень не завёрнут: %v", root)
	}
	if len(root["children"].([]any)) != 2 {
		t.Fatalf("дети потерялись: %v", root)
	}
}
