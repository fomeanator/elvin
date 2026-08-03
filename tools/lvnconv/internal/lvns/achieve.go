package lvns

// Достижения — БЕЗ новой команды рантайма.
//
// Соблазн завести op `achieve` велик, но достижение по сути не команда сцены, а
// СОСТОЯНИЕ ИГРОКА: его надо хранить между новеллами, показывать в отдельном
// экране и синхронизировать с сервером. Всё это у нас уже есть — межновелльные
// переменные `global.*` персистятся и уезжают в облако сами.
//
// Поэтому строка автора
//
//	achieve первая_кровь "Первая кровь"
//	achieve без_единого "Без единого удара" "Пройти главу, не получив урона"
//
// разворачивается в обычную запись состояния:
//
//	set global.ach_первая_кровь = "Первая кровь"
//
// Плашку показывает движок, заметив новую запись; экран достижений перебирает
// `global.ach_*`. Цена — две реализации вместо шести, и достижения работают в
// плеере, который о них никогда не слышал.
//
// Описание (третий аргумент) уходит в отдельный ключ `global.achd_<id>`: оно
// нужно экрану, но не участвует в проверках «получено ли».

import (
	"fmt"
	"regexp"
	"strings"
)

// reAchieve — `achieve <id> "Название" ["Описание"]`.
var reAchieve = regexp.MustCompile(
	`^\s*(?:achieve|достижение)\s+([^\s"]+)\s+"([^"]*)"(?:\s+"([^"]*)")?\s*$`)

// expandAchievements переводит строки достижений в записи состояния.
func expandAchievements(src string) (string, []string) {
	lines := strings.Split(src, "\n")
	out := make([]string, len(lines))
	var warns []string
	seen := map[string]int{}

	for i, line := range lines {
		m := reAchieve.FindStringSubmatch(line)
		if m == nil {
			out[i] = line
			continue
		}
		id, title, desc := m[1], m[2], m[3]
		if prev, dup := seen[id]; dup {
			// Одно достижение выдаётся из разных мест сюжета — это нормально
			// (две ветки, один итог). Но два РАЗНЫХ названия под одним
			// ключом означают опечатку в идентификаторе.
			warns = append(warns, fmt.Sprintf(
				"line %d: достижение «%s» уже объявлено в строке %d — если это другое достижение, дайте ему свой идентификатор",
				i+1, id, prev))
		} else {
			seen[id] = i + 1
		}
		var b strings.Builder
		fmt.Fprintf(&b, `set global.ach_%s = %q`, id, title)
		if desc != "" {
			fmt.Fprintf(&b, "\nset global.achd_%s = %q", id, desc)
		}
		out[i] = b.String()
	}
	return strings.Join(out, "\n"), warns
}
