package main

// Воронка ВНУТРИ главы: докуда дочитывают и на каких развилках уходят.
//
// Воронка по главам отвечает «в какой главе теряем», и на этом останавливается.
// Но глава — это час чтения; сказать сценаристу «теряете в первом эпизоде»
// значит не сказать ничего. Здесь единица измерения — авторская МЕТКА: реплик
// в главе тысячи, меток десятки, и именно метками автор размечает сцены.
//
// Два разных вопроса, которые нельзя смешивать:
//   - СЛАЙДЫ: сколько дошло до каждой метки. Падение между соседними метками —
//     это место, где читать перестали.
//   - РАЗВИЛКИ: как проходят выборы. Здесь важнее всего не распределение
//     вариантов, а разница «показали» минус «выбрали»: человек увидел выбор и
//     закрыл приложение. Это самый дорогой сигнал в главе — там, где он думает,
//     он же и уходит.
//
// Метка и выбор адресуются ИНДЕКСОМ команды, а не именем: выбор безымянен, а
// индекс кладёт и метки, и развилки, и точки выхода на одну ось. Имя метки,
// тексты вариантов и кадр сервер достаёт из скрипта сам — скрипт у него есть,
// и доверять клиенту то, что можно прочитать у себя, незачем.

import (
	"fmt"
	"net/http"
	"sort"
	"strconv"

	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

type slideRow struct {
	At      int     `json:"at"`
	Label   string  `json:"label,omitempty"`
	Reached int     `json:"reached"`
	OfStart float64 `json:"of_start"` // доля от вошедших в главу
	OfPrev  float64 `json:"of_prev"`  // доля от предыдущей метки
	Lost    int     `json:"lost"`     // сколько не дошло от предыдущей
	Line    string  `json:"line,omitempty"`
	BG      string  `json:"bg,omitempty"`
}

type optionRow struct {
	Option int     `json:"option"`
	Text   string  `json:"text,omitempty"`
	Picks  int     `json:"picks"`
	Share  float64 `json:"share"`
}

type choiceRow struct {
	At      int    `json:"at"`
	Label   string `json:"label,omitempty"`
	Shown   int    `json:"shown"`
	Picked  int    `json:"picked"`
	Written int    `json:"written,omitempty"`
	Visible int    `json:"visible,omitempty"`
	// LeftHere — увидели выбор и не выбрали. Не «ошибка», а решение уйти
	// ровно в тот момент, когда игру попросили о решении.
	LeftHere   int         `json:"left_here"`
	LeaveShare float64     `json:"leave_share"`
	MedianSecs int         `json:"median_seconds,omitempty"`
	Options    []optionRow `json:"options,omitempty"`
	LockedNote string      `json:"locked_note,omitempty"`
}

type slidesReport struct {
	Title    string      `json:"title"`
	Chapter  string      `json:"chapter"`
	Name     string      `json:"name,omitempty"`
	Starts   int         `json:"starts"`
	Finishes int         `json:"finishes"`
	Abandons int         `json:"abandons"`
	Slides   []slideRow  `json:"slides,omitempty"`
	Choices  []choiceRow `json:"choices,omitempty"`
	// Worst — метка, на которой потеряли больше всего между соседними.
	Worst     string   `json:"worst_slide,omitempty"`
	WorstLost int      `json:"worst_lost,omitempty"`
	Balance   string   `json:"balance,omitempty"` // сходится ли «вошли = дочитали + ушли»
	Note      string   `json:"note,omitempty"`
	Notes     []string `json:"notes,omitempty"`
}

// GET /v1/analytics/slides?title=…&chapter=…
func (s *AnalyticsService) handleSlides(w http.ResponseWriter, r *http.Request) {
	if !s.adminOK(w, r) {
		return
	}
	win, err := parseAnalyticsWindow(r)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	seg, err := parseSegment(r)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	members := s.segmentMembers(seg, win.Days)
	title := clip(r.URL.Query().Get("title"), 64)
	chapter := clip(r.URL.Query().Get("chapter"), 64)
	if title == "" || chapter == "" {
		http.Error(w, "нужны title и chapter", http.StatusBadRequest)
		return
	}

	s.rollups.mu.Lock()
	m, _ := s.windowFor(win.Days, members)
	var ch *chapRoll
	if t := m.Titles[title]; t != nil {
		ch = t.Chapters[chapter]
	}
	s.rollups.mu.Unlock()

	rep := slidesReport{Title: title, Chapter: chapter, Name: s.chapters.chapterName(title, chapter)}
	if ch == nil {
		rep.Note = "по этой главе в окне нет данных"
		writeJSON(w, http.StatusOK, rep)
		return
	}
	rep.Starts, rep.Finishes, rep.Abandons = ch.Starts, ch.Finishes, ch.Abandons

	script := s.chapters.loadDoc(title, chapter)

	// ── слайды ──────────────────────────────────────────────────────────────
	type slideHit struct {
		at, n int
	}
	var hits []slideHit
	for key, n := range ch.Slides {
		if at, err := strconv.Atoi(key); err == nil {
			hits = append(hits, slideHit{at, n})
		}
	}
	// По ходу главы, а не по частоте: воронка читается сверху вниз, иначе
	// «падение между соседними» не имеет смысла.
	sort.Slice(hits, func(i, j int) bool { return hits[i].at < hits[j].at })

	prev := rep.Starts
	worstLost := 0
	for _, h := range hits {
		row := slideRow{At: h.at, Reached: h.n,
			OfStart: ratio(h.n, rep.Starts), OfPrev: ratio(h.n, prev)}
		if prev > h.n {
			row.Lost = prev - h.n
		}
		if script != nil {
			var frame exitPoint
			describeFrame(script, slideFrameAt(script, h.at), &frame)
			row.Label, row.Line, row.BG = frame.Label, frame.Line, frame.BG
		}
		if row.Lost > worstLost {
			worstLost = row.Lost
			rep.Worst = row.Label
			if rep.Worst == "" {
				rep.Worst = "команда #" + strconv.Itoa(h.at)
			}
			rep.WorstLost = row.Lost
		}
		rep.Slides = append(rep.Slides, row)
		prev = h.n
	}

	// ── развилки ────────────────────────────────────────────────────────────
	var keys []int
	for key := range ch.Choices {
		if at, err := strconv.Atoi(key); err == nil {
			keys = append(keys, at)
		}
	}
	sort.Ints(keys)
	for _, at := range keys {
		c := ch.Choices[strconv.Itoa(at)]
		row := choiceRow{At: at, Shown: c.Shown, Picked: c.Picked,
			Written: c.Written, Visible: c.Visible}
		if c.Shown > c.Picked {
			row.LeftHere = c.Shown - c.Picked
			row.LeaveShare = ratio(row.LeftHere, c.Shown)
		}
		if len(c.Seconds) > 0 {
			secs := append([]int(nil), c.Seconds...)
			sort.Ints(secs)
			row.MedianSecs = secs[len(secs)/2]
		}
		// «Написано три, видно один» — развилка, которой у игрока нет.
		// Само по себе законно (гейты), но если так у всех, выбор мёртв.
		if c.Written > 0 && c.Visible > 0 && c.Visible < c.Written {
			row.LockedNote = "заперто гейтом: написано " + strconv.Itoa(c.Written) +
				", видно " + strconv.Itoa(c.Visible)
		}
		for opt, n := range c.Options {
			i, err := strconv.Atoi(opt)
			if err != nil {
				continue
			}
			row.Options = append(row.Options, optionRow{Option: i, Picks: n,
				Share: ratio(n, c.Picked), Text: optionText(script, at, i)})
		}
		sort.Slice(row.Options, func(i, j int) bool {
			if row.Options[i].Picks != row.Options[j].Picks {
				return row.Options[i].Picks > row.Options[j].Picks
			}
			return row.Options[i].Option < row.Options[j].Option
		})
		if script != nil {
			var frame exitPoint
			describeFrame(script, at, &frame)
			row.Label = frame.Label
		}
		rep.Choices = append(rep.Choices, row)
	}

	// Условие приёмки: вошли = дочитали + ушли. Расхождение не прячем — оно
	// означает либо незакрытую сессию (игрок ещё читает), либо потерянное
	// событие, и обе новости стоят того, чтобы их видеть.
	switch diff := rep.Starts - rep.Finishes - rep.Abandons; {
	case rep.Starts == 0:
	case diff == 0:
		rep.Balance = "сходится: вошли " + strconv.Itoa(rep.Starts) + " = дочитали " +
			strconv.Itoa(rep.Finishes) + " + ушли " + strconv.Itoa(rep.Abandons)
	case diff > 0:
		rep.Balance = "не сходится на " + strconv.Itoa(diff) +
			": столько сессий не закрылись ни концом главы, ни уходом (читают прямо сейчас, либо событие не доехало)"
	default:
		rep.Balance = "не сходится на " + strconv.Itoa(-diff) +
			": дочитавших и ушедших больше, чем вошедших — окно захватило хвост главы, начатой раньше"
	}

	if len(ch.Slides) == 0 {
		rep.Note = "событий меток за это окно нет — они приезжают со сборкой, где включён label_reach"
	}
	if script == nil {
		rep.Notes = append(rep.Notes, "скрипт главы не найден: показаны индексы команд без имён меток и текстов")
	}
	rep.Notes = append(rep.Notes,
		"считаются СОБЫТИЯ: одно перепрохождение главы тем же игроком добавляет метки повторно")
	// Ветвящаяся глава — не лестница. Метки на разных ветках несравнимы: до
	// одной дошли те, кто выбрал влево, до другой — те, кто вправо, и «падение
	// между соседними» там означает развилку, а не потерю читателя. Молчать об
	// этом нельзя: таблица выглядит как воронка и будет прочитана как воронка.
	if n := countChoices(script); n > 0 {
		rep.Notes = append(rep.Notes, fmt.Sprintf(
			"в главе %d развилок: игроки идут разными ветками, поэтому порядок меток — это порядок в скрипте, а не порядок чтения; падение между соседними метками на ветках означает выбор, а не уход",
			n))
	}
	writeJSON(w, http.StatusOK, rep)
}

// countChoices — сколько развилок в скрипте. Ровно столько раз глава
// перестаёт быть линейной.
func countChoices(doc *lvn.Doc) int {
	if doc == nil {
		return 0
	}
	n := 0
	for _, c := range doc.Script {
		if c.Op() == "choice" {
			n++
		}
	}
	return n
}

// slideFrameAt — индекс, по которому описывать слайд.
//
// В момент самой метки экран ещё пуст: метка стоит ПЕРЕД тем, что она
// открывает, а фон и реплика идут следом. Описав её буквально, отчёт покажет
// автору пустой кадр там, где на самом деле сцена. Поэтому берём первую
// реплику этого слайда — и останавливаемся на следующей метке, потому что
// дальше начинается уже другой слайд.
func slideFrameAt(doc *lvn.Doc, at int) int {
	if doc == nil || at < 0 || at >= len(doc.Script) {
		return at
	}
	for i := at; i < len(doc.Script); i++ {
		switch doc.Script[i].Op() {
		case "say":
			return i
		case "label":
			if i > at {
				return at // слайд без единой реплики — описываем как есть
			}
		}
	}
	return at
}

// optionText достаёт текст варианта из скрипта: клиент его тоже шлёт, но в
// отчёте лучше авторский — он не обрезан и не переведён на язык устройства.
func optionText(doc *lvn.Doc, at, option int) string {
	if doc == nil || at < 0 || at >= len(doc.Script) {
		return ""
	}
	opts, ok := doc.Script[at]["options"].([]any)
	if !ok || option < 0 || option >= len(opts) {
		return ""
	}
	o, ok := opts[option].(map[string]any)
	if !ok {
		return ""
	}
	txt, _ := o["text"].(string)
	return trimLine(txt, 70)
}
