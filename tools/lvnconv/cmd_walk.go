package main

// walk — рекурсивный обход ВСЕХ путей новеллы.
//
// Соук-бот играет случайными выборами: он отвечает на вопрос «падает ли», но
// не на вопрос «до чего игрок вообще не доберётся». Редкая ветка за гейтом не
// выпадет ни за две тысячи случайных прогонов, и мёртвый контент так и
// находили — глазами, спустя недели (треть главы за сломанным переходом).
//
// Здесь наоборот: на каждом выборе идём во ВСЕ стороны, на каждом `if` — и в
// then, и в else. Условия НЕ вычисляются намеренно: цель не «что будет при
// таких статах», а «есть ли путь вообще». Если реплика не достигнута даже
// когда все условия дружелюбны — она мертва наверняка, и это уже не догадка.
//
// Экспонента снимается памяткой: из одной команды нет смысла идти второй раз с
// тем же (или меньшим) запасом глубины — дальше откроется ровно то же. Поэтому
// обход стоит примерно len(script) × depth, а не 4^depth.
//
//	lvnconv walk [-depth N] [-json] [-strict] [-quiet] chapter.lvns [ещё файлы…]

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

// Глубина — это число ПЕРЕХОДОВ на одном пути (выбор, условие, goto, вызов),
// не длина пути: между переходами скрипт читается подряд и это бесплатно. От
// зависания на циклах спасает не она, а памятка в run(): во второй оборот мы
// входим с меньшим запасом, а из этой команды с большим уже ходили. Глубина
// ограничивает длину цепочки переходов — то есть насколько далеко обход вообще
// заглядывает.
//
// Дефолт большой намеренно. Рукописной новелле хватает двадцати пяти, но
// импортированная глава ставит по одной линии сотни условий, и на маленькой
// глубине обход не доходил до конца, объявляя мёртвыми две трети главы —
// худший вид отчёта: уверенно неверный. Стоимость обхода — len(script) × depth
// (замер: 91 глава, 141 тысяча команд, 1 секунда), так что запас бесплатен.
const defaultWalkDepth = 2000

type walkReport struct {
	File     string `json:"file"`
	Commands int    `json:"commands"`
	Reached  int    `json:"reached"`
	// Blocks — непрерывные куски, до которых не ведёт ни один путь. Блоками, а
	// не отдельными командами: «мёртвы строки 827-843» — это одна находка с
	// одной причиной, а список из семнадцати строк читается как семнадцать
	// разных беспорядочных проблем.
	Blocks []deadBlock `json:"dead_blocks"`
	// Служебная разметка ветвей: `goto __end13` сразу после другого goto и
	// метка, на которую он один и ссылался. Мёртво, но это шов компиляции, а не
	// потерянный контент — считаем числом, не перечисляем.
	Boilerplate int `json:"boilerplate_dead"`
	// Функции, которых не зовёт никто. Для главы это находка (эффекты оружия в
	// боевой новелле были определены и ни разу не вызваны), для файла-библиотеки
	// — норма: её функции зовёт потребитель, которого здесь просто нет. Поэтому
	// отдельным списком, а не в общей куче: тело такой функции обходится как
	// своя точка входа, иначе библиотека выглядела бы мёртвой на 94%.
	UncalledFuncs []string `json:"uncalled_funcs"`
	DeadOpts      []string `json:"dead_options"`
	Paths         int      `json:"paths"`
	CutDepth      int      `json:"cut_by_depth"`
	Err           string   `json:"error,omitempty"`
}

// deadBlock — недостижимый кусок скрипта.
type deadBlock struct {
	Start  int      `json:"start"`
	End    int      `json:"end"`
	Len    int      `json:"len"`
	Labels []string `json:"labels,omitempty"`
	Sample string   `json:"sample"`
}

// serviceOps — команды, из которых состоит разметка ветвей: сами по себе они
// ничего не показывают и не меняют.
var serviceOps = map[string]bool{"goto": true, "label": true, "return": true}

func cmdWalk(args []string) {
	fs := newFlagSet("walk")
	depth := fs.Int("depth", defaultWalkDepth, "сколько переходов проходить вглубь по одному пути")
	asJSON := fs.Bool("json", false, "отчёт в JSON (для CI)")
	strict := fs.Bool("strict", false, "выход 1, если найден недостижимый контент")
	quiet := fs.Bool("quiet", false, "только сводка, без перечисления мёртвых мест")
	_ = fs.Parse(args)
	if fs.NArg() == 0 {
		die("walk: ожидается хотя бы один <файл.lvn|.lvns>")
	}

	// По файлам — параллельно: главы независимы, а их у продукта двадцать пять.
	reports := make([]walkReport, fs.NArg())
	var wg sync.WaitGroup
	sem := make(chan struct{}, walkJobs())
	for i, path := range fs.Args() {
		wg.Add(1)
		go func(i int, path string) {
			defer wg.Done()
			sem <- struct{}{}
			defer func() { <-sem }()
			reports[i] = walkFile(path, *depth)
		}(i, path)
	}
	wg.Wait()

	if *asJSON {
		out, _ := json.MarshalIndent(reports, "", "  ")
		fmt.Println(string(out))
	} else {
		printWalkReports(reports, *quiet, *depth)
	}

	dead, failed := 0, 0
	for _, r := range reports {
		dead += len(r.Blocks) + len(r.DeadOpts)
		if r.Err != "" {
			failed++
		}
	}
	if failed > 0 || (*strict && dead > 0) {
		os.Exit(1)
	}
}

func walkJobs() int {
	n := 8
	if v := os.Getenv("LVN_WALK_JOBS"); v != "" {
		if k, err := fmt.Sscanf(v, "%d", &n); err != nil || k != 1 || n < 1 {
			n = 8
		}
	}
	return n
}

func walkFile(path string, depth int) walkReport {
	rep := walkReport{File: path}
	doc, err := loadForWalk(path)
	if err != nil {
		rep.Err = err.Error()
		return rep
	}
	w := newWalker(doc, depth)
	w.run(0, depth)
	rep.UncalledFuncs = w.walkUncalledFuncs(depth)

	rep.Commands = len(doc.Script)
	rep.Paths, rep.CutDepth = w.paths, w.cutDepth
	for i := range doc.Script {
		if w.seen[i] {
			rep.Reached++
		}
	}
	for _, blk := range deadBlocks(doc.Script, w.seen) {
		if blk.Sample == "" {
			rep.Boilerplate += blk.Len
			continue
		}
		rep.Blocks = append(rep.Blocks, blk)
	}
	// Вариант выбора, к которому обход не подошёл: он либо за недостижимым
	// выбором, либо ведёт в никуда.
	for i, c := range doc.Script {
		if c.Op() != "choice" {
			continue
		}
		opts, _ := c["options"].([]any)
		for oi, o := range opts {
			if w.optSeen[i][oi] {
				continue
			}
			text := ""
			if om, ok := o.(map[string]any); ok {
				text = lvn.Cmd(om).Str("text")
			}
			rep.DeadOpts = append(rep.DeadOpts, fmt.Sprintf("#%d вариант %d %q", i, oi, trim(text, 40)))
		}
	}
	return rep
}

// walkUncalledFuncs добирает тела функций, до которых основной поток не дошёл:
// каждая становится своей точкой входа. Возвращает их имена — по ним видно
// «определена, но не вызвана», и это ровно то, чем библиотека законно
// отличается от главы.
func (w *walker) walkUncalledFuncs(depth int) []string {
	var names []string
	for i, c := range w.script {
		if c.Op() != "label" {
			continue
		}
		id := c.Str("id")
		if !strings.HasPrefix(id, "__fn_") || w.seen[i] {
			continue
		}
		names = append(names, strings.TrimPrefix(id, "__fn_"))
		w.run(i, depth)
	}
	sort.Strings(names)
	return names
}

// deadBlocks склеивает недостигнутые команды в непрерывные куски. Sample пуст,
// когда в куске нет ничего, кроме разметки ветвей — такой блок безобиден.
func deadBlocks(script []lvn.Cmd, seen []bool) []deadBlock {
	var out []deadBlock
	for i := 0; i < len(script); i++ {
		if seen[i] {
			continue
		}
		blk := deadBlock{Start: i}
		var sample string
		for ; i < len(script) && !seen[i]; i++ {
			c := script[i]
			if id := c.Str("id"); c.Op() == "label" && id != "" {
				blk.Labels = append(blk.Labels, id)
			}
			if sample != "" || serviceOps[c.Op()] {
				continue
			}
			// Первая содержательная команда блока и есть его лицо: по ней автор
			// узнаёт место, не открывая файл.
			if c.Op() == "say" {
				sample = excerpt(c)
			} else {
				sample = c.Op() + " " + trim(describe(c), 40)
			}
		}
		blk.End = i - 1
		blk.Len = blk.End - blk.Start + 1
		blk.Sample = sample
		out = append(out, blk)
	}
	return out
}

// describe — короткая суть команды для отчёта: выражение, метка или цель.
func describe(c lvn.Cmd) string {
	for _, key := range []string{"expr", "id", "label", "text", "cond", "then"} {
		if v := c.Str(key); v != "" {
			return v
		}
	}
	return ""
}

// loadForWalk принимает и скомпилированный .lvn, и авторский .lvns — обход
// нужен ровно там, где новеллу пишут.
func loadForWalk(path string) (*lvn.Doc, error) {
	if strings.EqualFold(filepath.Ext(path), ".lvns") {
		src, err := lvns.ConvertFile(path)
		if err != nil {
			return nil, err
		}
		raw, err := json.Marshal(src)
		if err != nil {
			return nil, err
		}
		return lvn.Parse(raw)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	return lvn.Parse(data)
}

type walker struct {
	script []lvn.Cmd
	labels map[string]int

	seen    []bool
	optSeen map[int]map[int]bool
	// memo[pc] — самый большой запас глубины, с которым из этой команды уже
	// ходили. Повтор с меньшим запасом ничего нового не откроет.
	memo     map[int]int
	paths    int
	cutDepth int
}

func newWalker(doc *lvn.Doc, depth int) *walker {
	w := &walker{
		script:  doc.Script,
		labels:  map[string]int{},
		seen:    make([]bool, len(doc.Script)),
		optSeen: map[int]map[int]bool{},
		memo:    map[int]int{},
	}
	for i, c := range doc.Script {
		if c.Op() == "label" {
			if id := c.Str("id"); id != "" {
				w.labels[id] = i
			}
		}
	}
	return w
}

// branch уходит в отдельный путь, потратив один переход из запаса.
func (w *walker) branch(target string, budget int) {
	if budget <= 0 {
		w.cutDepth++
		return
	}
	pc, ok := w.jump(target)
	if !ok {
		return // висячий переход — это забота валидатора, здесь просто конец пути
	}
	w.run(pc, budget-1)
}

// jump переводит метку в индекс. Если метка в скрипте ОБЪЯВЛЕНА — идём в неё,
// даже когда это `__end`: иначе служебный конец главы попадал в отчёт как
// недостижимый, и каждая из девяноста глав врала об одном мёртвом месте.
// Не объявленная метка (встроенный `__end`, висячий переход) — просто конец
// пути: ловить такие ссылки — работа валидатора.
func (w *walker) jump(target string) (int, bool) {
	if target == "" {
		return 0, false
	}
	pc, ok := w.labels[target]
	return pc, ok
}

// run читает скрипт подряд от pc, ветвясь на каждом переходе. Памятка: из этой
// команды с таким же (или большим) запасом уже ходили — дальше откроется ровно
// то же, второй раз считать нечего. Именно она снимает экспоненту, и она же
// требует, чтобы запас был ЕДИНСТВЕННЫМ состоянием пути: пока в пути жил ещё и
// счётчик повторов, памятка отсекала ветки, которые при другом счётчике были
// достижимы, — обход терял живой контент и врал, что он мёртвый.
func (w *walker) run(pc, budget int) {
	if best, ok := w.memo[pc]; ok && best >= budget {
		return
	}
	w.memo[pc] = budget

	for pc >= 0 && pc < len(w.script) {
		w.seen[pc] = true
		c := w.script[pc]

		switch c.Op() {
		case "goto":
			// Переход тратит запас — иначе цикл из одних goto крутился бы вечно.
			if budget <= 0 {
				w.cutDepth++
				return
			}
			next, ok := w.jump(c.Str("label"))
			if !ok {
				w.paths++
				return
			}
			budget--
			pc = next
			if best, ok := w.memo[pc]; ok && best >= budget {
				return
			}
			w.memo[pc] = budget
			continue

		case "return":
			w.paths++
			return

		case "if":
			// Условие не вычисляем: интересен сам факт достижимости, а не
			// значения статов. Пустая ветка = падение на следующую команду.
			for _, target := range []string{c.Str("then"), c.Str("else")} {
				if target == "" {
					w.run(pc+1, budget)
					continue
				}
				w.branch(target, budget)
			}
			return

		case "choice":
			opts, _ := c["options"].([]any)
			if w.optSeen[pc] == nil {
				w.optSeen[pc] = map[int]bool{}
			}
			for oi, o := range opts {
				w.optSeen[pc][oi] = true
				om, _ := o.(map[string]any)
				oc := lvn.Cmd(om)
				// Тело варианта может само увести переходом — тогда путь идёт
				// туда, иначе выбор просто проваливается на следующую команду.
				target := oc.Str("goto")
				if target == "" {
					target = bodyGoto(oc)
				}
				if target == "" {
					w.run(pc+1, budget-1)
					continue
				}
				w.branch(target, budget)
			}
			// Просроченный таймер — такая же ветка, и её забывают проверять.
			if tg := c.Str("timeout_goto"); tg != "" {
				w.branch(tg, budget)
			}
			if len(opts) == 0 && c.Str("timeout_goto") == "" {
				pc++
				continue
			}
			return

		case "call":
			// Вызов уходит в метку и возвращается: обходим и её, и продолжение.
			w.branch(c.Str("label"), budget)
			pc++
			continue

		case "obj", "actor":
			// Кликабельные и перетаскиваемые объекты — тоже развилки: точка
			// входа в ветку, про которую в тексте новеллы ничего не сказано.
			for _, target := range hotspotTargets(c) {
				w.branch(target, budget)
			}
			pc++
			continue

		default:
			pc++
		}
	}
	w.paths++
}

// bodyGoto достаёт переход из тела варианта выбора (единственная ветвящаяся
// команда, которую тело имеет право содержать).
func bodyGoto(oc lvn.Cmd) string {
	body, ok := oc["body"].([]any)
	if !ok {
		return ""
	}
	for _, b := range body {
		bm, ok := b.(map[string]any)
		if !ok {
			continue
		}
		if bc := lvn.Cmd(bm); bc.Str("op") == "goto" {
			return bc.Str("label")
		}
	}
	return ""
}

// hotspotTargets собирает метки, куда уводит интерактив объекта: клик,
// попадание перетаскиванием и промах.
func hotspotTargets(c lvn.Cmd) []string {
	var out []string
	switch v := c["on_click"].(type) {
	case string:
		out = append(out, v)
	case map[string]any:
		out = append(out, lvn.Cmd(v).Str("goto"))
	}
	if raw := c.Str("on_drop"); raw != "" {
		for _, pair := range strings.FieldsFunc(raw, func(r rune) bool { return r == ' ' || r == ',' }) {
			if k := strings.Index(pair, ":"); k > 0 && k < len(pair)-1 {
				out = append(out, pair[k+1:])
			}
		}
	}
	if miss := c.Str("on_drop_miss"); miss != "" {
		out = append(out, miss)
	}
	return out
}

func excerpt(c lvn.Cmd) string {
	who := c.Str("who")
	text := trim(c.Str("text"), 50)
	if who != "" {
		return who + ": " + text
	}
	return text
}

func trim(s string, n int) string {
	s = strings.Join(strings.Fields(s), " ")
	if runes := []rune(s); len(runes) > n {
		return string(runes[:n]) + "…"
	}
	return s
}

func printWalkReports(reports []walkReport, quiet bool, depth int) {
	totalCmds, totalReached, totalDead := 0, 0, 0
	for _, r := range reports {
		if r.Err != "" {
			fmt.Fprintf(os.Stderr, "%s: НЕ РАЗОБРАН — %s\n", r.File, r.Err)
			continue
		}
		dead := len(r.Blocks) + len(r.DeadOpts)
		totalCmds += r.Commands
		totalReached += r.Reached
		totalDead += dead

		pct := 100.0
		if r.Commands > 0 {
			pct = float64(r.Reached) / float64(r.Commands) * 100
		}
		fmt.Printf("%s: %d/%d команд достижимо (%.1f%%), путей %d",
			filepath.Base(r.File), r.Reached, r.Commands, pct, r.Paths)
		if r.CutDepth > 0 {
			fmt.Printf(", обрублено по глубине %d", r.CutDepth)
		}
		fmt.Println()
		if r.CutDepth > 0 {
			// Иначе неполный обход не отличить от мёртвого контента — а это
			// разные новости.
			fmt.Printf("  ⚠ обход не дошёл до конца %d раз(а): покрытие НЕПОЛНОЕ, повторите с -depth больше %d\n",
				r.CutDepth, depth)
		}

		if len(r.UncalledFuncs) > 0 {
			fmt.Printf("  функции без вызова (%d): %s\n", len(r.UncalledFuncs), strings.Join(r.UncalledFuncs, ", "))
		}
		if r.Boilerplate > 0 {
			fmt.Printf("  служебной разметки недостижимо: %d команд(ы) — это норма компиляции\n", r.Boilerplate)
		}
		if quiet || dead == 0 {
			continue
		}
		blocks := append([]deadBlock(nil), r.Blocks...)
		sort.Slice(blocks, func(i, j int) bool { return blocks[i].Len > blocks[j].Len })
		for _, b := range blocks {
			fmt.Printf("  МЁРТВО #%d…#%d (%d команд): %s\n", b.Start, b.End, b.Len, b.Sample)
			if len(b.Labels) > 0 {
				fmt.Printf("      метки: %s\n", strings.Join(b.Labels, ", "))
			}
		}
		printDead("недостижимые варианты выбора", r.DeadOpts)
	}
	if len(reports) > 1 {
		pct := 100.0
		if totalCmds > 0 {
			pct = float64(totalReached) / float64(totalCmds) * 100
		}
		fmt.Printf("\nвсего: %d/%d команд достижимо (%.1f%%), мёртвых мест %d\n",
			totalReached, totalCmds, pct, totalDead)
	}
}

func printDead(title string, items []string) {
	if len(items) == 0 {
		return
	}
	sort.Strings(items)
	fmt.Printf("  %s (%d):\n", title, len(items))
	for _, it := range items {
		fmt.Printf("    %s\n", it)
	}
}
