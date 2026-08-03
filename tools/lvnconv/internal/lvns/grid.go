package lvns

// Сетка сцены: координаты в КЛЕТКАХ, а не в метрах.
//
// Зачем. Расставить десяток объектов в метрах человеку тяжело, а нейросети —
// почти невозможно: она не видит кадра и не может проверить, не воткнула ли
// склеп в дерево. Метры непрерывны, ошибиться в них можно на любую долю, и
// ошибка не ловится ничем.
//
// Клетки эту задачу превращают в арифметику, которую видно глазами:
//
//	grid 2
//	o3d id=склеп model=crypt-a pos="-3,0,6"     // клетка (−3, 6), то есть (−6, 12) м
//	o3d id=фонарь model=lantern pos="1,0,2"     // клетка (1, 2)
//	grid off
//
// Занятость проверяется на месте: два тела в одной клетке — предупреждение с
// именами обоих. Именно это и нужно автору-нейросети: не «выглядит криво», а
// «склеп и фонарь стоят в клетке (1, 2)».
//
// Внутри клетки координата остаётся дробной — «зернистость» никуда не делась:
// `pos="1.5,0,2.25"` это середина клетки по X и четверть по Z. Проверка
// занятости при этом смотрит на ЦЕЛУЮ часть, поэтому мелкая подстройка не
// поднимает ложных тревог.
//
// Это КОНСТРУКЦИЯ ВРЕМЕНИ КОМПИЛЯЦИИ (docs/adding-an-op.md): клетки
// разворачиваются в обычные метры до разбора, рантайм о сетке не знает, новых
// опов не появляется. Цена — две реализации, а не шесть.

import (
	"fmt"
	"math"
	"regexp"
	"strconv"
	"strings"
)

// reGrid — объявление сетки.
//
//	grid 2           клетка два метра
//	grid 2 sub 10    и внутри неё подсетка 10×10 — по 20 см
//	grid off         назад в метры
//
// Две ступени решают две разные задачи. КРУПНОЕ (склеп, дерево, дом) ставится
// клетками: их немного, они не должны пересекаться, и целые числа тут — вся
// нужная точность. МЕЛКОЕ (свеча на могиле, кружка на столе) клетками не
// поставить — оно всё окажется в одной. Подсетка даёт ему свой шаг, не ломая
// крупную расстановку: дробная часть координаты и есть номер подклетки.
var reGrid = regexp.MustCompile(`^\s*(?:grid|сетка)\s+(off|выкл|[0-9]*\.?[0-9]+)(?:\s+(?:sub|под)\s+([0-9]+))?\s*$`)

// Поля, которые сетка переводит из клеток в метры. Размеры (`size`, `height`)
// НЕ трогаем: рост объекта — это метры, и мерить его клетками неудобно даже
// там, где место мерится ими.
var gridPointFields = map[string]bool{"pos": true, "at": true}
var gridScalarFields = map[string]bool{"x": true, "y": true, "z": true, "gap": true}
var gridAreaFields = map[string]bool{"area": true}

type gridCell struct {
	x, z   int // клетка
	sx, sz int // подклетка внутри неё
	line   int
	id     string
}

// reSizeField — размер тела: по нему видно, крупное оно или мелочь.
var reSizeField = regexp.MustCompile(`\bsize="?([0-9]*\.?[0-9]+)`)

// isBigBody: тело шире половины клетки занимает её целиком. Так «склеп» и
// «свеча» проверяются по-разному без единого слова от автора — он и не должен
// объяснять компилятору, что склеп больше свечи.
func isBigBody(line string, cell float64) bool {
	m := reSizeField.FindStringSubmatch(line)
	if m == nil {
		return true // размер не задан — считаем крупным, так безопаснее
	}
	f, err := strconv.ParseFloat(m[1], 64)
	if err != nil {
		return true
	}
	return f >= cell*0.5
}

// footprintRadius — сколько клеток ВОКРУГ центра занимает тело. Ноль значит
// «только своя клетка»: так стоит всё, что не шире клетки.
func footprintRadius(line string, cell float64) int {
	m := reSizeField.FindStringSubmatch(line)
	if m == nil {
		return 0
	}
	f, err := strconv.ParseFloat(m[1], 64)
	if err != nil || f <= cell {
		return 0
	}
	return int(math.Floor((f/cell - 1) / 2))
}

// expandGrid переводит координаты из клеток в метры и возвращает предупреждения
// о телах, попавших в одну клетку.
func expandGrid(src string) (string, []string) {
	lines := strings.Split(src, "\n")
	out := make([]string, 0, len(lines))
	var warns []string
	taken := map[[2]int]gridCell{}     // крупные тела — по клеткам
	takenFine := map[[4]int]gridCell{} // мелочь — по подклеткам внутри клетки
	cell := 0.0                        // 0 — сетка выключена, координаты уже в метрах
	sub := 0                           // 0 — подсетки нет

	for i, line := range lines {
		if m := reGrid.FindStringSubmatch(line); m != nil {
			v := m[1]
			if v == "off" || v == "выкл" {
				cell = 0
				sub = 0
			} else {
				sub = 0
				if m[2] != "" {
					if n, err := strconv.Atoi(m[2]); err == nil && n > 1 {
						sub = n
					} else {
						warns = append(warns, fmt.Sprintf("line %d: подсетка %q должна быть целым больше единицы", i+1, m[2]))
					}
				}
				f, err := strconv.ParseFloat(v, 64)
				if err != nil || f <= 0 {
					warns = append(warns, fmt.Sprintf("line %d: размер клетки %q не число больше нуля", i+1, v))
				} else {
					cell = f
				}
			}
			out = append(out, "") // директива не оставляет команды
			continue
		}
		if cell <= 0 {
			out = append(out, line)
			continue
		}
		converted, cells := gridLine(line, cell, sub)
		out = append(out, converted)

		// Занятость: сообщаем о столкновении с именем СОСЕДА и номером строки,
		// чтобы правка была очевидной, а не «поищите где-то в сцене».
		id := gridID(line)
		big := isBigBody(line, cell)
		for _, c := range cells {
			if big || sub <= 0 {
				// Крупное тело занимает СВОЙ СЛЕД, а не одну клетку: склеп
				// шириной шесть метров при клетке два стоит на девяти клетках,
				// и его угол влезает в соседнюю незаметно для автора. Считаем
				// по габариту — тогда «занято» значит занято на самом деле.
				r := footprintRadius(line, cell)
				clash := false
				for dx := -r; dx <= r && !clash; dx++ {
					for dz := -r; dz <= r && !clash; dz++ {
						key := [2]int{c.x + dx, c.z + dz}
						if prev, busy := taken[key]; busy && prev.id != id {
							where := fmt.Sprintf("(%d, %d)", key[0], key[1])
							if dx != 0 || dz != 0 {
								where += " — она под соседним телом"
							}
							warns = append(warns, fmt.Sprintf(
								"line %d: клетка %s занята — «%s» (строка %d) и «%s»",
								i+1, where, prev.id, prev.line, id))
							clash = true
						}
					}
				}
				if clash {
					continue
				}
				for dx := -r; dx <= r; dx++ {
					for dz := -r; dz <= r; dz++ {
						taken[[2]int{c.x + dx, c.z + dz}] = gridCell{
							x: c.x + dx, z: c.z + dz, line: i + 1, id: id}
					}
				}
				continue
			}
			// Мелочь живёт в ПОДКЛЕТКЕ: две свечи на одной могиле — нормально,
			// две свечи в одной точке — нет.
			key := [4]int{c.x, c.z, c.sx, c.sz}
			if prev, busy := takenFine[key]; busy && prev.id != id {
				warns = append(warns, fmt.Sprintf(
					"line %d: подклетка (%d, %d)/(%d, %d) занята — «%s» (строка %d) и «%s»",
					i+1, c.x, c.z, c.sx, c.sz, prev.id, prev.line, id))
				continue
			}
			takenFine[key] = gridCell{x: c.x, z: c.z, line: i + 1, id: id}
		}
	}
	return strings.Join(out, "\n"), warns
}

var reIDField = regexp.MustCompile(`\bid=("[^"]*"|\S+)`)

func gridID(line string) string {
	if m := reIDField.FindStringSubmatch(line); m != nil {
		return strings.Trim(m[1], `"`)
	}
	return "без имени"
}

// gridLine переводит координатные поля одной строки и возвращает занятые клетки.
func gridLine(line string, cell float64, sub int) (string, []gridCell) {
	if !isGridCommand(line) {
		return line, nil
	}
	var cells []gridCell
	tokens := splitMapTokens(line)
	for i, tok := range tokens {
		eq := strings.Index(tok, "=")
		if eq <= 0 {
			continue
		}
		key := tok[:eq]
		val := strings.Trim(tok[eq+1:], `"`)
		switch {
		case gridPointFields[key]:
			conv, pts := scalePoints(val, cell, sub, key == "at")
			tokens[i] = fmt.Sprintf("%s=%q", key, conv)
			cells = append(cells, pts...)
		case gridAreaFields[key]:
			tokens[i] = fmt.Sprintf("%s=%q", key, scaleList(val, cell))
		case gridScalarFields[key]:
			if f, err := strconv.ParseFloat(val, 64); err == nil {
				tokens[i] = fmt.Sprintf("%s=%s", key, trimFloat(f*cell))
			}
		}
	}
	return strings.Join(tokens, " "), cells
}

func isGridCommand(line string) bool {
	t := strings.TrimSpace(line)
	for _, op := range []string{"o3d ", "bg3d ", "light "} {
		if strings.HasPrefix(t, op) {
			return true
		}
	}
	return false
}

// scalePoints умножает «x,y,z» или список «x,z;x,z» и собирает занятые клетки.
func scalePoints(v string, cell float64, sub int, isList bool) (string, []gridCell) {
	var cells []gridCell
	// Прилипание к подсетке: «1.47» при подсетке 10 становится «1.5». Без него
	// координаты копятся мусорными хвостами, и проверка занятости перестаёт
	// что-либо значить — каждое тело оказывается в своей уникальной точке.
	snap := func(f float64) float64 {
		if sub <= 0 {
			return f
		}
		return math.Round(f*float64(sub)) / float64(sub)
	}
	conv := func(one string) string {
		parts := strings.Split(one, ",")
		for i, p := range parts {
			f, err := strconv.ParseFloat(strings.TrimSpace(p), 64)
			if err != nil {
				return one
			}
			parts[i] = trimFloat(snap(f) * cell)
		}
		// Клетка — по ЦЕЛОЙ части: подстройка внутри клетки не считается
		// вторым объектом в ней.
		if len(parts) >= 2 {
			xs, _ := strconv.ParseFloat(strings.TrimSpace(strings.Split(one, ",")[0]), 64)
			zi := 1
			if len(parts) >= 3 {
				zi = 2 // «x,y,z» — глубина третья
			}
			zs, _ := strconv.ParseFloat(strings.TrimSpace(strings.Split(one, ",")[zi]), 64)
			cx, cz := int(math.Floor(xs)), int(math.Floor(zs))
			var sx, sz int
			if sub > 0 {
				sx = int(math.Round((xs - math.Floor(xs)) * float64(sub)))
				sz = int(math.Round((zs - math.Floor(zs)) * float64(sub)))
			}
			cells = append(cells, gridCell{x: cx, z: cz, sx: sx, sz: sz})
		}
		return strings.Join(parts, ",")
	}
	if !isList {
		return conv(v), cells
	}
	items := strings.Split(v, ";")
	for i, it := range items {
		items[i] = conv(it)
	}
	return strings.Join(items, ";"), cells
}

func scaleList(v string, cell float64) string {
	parts := strings.Split(v, ",")
	for i, p := range parts {
		f, err := strconv.ParseFloat(strings.TrimSpace(p), 64)
		if err != nil {
			return v
		}
		parts[i] = trimFloat(f * cell)
	}
	return strings.Join(parts, ",")
}

func trimFloat(f float64) string {
	// Округляем до десятых миллиметра: двоичная дробь даёт хвосты вида
	// «0.6000000000000001», и они попадают автору на глаза в каждом
	// сгенерированном скрипте. Точности сцены хватает с большим запасом.
	f = math.Round(f*10000) / 10000
	return strconv.FormatFloat(f, 'g', -1, 64)
}
