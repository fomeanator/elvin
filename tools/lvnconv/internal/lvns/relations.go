package lvns

// Расстановка ОТНОСИТЕЛЬНО других тел: «рядом с фонарём», «на надгробии».
//
// Координаты — не то, чем человек (и тем более нейросеть) думает о сцене. Он
// думает «скамья у фонаря», «свеча на могиле», «стол посреди комнаты». Перевод
// этого в метры — механическая работа, в которой модель ошибается тем чаще, чем
// больше объектов: чтобы поставить скамью рядом с фонарём, надо ПОМНИТЬ, где
// фонарь, и не промахнуться.
//
//	o3d id=фонарь model=lantern pos="2,0,4"
//	o3d id=скамья model=bench near=фонарь dist=1.4 side=left
//	o3d id=свеча  shape=cylinder on=надгробие
//
// Так же поступают исследовательские генераторы сцен (Holodeck, I-Design):
// модель описывает ОТНОШЕНИЯ, а решатель считает позы. Разница в том, что им
// нужен отдельный оптимизатор ограничений, а нам — нет: наши отношения
// однозначны, и компилятор считает их арифметикой.
//
// Как и сетка, это конструкция ВРЕМЕНИ КОМПИЛЯЦИИ: рантайм получает обычный
// `pos` в метрах и об отношениях не знает.

import (
	"fmt"
	"regexp"
	"strconv"
	"strings"
)

type relBody struct {
	x, y, z float64
	sizeY   float64
	sizeXZ  float64
}

// expandRelations заменяет `near=`/`on=` вычисленным `pos`.
func expandRelations(src string) (string, []string) {
	lines := strings.Split(src, "\n")
	out := make([]string, len(lines))
	copy(out, lines)
	var warns []string
	known := map[string]relBody{}

	for i, line := range lines {
		t := strings.TrimSpace(line)
		if !strings.HasPrefix(t, "o3d ") {
			continue
		}
		id := gridID(t)
		near := fieldStr(t, "near")
		on := fieldStr(t, "on")

		if near == "" && on == "" {
			// Обычное тело — запоминаем, чтобы к нему могли привязаться.
			if x, y, z, ok := fieldPoint(t, "pos"); ok {
				known[id] = relBody{x: x, y: y, z: z,
					sizeY: sizeAxis(t, 1), sizeXZ: fieldSize(t)}
			}
			continue
		}

		anchor := near
		if anchor == "" {
			anchor = on
		}
		base, ok := known[anchor]
		if !ok {
			warns = append(warns, fmt.Sprintf(
				"line %d: тело «%s» привязано к «%s», которого ещё нет — привязка работает только к тому, что поставлено ВЫШЕ по тексту",
				i+1, id, anchor))
			continue
		}

		x, y, z := base.x, base.y, base.z
		if on != "" {
			// «На» — это поверх: поднимаем на высоту опоры. Так свеча встаёт
			// на надгробие, а не внутрь него.
			y = base.y + base.sizeY
		} else {
			dist := fieldNum(t, "dist")
			if dist == 0 {
				// По умолчанию — вплотную, но не внутрь: половина габарита
				// опоры плюс полметра воздуха.
				dist = base.sizeXZ*0.5 + 0.5
			}
			switch fieldStr(t, "side") {
			case "left", "слева":
				x -= dist
			case "right", "справа":
				x += dist
			case "back", "сзади", "behind":
				z += dist
			case "front", "спереди", "":
				z -= dist // ближе к камере: она смотрит вдоль +Z
			default:
				warns = append(warns, fmt.Sprintf(
					"line %d: сторона %q неизвестна — бывает left/right/front/back",
					i+1, fieldStr(t, "side")))
				z -= dist
			}
		}

		newLine := setField(line, "pos", fmt.Sprintf("%s,%s,%s",
			trimFloat(x), trimFloat(y), trimFloat(z)))
		newLine = dropFields(newLine, "near", "on", "dist", "side")
		out[i] = newLine
		known[id] = relBody{x: x, y: y, z: z, sizeY: sizeAxis(t, 1), sizeXZ: fieldSize(t)}
	}
	return strings.Join(out, "\n"), warns
}

// sizeAxis — одна ось размера («x,y,z»), 0 если не задан.
func sizeAxis(line string, axis int) float64 {
	v := fieldStr(line, "size")
	if v == "" {
		return 0
	}
	parts := strings.Split(v, ",")
	if len(parts) == 1 {
		f, _ := strconv.ParseFloat(strings.TrimSpace(parts[0]), 64)
		return f
	}
	if axis >= len(parts) {
		return 0
	}
	f, _ := strconv.ParseFloat(strings.TrimSpace(parts[axis]), 64)
	return f
}

// setField ставит или заменяет поле в строке команды.
func setField(line, key, value string) string {
	re := regexp.MustCompile(`\b` + key + `=("[^"]*"|\S+)`)
	if re.MatchString(line) {
		return re.ReplaceAllString(line, key+`="`+value+`"`)
	}
	return strings.TrimRight(line, " ") + fmt.Sprintf(` %s=%q`, key, value)
}

// dropFields убирает поля отношения: рантайм их не знает и знать не должен.
func dropFields(line string, keys ...string) string {
	for _, k := range keys {
		re := regexp.MustCompile(`\s*\b` + k + `=("[^"]*"|\S+)`)
		line = re.ReplaceAllString(line, "")
	}
	return line
}
