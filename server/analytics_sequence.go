package main

// ВОРОНКА ПО ШАГАМ И ПУТЬ ИГРОКА (TR-126, Илья 16.09).
//
// «Мы прям по каждой сессии сможем смотреть: сколько прошло 10 ход игроков в
// главе 0 при старте, сколько забайтилось гардеробом и купило?» — и тут же:
// «на самом деле не поможет только точки знать». Поэтому два ответа:
//
//   - ВОРОНКА ПО ШАГАМ: шаги задаются в запросе («начал главу 0 → прошёл 10
//     строк → зашёл в гардероб → купил»), считаются ПО ИГРОКУ в порядке
//     времени; у каждого обрыва — куда ушли вместо следующего шага (первое
//     событие после последнего пройденного шага у тех, кто дальше не дошёл).
//   - ПУТЬ ИГРОКА: вся лента событий одного игрока за окно, по времени, с
//     тем, что важно в каждом (глава, строка, экран, покупка).
//
// Оба читают сырые дневные файлы аналитики: их десятки килобайт в день, а
// вопрос «почему» требует порядка событий, которого в свёртке нет.

import (
	"bufio"
	"encoding/json"
	"net/http"
	"os"
	"sort"
	"strconv"
	"strings"
)

// seqStep — один шаг воронки: имя события и условия на поля.
//
// Запись: «name:key=val,key>=N,key<=N»; особые имена:
//   - progress:chapter=X,at>=N — «прошёл строку N главы X»: chapter_finish
//     этой главы, либо chapter_abandon / label_reach с at ≥ N;
//   - screen=X — «был на экране X»: минута ui_use со временем на нём.
type seqStep struct {
	Label string
	Name  string
	Cond  []seqCond
}

type seqCond struct {
	Key string
	Op  string // = >= <=
	Val string
}

func parseSteps(spec string) []seqStep {
	var steps []seqStep
	for _, raw := range strings.Split(spec, ";") {
		raw = strings.TrimSpace(raw)
		if raw == "" {
			continue
		}
		st := seqStep{Label: raw}
		name, conds, _ := strings.Cut(raw, ":")
		st.Name = strings.TrimSpace(name)
		if n, v, ok := strings.Cut(st.Name, "="); ok { // screen=X
			st.Name = n
			conds = n + "=" + v + "," + conds
		}
		for _, c := range strings.Split(conds, ",") {
			c = strings.TrimSpace(c)
			if c == "" {
				continue
			}
			for _, op := range []string{">=", "<=", "="} {
				if k, v, ok := strings.Cut(c, op); ok {
					st.Cond = append(st.Cond, seqCond{Key: strings.TrimSpace(k), Op: op, Val: strings.TrimSpace(v)})
					break
				}
			}
		}
		steps = append(steps, st)
		if len(steps) >= 8 {
			break
		}
	}
	return steps
}

// seqEvent — событие в удобном для проверки виде.
type seqEvent struct {
	Name    string
	TS      string
	Chapter string
	Props   map[string]json.RawMessage
}

func (e seqEvent) num(key string) (float64, bool) {
	if e.Props == nil {
		return 0, false
	}
	v, ok := e.Props[key]
	if !ok {
		return 0, false
	}
	var f float64
	if json.Unmarshal(v, &f) == nil {
		return f, true
	}
	return 0, false
}

func (e seqEvent) str(key string) string {
	if key == "chapter" && e.Chapter != "" {
		return e.Chapter
	}
	return rollupProp(e.Props, key)
}

func (c seqCond) holds(e seqEvent) bool {
	switch c.Op {
	case "=":
		return e.str(c.Key) == c.Val
	default:
		want, err := strconv.ParseFloat(c.Val, 64)
		got, ok := e.num(c.Key)
		if err != nil || !ok {
			return false
		}
		if c.Op == ">=" {
			return got >= want
		}
		return got <= want
	}
}

// matches — подходит ли событие под шаг.
func (st seqStep) matches(e seqEvent) bool {
	switch st.Name {
	case "progress":
		var chapter string
		var at float64 = -1
		for _, c := range st.Cond {
			if c.Key == "chapter" {
				chapter = c.Val
			}
			if c.Key == "at" && c.Op == ">=" {
				at, _ = strconv.ParseFloat(c.Val, 64)
			}
		}
		if chapter != "" && e.str("chapter") != chapter {
			return false
		}
		switch e.Name {
		case evChapterFinish:
			return true
		case evChapterAbandon, evLabelReach:
			got, ok := e.num("at")
			return ok && got >= at
		}
		return false
	case "screen":
		want := ""
		for _, c := range st.Cond {
			if c.Key == "screen" {
				want = c.Val
			}
		}
		if e.Name != evUsage || want == "" || e.Props == nil {
			return false
		}
		var timeMap map[string]float64
		if raw, ok := e.Props["time"]; ok && json.Unmarshal(raw, &timeMap) == nil && timeMap[want] > 0 {
			return true
		}
		var taps map[string]int
		if raw, ok := e.Props["taps"]; ok && json.Unmarshal(raw, &taps) == nil {
			for k := range taps {
				if strings.HasPrefix(k, want+"/") {
					return true
				}
			}
		}
		return false
	}
	if e.Name != st.Name {
		return false
	}
	for _, c := range st.Cond {
		if !c.holds(e) {
			return false
		}
	}
	return true
}

// seqGist — чем событие важно для «куда ушли» и для пути игрока.
func seqGist(e seqEvent) string {
	switch e.Name {
	case evUsage:
		var timeMap map[string]float64
		if raw, ok := e.Props["time"]; ok && json.Unmarshal(raw, &timeMap) == nil {
			best, bestS := "", 0.0
			for k, v := range timeMap {
				if v > bestS {
					best, bestS = k, v
				}
			}
			if best != "" {
				return "screen:" + best
			}
		}
		return "ui_use"
	case evChapterStart, evChapterFinish:
		return e.Name + " " + e.str("chapter")
	case evChapterAbandon:
		if at, ok := e.num("at"); ok {
			return e.Name + " " + e.str("chapter") + " #" + strconv.Itoa(int(at))
		}
		return e.Name + " " + e.str("chapter")
	case evLabelReach:
		return "label " + e.str("label")
	case evChoicePick:
		return "choice " + clip(e.str("text"), 40)
	case "wardrobe_buy", "wardrobe_buy_fail", "spend_declined", "spend_denied":
		return e.Name + " " + e.str("sku")
	}
	return e.Name
}

// readSequences — события окна по игрокам (user, иначе sid), по времени.
func (s *AnalyticsService) readSequences(days []string, only string) map[string][]seqEvent {
	byUser := map[string][]seqEvent{}
	for _, day := range days {
		f, err := os.Open(s.rollups.dayPath(day))
		if err != nil {
			continue
		}
		sc := bufio.NewScanner(f)
		sc.Buffer(make([]byte, 64<<10), 1<<20)
		for sc.Scan() {
			var ev rollupEvent
			if json.Unmarshal(sc.Bytes(), &ev) != nil || ev.Name == "" {
				continue
			}
			who := ev.User
			if who == "" {
				who = firstNonEmptyStr(ev.SID, rollupProp(ev.Props, "sid"))
			}
			if who == "" || (only != "" && who != only) {
				continue
			}
			chapter := firstNonEmptyStr(ev.Chapter, rollupProp(ev.Props, "chapter"))
			byUser[who] = append(byUser[who], seqEvent{Name: ev.Name, TS: normalizeAnalyticsTS(ev.TS, day), Chapter: chapter, Props: ev.Props})
		}
		f.Close()
	}
	for who := range byUser {
		list := byUser[who]
		sort.SliceStable(list, func(i, j int) bool { return list[i].TS < list[j].TS })
	}
	return byUser
}

type seqStepRow struct {
	Label   string      `json:"label"`
	Users   int         `json:"users"`
	OfFirst float64     `json:"of_first"`
	OfPrev  float64     `json:"of_prev"`
	Lost    int         `json:"lost"`
	Instead []nameCount `json:"instead,omitempty"` // куда ушли те, кто не дошёл до следующего
}

type sequenceReport struct {
	Steps   []seqStepRow `json:"steps"`
	Players int          `json:"players"`
	Note    string       `json:"note,omitempty"`
}

// sequenceFunnel — воронка по шагам по игрокам в порядке времени.
func sequenceFunnel(byUser map[string][]seqEvent, steps []seqStep) sequenceReport {
	rep := sequenceReport{Players: len(byUser)}
	if len(steps) == 0 {
		rep.Note = "шаги не заданы"
		return rep
	}
	counts := make([]int, len(steps))
	instead := make([]map[string]int, len(steps))
	for i := range instead {
		instead[i] = map[string]int{}
	}
	for _, list := range byUser {
		reached := 0
		lastAt := -1
		for idx, e := range list {
			if reached < len(steps) && steps[reached].matches(e) {
				reached++
				lastAt = idx
				counts[reached-1]++
			}
		}
		// не дошёл до следующего: первое событие после последнего шага —
		// и есть «куда ушёл вместо»
		if reached > 0 && reached < len(steps) && lastAt+1 < len(list) {
			instead[reached-1][seqGist(list[lastAt+1])]++
		}
	}
	first := counts[0]
	prev := first
	for i, st := range steps {
		row := seqStepRow{Label: st.Label, Users: counts[i], OfFirst: ratio(counts[i], first), OfPrev: ratio(counts[i], prev)}
		if prev > counts[i] {
			row.Lost = prev - counts[i]
		}
		if i < len(steps)-1 {
			row.Instead = topCounts(instead[i], 8)
		}
		rep.Steps = append(rep.Steps, row)
		prev = counts[i]
	}
	if first == 0 {
		rep.Note = "первый шаг никто не сделал в этом окне — проверьте имя события и условия"
	}
	return rep
}

// sequencesFor — общий вход трёх отчётов по игрокам: права, окно, чтение
// дней. Ответ false — отказ уже написан в w.
func (s *AnalyticsService) sequencesFor(w http.ResponseWriter, r *http.Request, only string) (map[string][]seqEvent, bool) {
	if !s.adminOK(w, r) {
		return nil, false
	}
	win, err := parseAnalyticsWindow(r)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return nil, false
	}
	return s.readSequences(win.Days, only), true
}

// GET /v1/analytics/sequence?days=&steps=a:k=v;b;… — воронка по шагам.
func (s *AnalyticsService) handleSequence(w http.ResponseWriter, r *http.Request) {
	byUser, ok := s.sequencesFor(w, r, "")
	if !ok {
		return
	}
	writeJSON(w, http.StatusOK, sequenceFunnel(byUser, parseSteps(clip(r.URL.Query().Get("steps"), 2000))))
}

type timelineRow struct {
	TS   string `json:"ts"`
	Name string `json:"name"`
	Gist string `json:"gist"`
	SID  string `json:"sid,omitempty"`
}

// GET /v1/analytics/player?user=&days= — путь игрока: лента событий по времени.
func (s *AnalyticsService) handlePlayer(w http.ResponseWriter, r *http.Request) {
	user := clip(r.URL.Query().Get("user"), 64)
	if user == "" {
		http.Error(w, "user required", http.StatusBadRequest)
		return
	}
	byUser, ok := s.sequencesFor(w, r, user)
	if !ok {
		return
	}
	list := byUser[user]
	rows := make([]timelineRow, 0, len(list))
	for _, e := range list {
		rows = append(rows, timelineRow{TS: e.TS, Name: e.Name, Gist: seqGist(e), SID: rollupProp(e.Props, "sid")})
		if len(rows) >= 3000 {
			break
		}
	}
	writeJSON(w, http.StatusOK, map[string]any{"user": user, "events": rows})
}

// ПУТИ (Илья 16.09: «какой процент на магазин кликает в меню, на гардероб и
// т.д.»): для экрана — доля игроков, побывавших на нём, которые нажали
// каждый элемент, и куда они после этого попали (цепочки «нажал → сделал»).
// Считается по ИГРОКАМ, а не по тапам: один игрок, десять раз нажавший
// «Магазин», — это один игрок.
type pathElement struct {
	Element string      `json:"element"`
	Players int         `json:"players"`
	Share   float64     `json:"share"` // от игроков, бывших на экране
	Taps    int         `json:"taps"`
	Next    []nameCount `json:"next,omitempty"` // куда попали после нажатия (по игрокам)
}

type pathsReport struct {
	Screen   string        `json:"screen"`
	Players  int           `json:"players"` // бывали на экране
	Elements []pathElement `json:"elements"`
	Screens  []nameCount   `json:"screens,omitempty"` // все экраны окна по игрокам — для выбора
	Note     string        `json:"note,omitempty"`
}

func pathsFor(byUser map[string][]seqEvent, screen string) pathsReport {
	rep := pathsReport{Screen: screen}
	onScreen := map[string]int{}
	players := map[string]bool{}
	taps := map[string]int{}
	tapPlayers := map[string]map[string]bool{}
	next := map[string]map[string]map[string]bool{} // element → outcome → players
	for who, list := range byUser {
		for _, e := range list {
			if e.Name != evUsage || e.Props == nil {
				continue
			}
			var timeMap map[string]float64
			if raw, ok := e.Props["time"]; ok && json.Unmarshal(raw, &timeMap) == nil {
				for scr, secs := range timeMap {
					if secs > 0 {
						onScreen[scr]++ // по минутам; ниже — по игрокам
					}
					if scr == screen && secs > 0 {
						players[who] = true
					}
				}
			}
			var tp map[string]int
			if raw, ok := e.Props["taps"]; ok && json.Unmarshal(raw, &tp) == nil {
				for k, n := range tp {
					if !strings.HasPrefix(k, screen+"/") {
						continue
					}
					el := k[len(screen)+1:]
					taps[el] += n
					if tapPlayers[el] == nil {
						tapPlayers[el] = map[string]bool{}
					}
					tapPlayers[el][who] = true
					players[who] = true
				}
			}
			var ch map[string]int
			if raw, ok := e.Props["chains"]; ok && json.Unmarshal(raw, &ch) == nil {
				for k := range ch {
					if !strings.HasPrefix(k, screen+"/") {
						continue
					}
					el, outcome, ok := strings.Cut(k[len(screen)+1:], ">")
					if !ok {
						continue
					}
					if next[el] == nil {
						next[el] = map[string]map[string]bool{}
					}
					if next[el][outcome] == nil {
						next[el][outcome] = map[string]bool{}
					}
					next[el][outcome][who] = true
				}
			}
		}
	}
	rep.Players = len(players)
	for el, set := range tapPlayers {
		row := pathElement{Element: el, Players: len(set), Share: ratio(len(set), rep.Players), Taps: taps[el]}
		counts := map[string]int{}
		for outcome, ps := range next[el] {
			counts[outcome] = len(ps)
		}
		row.Next = topCounts(counts, 6)
		rep.Elements = append(rep.Elements, row)
	}
	sort.Slice(rep.Elements, func(i, j int) bool {
		if rep.Elements[i].Players != rep.Elements[j].Players {
			return rep.Elements[i].Players > rep.Elements[j].Players
		}
		return rep.Elements[i].Element < rep.Elements[j].Element
	})
	// экраны окна — по числу игроков, чтобы было из чего выбирать
	screenPlayers := map[string]map[string]bool{}
	for who, list := range byUser {
		for _, e := range list {
			if e.Name != evUsage || e.Props == nil {
				continue
			}
			var timeMap map[string]float64
			if raw, ok := e.Props["time"]; ok && json.Unmarshal(raw, &timeMap) == nil {
				for scr, secs := range timeMap {
					if secs <= 0 {
						continue
					}
					if screenPlayers[scr] == nil {
						screenPlayers[scr] = map[string]bool{}
					}
					screenPlayers[scr][who] = true
				}
			}
		}
	}
	counts := map[string]int{}
	for scr, set := range screenPlayers {
		counts[scr] = len(set)
	}
	rep.Screens = topCounts(counts, 40)
	_ = onScreen
	if rep.Players == 0 {
		rep.Note = "на этом экране в окне никого не было — события использования приезжают со сборкой, где включён LvnUsage"
	}
	return rep
}

// GET /v1/analytics/paths?days=&screen=home — пути с экрана по игрокам.
func (s *AnalyticsService) handlePaths(w http.ResponseWriter, r *http.Request) {
	screen := clip(r.URL.Query().Get("screen"), 64)
	if screen == "" {
		screen = "home"
	}
	byUser, ok := s.sequencesFor(w, r, "")
	if !ok {
		return
	}
	writeJSON(w, http.StatusOK, pathsFor(byUser, screen))
}
