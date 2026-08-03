package lvns

// include — подключение одного .lvns-файла в другой.
//
// Зачем: большая игра пишется главами, а таблицы, функции и пресеты постановки
// у них общие. Без include общий кусок копируется в каждую главу, и любая
// правка требует обойти все копии — то есть расходится ровно так же, как
// расходились шесть зеркал словаря опов.
//
// Это КОНСТРУКЦИЯ ВРЕМЕНИ КОМПИЛЯЦИИ (docs/adding-an-op.md): подстановка
// происходит до разбора, рантайм о ней не знает, новых опов не появляется.
// Поэтому цена — две реализации (этот компилятор и его C#-порт), а не шесть.
//
// Решения, которые стоило принять явно:
//
//   - путь резолвится относительно ПОДКЛЮЧАЮЩЕГО файла, а не рабочей
//     директории: иначе одна и та же глава компилируется по-разному из разных
//     мест, и это находят в самый неудобный момент;
//   - повторное подключение ИДЕМПОТЕНТНО (второй раз пропускается). Текстовая
//     подстановка дважды внесла бы дубли меток, а дубль метки — ошибка
//     валидатора. Ромб A→B, A→C, B→D, C→D обязан работать;
//   - цикл — ошибка с полной цепочкой файлов, а не переполнение стека;
//   - номера строк остаются честными: ведётся карта «строка склейки → файл и
//     строка в нём», и сообщение об ошибке переписывается на границе Convert.

import (
	"fmt"
	"os"
	"path"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
)

// reInclude — директива подключения. Путь только в кавычках: без них пробел в
// имени файла разобрался бы как второй аргумент и ошибка была бы невнятной.
var reInclude = regexp.MustCompile(`^\s*include\s+"([^"]+)"\s*$`)

// reMap — подключение КАРТЫ: «map "maps/деревня.lvnmap"» или с именем
// «map "maps/деревня.lvnmap" as деревня». Имя становится приставкой к id тел,
// чтобы две карты в одной главе не спорили за одинаковые идентификаторы.
var reMap = regexp.MustCompile(`^\s*map\s+"([^"]+)"(?:\s+as\s+([^\s]+))?\s*$`)

// srcRef — откуда пришла строка склеенного источника.
type srcRef struct {
	File string // путь как его написал автор (для сообщений), "" для корневого файла
	Line int    // номер строки в ЭТОМ файле, начиная с 1
}

// incLoader — откуда берутся подключаемые файлы. Их ДВА источника, и оба
// настоящие: диск (CLI, сервер) и набор открытых в редакторе буферов (веб-IDE и
// плейграунд компилируются в браузере, там файловой системы нет вовсе). Без
// второго include в студии просто не работает: редактор передаёт компилятору
// текст, а текст не знает, относительно чего резолвить путь — ровно эта ошибка
// и висела в IDE на строке с include.
type incLoader interface {
	// load отдаёт содержимое подключаемого файла, его КЛЮЧ (по нему считаются
	// повторы и циклы) и «каталог» для вложенных подключений.
	load(dir, rel string) (key, content, nextDir string, err error)
	// where — как назвать место поиска в сообщении об ошибке.
	where(dir, rel string) string
}

// diskLoader — обычные файлы.
type diskLoader struct{}

func (diskLoader) load(dir, rel string) (string, string, string, error) {
	abs := resolveDiskPath(dir, rel)
	data, err := os.ReadFile(abs)
	return abs, string(data), filepath.Dir(abs), err
}

func (diskLoader) where(dir, rel string) string {
	return resolveDiskPath(dir, rel)
}

// resolveDiskPath — путь include на диске. Обычный путь — относительно
// подключающего файла. Путь на "@" — ПАКЕТ (`include "@scope/pkg/file.lvns"`):
// ищется каталог lvns_packages/ вверх по дереву от подключающего файла — так
// пакет находится и из глубины проекта, и из scripts/ на сервере, и из самого
// vendor-каталога (пакет подключает пакет). Скачивает и проверяет пакеты
// `lvnconv deps sync` (internal/deps); компилятор в сеть не ходит никогда.
func resolveDiskPath(dir, rel string) string {
	if !strings.HasPrefix(rel, "@") {
		return filepath.Clean(filepath.Join(dir, filepath.FromSlash(rel)))
	}
	for d := filepath.Clean(dir); ; d = filepath.Dir(d) {
		cand := filepath.Join(d, "lvns_packages", filepath.FromSlash(rel))
		if _, err := os.Stat(cand); err == nil {
			return cand
		}
		if d == filepath.Dir(d) { // корень ФС
			// не нашли — вернуть «ожидаемое» место рядом с проектом, чтобы
			// сообщение об ошибке говорило, ГДЕ искали
			return filepath.Join(filepath.Clean(dir), "lvns_packages", filepath.FromSlash(rel))
		}
	}
}

// memLoader — открытые буферы редактора. Ключ обычного файла это ИМЯ: в студии
// все скрипты новеллы лежат рядом, и подкаталоги пришлось бы выдумывать на
// стороне UI. У ПАКЕТА (см. deps) ключ — полный путь "@scope/pkg/file.lvns":
// имя файла внутри пакета не уникально (два пакета вправе иметь свой
// duel.lvns), а срезав путь до имени, редактор искал бы чужой файл — и не
// находил бы никакого, как только плоская копия пакета исчезала.
type memLoader struct{ files map[string]string }

func (m memLoader) load(dir, rel string) (string, string, string, error) {
	key := memKey(dir, rel)
	c, ok := m.files[key]
	if !ok {
		// Файл пакета мог прийти и под плоским именем (старая студия хранила
		// библиотеки рядом) — принимаем оба, чтобы правка include не требовала
		// одновременного обновления и редактора, и сервера.
		if alt := path.Base(rel); alt != key {
			if c, ok = m.files[alt]; ok {
				return alt, c, "", nil
			}
		}
		return key, "", "", fmt.Errorf("нет такого файла")
	}
	// Внутри пакета относительный include — сосед по каталогу пакета.
	next := ""
	if strings.HasPrefix(key, "@") {
		next = path.Dir(key)
	}
	return key, c, next, nil
}

// memKey — ключ буфера: пакетный путь целиком, обычный — по имени файла,
// сосед внутри пакета — склейка с каталогом пакета.
func memKey(dir, rel string) string {
	if strings.HasPrefix(rel, "@") {
		return path.Clean(rel)
	}
	if dir != "" {
		return path.Clean(path.Join(dir, rel))
	}
	return path.Base(rel)
}

func (m memLoader) where(dir, rel string) string {
	if strings.HasPrefix(rel, "@") {
		return "пакет " + path.Clean(rel) + " не подключён к новелле (lvnconv deps sync)"
	}
	names := make([]string, 0, len(m.files))
	for n := range m.files {
		names = append(names, n)
	}
	sort.Strings(names)
	if len(names) == 0 {
		return "в новелле нет других файлов"
	}
	return "есть: " + strings.Join(names, ", ")
}

// expandIncludes раскрывает директивы и возвращает склеенный источник вместе с
// картой происхождения каждой строки.
func expandIncludes(src, dir, display, rootAbs string, ld incLoader) (string, []srcRef, error) {
	var out []string
	var refs []srcRef
	seen := map[string]bool{}
	// Корень в цепочке с самого начала: иначе a -> b -> a читалось бы как
	// "b -> a -> b", и автор не увидел бы, откуда всё началось.
	chain := []string{}
	if rootAbs != "" {
		chain = append(chain, rootAbs)
		seen[rootAbs] = true
	}
	if err := expandInto(&out, &refs, src, dir, display, seen, chain, ld); err != nil {
		return "", nil, err
	}
	return strings.Join(out, "\n"), refs, nil
}

func expandInto(out *[]string, refs *[]srcRef, src, dir, display string, seen map[string]bool, chain []string, ld incLoader) error {
	for i, line := range strings.Split(src, "\n") {
		m := reInclude.FindStringSubmatch(line)
		if m == nil {
			*out = append(*out, line)
			*refs = append(*refs, srcRef{File: display, Line: i + 1})
			continue
		}
		rel := m[1]
		key, data, nextDir, lerr := ld.load(dir, rel)

		for _, c := range chain {
			if c == key {
				return fmt.Errorf("%s: include cycle: %s",
					at(display, i+1), strings.Join(shortChain(append(chain, key)), " -> "))
			}
		}
		if seen[key] {
			// Уже подключён выше. Молча пропускаем — это не ошибка автора, а
			// нормальная форма: два файла подключают одну общую механику.
			*out = append(*out, "// include "+rel+" (уже подключён)")
			*refs = append(*refs, srcRef{File: display, Line: i + 1})
			continue
		}
		if lerr != nil {
			return fmt.Errorf("%s: include %q: файл не найден (%s)",
				at(display, i+1), rel, ld.where(dir, rel))
		}
		seen[key] = true
		if err := expandInto(out, refs, data, nextDir, rel, seen, append(chain, key), ld); err != nil {
			return err
		}
	}
	return nil
}

func at(display string, line int) string {
	if display == "" {
		return fmt.Sprintf("line %d", line)
	}
	return fmt.Sprintf("%s:%d", display, line)
}

func shortChain(paths []string) []string {
	out := make([]string, 0, len(paths))
	for _, p := range paths {
		out = append(out, filepath.Base(p))
	}
	return out
}

// reLinePrefix ловит "line N:" в начале ошибки компилятора. Все 35 мест в
// convert.go форматируют позицию именно так, поэтому переписать её достаточно
// ОДИН раз здесь, а не тащить файл через весь разбор.
var reLinePrefix = regexp.MustCompile(`^line (\d+):`)

// remapError переводит номер строки склейки в «файл:строка». Без этого автор,
// получив "line 412", идёт искать её в своей главе на 60 строк.
func remapError(err error, refs []srcRef) error {
	if err == nil {
		return nil
	}
	msg := err.Error()
	m := reLinePrefix.FindStringSubmatch(msg)
	if m == nil {
		return err
	}
	n, cerr := strconv.Atoi(m[1])
	if cerr != nil || n < 1 || n > len(refs) {
		return err
	}
	r := refs[n-1]
	// Перекладывать надо ВСЕГДА, а не только для подключённых файлов: строки
	// самого корневого файла после include тоже сдвинуты на длину подстановки.
	// Первая версия этого не делала, и ошибка в главе на строке 10 приезжала
	// как "line 66" — ровно та потеря ориентации, против которой всё затевалось.
	if r.File == "" {
		return fmt.Errorf("line %d:%s", r.Line, strings.TrimPrefix(msg, m[0]))
	}
	return fmt.Errorf("%s:%d:%s", r.File, r.Line, strings.TrimPrefix(msg, m[0]))
}

// ConvertFile компилирует .lvns с диска. Это единственный вход, умеющий
// include: подстановка требует знать, ОТНОСИТЕЛЬНО ЧЕГО резолвить путь, а
// Convert принимает только текст.
func ConvertFile(p string) (*Doc, error) {
	src, err := os.ReadFile(p)
	if err != nil {
		return nil, err
	}
	abs, _ := filepath.Abs(p)
	joined, refs, err := expandIncludes(string(src), filepath.Dir(p), "", filepath.Clean(abs), diskLoader{})
	if err != nil {
		return nil, err
	}
	// Карты разворачиваются ПОСЛЕ include: подключённый файл тоже вправе
	// нарисовать своё место, и путь к карте считается от него же.
	joined, err = expandMaps(joined, filepath.Dir(p))
	if err != nil {
		return nil, err
	}
	// Сетка разворачивается ПОСЛЕ карт: карта тоже может быть написана в
	// клетках, и её команды должны пройти тот же перевод.
	// Достижения — первыми: они превращаются в обычный `set`, и дальше их
	// не отличить от того, что автор написал руками.
	var achWarns []string
	joined, achWarns = expandAchievements(joined)
	for _, w := range achWarns {
		fmt.Fprintln(os.Stderr, "warning: "+w)
	}
	// Погода — РАНЬШЕ всего остального света: она разворачивается в обычные
	// `light` и `bg3d`, и всё, что автор написал ПОСЛЕ неё, должно эти
	// значения перекрывать, а не наоборот. «Ясная ночь, но фонарь ярче» —
	// естественный порядок чтения, и порядок команд обязан ему следовать.
	var weatherWarns []string
	joined, weatherWarns = expandWeather(joined)
	for _, w := range weatherWarns {
		fmt.Fprintln(os.Stderr, "warning: "+w)
	}
	// Отношения разворачиваются ДО сетки: «рядом с фонарём» считается в
	// метрах, а сетка потом переведёт клетки — иначе привязка считалась бы
	// от нерастянутых координат.
	var relWarns []string
	joined, relWarns = expandRelations(joined)
	for _, w := range relWarns {
		fmt.Fprintln(os.Stderr, "warning: "+w)
	}
	var gridWarns []string
	joined, gridWarns = expandGrid(joined)
	for _, w := range gridWarns {
		fmt.Fprintln(os.Stderr, "warning: "+w)
	}
	// Проверки сцены идут ПОСЛЕ сетки: они смотрят на метры, а не на клетки,
	// и должны видеть те же числа, что получит рантайм.
	for _, w := range LintScene(joined) {
		fmt.Fprintln(os.Stderr, "warning: "+w)
	}
	doc, err := Convert(joined)
	return doc, remapError(err, refs)
}

// strayInclude ловит директиву, дожившую до разбора. Так бывает при вызове
// Convert напрямую (без файла) — и без этой проверки строка провалилась бы в
// ветку наррации и НАПЕЧАТАЛАСЬ БЫ ИГРОКУ как реплика. Ровно эта ловушка
// описана седьмым шагом в docs/adding-an-op.md как единственная без стража.
func strayInclude(lines []string) error {
	for i, l := range lines {
		if m := reInclude.FindStringSubmatch(l); m != nil {
			return fmt.Errorf("line %d: include %q: подключение работает только при компиляции файла "+
				"(lvnconv convert -i …), а не текста без пути", i+1, m[1])
		}
	}
	return nil
}

// ConvertFiles компилирует .lvns из НАБОРА ОТКРЫТЫХ ФАЙЛОВ, а не с диска:
// именно так работает веб-IDE и плейграунд — компилятор там живёт в браузере
// (wasm), файловой системы нет, а `include` всё равно обязан работать, иначе
// игру из нескольких глав в студии не написать.
//
// Ключи files — имена файлов так, как автор пишет их в include ("механики.lvns").
// Каталогов нет: все скрипты новеллы лежат рядом.
//
// self — имя ОТКРЫТОГО файла. Оно нужно не для красоты: без него корневой буфер
// безымянный, и цикл, который идёт через него самого (глава подключает
// механики, механики подключают главу), не опознаётся как цикл — он просто
// молча разворачивается второй копией. Пустое self допустимо для разового
// фрагмента, у которого имени и правда нет.
func ConvertFiles(src, self string, files map[string]string) (*Doc, error) {
	root := ""
	if self != "" {
		root = path.Base(self)
	}
	joined, refs, err := expandIncludes(src, "", "", root, memLoader{files: files})
	if err != nil {
		return nil, err
	}
	doc, err := Convert(joined)
	return doc, remapError(err, refs)
}

// expandMaps подставляет команды сцены вместо строки «map "…"».
//
// Это конструкция ВРЕМЕНИ КОМПИЛЯЦИИ, как и include: рантайм о картах не знает,
// новых опов не появляется. Карта из ста символов превращается в несколько
// команд `o3d` со списками точек — то есть в ту же сотню объектов, но
// разделяющих меш и материал.
func expandMaps(src, dir string) (string, error) {
	lines := strings.Split(src, "\n")
	out := make([]string, 0, len(lines))
	for i, line := range lines {
		m := reMap.FindStringSubmatch(line)
		if m == nil {
			out = append(out, line)
			continue
		}
		rel, name := m[1], m[2]
		if name == "" {
			// Имя по умолчанию — из имени файла: maps/деревня.lvnmap → деревня.
			name = strings.TrimSuffix(filepath.Base(rel), filepath.Ext(rel))
		}
		p := rel
		if !filepath.IsAbs(p) {
			p = filepath.Join(dir, rel)
		}
		made, err := ExpandMap(p, name)
		if err != nil {
			return "", fmt.Errorf("line %d: %w", i+1, err)
		}
		out = append(out, made...)
	}
	return strings.Join(out, "\n"), nil
}
