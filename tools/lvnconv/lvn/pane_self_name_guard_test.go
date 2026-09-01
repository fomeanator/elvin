package lvn

import (
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ПАНЕЛЬ НАЗЫВАЕТ СЕБЯ САМА.
//
// Меню сцены умеет переодеться — показать себя заново новыми словами, — и для
// этого обязано помнить, ЧТО именно сейчас показано. Пока меню только
// открывали и закрывали, «где мы» знала одна история вызовов; потом язык стали
// переключать ПРЯМО В НЁМ, и выяснилось, что перерисовать себя меню не может:
// кнопка языка меняла свою подпись вручную, а заголовок рядом, вкладки и весь
// остальной текст оставались на прежнем. Игрок видел одно переведённое слово —
// то, по которому нажал.
//
// Починка — одна строка в начале каждой панели: `_pane = ShowX;`. Форма
// правильная (помнит тот, кто знает), но ХРУПКАЯ: забыть строку в новой панели
// ничего не стоит, а расплата отложенная и тихая — переодевание молча покажет
// ПРЕДЫДУЩУЮ панель.
//
// Переносить память к вызывающему было бы хуже: панель зовут из полутора
// десятков мест, и забывать пришлось бы в каждом. Поэтому строка остаётся, а
// стережёт её этот тест.
func TestEveryStageMenuPaneNamesItself(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/UI")
	head := regexp.MustCompile(`(?m)^\s*private void (Show\w+|ConfirmOverwrite|LoadFailedNotice|SaveFailedNotice)\([^)]*\)\s*$`)

	var missing []string
	for _, f := range csFiles(t, dir) {
		base := filepath.Base(f)
		if !strings.HasPrefix(base, "StageMenu") {
			continue
		}
		body := string(mustRead(t, f))
		for _, m := range head.FindAllStringSubmatchIndex(body, -1) {
			name := body[m[2]:m[3]]
			// Тело панели: от заголовка до конца — смотрим первые строки.
			rest := body[m[1]:]
			if len(rest) > 900 {
				rest = rest[:900]
			}
			if !strings.Contains(stripComments(rest), "_pane = ") {
				missing = append(missing, base+":"+name)
			}
		}
	}
	if len(missing) > 0 {
		t.Errorf("панели не называют себя (%d): %v\n"+
			"Без этой строки переодевание молча покажет ПРЕДЫДУЩУЮ панель: "+
			"смена языка переведёт одно слово, по которому нажали, и ничего больше",
			len(missing), missing)
	}
}
