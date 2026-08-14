package main

// «Важные места»: воронка по меткам, которые расставил автор.
//
// Отчёт по слайдам строится по ВСЕМ авторским меткам, а их в живой главе сорок
// семь и называются они n26_000000 — это адрес для компилятора, а не для
// человека. Работать по такому списку нельзя: значимых мест в главе единицы, и
// найти их среди сорока семи строк невозможно.
//
// track "первый поцелуй" решает ровно это: автор сам говорит, где важное, и
// сам это называет. Метка ничего не делает в кадре — вся её работа в том,
// чтобы попасть сюда.
//
// СЧИТАЕМ ЛЮДЕЙ, А НЕ СОБЫТИЯ. «Сколько человек добралось до первого поцелуя» —
// вопрос про людей; перепрохождение главы одним игроком не делает их больше.
// Событий тоже показываем — по их отрыву от числа людей видно, что место
// проходят повторно.
//
// ДЕНЬГИ РЯДОМ. Вопрос, который задают на самом деле, звучит «какой процент на
// каком месте отваливается ИЛИ ПЛАТИТ». Поэтому в строке метки есть доля
// платящих среди дошедших. Формулировка осторожная намеренно: мы знаем, что
// человек дошёл до метки и что он платил в этом окне, но не знаем, что он
// заплатил ИМЕННО ПОСЛЕ неё — свёртка хранит день, а не порядок. Обещать
// причинность там, где есть только совпадение, нельзя.

import (
	"net/http"
	"sort"
)

type markRow struct {
	Mark    string  `json:"mark"`
	Players int     `json:"players"` // сколько ЧЕЛОВЕК дошло
	Events  int     `json:"events"`  // сколько раз пройдена
	OfAll   float64 `json:"of_all"`  // доля от всех игроков окна
	OfPrev  float64 `json:"of_prev"` // доля от предыдущей метки
	Lost    int     `json:"lost"`

	Payers     int     `json:"payers"`
	Conversion float64 `json:"conversion"` // платящих среди дошедших
	Revenue    float64 `json:"revenue"`
}

type marksReport struct {
	From    string    `json:"from"`
	To      string    `json:"to"`
	Title   string    `json:"title,omitempty"`
	Players int       `json:"players"`
	Marks   []markRow `json:"marks,omitempty"`
	Worst   string    `json:"worst_mark,omitempty"`
	Segment string    `json:"segment,omitempty"`
	Note    string    `json:"note,omitempty"`
	Notes   []string  `json:"notes,omitempty"`
}

// GET /v1/analytics/marks?title=…&days=30&segment=…
func (s *AnalyticsService) handleMarks(w http.ResponseWriter, r *http.Request) {
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

	rep := marksReport{From: win.From, To: win.To, Title: title, Segment: seg.Human()}

	// Кто до каких меток дошёл — по игрокам.
	reached := map[string]map[string]bool{}
	players := map[string]bool{}
	s.rollups.mu.Lock()
	for _, d := range win.Days {
		for uid, pr := range s.rollups.day(d).Users {
			if members != nil && !members[uid] {
				continue
			}
			players[uid] = true
			for _, m := range pr.Marks {
				if reached[m] == nil {
					reached[m] = map[string]bool{}
				}
				reached[m][uid] = true
			}
		}
	}
	// Сколько РАЗ пройдена — из свёртки главы; если задан title, только его.
	events := map[string]int{}
	m, _ := s.windowFor(win.Days, members)
	for tid, t := range m.Titles {
		if title != "" && tid != title {
			continue
		}
		for _, ch := range t.Chapters {
			for name, n := range ch.Marks {
				events[name] += n
			}
		}
	}
	s.rollups.mu.Unlock()
	rep.Players = len(players)

	if len(reached) == 0 {
		rep.Note = "авторских меток в окне нет. Ставятся в сценарии: track \"первый поцелуй\" — " +
			"и приезжают со сборкой, где эта команда уже есть"
		writeJSON(w, http.StatusOK, rep)
		return
	}

	payers := map[string]bool{}
	revenue := map[string]float64{}
	if s.payments != nil {
		inWindow := map[string]bool{}
		for _, d := range win.Days {
			inWindow[d] = true
		}
		for _, p := range s.payments.Purchases() {
			day, err := timeParseRFC(p.TS)
			if err != nil || !inWindow[day] {
				continue
			}
			payers[p.User] = true
			if v, _, ok := s.payments.Price(p.SKU); ok {
				revenue[p.User] += v
			}
		}
	}

	for name, who := range reached {
		row := markRow{Mark: name, Players: len(who), Events: events[name],
			OfAll: ratio(len(who), rep.Players)}
		for uid := range who {
			if payers[uid] {
				row.Payers++
			}
			row.Revenue += revenue[uid]
		}
		row.Conversion = ratio(row.Payers, row.Players)
		row.Revenue = round2(row.Revenue)
		rep.Marks = append(rep.Marks, row)
	}

	// По убыванию числа дошедших: это и есть порядок прохождения, потому что
	// дальше метка — меньше людей. Порядок в скрипте нам неизвестен (метки
	// могут стоять в разных главах и ветках), и выдумывать его нельзя.
	sort.Slice(rep.Marks, func(i, j int) bool {
		if rep.Marks[i].Players != rep.Marks[j].Players {
			return rep.Marks[i].Players > rep.Marks[j].Players
		}
		return rep.Marks[i].Mark < rep.Marks[j].Mark
	})

	worstLost := 0
	for i := range rep.Marks {
		if i == 0 {
			rep.Marks[i].OfPrev = 1
			continue
		}
		prev := rep.Marks[i-1].Players
		rep.Marks[i].OfPrev = ratio(rep.Marks[i].Players, prev)
		if prev > rep.Marks[i].Players {
			rep.Marks[i].Lost = prev - rep.Marks[i].Players
		}
		if rep.Marks[i].Lost > worstLost {
			worstLost, rep.Worst = rep.Marks[i].Lost, rep.Marks[i].Mark
		}
	}

	rep.Notes = append(rep.Notes,
		"порядок — по числу дошедших, а не по месту в сценарии: метки могут стоять в разных "+
			"главах и ветках, и выдумывать их последовательность нельзя",
		"«платящие» — это доля платящих СРЕДИ ДОШЕДШИХ до метки, а не «заплатили после неё»: "+
			"свёртка хранит день, а не порядок событий")
	if rep.Players < analyticsMinSample {
		rep.Note = "игроков мало: места видны, но проценты пока ни о чём не говорят"
	}
	writeJSON(w, http.StatusOK, rep)
}
