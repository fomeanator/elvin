package lvns

// Карты: место рисуется СИМВОЛАМИ в отдельном файле.
//
// Зачем отдельный формат, а не «расставьте объекты командами». Деревня из
// сорока домов — это сорок строк координат, которые невозможно ни написать, ни
// прочитать, ни поправить: сдвинул дом — пересчитывай соседей. Карта решает то
// же самое рисунком: автор ВИДИТ план сверху и правит его как текст.
//
//	tile 2
//	# = box   size="2,3,2" color=#5c6068
//	T = cone  size="1.6,5,1.6" color=#1d3526 shader=wind wind=0.15
//	~ = plane size="2,0.1,2" color=#2f5d6b shader=water
//	. = ground color=#3a4636
//	@ = camera
//
//	map:
//	###########
//	#....T....#
//	#..~~~..T.#
//	#....@....#
//	###########
//
// Разворачивается это в обычные команды `o3d`: по одной на символ, со списком
// точек в поле `at`. То есть карта — надстройка компилятора, а не новая
// сущность рантайма: сто деревьев остаются одним материалом и одним вызовом
// отрисовки, а движку не нужно знать, что такое карта.
//
// Начало координат — ЦЕНТР карты, ось X вправо, Z от зрителя вглубь. Так
// камера, поставленная в `@`, смотрит на середину плана, а не в угол.

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

type mapLegend struct {
	symbol rune
	kind   string            // ground / camera / имя формы или модели
	fields map[string]string // остальные поля идут в команду как есть
}

type parsedMap struct {
	tile    float64
	legend  map[rune]mapLegend
	rows    []string
	camera  *[2]float64 // место символа @, в метрах
	ordered []rune      // порядок объявления: команды выходят предсказуемо
}

// ExpandMap читает файл карты и возвращает СТРОКИ .lvns — обычные команды
// сцены. Карта, как и include, живёт во времени компиляции: рантайм о ней не
// знает, новых опов не появляется, цена — одна реализация вместо шести.
func ExpandMap(path, idPrefix string) ([]string, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("карта %s: %w", path, err)
	}
	m, err := parseMap(string(raw))
	if err != nil {
		return nil, fmt.Errorf("карта %s: %w", filepath.Base(path), err)
	}
	return m.lines(idPrefix), nil
}

func parseMap(src string) (*parsedMap, error) {
	m := &parsedMap{tile: 2, legend: map[rune]mapLegend{}}
	inGrid := false
	for _, line := range strings.Split(src, "\n") {
		trimmed := strings.TrimRight(line, "\r")
		if inGrid {
			// В сетке значим КАЖДЫЙ символ, включая пробелы: пробел — это
			// пустое место, а не отступ. Поэтому строку не подрезаем слева.
			if strings.TrimSpace(trimmed) == "" {
				continue
			}
			m.rows = append(m.rows, trimmed)
			continue
		}
		t := strings.TrimSpace(trimmed)
		if t == "" || strings.HasPrefix(t, "//") {
			continue
		}
		if t == "map:" || t == "карта:" {
			inGrid = true
			continue
		}
		if strings.HasPrefix(t, "tile ") || strings.HasPrefix(t, "клетка ") {
			v := strings.TrimSpace(t[strings.Index(t, " ")+1:])
			f, err := strconv.ParseFloat(v, 64)
			if err != nil || f <= 0 {
				return nil, fmt.Errorf("размер клетки: %q", v)
			}
			m.tile = f
			continue
		}
		// Строка легенды: «X = вид поле=значение …»
		eq := strings.Index(t, "=")
		if eq <= 0 {
			continue
		}
		symPart := strings.TrimSpace(t[:eq])
		if len([]rune(symPart)) != 1 {
			continue // не легенда — комментарий вида «# что-то»
		}
		sym := []rune(symPart)[0]
		rest := strings.TrimSpace(t[eq+1:])
		fields := parseMapFields(rest)
		kind := fields["__kind"]
		delete(fields, "__kind")
		m.legend[sym] = mapLegend{symbol: sym, kind: kind, fields: fields}
		m.ordered = append(m.ordered, sym)
	}
	if len(m.rows) == 0 {
		return nil, fmt.Errorf("нет сетки (ожидалась строка «map:» или «карта:»)")
	}
	return m, nil
}

// parseMapFields разбирает «cone size="1.6,5,1.6" color=#1d3526» в вид и поля.
func parseMapFields(s string) map[string]string {
	out := map[string]string{}
	first := true
	for _, tok := range splitMapTokens(s) {
		if first {
			out["__kind"] = tok
			first = false
			continue
		}
		if i := strings.Index(tok, "="); i > 0 {
			out[tok[:i]] = strings.Trim(tok[i+1:], `"`)
		}
	}
	return out
}

// splitMapTokens режет по пробелам, не трогая кавычки: size="1.6,5,1.6".
func splitMapTokens(s string) []string {
	var out []string
	var cur strings.Builder
	inQuote := false
	for _, r := range s {
		switch {
		case r == '"':
			inQuote = !inQuote
			cur.WriteRune(r)
		case r == ' ' && !inQuote:
			if cur.Len() > 0 {
				out = append(out, cur.String())
				cur.Reset()
			}
		default:
			cur.WriteRune(r)
		}
	}
	if cur.Len() > 0 {
		out = append(out, cur.String())
	}
	return out
}

func (m *parsedMap) lines(prefix string) []string {
	width := 0
	for _, r := range m.rows {
		if n := len([]rune(r)); n > width {
			width = n
		}
	}
	height := len(m.rows)
	// Центр карты — начало координат: камера в «@» смотрит на середину плана.
	offX := float64(width-1) / 2 * m.tile
	offZ := float64(height-1) / 2 * m.tile

	spots := map[rune][][2]float64{}
	for z, row := range m.rows {
		for x, sym := range []rune(row) {
			if sym == ' ' {
				continue
			}
			lg, ok := m.legend[sym]
			if !ok {
				continue // символ без легенды — пустое место, а не ошибка
			}
			wx := float64(x)*m.tile - offX
			wz := offZ - float64(z)*m.tile // первая строка — дальний край
			if lg.kind == "camera" {
				m.camera = &[2]float64{wx, wz}
				continue
			}
			spots[sym] = append(spots[sym], [2]float64{wx, wz})
		}
	}

	var out []string

	// Земля — ОДНА плоскость на всю карту, а не клетка под каждым символом:
	// тысяча квадратов вместо одного стоила бы тысячу вызовов отрисовки ровно
	// там, где видно один ровный пол.
	for _, sym := range m.ordered {
		lg := m.legend[sym]
		if lg.kind != "ground" {
			continue
		}
		line := fmt.Sprintf(`o3d id=%s_ground shape=plane pos="0,0,0" size="%g,1,%g"`,
			prefix, float64(width)*m.tile, float64(height)*m.tile)
		out = append(out, line+fieldsSuffix(lg.fields))
	}

	order := append([]rune(nil), m.ordered...)
	sort.SliceStable(order, func(i, j int) bool { return false }) // порядок объявления
	for _, sym := range order {
		lg := m.legend[sym]
		if lg.kind == "ground" || lg.kind == "camera" {
			continue
		}
		list := spots[sym]
		if len(list) == 0 {
			continue
		}
		at := make([]string, 0, len(list))
		for _, p := range list {
			at = append(at, fmt.Sprintf("%g,%g", p[0], p[1]))
		}
		// Вид: примитив из каталога форм или имя модели набора.
		what := "model=" + lg.kind
		if isShape(lg.kind) {
			what = "shape=" + lg.kind
		}
		line := fmt.Sprintf(`o3d id=%s_%s %s at="%s"`,
			prefix, symbolName(sym), what, strings.Join(at, ";"))
		out = append(out, line+fieldsSuffix(lg.fields))
	}

	// Камера ставится последней: сцена уже построена, ракурс встаёт на готовое.
	if m.camera != nil {
		out = append(out, fmt.Sprintf("bg3d build=1 x=%g y=1.7 z=%g pitch=2 fov=52",
			m.camera[0], m.camera[1]))
	}
	return out
}

// fieldsSuffix дописывает поля легенды к команде. Порядок стабильный —
// диффы карт должны быть читаемыми, а не перетасовываться при каждой сборке.
func fieldsSuffix(fields map[string]string) string {
	keys := make([]string, 0, len(fields))
	for k := range fields {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	var b strings.Builder
	for _, k := range keys {
		v := fields[k]
		if _, err := strconv.ParseFloat(v, 64); err == nil {
			fmt.Fprintf(&b, " %s=%s", k, v)
		} else {
			fmt.Fprintf(&b, " %s=%q", k, v)
		}
	}
	return b.String()
}

func isShape(kind string) bool {
	switch kind {
	case "box", "plane", "sphere", "cylinder", "cone", "disc":
		return true
	}
	return false
}

// symbolName даёт идентификатору читаемое имя: «#» → «wall1» не выйдет, но
// «map_35» всегда однозначно и не спорит с кириллицей в id.
func symbolName(r rune) string {
	if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') {
		return string(r)
	}
	return strconv.Itoa(int(r))
}
