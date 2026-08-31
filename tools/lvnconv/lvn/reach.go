package lvn

// Достижимость: какие команды главы игрок может увидеть хоть при каком-нибудь
// сочетании выборов, а какие — не увидит никогда.
//
// Живёт в пакете языка, а не в команде, по той же причине, по которой здесь же
// живёт валидатор: иначе появилось бы две реализации обхода — одна для
// `lvnconv walk`, другая для проверки при сохранении, — и они бы разошлись.
// Расхождение реализаций в этом проекте уже стоило дорого.
//
// На каждом выборе идём во ВСЕ стороны, на каждом `if` — и в then, и в else.
// Условия НЕ вычисляются намеренно: вопрос не «что будет при таких статах», а
// «есть ли путь вообще». Не достигнуто даже при дружелюбных условиях — мертво
// наверняка. Экспонента снимается памяткой: из команды нет смысла идти второй
// раз с тем же или меньшим запасом глубины, поэтому обход стоит примерно
// len(script) × depth, а не 4^depth.

import (
	"sort"
	"strings"
)

// DefaultReachDepth — сколько ПЕРЕХОДОВ проходить вглубь по одному пути (не
// длина пути: между переходами скрипт читается подряд и это бесплатно).
// Рукописной новелле хватает двадцати пяти, но импортированная глава ставит по
// одной линии сотни условий: на малой глубине обход не доходит до конца и
// объявляет мёртвым живое — худший вид отчёта, уверенно неверный.
const DefaultReachDepth = 2000

// DeadBlock — непрерывный кусок скрипта, до которого не ведёт ни один путь.
// Блоками, а не отдельными командами: «мертвы строки 827-843» — это одна
// находка с одной причиной, а список из семнадцати строк читается как
// семнадцать разных проблем.
type DeadBlock struct {
	Start  int      `json:"start"`
	End    int      `json:"end"`
	Len    int      `json:"len"`
	Labels []string `json:"labels,omitempty"`
	Sample string   `json:"sample"`
}

// DeadOption — вариант выбора, к которому обход не подошёл.
type DeadOption struct {
	Cmd    int    `json:"cmd"`
	Option int    `json:"option"`
	Text   string `json:"text"`
}

// ReachReport — что обход узнал про главу.
type ReachReport struct {
	Seen []bool
	// Blocks — мёртвые куски С СОДЕРЖИМЫМ (реплика, стейдж, присваивание).
	Blocks []DeadBlock
	// Boilerplate — мёртвые команды из одной лишь разметки ветвей (`goto
	// __end13` сразу после другого goto и метка, на которую он один и
	// ссылался): шов компиляции, а не потерянный контент.
	Boilerplate int
	// UncalledFuncs — функции, которых не зовёт никто. Для главы это находка,
	// для файла-библиотеки норма: её функции зовёт потребитель, которого в
	// файле нет. Их тела обходятся как отдельные точки входа.
	UncalledFuncs []string
	DeadOptions   []DeadOption
	Forks         int // развилок всего (варианты выбора + обе стороны условий)
	ForksTaken    int // из них пройдено обходом
	Paths         int // обходов, дошедших до конца
	CutByDepth    int // сколько раз обход упёрся в предел глубины
}

// Reached — сколько команд достижимо.
func (r ReachReport) Reached() int {
	n := 0
	for _, ok := range r.Seen {
		if ok {
			n++
		}
	}
	return n
}

// Reach обходит документ и рассказывает, что из него достижимо.
func Reach(d *Doc, depth int) ReachReport {
	if depth <= 0 {
		depth = DefaultReachDepth
	}
	w := newWalker(d, depth)
	w.run(0, depth)
	w.walkUncalledFuncs(depth)

	rep := ReachReport{
		Seen:          w.seen,
		UncalledFuncs: w.uncalled,
		Paths:         w.paths,
		CutByDepth:    w.cutDepth,
	}
	for _, blk := range deadBlocks(w.script, w.seen) {
		if blk.Sample == "" {
			rep.Boilerplate += blk.Len
			continue
		}
		rep.Blocks = append(rep.Blocks, blk)
	}
	for i, c := range d.Script {
		switch c.Op() {
		case "choice":
			opts, _ := c["options"].([]any)
			rep.Forks += len(opts)
			for oi, o := range opts {
				if w.optSeen[i][oi] {
					rep.ForksTaken++
					continue
				}
				text := ""
				if om, ok := o.(map[string]any); ok {
					text = Cmd(om).Str("text")
				}
				rep.DeadOptions = append(rep.DeadOptions, DeadOption{Cmd: i, Option: oi, Text: text})
			}
		case "if":
			// У условия две стороны, и обе — развилка: обход берёт обе.
			rep.Forks += 2
			if w.seen[i] {
				rep.ForksTaken += 2
			}
		}
	}
	return rep
}

// serviceOps — команды, из которых состоит разметка ветвей: сами по себе они
// ничего не показывают и не меняют.
var serviceOps = map[string]bool{"goto": true, "label": true, "return": true}

type walker struct {
	script []Cmd
	labels map[string]int

	seen    []bool
	optSeen map[int]map[int]bool
	// memo[pc] — самый большой запас глубины, с которым из этой команды уже
	// ходили. Повтор с меньшим запасом ничего нового не откроет.
	memo     map[int]int
	uncalled []string
	paths    int
	cutDepth int
}

func newWalker(d *Doc, depth int) *walker {
	w := &walker{
		script:  d.Script,
		labels:  map[string]int{},
		seen:    make([]bool, len(d.Script)),
		optSeen: map[int]map[int]bool{},
		memo:    map[int]int{},
	}
	for i, c := range d.Script {
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
		return // висячий переход — забота валидатора, здесь просто конец пути
	}
	w.run(pc, budget-1)
}

// jump переводит метку в индекс. Если метка ОБЪЯВЛЕНА — идём в неё, даже когда
// это `__end`: иначе служебный конец главы попадал в отчёт как недостижимый.
// Не объявленная метка (встроенный `__end`, висячий переход) — просто конец
// пути.
func (w *walker) jump(target string) (int, bool) {
	if target == "" {
		return 0, false
	}
	pc, ok := w.labels[target]
	return pc, ok
}

// run читает скрипт подряд от pc, ветвясь на каждом переходе. Памятка требует,
// чтобы запас был ЕДИНСТВЕННЫМ состоянием пути: пока в пути жил ещё и счётчик
// повторов, памятка отсекала ветки, которые при другом счётчике были
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
				oc := Cmd(om)
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

		case "obj", "actor", "ui":
			// Кликабельные и перетаскиваемые объекты — тоже развилки: точка
			// входа в ветку, про которую в тексте новеллы ничего не сказано.
			// То же и кнопки внутри дерева `ui`, только лежат они на глубине.
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

// walkUncalledFuncs добирает тела функций, до которых основной поток не дошёл:
// каждая становится своей точкой входа. Их имена — отдельный сигнал: «определена,
// но не вызвана» — то, чем библиотека законно отличается от главы.
func (w *walker) walkUncalledFuncs(depth int) {
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
	w.uncalled = names
}

// deadBlocks склеивает недостигнутые команды в непрерывные куски. Sample пуст,
// когда в куске нет ничего, кроме разметки ветвей — такой блок безобиден.
func deadBlocks(script []Cmd, seen []bool) []DeadBlock {
	var out []DeadBlock
	for i := 0; i < len(script); i++ {
		if seen[i] {
			continue
		}
		blk := DeadBlock{Start: i}
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
				sample = sayExcerpt(c)
			} else {
				sample = c.Op() + " " + trimTo(describe(c), 40)
			}
		}
		blk.End = i - 1
		blk.Len = blk.End - blk.Start + 1
		blk.Sample = sample
		out = append(out, blk)
	}
	return out
}

// bodyGoto достаёт переход из тела варианта выбора (единственная ветвящаяся
// команда, которую тело имеет право содержать).
func bodyGoto(oc Cmd) string {
	body, ok := oc["body"].([]any)
	if !ok {
		return ""
	}
	for _, b := range body {
		bm, ok := b.(map[string]any)
		if !ok {
			continue
		}
		if bc := Cmd(bm); bc.Str("op") == "goto" {
			return bc.Str("label")
		}
	}
	return ""
}

// hotspotTargets собирает метки, куда уводит интерактив объекта: клик,
// попадание перетаскиванием и промах.
// HotspotTargets — КУДА МОЖЕТ УВЕСТИ КОМАНДА ПОМИМО ОБЫЧНОГО ПОТОКА: клик по
// объекту, перетаскивание, кнопка внутри дерева `ui`. Экспортировано, потому
// что этот же вопрос задаёт импортёр, вычищая недостижимое: пока он спрашивал
// себя сам, он не знал про точки входа вовсе — и вырезал бы ветку, в которую
// игрок попадает нажатием.
func HotspotTargets(c Cmd) []string { return hotspotTargets(c) }

func hotspotTargets(c Cmd) []string {
	var out []string
	switch v := c["on_click"].(type) {
	case string:
		out = append(out, v)
	case map[string]any:
		out = append(out, Cmd(v).Str("goto"))
	}
	// Кнопки внутри дерева `ui` уводят так же, как объекты сцены, только
	// лежат на глубине. Не заглянув туда, обход объявил бы каждую метку,
	// достижимую лишь нажатием, мёртвой — и весь смысл проверки пропал бы:
	// автор перестал бы верить предупреждениям.
	if tree, ok := c["tree"].(map[string]any); ok {
		out = append(out, uiClickTargets(tree)...)
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

// describe — короткая суть команды для отчёта: выражение, метка или цель.
func describe(c Cmd) string {
	for _, key := range []string{"expr", "id", "label", "text", "cond", "then"} {
		if v := c.Str(key); v != "" {
			return v
		}
	}
	return ""
}

func sayExcerpt(c Cmd) string {
	who := c.Str("who")
	text := trimTo(c.Str("text"), 50)
	if who != "" {
		return who + ": " + text
	}
	return text
}

func trimTo(s string, n int) string {
	s = strings.Join(strings.Fields(s), " ")
	if runes := []rune(s); len(runes) > n {
		return string(runes[:n]) + "…"
	}
	return s
}

// uiClickTargets обходит дерево `ui` и собирает все on_click.
//
// ОБЕ ЗАПИСИ, как и на уровне команды: метка одним словом или объект
// `{goto, set}`. Рантайм понимает обе везде (см. LvnClick), а обход дерева знал
// только первую — и метка, к которой ведёт единственная кнопка меню, считалась
// НЕДОСТИЖИМОЙ: опечатка в ней проходила молча, а в игре кнопка вела в никуда.
func uiClickTargets(node map[string]any) []string {
	var out []string
	switch v := node["on_click"].(type) {
	case string:
		if v != "" {
			out = append(out, v)
		}
	case map[string]any:
		if g, ok := v["goto"].(string); ok && g != "" {
			out = append(out, g)
		}
	}
	if kids, ok := node["children"].([]any); ok {
		for _, k := range kids {
			if m, ok := k.(map[string]any); ok {
				out = append(out, uiClickTargets(m)...)
			}
		}
	}
	return out
}
