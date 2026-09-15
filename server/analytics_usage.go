package main

// ЧЕМ ПОЛЬЗУЮТСЯ (TR-126). Илья 15.09: «хочу, чтобы мы знали, чем человек
// пользуется, куда кликает и сколько раз, сколько времени проводит — везде».
//
// Устройство раз в минуту присылает одно событие ui_use: секунды по экранам
// и тапы по элементам (LvnUsage). Свёртка складывает их по дням, отчёт
// отвечает: на каких экранах проводят время, во что там жмут и сколько раз.
// Единица — экран интерфейса, как его называет оболочка (story, home,
// GachaScreen…); элемент — имя элемента дерева или надпись кнопки.

import (
	"net/http"
	"sort"
)

type usageScreen struct {
	Screen   string      `json:"screen"`
	Seconds  int         `json:"seconds"`
	Share    float64     `json:"share"` // доля времени от всех экранов
	Taps     int         `json:"taps"`
	Elements []nameCount `json:"elements,omitempty"` // во что жмут, по убыванию
}

type usageReport struct {
	Days         int           `json:"days"`
	TotalSeconds int           `json:"total_seconds"`
	TotalTaps    int           `json:"total_taps"`
	Screens      []usageScreen `json:"screens"`
	Note         string        `json:"note,omitempty"`
}

const usageElementsPerScreen = 40

func usageReportOf(m *dayRollup, days int) usageReport {
	rep := usageReport{Days: days}
	if m == nil || len(m.Usage) == 0 {
		rep.Note = "событий использования за это окно нет — они приезжают со сборкой, где включён LvnUsage"
		return rep
	}
	for screen, u := range m.Usage {
		row := usageScreen{Screen: screen, Seconds: u.Seconds}
		for _, n := range u.Taps {
			row.Taps += n
		}
		row.Elements = topCounts(u.Taps, usageElementsPerScreen)
		rep.TotalSeconds += u.Seconds
		rep.TotalTaps += row.Taps
		rep.Screens = append(rep.Screens, row)
	}
	for i := range rep.Screens {
		rep.Screens[i].Share = ratio(rep.Screens[i].Seconds, rep.TotalSeconds)
	}
	sort.Slice(rep.Screens, func(i, j int) bool {
		if rep.Screens[i].Seconds != rep.Screens[j].Seconds {
			return rep.Screens[i].Seconds > rep.Screens[j].Seconds
		}
		return rep.Screens[i].Screen < rep.Screens[j].Screen
	})
	return rep
}

// GET /v1/analytics/usage?day=|days=|from=&to=[&segment=…] — экраны по
// времени, у каждого — тапы по элементам.
func (s *AnalyticsService) handleUsage(w http.ResponseWriter, r *http.Request) {
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
	s.rollups.mu.Lock()
	m, _ := s.windowFor(win.Days, members)
	rep := usageReportOf(m, len(win.Days))
	s.rollups.mu.Unlock()
	writeJSON(w, http.StatusOK, rep)
}
