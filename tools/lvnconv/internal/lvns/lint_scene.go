package lvns

// Проверки СЦЕНЫ — то, что автор-нейросеть не может увидеть сам.
//
// У языка уже есть валидатор структуры: он ловит висячие переходы, дубли меток,
// неизвестные поля. Но самые дорогие ошибки в 3D — другого рода: скрипт
// корректен, компилируется, играет — и показывает пустой кадр. Модель не видит
// экрана и узнаёт об этом последней, от человека.
//
// Здесь ловится ровно этот класс: сцена собрана правильно, но смотреть в ней
// не на что.
//
//	· строка похожа на команду, но ею не является — она напечатается репликой;
//	· тело поставлено дважды под одним именем — второе молча заменит первое;
//	· тело стоит за туманом или позади камеры — его не будет видно;
//	· в сцене нет ни одного источника света — чёрный кадр;
//	· размер тела не похож ни на что настоящее — дерево в двадцать сантиметров.
//
// Всё это — предупреждения, а не ошибки: автор вправе сделать сцену, где герой
// стоит за спиной камеры. Но он должен об этом узнать до того, как это увидит
// игрок.

import (
	"fmt"
	"math"
	"regexp"
	"strconv"
	"strings"
)

type sceneBody struct {
	id      string
	line    int
	x, y, z float64
	size    float64
	hasPos  bool
}

// SetModels — состав известных наборов: id набора → имена его объектов.
// Заполняется из манифеста (`lvnconv convert -sets manifest.json`); пустая
// карта просто отключает проверку имён моделей.
var SetModels = map[string][]string{}

// LintScene возвращает предупреждения по исходнику .lvns.
func LintScene(src string) []string {
	var warns []string
	lines := strings.Split(src, "\n")

	seen := map[string]int{} // id → строка первой постановки
	var bodies []sceneBody
	var camera *sceneBody
	fogFar := 0.0
	lights := 0
	sunPower := 0.0
	palette := map[string]struct{}{}
	copies := 0
	setID := "" // какой набор стоит: по нему сверяются имена моделей

	for i, raw := range lines {
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "//") {
			continue
		}
		n := i + 1
		// «// lvn-ok» снимает со строки все проверки разом: автор сказал, что
		// знает, что делает, и спорить с ним по каждому полю бессмысленно.
		if lintSuppressed(line) {
			// Но состояние всё равно копим: тело нужно помнить, чтобы
			// проверить ОСТАЛЬНЫЕ на пересечение и видимость.
			if firstWord(line) == "o3d" {
				b := sceneBody{id: gridID(line), line: n, size: fieldSize(line)}
				if x, y, z, ok := fieldPoint(line, "pos"); ok {
					b.x, b.y, b.z, b.hasPos = x, y, z, true
				}
				bodies = append(bodies, b)
			}
			continue
		}

		if w := lintLooksLikeCommand(line, n); w != "" {
			warns = append(warns, w)
		}

		op := firstWord(line)
		switch op {
		case "o3d":
			id := gridID(line)
			// ПОСТАНОВКА или ИЗМЕНЕНИЕ. `o3d id=дверь yaw=90 dur=1` — вторая
			// команда по тому же телу, и это норма: так его двигают. Тело
			// СОЗДАЁТ только команда, которая говорит, ЧЕМ оно является
			// (shape/model/sprite). Без этого различия предупреждения сыпались
			// бы на каждую анимацию, и их перестали бы читать.
			creates := strings.Contains(line, "shape=") ||
				strings.Contains(line, "model=") ||
				strings.Contains(line, "sprite=")
			if id != "без имени" && creates {
				if prev, dup := seen[id]; dup {
					// Второй `o3d` с тем же id НЕ ставит второе тело — он
					// двигает первое. Модель повторяет имена охотно («камень»,
					// «камень», «камень»), и сцена молча теряет объекты.
					warns = append(warns, fmt.Sprintf(
						"line %d: тело «%s» уже поставлено в строке %d — эта команда его ПЕРЕДВИНЕТ, а не добавит второе. Дайте другое имя",
						n, id, prev))
				} else {
					seen[id] = n
				}
			}
			b := sceneBody{id: id, line: n, size: fieldSize(line)}
			if x, y, z, ok := fieldPoint(line, "pos"); ok {
				b.x, b.y, b.z, b.hasPos = x, y, z, true
			}
			bodies = append(bodies, b)
			if w := lintSize(b, line); w != "" {
				warns = append(warns, w)
			}
			if w := lintColor(line, n); w != "" {
				warns = append(warns, w)
			}
			if w := lintModelName(line, n, setID); w != "" {
				warns = append(warns, w)
			}
			if w := lintNoColour(line, n, id); w != "" {
				warns = append(warns, w)
			}
			warns = append(warns, lintLimits(line, n, "o3d", id)...)
			// Каждый ОТДЕЛЬНЫЙ цвет — свой материал, то есть свой вызов
			// отрисовки. Палитра из сорока оттенков стоит дороже, чем кажется.
			if c := fieldStr(line, "color"); c != "" {
				palette[c] = struct{}{}
			}
			if n := fieldNum(line, "count"); n > 0 {
				copies += int(n)
			} else {
				copies++
			}
		case "bg3d":
			warns = append(warns, lintLimits(line, n, "bg3d", "камера")...)
			if id := fieldStr(line, "id"); id != "" && id != "off" {
				setID = id
			}
			c := sceneBody{line: n}
			c.x = fieldNum(line, "x")
			c.y = fieldNum(line, "y")
			c.z = fieldNum(line, "z")
			if strings.Contains(line, "x=") || strings.Contains(line, "z=") {
				camera = &c
			}
		case "light":
			warns = append(warns, lintLimits(line, n, "light", fieldStr(line, "kind"))...)
			kind := fieldStr(line, "kind")
			switch kind {
			case "fog":
				fogFar = fieldNum(line, "far")
			case "sky":
				// небо светит через ambient, но само по себе кадр не спасает
			default:
				lights++
				if kind == "sun" || kind == "" {
					sunPower += fieldNum(line, "power")
				}
			}
		}
	}

	if len(bodies) > 0 && lights == 0 {
		warns = append(warns, "в сцене нет ни одного источника света (light kind=sun/fill/point) — кадр будет чёрным")
	}
	if len(bodies) > 0 && lights > 0 && sunPower == 0 {
		warns = append(warns, "нет главного света (light kind=sun) — тени в сцене не появятся")
	}

	// Бюджет сцены. Числа — из правил наборов (docs/3d-set-rules.md): выше них
	// кадр начинает стоить заметно, и узнать об этом лучше при сборке, а не на
	// телефоне у игрока.
	if copies > 400 {
		warns = append(warns, fmt.Sprintf(
			"в сцене %d тел — больше четырёхсот. На слабом телефоне это заметно; посевом (count=) они стоили бы дешевле",
			copies))
	}
	if len(palette) > 24 {
		warns = append(warns, fmt.Sprintf(
			"в сцене %d разных цветов — каждый это свой материал и свой вызов отрисовки. Короткая палитра и дешевле, и красивее",
			len(palette)))
	}
	if camera != nil {
		warns = append(warns, lintVisibility(bodies, *camera, fogFar)...)
	}
	return warns
}

// sourceDirectives — конструкции ИСХОДНИКА: они не опы (рантайм их не видит),
// но и не проза. Пропустить их через проверку «похоже на команду» значит
// ругаться на исправный код — а ложная тревога стоит дороже пропущенной:
// после первой же автор перестаёт читать предупреждения.
var sourceDirectives = map[string]bool{
	"grid": true, "сетка": true, "map": true, "карта": true,
	"include": true, "func": true, "actor_map": true, "cast": true,
	"defanim": true, "deps": true, "weave": true, "scene": true,
	"ext": true, "move": true, "play": true, "voice": true,
	"for": true, "while": true, "if": true, "else": true, "return": true,
}

// reCommandish — строка, которая ВЫГЛЯДИТ командой: слово, за ним поля вида
// ключ=значение. Настоящая команда сюда не попадает (её op известен), а вот
// опечатка — попадает, и без этой проверки она молча печатается игроку.
var reCommandish = regexp.MustCompile(`^([A-Za-zА-Яа-я_][A-Za-zА-Яа-я0-9_]*)\s+[a-zA-Zа-яА-Я_]+=`)

func lintLooksLikeCommand(line string, n int) string {
	m := reCommandish.FindStringSubmatch(line)
	if m == nil {
		return ""
	}
	word := m[1]
	if KnownOps[word] || sourceDirectives[word] {
		return ""
	}
	// Реплика с двоеточием — не команда: «Аня: правда=ложь» законна.
	if i := strings.Index(line, ":"); i > 0 && i < strings.Index(line, "=") {
		return ""
	}
	return fmt.Sprintf(
		"line %d: «%s» — не команда языка, эта строка НАПЕЧАТАЕТСЯ ИГРОКУ как реплика. Опечатка в имени команды?",
		n, word)
}

// lintSize: размеры, не похожие ни на что настоящее. Пределы намеренно
// широкие — сцена новеллы бывает какой угодно, но три порядка мимо это всегда
// ошибка перевода единиц, а не замысел.
func lintSize(b sceneBody, line string) string {
	if b.size <= 0 {
		return ""
	}
	if b.size < 0.02 {
		return fmt.Sprintf("line %d: тело «%s» размером %.3g м — меньше двух сантиметров, его не будет видно. Размер задаётся в МЕТРАХ",
			b.line, b.id, b.size)
	}
	if b.size > 200 && !strings.Contains(line, "shape=plane") {
		return fmt.Sprintf("line %d: тело «%s» размером %.4g м — больше двухсот метров. Размер задаётся в МЕТРАХ",
			b.line, b.id, b.size)
	}
	return ""
}

// reHexColor — цвет, который движок действительно понимает.
var reHexColor = regexp.MustCompile(`^#?[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$`)

// lintColor: «red» вместо «#ff0000» молча игнорируется — тело остаётся белым,
// и автор списывает это на освещение.
func lintColor(line string, n int) string {
	for _, key := range []string{"color", "colors", "outline_color", "top", "bottom"} {
		v := fieldStr(line, key)
		if v == "" {
			continue
		}
		for _, one := range strings.Split(v, ",") {
			one = strings.TrimSpace(one)
			if one == "" || reHexColor.MatchString(one) {
				continue
			}
			return fmt.Sprintf(
				"line %d: %s=%q — цвет пишется как #rrggbb («красный» и «red» движок не понимает, тело останется белым)",
				n, key, one)
		}
	}
	return ""
}

// lintModelName: ссылка на объект, которого в наборе нет. Тело при этом молча
// не встаёт — ни ошибки, ни следа в логе, автор ищет причину в свете и камере.
func lintModelName(line string, n int, setID string) string {
	if setID == "" {
		return ""
	}
	known, ok := SetModels[setID]
	if !ok || len(known) == 0 {
		return "" // про этот набор мы ничего не знаем — молчим
	}
	var names []string
	if v := fieldStr(line, "model"); v != "" {
		names = append(names, v)
	}
	if v := fieldStr(line, "kinds"); v != "" {
		for _, one := range strings.Split(v, ",") {
			names = append(names, strings.TrimSpace(one))
		}
	}
	for _, name := range names {
		if name == "" || isShapeName(name) {
			continue
		}
		found := false
		for _, k := range known {
			if k == name {
				found = true
				break
			}
		}
		if found {
			continue
		}
		msg := fmt.Sprintf("line %d: в наборе «%s» нет модели %q — тело не встанет", n, setID, name)
		if near := closestName(name, known); near != "" {
			msg += fmt.Sprintf(". Может быть, %q?", near)
		}
		return msg
	}
	return ""
}

func isShapeName(s string) bool {
	switch s {
	case "box", "plane", "sphere", "cylinder", "cone", "disc",
		"ground", "земля", "terrain":
		return true
	}
	return false
}

// closestName — ближайшее имя по расстоянию редактирования. Подсказка важнее
// самой ошибки: «нет модели crypt-x» без «может быть, crypt-a?» заставляет
// автора идти читать состав набора, которого он не видит.
func closestName(want string, known []string) string {
	best, bestD := "", 1<<30
	for _, k := range known {
		d := editDistance(want, k)
		if d < bestD {
			best, bestD = k, d
		}
	}
	if bestD > len(want)/2+1 {
		return "" // слишком далеко — подсказка была бы вредной
	}
	return best
}

// lintNoColour: примитив без цвета и текстуры выходит БЕЛЫМ. Это честное
// поведение (материал по умолчанию белый), но почти никогда не замысел: белое
// дерево посреди леса читается как сбой, а не как решение автора.
func lintNoColour(line string, n int, id string) string {
	if fieldStr(line, "shape") == "" {
		return "" // модель приносит свои материалы, спрайт — свою картинку
	}
	if fieldStr(line, "color") != "" || fieldStr(line, "colors") != "" ||
		fieldStr(line, "texture") != "" {
		return ""
	}
	// Огонь, дым и аура красят себя сами — им цвет не нужен.
	switch fieldStr(line, "shader") {
	case "fire", "smoke", "aura", "water", "glass":
		return ""
	}
	return fmt.Sprintf(
		"line %d: у тела «%s» нет ни цвета, ни текстуры — оно будет БЕЛЫМ. Добавьте color=\"#rrggbb\"",
		n, id)
}

// lintVisibility: что стоит вне кадра — за спиной камеры или за туманом.
func lintVisibility(bodies []sceneBody, cam sceneBody, fogFar float64) []string {
	var warns []string
	for _, b := range bodies {
		if !b.hasPos {
			continue
		}
		dz := b.z - cam.z
		dist := math.Sqrt((b.x-cam.x)*(b.x-cam.x) + dz*dz)

		// Камера смотрит вдоль +Z: тело с меньшим z за спиной.
		if dz < -0.5 {
			warns = append(warns, fmt.Sprintf(
				"line %d: тело «%s» ПОЗАДИ камеры (z=%.4g при камере z=%.4g) — в кадр оно не попадёт",
				b.line, b.id, b.z, cam.z))
			continue
		}
		if fogFar > 0 && dist > fogFar*1.15 {
			warns = append(warns, fmt.Sprintf(
				"line %d: тело «%s» в %.3g м, а туман смыкается на %.3g м — его не будет видно",
				b.line, b.id, dist, fogFar))
		}
		if dist < 0.6 && b.size > 0.5 {
			warns = append(warns, fmt.Sprintf(
				"line %d: тело «%s» в %.2g м от камеры — она окажется внутри него",
				b.line, b.id, dist))
		}
	}
	return warns
}

// --- разбор полей строки ---------------------------------------------------

func firstWord(line string) string {
	f := strings.Fields(line)
	if len(f) == 0 {
		return ""
	}
	return f[0]
}

func fieldStr(line, key string) string {
	re := regexp.MustCompile(`\b` + key + `=("[^"]*"|\S+)`)
	if m := re.FindStringSubmatch(line); m != nil {
		return strings.Trim(m[1], `"`)
	}
	return ""
}

func fieldNum(line, key string) float64 {
	v := fieldStr(line, key)
	f, err := strconv.ParseFloat(v, 64)
	if err != nil {
		return 0
	}
	return f
}

func fieldPoint(line, key string) (float64, float64, float64, bool) {
	v := fieldStr(line, key)
	parts := strings.Split(v, ",")
	if len(parts) < 3 {
		return 0, 0, 0, false
	}
	x, e1 := strconv.ParseFloat(strings.TrimSpace(parts[0]), 64)
	y, e2 := strconv.ParseFloat(strings.TrimSpace(parts[1]), 64)
	z, e3 := strconv.ParseFloat(strings.TrimSpace(parts[2]), 64)
	return x, y, z, e1 == nil && e2 == nil && e3 == nil
}

// fieldSize — габарит тела в плане (без высоты), как и в плане сцены.
func fieldSize(line string) float64 {
	v := fieldStr(line, "size")
	if v == "" {
		return 0
	}
	parts := strings.Split(v, ",")
	best := 0.0
	for i, p := range parts {
		if len(parts) >= 3 && i == 1 {
			continue
		}
		if f, err := strconv.ParseFloat(strings.TrimSpace(p), 64); err == nil && f > best {
			best = f
		}
	}
	return best
}
