package lvn

// РАСПИСАНИЕ КАДРА: кто в каком месте чем является — для каждой команды главы.
//
// Управление сценой идёт через сценарий, и это верно: автор пишет историю, а не
// расставляет спрайты руками. Но состояние кадра при этом нигде не записано —
// оно существует только как СЛЕД от последовательности команд. Пока командует
// один сценарий, следа хватает; стоит вмешаться кому-то ещё (катсцена, витрина,
// гардероб), и никто, включая саму историю, уже не знает, каким остался кадр.
//
// Здесь тот же сценарий читается как граф и превращается в расписание. Дальше
// это просто данные, и с ними можно то, чего со следом нельзя: восстановить
// сохранение без переигрывания, вернуть кадр после катсцены по записи, а не по
// памяти о том, кого уводили, и — главное для сборки — проверить главу ДО
// запуска: где герой говорит, не будучи показанным, и кто остался в кадре
// забытым до конца.
//
// Обход тот же, что у достижимости (walker): вторая реализация обхода в этом
// проекте уже однажды разошлась с первой, и повторять это незачем.

import (
	"fmt"
	"sort"
	"strings"
)

// FrameActor — один человек (или предмет) в кадре.
type FrameActor struct {
	// Pose — команда, которой его поставили: место, размер, оси облика.
	Pose Cmd
	// Fx — грим (тёмный силуэт, голограмма, растворение). Живёт ОТДЕЛЬНО от
	// позы, потому что приходит своей командой `sfx`, — и именно поэтому
	// терялся, когда кадр возвращали одной лишь позой.
	Fx Cmd
	// Visible — виден ли. Скрытый остаётся записью: сцена помнит, чем его
	// вернуть, и это не то же самое, что «его нет».
	Visible bool
}

// Frame — состояние сцены в точке сценария.
type Frame struct {
	Actors map[string]FrameActor
	Bg     Cmd
	Veil   Cmd
}

func newFrame() *Frame { return &Frame{Actors: map[string]FrameActor{}} }

func (f *Frame) clone() *Frame {
	c := &Frame{Actors: make(map[string]FrameActor, len(f.Actors)), Bg: f.Bg, Veil: f.Veil}
	for k, v := range f.Actors {
		c.Actors[k] = v
	}
	return c
}

// absorb записывает команду в кадр. Возвращает false для всего, что не про
// состояние сцены (реплики, звук, ожидания) — такие команды кадр не меняют.
func (f *Frame) absorb(c Cmd) bool {
	switch c.Op() {
	case "actor", "obj":
		id := c.Str("id")
		if id == "" {
			return false
		}
		a := f.Actors[id]
		a.Pose = c
		a.Visible = true
		if v, ok := c["show"]; ok {
			if b, isBool := v.(bool); isBool {
				a.Visible = b
			}
		}
		f.Actors[id] = a
		return true
	case "sfx":
		id := c.Str("id")
		if id == "" {
			return false
		}
		a := f.Actors[id]
		// Полное `off` снимает грим, а не становится им.
		//
		// ЧИТАЕМ КАК РАНТАЙМ, А НЕ «ЕСТЬ ЛИ КЛЮЧ». Признак считается
		// поднятым, когда поле есть И слово в нём его не отменяет: рукописный
		// `"off": false` означает «не снимать». Раньше здесь стояла проверка
		// на присутствие ключа, и повтор кадра расходился с плеером — то есть
		// проверка сертифицировала не то поведение, которое увидит игрок.
		// Зеркало C#: Lvn.LvnBool.Flag.
		if flagOn(c["off"]) && c.Str("part") == "" {
			a.Fx = nil
		} else {
			a.Fx = c
		}
		f.Actors[id] = a
		return true
	case "bg", "bg3d":
		f.Bg = c
		return true
	case "fade", "dim", "flash", "tint", "blur", "fx":
		f.Veil = c
		return true
	case "clear":
		// «Скрыть всех», а не «забыть всех»: показанный снова без position
		// обязан встать на своё прежнее место.
		for id, a := range f.Actors {
			a.Visible = false
			f.Actors[id] = a
		}
		return true
	}
	return false
}

// same — одинаковы ли кадры. Нужен расписанию: узел, к которому пришли двумя
// путями, считается известным, только если сцена в нём совпала.
func (f *Frame) same(o *Frame) bool {
	if o == nil || len(f.Actors) != len(o.Actors) {
		return false
	}
	if fmt.Sprint(f.Bg) != fmt.Sprint(o.Bg) || fmt.Sprint(f.Veil) != fmt.Sprint(o.Veil) {
		return false
	}
	for id, a := range f.Actors {
		b, ok := o.Actors[id]
		if !ok || a.Visible != b.Visible {
			return false
		}
		if fmt.Sprint(a.Pose) != fmt.Sprint(b.Pose) || fmt.Sprint(a.Fx) != fmt.Sprint(b.Fx) {
			return false
		}
	}
	return true
}

// Visible — кто виден в кадре, по алфавиту (для отчётов и сравнений).
func (f *Frame) Visible() []string {
	out := []string{}
	for id, a := range f.Actors {
		if a.Visible {
			out = append(out, id)
		}
	}
	sort.Strings(out)
	return out
}

// FrameStop — кадр в точке сценария плюс честность этого знания.
type FrameStop struct {
	// Frame — кадр по ПЕРВОМУ пути, которым сюда пришли. Точен, когда Certain.
	Frame *Frame
	// Certain — одинаков ли кадр по всем путям, приводящим сюда. False — узел
	// лежит после схождения ветвей с разной сценой, и точный ответ даёт только
	// трасса исполнения. Врать про кадр хуже, чем признать неоднозначность.
	Certain bool
	// Reached — доходил ли обход до этой команды вообще.
	Reached bool

	// ИНВАРИАНТ УЗЛА — то, что верно на ВСЕХ путях сюда. Ради ветвистых игр:
	// там точный кадр у половины узлов зависит от пути, и одного состояния
	// мало, а перебирать комбинации нельзя — их экспоненциально много. Зато
	// пересечение путей считается за тот же один проход и даёт то, на что
	// можно опереться: «здесь ТОЧНО стоит A», «здесь ТОЧНО нет B», про
	// остальных — честное «неизвестно».
	//
	// Проверки строятся только на инварианте: находка, верная лишь на одном из
	// путей, — это ложная тревога, а ложная тревога в ежедневном отчёте
	// убивает доверие ко всему отчёту.
	AlwaysVisible map[string]bool
	NeverVisible  map[string]bool
}

// FrameIssue — находка расписания: место, где сцена расходится со смыслом.
type FrameIssue struct {
	Cmd   int    `json:"cmd"`
	Kind  string `json:"kind"`
	Who   string `json:"who,omitempty"`
	Note  string `json:"note"`
	Label string `json:"label,omitempty"`
}

// FrameReport — расписание главы и то, что оно заметило.
type FrameReport struct {
	Stops  []FrameStop
	Issues []FrameIssue
	// Uncertain — сколько узлов зависят от пути (ветви сошлись с разной сценой).
	Uncertain int
}

// Schedule проходит граф главы и расписывает кадр для каждой команды.
//
// Ходит теми же переходами, что и обход достижимости, но несёт с собой
// состояние сцены. Узел, к которому пришли двумя путями с РАЗНОЙ сценой,
// помечается неопределённым — и остаётся таким: одна ложь в расписании дороже
// сотни честных пропусков.
func Schedule(d *Doc, depth int) FrameReport {
	if d == nil || len(d.Script) == 0 {
		return FrameReport{}
	}
	if depth <= 0 {
		depth = DefaultReachDepth
	}
	labels := map[string]int{}
	for i, c := range d.Script {
		if c.Op() == "label" {
			if id := c.Str("id"); id != "" {
				labels[id] = i
			}
		}
	}

	// Весь состав главы: кто вообще появляется в ней как актёр. Нужен для
	// честного «его тут точно нет» — актёр, которого на этом пути НЕ ПОКАЗЫВАЛИ
	// вовсе, отсутствует в кадре так же надёжно, как явно скрытый, но в самом
	// кадре его записи нет, и без списка состава он молча выпадал бы из
	// проверки.
	cast := map[string]bool{}
	for _, c := range d.Script {
		if op := c.Op(); op == "actor" || op == "obj" {
			if id := c.Str("id"); id != "" {
				cast[id] = true
			}
		}
	}

	stops := make([]FrameStop, len(d.Script))
	// Ограничитель повторных заходов: кадр несёт состояние, поэтому памятка
	// «по запасу глубины» из обхода достижимости здесь не годится — она
	// обрезала бы вторые заходы с ДРУГОЙ сценой, ради которых мы и ходим.
	// Считаем заходы: их хватает, чтобы увидеть расхождение, и не хватает,
	// чтобы устроить экспоненту.
	const maxVisits = 4
	visits := make([]int, len(d.Script))

	type step struct {
		pc     int
		budget int
		frame  *Frame
	}
	queue := []step{{pc: 0, budget: depth, frame: newFrame()}}

	for len(queue) > 0 {
		cur := queue[0]
		queue = queue[1:]
		pc, budget, frame := cur.pc, cur.budget, cur.frame

		for pc >= 0 && pc < len(d.Script) {
			if visits[pc] >= maxVisits {
				break
			}
			visits[pc]++

			frame = frame.clone()
			c := d.Script[pc]
			frame.absorb(c)

			if !stops[pc].Reached {
				stops[pc] = FrameStop{
					Frame: frame, Certain: true, Reached: true,
					AlwaysVisible: shownSet(frame),
					NeverVisible:  absentSet(frame, cast),
				}
			} else {
				if stops[pc].Certain && !stops[pc].Frame.same(frame) {
					stops[pc].Certain = false
				}
				// Инвариант СУЖАЕТСЯ каждым новым путём: остаётся лишь то, что
				// подтвердилось и здесь. Так знание остаётся верным, сколько бы
				// ветвей ни сходилось в этой точке.
				intersect(stops[pc].AlwaysVisible, shownSet(frame))
				intersect(stops[pc].NeverVisible, absentSet(frame, cast))
			}

			switch c.Op() {
			case "goto":
				if budget <= 0 {
					pc = -1
					continue
				}
				next, ok := labels[c.Str("label")]
				if !ok {
					pc = -1
					continue
				}
				budget--
				pc = next
				continue
			case "return":
				pc = -1
				continue
			case "choice":
				// Во все стороны: вопрос не «что выберет игрок», а «каким
				// кадр может оказаться».
				for _, target := range choiceTargets(c) {
					if next, ok := labels[target]; ok && budget > 0 {
						queue = append(queue, step{pc: next, budget: budget - 1, frame: frame})
					}
				}
				pc = -1
				continue
			case "if":
				if target := c.Str("then"); target != "" {
					if next, ok := labels[target]; ok && budget > 0 {
						queue = append(queue, step{pc: next, budget: budget - 1, frame: frame})
					}
				}
				if target := c.Str("else"); target != "" {
					if next, ok := labels[target]; ok && budget > 0 {
						queue = append(queue, step{pc: next, budget: budget - 1, frame: frame})
					}
				}
			}
			pc++
		}
	}

	rep := FrameReport{Stops: stops}
	for _, s := range stops {
		if s.Reached && !s.Certain {
			rep.Uncertain++
		}
	}
	rep.Issues = frameIssues(d, stops)
	return rep
}

// shownSet — кто виден в этом кадре.
func shownSet(f *Frame) map[string]bool {
	out := map[string]bool{}
	for id, a := range f.Actors {
		if a.Visible {
			out[id] = true
		}
	}
	return out
}

// absentSet — кого в кадре НЕТ: и явно скрытые, и те, кого на этом пути ещё не
// показывали. Для зрителя разницы никакой — человека на экране нет; а для
// проверки разница была решающей: без второй половины актёр, скрытый на одной
// ветви и не показанный на другой, выпадал из «его тут точно нет».
func absentSet(f *Frame, cast map[string]bool) map[string]bool {
	out := map[string]bool{}
	for id := range cast {
		a, known := f.Actors[id]
		if !known || !a.Visible {
			out[id] = true
		}
	}
	return out
}

// intersect оставляет в dst только то, что есть и в other.
func intersect(dst, other map[string]bool) {
	for k := range dst {
		if !other[k] {
			delete(dst, k)
		}
	}
}

// choiceTargets — куда ведут варианты выбора.
func choiceTargets(c Cmd) []string {
	out := []string{}
	raw, ok := c["options"].([]any)
	if !ok {
		return out
	}
	for _, o := range raw {
		m, ok := o.(map[string]any)
		if !ok {
			continue
		}
		if s, ok := m["goto"].(string); ok && s != "" {
			out = append(out, s)
		}
		if s, ok := m["label"].(string); ok && s != "" {
			out = append(out, s)
		}
	}
	return out
}

// frameIssues — что расписание заметило в главе.
//
// Проверки нарочно узкие: сообщать стоит лишь о том, что почти наверняка
// ошибка автора, а не о вкусовщине. Ложная тревога в отчёте, который читают
// каждый день, убивает доверие ко всему отчёту.
func frameIssues(d *Doc, stops []FrameStop) []FrameIssue {
	var out []FrameIssue
	for i, c := range d.Script {
		if !stops[i].Reached || stops[i].Frame == nil {
			continue
		}
		if c.Op() != "say" {
			continue
		}
		// КТО ГОВОРИТ — это `who_id`, а не `who`. В `who` лежит ОТОБРАЖАЕМОЕ
		// ИМЯ («Агент», «Автор»), а актёр на сцене зовётся идентификатором
		// («agent»): разные пространства имён, и сравнивать их бессмысленно —
		// проверка молчала на всём контенте, пока сравнивала именно их.
		// Компилятор кладёт `who_id`, когда имя и актёр связаны `actor_map`;
		// без такой связи спрашивать не о чем — глава сама не знает, кто это.
		who := c.Str("who_id")
		if who == "" {
			continue
		}
		// Говорит тот, кого НЕТ В КАДРЕ НИ НА ОДНОМ ПУТИ. Проверка идёт по
		// инварианту, а не по первому попавшемуся пути: иначе на ветвистой игре
		// отчёт был бы полон находок, верных лишь для одной ветви.
		//
		// Само по себе это не ошибка (голос за кадром, рассказчик, система),
		// поэтому сообщаем только про того, кого глава где-то показывает как
		// актёра: значит, он предполагался видимым.
		if !stops[i].NeverVisible[who] {
			continue
		}
		if !stagedSomewhere(d, who) {
			continue
		}
		// ...И ЕГО НЕ ПОКАЖУТ В ЭТОЙ СЦЕНЕ ВООБЩЕ.
		//
		// Два уточнения, каждое выведено живым контентом. Первое:
		// импортированные главы ставят реплику на кадр раньше показа
		// («Промолчать.» от лица героини, а следующей командой она входит) —
		// зритель видит их вместе, сообщать не о чем. Второе, важнее: реплика
		// от первого лица при скрытом герое — НОРМА жанра, так написана вся
		// Cold; протагонист говорит, не показываясь, сотни раз за главу.
		//
		// Настоящий сигнал один: человек говорит, и его не увидят до конца
		// сцены — то есть до смены фона. Тогда голос звучит из пустоты
		// по-настоящему. Без этих оговорок отчёт давал 670 находок; столько
		// никто читать не станет, и настоящие случаи утонули бы среди них.
		if appearsBeforeSceneEnd(d, i, who) {
			continue
		}
		out = append(out, FrameIssue{
			Cmd:  i,
			Kind: "speaks-offstage",
			Who:  who,
			Note: fmt.Sprintf("%s говорит, но в кадре его нет — и до конца сцены не появится", who),
		})
	}
	out = append(out, forgottenOnStage(d, stops)...)
	return out
}

// forgottenOnStage — кто остался стоять в кадре к концу главы.
//
// Глава кончается, кадр передают меню или следующей главе — и человек,
// которого автор забыл увести, уезжает вместе с ним. Мы этот класс дефектов
// ловили руками неделю: «агент остался», «героиня не уходит». Проверка ищет их
// в тексте главы за секунду.
//
// Только по инварианту: тот, кто остаётся в кадре НА ВСЕХ путях к последней
// команде. Забытый на одной ветви из десяти — это уже не «забыли увести», а
// вопрос к конкретной ветке, и такие находки лучше не смешивать.
func forgottenOnStage(d *Doc, stops []FrameStop) []FrameIssue {
	last := -1
	for i := len(stops) - 1; i >= 0; i-- {
		if stops[i].Reached {
			last = i
			break
		}
	}
	if last < 0 || stops[last].AlwaysVisible == nil {
		return nil
	}
	var ids []string
	for id := range stops[last].AlwaysVisible {
		ids = append(ids, id)
	}
	sort.Strings(ids)

	var out []FrameIssue
	for _, id := range ids {
		out = append(out, FrameIssue{
			Cmd:  last,
			Kind: "left-on-stage",
			Who:  id,
			Note: fmt.Sprintf("%s остаётся в кадре к концу главы — его унесёт в меню или в следующую главу", id),
		})
	}
	return out
}

// appearsBeforeSceneEnd — покажут ли героя до конца текущей сцены (смены фона).
// Сцена — естественная единица: внутри неё зритель держит в голове, кто где
// стоит, а на смене фона счёт начинается заново.
func appearsBeforeSceneEnd(d *Doc, from int, id string) bool {
	for j := from + 1; j < len(d.Script); j++ {
		c := d.Script[j]
		switch c.Op() {
		case "bg", "bg3d":
			return false // сцена сменилась — в ТОЙ его так и не показали
		case "actor", "obj":
			if c.Str("id") != id {
				continue
			}
			if v, ok := c["show"]; !ok {
				return true
			} else if b, isBool := v.(bool); isBool && b {
				return true
			}
		}
	}
	return false
}

// stagedSomewhere — показывает ли глава этого героя хоть где-нибудь. Отсекает
// рассказчика и системные голоса, которых ставить и не собирались.
func stagedSomewhere(d *Doc, id string) bool {
	for _, c := range d.Script {
		if (c.Op() == "actor" || c.Op() == "obj") && c.Str("id") == id {
			if v, ok := c["show"]; !ok {
				return true
			} else if b, isBool := v.(bool); isBool && b {
				return true
			}
		}
	}
	return false
}

// FrameSummary — короткая сводка для человека.
func (r FrameReport) FrameSummary() string {
	reached := 0
	for _, s := range r.Stops {
		if s.Reached {
			reached++
		}
	}
	var b strings.Builder
	fmt.Fprintf(&b, "расписание кадра: %d команд пройдено, %d зависят от пути", reached, r.Uncertain)
	if len(r.Issues) > 0 {
		fmt.Fprintf(&b, ", находок %d", len(r.Issues))
	}
	return b.String()
}

// flagOn — поле-признак: присутствует и не отменено словом.
//
// Зеркало `Lvn.LvnBool.Flag` из рантайма. Словарь отказа взят тот же, что у
// него: без совпадения повтор кадра и живой показ расходятся на рукописном
// `.lvn`, а расхождение C#↔Go — главный структурный риск движка.
func flagOn(v any) bool {
	switch x := v.(type) {
	case nil:
		return false
	case bool:
		return x
	case float64:
		return x != 0
	case string:
		switch strings.ToLower(strings.TrimSpace(x)) {
		case "0", "false", "no", "off", "нет", "":
			return false
		}
		return true
	default:
		return true
	}
}
