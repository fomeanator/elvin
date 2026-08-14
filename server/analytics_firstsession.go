package main

// Первая сессия: что происходит в первые пять минут.
//
// Самое дорогое место в мобильной игре. Удержание говорит, что человек не
// вернулся; эта воронка говорит, докуда он вообще дошёл в первый раз — и
// именно она отвечает, куда бить: в загрузку, в витрину или в первую главу.
//
// Считается ПО ИГРОКАМ и только по их ПЕРВОМУ дню. Смешивать сюда возвраты
// нельзя: ветеран, зашедший почитать двадцатую главу, сделает вид, что
// новички прекрасно доходят до конца.
//
// Ступени намеренно грубые — их пять, и каждая означает решение игрока, а не
// техническое событие:
//
//	запустил → увидел первый экран → начал главу → дочитал главу → вернулся назавтра
//
// Между «запустил» и «увидел экран» человек смотрит на загрузку: потери здесь
// чинятся не сюжетом, а весом и скоростью, поэтому ступень отдельная.

import (
	"net/http"
	"sort"
	"time"
)

type sessionStep struct {
	Step    string  `json:"step"`
	Players int     `json:"players"`
	OfPrev  float64 `json:"of_prev"`  // доля от предыдущей ступени
	OfStart float64 `json:"of_start"` // доля от всех запустивших
	// NoData — событие этой ступени не приходило в окне НИ РАЗУ: значит
	// ступень не «провалена», а ещё не измеряется (клиент старой сборки).
	// Без этого различия отчёт называет худшей ступень, которой просто нет,
	// и отправляет чинить то, что не сломано.
	NoData bool `json:"no_data,omitempty"`
}

type firstSessionReport struct {
	From      string        `json:"from"`
	To        string        `json:"to"`
	Newcomers int           `json:"newcomers"`
	Steps     []sessionStep `json:"steps"`
	BootMs    *bootTiming   `json:"boot_ms,omitempty"`
	Worst     string        `json:"worst_step,omitempty"`
	Segment   string        `json:"segment,omitempty"`
	Note      string        `json:"note,omitempty"`
}

// bootTiming — сколько человек ждал загрузки. Медиана честнее среднего: одно
// зависшее устройство на слабой сети перекашивает среднее так, что по нему
// нельзя принять ни одного решения.
type bootTiming struct {
	Median int `json:"median"`
	P90    int `json:"p90"`
	Max    int `json:"max"`
	N      int `json:"n"`
}

func (s *AnalyticsService) handleFirstSession(w http.ResponseWriter, r *http.Request) {
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

	days := append([]string(nil), win.Days...)
	sort.Strings(days)

	// Первый день каждого игрока и что он в этот день успел.
	type firstDay struct {
		day     string
		steps   map[string]bool
		bootMs  []int
		nextDay bool
	}
	seen := map[string]*firstDay{}

	s.rollups.mu.Lock()
	for _, d := range days {
		day := s.rollups.day(d)
		for uid, pr := range day.Users {
			if members != nil && !members[uid] {
				continue
			}
			if _, known := seen[uid]; !known {
				seen[uid] = &firstDay{day: d, steps: map[string]bool{}}
			}
			fd := seen[uid]
			if fd.day != d {
				// Игрок вернулся в другой день — для этой воронки важен лишь
				// сам факт возврата назавтра, остальное считает удержание.
				continue
			}
			for _, st := range pr.Steps {
				fd.steps[st] = true
			}
			fd.bootMs = append(fd.bootMs, pr.BootMs...)
		}
	}
	// Возврат на следующий день — последняя ступень воронки: без неё «дочитал
	// главу» ещё ничего не значит.
	for uid, fd := range seen {
		base, err := time.Parse("2006-01-02", fd.day)
		if err != nil {
			continue
		}
		next := base.AddDate(0, 0, 1).Format("2006-01-02")
		for _, d := range days {
			if d != next {
				continue
			}
			if _, ok := s.rollups.day(d).Users[uid]; ok {
				fd.nextDay = true
			}
		}
	}
	// Какие события вообще встречались в окне — чтобы отличить «никто не
	// дошёл» от «ещё не измеряем».
	present := map[string]bool{}
	for _, d := range days {
		for name, n := range s.rollups.day(d).Names {
			if n > 0 {
				present[name] = true
			}
		}
	}
	s.rollups.mu.Unlock()

	rep := firstSessionReport{To: win.To, Newcomers: len(seen), Segment: seg.Human()}
	if len(days) > 0 {
		rep.From = days[0]
	}

	ladder := []struct{ key, label string }{
		{evBoot, "запустил"},
		{evFirstScreen, "увидел первый экран"},
		{evChapterStart, "начал главу"},
		{evChapterFinish, "дочитал главу"},
	}
	var boots []int
	counts := make([]int, len(ladder)+1)
	for _, fd := range seen {
		for i, st := range ladder {
			if fd.steps[st.key] {
				counts[i]++
			}
		}
		if fd.nextDay {
			counts[len(ladder)]++
		}
		boots = append(boots, fd.bootMs...)
	}

	start := counts[0]
	worstDrop := -1.0
	lastMeasured := start // сколько было на последней ступени, которая измеряется
	for i, st := range ladder {
		prev := start
		if i > 0 {
			// От предыдущей ИЗМЕРЯЕМОЙ: иначе ступень, событий которой ещё
			// нет, обнуляет всю воронку ниже себя.
			prev = lastMeasured
		}
		step := sessionStep{Step: st.label, Players: counts[i],
			OfPrev: ratio(counts[i], prev), OfStart: ratio(counts[i], start),
			NoData: !present[st.key]}
		rep.Steps = append(rep.Steps, step)
		if i > 0 && prev > 0 && !step.NoData {
			if drop := 1 - step.OfPrev; drop > worstDrop {
				worstDrop, rep.Worst = drop, st.label
			}
		}
		if !step.NoData {
			lastMeasured = counts[i]
		}
	}
	rep.Steps = append(rep.Steps, sessionStep{Step: "вернулся назавтра",
		Players: counts[len(ladder)], OfPrev: ratio(counts[len(ladder)], lastMeasured),
		OfStart: ratio(counts[len(ladder)], start)})

	if len(boots) > 0 {
		sort.Ints(boots)
		rep.BootMs = &bootTiming{
			Median: boots[len(boots)/2],
			P90:    boots[(len(boots)*9)/10],
			Max:    boots[len(boots)-1],
			N:      len(boots),
		}
	}
	if rep.Newcomers < analyticsMinSample {
		rep.Note = "новичков мало: ступени видны, но проценты пока ни о чём не говорят"
	}
	writeJSON(w, http.StatusOK, rep)
}
