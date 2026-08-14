package main

// Удержание: сколько из пришедших в один день вернулись потом.
//
// Метрика номер один для любого продукта, который собирается покупать трафик.
// Без неё реклама — трата вслепую: неизвестно, задерживается ли человек, за
// которого заплатили. Всё остальное (воронки, конверсии по слайдам) отвечает
// на вопрос «что чинить», и только удержание отвечает на «стоит ли вообще
// лить».
//
// Считается по УЖЕ ЛЕЖАЩИМ данным: суточная свёртка хранит игроков дня, а
// когорта — это день, когда игрок появился впервые. Ни одного нового события
// на клиенте не нужно, поэтому отчёт работает и на исторических данных.
//
// Важная оговорка, которую отчёт делает сам: когорта видна только внутри окна.
// Игрок, впервые пришедший ДО начала окна, в своей когорте не числится — иначе
// «новичками» стали бы все, кто просто вернулся.

import (
	"net/http"
	"sort"
	"time"
)

// retentionRow — одна когорта: сколько пришло и сколько вернулось на N-й день.
type retentionRow struct {
	Day      string             `json:"day"`            // день первого прихода
	Size     int                `json:"size"`           // сколько игроков пришло впервые
	Back     map[string]int     `json:"back"`           // «1» → сколько вернулось на следующий день
	Share    map[string]float64 `json:"share"`          // то же в долях
	Retained int                `json:"retained_total"` // вернулись хоть раз после первого дня
}

type retentionReport struct {
	From    string             `json:"from"`
	To      string             `json:"to"`
	Cohorts []retentionRow     `json:"cohorts"`
	Overall map[string]float64 `json:"overall"` // средневзвешенное по когортам
	Players int                `json:"players"`
	Note    string             `json:"note,omitempty"`
}

// checkpoints — дни, за которые принято отчитываться. D1 и D7 решают всё:
// первый показывает, понятна ли игра, второй — есть ли привычка.
var retentionDays = []int{1, 3, 7, 14, 30}

func (s *AnalyticsService) handleRetention(w http.ResponseWriter, r *http.Request) {
	if !s.adminOK(w, r) {
		return
	}
	win, err := parseAnalyticsWindow(r)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}

	// День → множество игроков, активных в этот день.
	active := map[string]map[string]bool{}
	s.rollups.mu.Lock()
	for _, d := range win.Days {
		day := s.rollups.day(d)
		set := make(map[string]bool, len(day.Users))
		for uid := range day.Users {
			set[uid] = true
		}
		active[d] = set
	}
	s.rollups.mu.Unlock()

	days := append([]string(nil), win.Days...)
	sort.Strings(days)

	// Когорта игрока — первый день, в котором он вообще замечен внутри окна.
	first := map[string]string{}
	for _, d := range days {
		for uid := range active[d] {
			if _, seen := first[uid]; !seen {
				first[uid] = d
			}
		}
	}

	rep := retentionReport{To: win.To, Players: len(first)}
	if len(days) > 0 {
		rep.From = days[0]
	}
	byCohort := map[string][]string{}
	for uid, d := range first {
		byCohort[d] = append(byCohort[d], uid)
	}

	sums := map[string]int{}  // сколько вернулось суммарно по всем когортам
	bases := map[string]int{} // сколько МОГЛО вернуться (когорта успела дожить до дня N)
	for _, d := range days {
		members := byCohort[d]
		if len(members) == 0 {
			continue
		}
		row := retentionRow{Day: d, Size: len(members),
			Back: map[string]int{}, Share: map[string]float64{}}
		base, _ := time.Parse("2006-01-02", d)
		returned := map[string]bool{}
		for _, n := range retentionDays {
			target := base.AddDate(0, 0, n).Format("2006-01-02")
			set, inWindow := active[target]
			if !inWindow {
				// День ещё не наступил или вне окна: не пишем ноль — ноль
				// здесь означал бы «никто не вернулся», а это неправда.
				continue
			}
			cnt := 0
			for _, uid := range members {
				if set[uid] {
					cnt++
					returned[uid] = true
				}
			}
			key := itoa(n)
			row.Back[key] = cnt
			row.Share[key] = ratio(cnt, len(members))
			sums[key] += cnt
			bases[key] += len(members)
		}
		row.Retained = len(returned)
		rep.Cohorts = append(rep.Cohorts, row)
	}

	rep.Overall = map[string]float64{}
	for key, base := range bases {
		rep.Overall[key] = ratio(sums[key], base)
	}
	if len(rep.Cohorts) == 0 {
		rep.Note = "в окне нет ни одной когорты — данных мало или окно слишком короткое"
	} else if rep.Players < analyticsMinSample {
		rep.Note = "выборка мала: числа показаны, но доверять им как процентам рано"
	}
	writeJSON(w, http.StatusOK, rep)
}
