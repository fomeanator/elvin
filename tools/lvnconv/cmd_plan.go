package main

// `lvnconv plan` — ПЛАН СЦЕНЫ СВЕРХУ, нарисованный символами.
//
// Зачем отдельная команда. Автор-нейросеть не видит кадра: она пишет
// координаты вслепую и узнаёт об ошибке, только когда человек посмотрит на
// экран и скажет «склеп в дереве». Предупреждения о занятых клетках ловят
// прямые столкновения, но не отвечают на вопрос «а как оно вообще стоит».
//
// План отвечает: одна команда — и видно расстановку целиком, теми же клетками,
// какими она написана.
//
//	lvnconv plan -i scene.lvns
//
//	        -6 -4 -2  0  2  4  6
//	   12    .  с  .  .  .  .  .      с — склеп
//	    9    .  .  р  р  р  .  .      р — роща (20 копий)
//	    4    .  .  .  ф  .  .  .      ф — фонарь
//	    0    з  з  з  з  з  з  з      з — земля
//
// Это инструмент ВРЕМЕНИ РАЗРАБОТКИ, а не часть языка: он ничего не меняет в
// скрипте и ничего не добавляет в рантайм.

import (
	"encoding/json"
	"flag"
	"fmt"
	"math"
	"os"
	"regexp"
	"sort"
	"strconv"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

type planBody struct {
	id   string
	x, z float64
	list [][2]float64 // тела, расставленные по точкам (`at=`)
	num  int          // копий у посева
	size float64      // наибольший габарит: по нему видно, крупное тело или мелочь
}

func cmdPlan(args []string) error {
	fs := flag.NewFlagSet("plan", flag.ExitOnError)
	in := fs.String("i", "", "исходник .lvns")
	cellSize := fs.Float64("cell", 0, "размер клетки в метрах (0 — взять из директивы grid)")
	zoom := fs.String("zoom", "", "увеличить одну клетку: -zoom \"1,3\" покажет её подсетку")
	if err := fs.Parse(args); err != nil {
		return err
	}
	if *in == "" {
		return fmt.Errorf("нужен -i <файл.lvns>")
	}

	doc, err := lvns.ConvertFile(*in)
	if err != nil {
		return err
	}
	raw, err := json.Marshal(doc)
	if err != nil {
		return err
	}
	var parsed struct {
		Script []map[string]any `json:"script"`
	}
	if err := json.Unmarshal(raw, &parsed); err != nil {
		return err
	}

	cell := *cellSize
	if cell <= 0 {
		cell = gridFromSource(*in)
	}
	if cell <= 0 {
		cell = 2 // разумная клетка по умолчанию: шаг человека и ширина двери
	}

	var bodies []planBody
	var camera *planBody
	for _, c := range parsed.Script {
		op, _ := c["op"].(string)
		switch op {
		case "o3d":
			b := planBody{id: str(c["id"])}
			if at := str(c["at"]); at != "" {
				for _, one := range strings.Split(at, ";") {
					if p, ok := point2(one); ok {
						b.list = append(b.list, p)
					}
				}
			}
			if p, ok := point3(str(c["pos"])); ok {
				b.x, b.z = p[0], p[1]
			}
			if n, ok := c["count"].(float64); ok {
				b.num = int(n)
			}
			b.size = biggestSize(c["size"])
			bodies = append(bodies, b)
		case "bg3d":
			cx, _ := c["x"].(float64)
			cz, _ := c["z"].(float64)
			camera = &planBody{id: "камера", x: cx, z: cz}
		}
	}
	if len(bodies) == 0 {
		fmt.Println("в сцене нет тел (o3d) — плану нечего показывать")
		return nil
	}

	if *zoom != "" {
		// Увеличение одной клетки: макро-план показывает, ЧТО стоит на участке,
		// а этот — как разложена мелочь внутри него. Две задачи, два масштаба;
		// смешивать их в одной картинке бессмысленно — мелочь либо не видна,
		// либо тонет крупное.
		sub := subFromSource(*in)
		if sub <= 0 {
			sub = 10
		}
		cx, cz, ok := zoomCell(*zoom)
		if !ok {
			return fmt.Errorf("-zoom ждёт клетку вида \"1,3\"")
		}
		fmt.Printf("Клетка (%d, %d) сцены %s — подсетка %d×%d, шаг %g м\n\n",
			cx, cz, *in, sub, sub, cell/float64(sub))
		drawZoom(bodies, cell, sub, cx, cz)
		return nil
	}

	fmt.Printf("План сцены %s — клетка %g м\n\n", *in, cell)
	drawPlan(bodies, camera, cell)
	return nil
}

// gridFromSource достаёт размер клетки из директивы `grid N`, если она есть.
func gridFromSource(path string) float64 {
	data, err := os.ReadFile(path)
	if err != nil {
		return 0
	}
	for _, line := range strings.Split(string(data), "\n") {
		t := strings.TrimSpace(line)
		for _, p := range []string{"grid ", "сетка "} {
			if strings.HasPrefix(t, p) {
				if f, err := strconv.ParseFloat(strings.TrimSpace(t[len(p):]), 64); err == nil && f > 0 {
					return f
				}
			}
		}
	}
	return 0
}

// subFromSource достаёт размер подсетки из `grid N sub M`.
func subFromSource(path string) int {
	data, err := os.ReadFile(path)
	if err != nil {
		return 0
	}
	re := regexp.MustCompile(`(?m)^\s*(?:grid|сетка)\s+[0-9.]+\s+(?:sub|под)\s+([0-9]+)`)
	if m := re.FindStringSubmatch(string(data)); m != nil {
		if n, err := strconv.Atoi(m[1]); err == nil {
			return n
		}
	}
	return 0
}

func zoomCell(s string) (int, int, bool) {
	parts := strings.Split(s, ",")
	if len(parts) != 2 {
		return 0, 0, false
	}
	x, e1 := strconv.Atoi(strings.TrimSpace(parts[0]))
	z, e2 := strconv.Atoi(strings.TrimSpace(parts[1]))
	return x, z, e1 == nil && e2 == nil
}

// drawZoom — подсетка ОДНОЙ клетки: что где лежит внутри участка.
func drawZoom(bodies []planBody, cell float64, sub, cx, cz int) {
	type mark struct {
		sx, sz int
		id     string
	}
	var inside []mark
	for _, b := range bodies {
		pts := b.list
		if len(pts) == 0 {
			pts = [][2]float64{{b.x, b.z}}
		}
		for _, p := range pts {
			// В какой клетке лежит точка и куда попадает внутри неё.
			gx := math.Floor(p[0] / cell)
			gz := math.Floor(p[1] / cell)
			if int(gx) != cx || int(gz) != cz {
				continue
			}
			fx := (p[0]/cell - gx) * float64(sub)
			fz := (p[1]/cell - gz) * float64(sub)
			inside = append(inside, mark{sx: int(fx), sz: int(fz), id: b.id})
		}
	}
	if len(inside) == 0 {
		fmt.Println("в этой клетке ничего не стоит")
		return
	}

	letters := map[string]rune{}
	used := map[rune]bool{'#': true, '·': true}
	next := 'а'
	grid := map[[2]int]rune{}
	for _, m := range inside {
		r, ok := letters[m.id]
		if !ok {
			for _, c := range m.id {
				if !used[c] {
					r = c
					break
				}
			}
			if r == 0 {
				for used[next] {
					next++
				}
				r = next
				next++
			}
			used[r] = true
			letters[m.id] = r
		}
		key := [2]int{m.sx, m.sz}
		if have, busy := grid[key]; busy && have != r {
			grid[key] = '#'
			continue
		}
		grid[key] = r
	}

	var head strings.Builder
	head.WriteString("     ")
	for x := 0; x < sub; x++ {
		head.WriteString(fmt.Sprintf("%3d", x))
	}
	fmt.Println(head.String())
	for z := sub - 1; z >= 0; z-- {
		var row strings.Builder
		row.WriteString(fmt.Sprintf("%4d ", z))
		for x := 0; x < sub; x++ {
			if ch, ok := grid[[2]int{x, z}]; ok {
				row.WriteString(fmt.Sprintf("  %c", ch))
			} else {
				row.WriteString("  ·")
			}
		}
		fmt.Println(row.String())
	}
	fmt.Println("\nЛегенда:")
	ids := make([]string, 0, len(letters))
	for id := range letters {
		ids = append(ids, id)
	}
	sort.Strings(ids)
	for _, id := range ids {
		fmt.Printf("  %c — %s\n", letters[id], id)
	}
}

// biggestSize — габарит тела В ПЛАНЕ, то есть по осям X и Z.
//
// Высоту в расчёт не берём намеренно: план — вид сверху, и венок «0.5,1,0.5»
// занимает полметра, а не метр. Со счётом по наибольшей стороне он попадал в
// крупные и давал ложное столкновение с надгробием, на котором лежит.
func biggestSize(v any) float64 {
	s, _ := v.(string)
	if s == "" {
		if f, ok := v.(float64); ok {
			return f
		}
		return 0
	}
	parts := strings.Split(s, ",")
	best := 0.0
	for i, p := range parts {
		if len(parts) >= 3 && i == 1 {
			continue // средняя компонента — высота
		}
		if f, err := strconv.ParseFloat(strings.TrimSpace(p), 64); err == nil && f > best {
			best = f
		}
	}
	return best
}

func drawPlan(bodies []planBody, camera *planBody, cell float64) {
	type mark struct {
		x, z int
		ch   rune
		id   string
	}
	var marks []mark
	letters := map[string]rune{}
	next := 'а'

	used := map[rune]bool{'@': true, '#': true, '·': true}
	letterFor := func(id string) rune {
		if r, ok := letters[id]; ok {
			return r
		}
		// Первая буква имени — самая читаемая подпись. Но «склеп» и «скамья»
		// начинаются одинаково, и одна буква на двоих делает план ложью:
		// перебираем следующие буквы имени, потом алфавит.
		var r rune
		for _, c := range id {
			if !used[c] {
				r = c
				break
			}
		}
		if r == 0 {
			for next <= 'я' && used[next] {
				next++
			}
			r = next
			next++
		}
		used[r] = true
		letters[id] = r
		return r
	}

	add := func(id string, x, z float64) {
		marks = append(marks, mark{
			x: int(math.Floor(x / cell)), z: int(math.Floor(z / cell)),
			ch: letterFor(id), id: id,
		})
	}
	// МАКРО-план показывает только крупное. Свечи и монеты, разложенные
	// подклетками, здесь дали бы столкновение с надгробием, на котором стоят —
	// то есть ложную тревогу там, где всё правильно. Мелочь живёт в увеличении
	// (-zoom), а на общем плане клетка с ней помечается точкой.
	small := map[[2]int]bool{}
	for _, b := range bodies {
		pts := b.list
		if len(pts) == 0 {
			pts = [][2]float64{{b.x, b.z}}
		}
		if b.size > 0 && b.size < cell*0.5 {
			for _, p := range pts {
				small[[2]int{int(math.Floor(p[0] / cell)), int(math.Floor(p[1] / cell))}] = true
			}
			continue
		}
		for _, p := range pts {
			add(b.id, p[0], p[1])
		}
	}
	if camera != nil {
		marks = append(marks, mark{x: int(math.Floor(camera.x / cell)),
			z: int(math.Floor(camera.z / cell)), ch: '@', id: "камера"})
	}

	minX, maxX, minZ, maxZ := 0, 0, 0, 0
	// Границы считаем и по мелочи: иначе фонарь у края сцены просто исчезает
	// с плана, и автор считает, что забыл его поставить.
	first := true
	for key := range small {
		if first {
			minX, maxX, minZ, maxZ = key[0], key[0], key[1], key[1]
			first = false
		}
		if key[0] < minX {
			minX = key[0]
		}
		if key[0] > maxX {
			maxX = key[0]
		}
		if key[1] < minZ {
			minZ = key[1]
		}
		if key[1] > maxZ {
			maxZ = key[1]
		}
	}
	for i, m := range marks {
		if i == 0 && first {
			minX, maxX, minZ, maxZ = m.x, m.x, m.z, m.z
		}
		if m.x < minX {
			minX = m.x
		}
		if m.x > maxX {
			maxX = m.x
		}
		if m.z < minZ {
			minZ = m.z
		}
		if m.z > maxZ {
			maxZ = m.z
		}
	}
	// Поля вокруг: план читается лучше, когда край сцены не прижат к рамке.
	minX--
	maxX++
	minZ--
	maxZ++

	grid := map[[2]int]rune{}
	for key := range small {
		grid[key] = '\u02d9' // в клетке лежит мелочь — смотри её через -zoom
	}
	for _, m := range marks {
		key := [2]int{m.x, m.z}
		if have, busy := grid[key]; busy && have != m.ch && have != '\u02d9' {
			grid[key] = '#' // столкновение — видно сразу
			continue
		}
		grid[key] = m.ch
	}

	// Шапка с номерами клеток по X.
	var head strings.Builder
	head.WriteString("      ")
	for x := minX; x <= maxX; x++ {
		head.WriteString(fmt.Sprintf("%3d", x))
	}
	fmt.Println(head.String())

	// Дальние клетки сверху — как на карте местности.
	for z := maxZ; z >= minZ; z-- {
		var row strings.Builder
		row.WriteString(fmt.Sprintf("%5d ", z))
		for x := minX; x <= maxX; x++ {
			if ch, ok := grid[[2]int{x, z}]; ok {
				row.WriteString(fmt.Sprintf("  %c", ch))
			} else {
				row.WriteString("  ·")
			}
		}
		fmt.Println(row.String())
	}

	fmt.Println("\nЛегенда:")
	ids := make([]string, 0, len(letters))
	for id := range letters {
		ids = append(ids, id)
	}
	sort.Strings(ids)
	for _, id := range ids {
		big := false
		for _, b := range bodies {
			if b.id == id && (b.size == 0 || b.size >= cell*0.5) {
				big = true
			}
		}
		if !big {
			continue // мелочь подписана в увеличении, здесь она только шум
		}
		count := ""
		for _, b := range bodies {
			if b.id != id {
				continue
			}
			if b.num > 1 {
				count = fmt.Sprintf(" (%d копий, разбросаны)", b.num)
			} else if len(b.list) > 1 {
				count = fmt.Sprintf(" (%d по точкам)", len(b.list))
			}
		}
		fmt.Printf("  %c — %s%s\n", letters[id], id, count)
	}
	if camera != nil {
		fmt.Println("  @ — камера")
	}
	if len(small) > 0 {
		fmt.Println("  ˙ — в клетке есть мелочь: посмотрите её через -zoom \"x,z\"")
	}
	fmt.Println("  # — ДВА КРУПНЫХ ТЕЛА В ОДНОЙ КЛЕТКЕ")
}

func str(v any) string {
	s, _ := v.(string)
	return s
}

func point2(s string) ([2]float64, bool) {
	parts := strings.Split(s, ",")
	if len(parts) < 2 {
		return [2]float64{}, false
	}
	x, e1 := strconv.ParseFloat(strings.TrimSpace(parts[0]), 64)
	z, e2 := strconv.ParseFloat(strings.TrimSpace(parts[1]), 64)
	return [2]float64{x, z}, e1 == nil && e2 == nil
}

func point3(s string) ([2]float64, bool) {
	parts := strings.Split(s, ",")
	if len(parts) < 3 {
		return point2(s)
	}
	x, e1 := strconv.ParseFloat(strings.TrimSpace(parts[0]), 64)
	z, e2 := strconv.ParseFloat(strings.TrimSpace(parts[2]), 64)
	return [2]float64{x, z}, e1 == nil && e2 == nil
}
