package main

// Вариант А против варианта Б: одна таблица, по которой принимают решение.
//
// Отчёт намеренно показывает НЕ ОДНО число. Вариант, поднявший покупки и
// уронивший дочитывание, — это не победа, а разменянное завтра на сегодня;
// увидеть такое можно только рядом. Поэтому здесь всегда четыре показателя:
// дошли до конца главы, вернулись назавтра, доля платящих, доход на игрока.
// Первый выбирают целью, остальные работают предохранителями.
//
// ЗНАЧИМОСТЬ. Главная опасность маленькой выборки — не ошибиться в расчётах, а
// принять шум за результат. На сотне игроков разница в пять процентов не
// значит ничего, и отчёт обязан говорить это словами, а не оставлять читателя
// наедине со стрелочкой «+5%». Считаем нормальное приближение для разности
// долей: при наших числах его хватает, а чего не хватает — так это терпения,
// и об этом отчёт тоже пишет.
//
// ПОДГЛЯДЫВАНИЕ. Остановить тест в тот момент, когда он «выглядит хорошо», —
// самый частый способ выкатить шум. Поэтому отчёт показывает, сколько игроков
// нужно НА САМОМ ДЕЛЕ, чтобы заметить разницу такого размера.

import (
	"math"
	"net/http"
	"sort"
	"time"
)

type variantStats struct {
	Variant string `json:"variant"`
	Players int    `json:"players"`

	Started  int     `json:"chapter_starts"`
	Finished int     `json:"chapter_finishes"`
	Complete float64 `json:"completion"` // дочитали / начали

	Returned  int     `json:"returned_next_day"`
	Retention float64 `json:"retention_d1"`

	Payers     int     `json:"payers"`
	Conversion float64 `json:"conversion"`
	Revenue    float64 `json:"revenue"`
	ARPU       float64 `json:"arpu"`
}

// verdictLine — вывод по ОДНОМУ показателю, словами.
type verdictLine struct {
	Metric      string  `json:"metric"`
	Base        float64 `json:"base"`
	Test        float64 `json:"test"`
	Delta       float64 `json:"delta"`
	Significant bool    `json:"significant"`
	NeedPlayers int     `json:"need_players,omitempty"` // сколько нужно на группу
	Text        string  `json:"text"`
}

type experimentReport struct {
	Name     string         `json:"name"`
	From     string         `json:"from"`
	To       string         `json:"to"`
	Variants []variantStats `json:"variants"`
	Verdict  []verdictLine  `json:"verdict,omitempty"`
	Note     string         `json:"note,omitempty"`
	Notes    []string       `json:"notes,omitempty"`
}

// GET /v1/analytics/experiment?name=первая_сцена&days=14
func (s *AnalyticsService) handleExperiment(w http.ResponseWriter, r *http.Request) {
	if !s.adminOK(w, r) {
		return
	}
	win, err := parseAnalyticsWindow(r)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	name := clip(r.URL.Query().Get("name"), 64)
	if name == "" {
		http.Error(w, "нужен name эксперимента", http.StatusBadRequest)
		return
	}
	rep := experimentReport{Name: name, From: win.From, To: win.To}

	// Всё считаем ПО ИГРОКАМ, а не по событиям: перепрохождение главы одним
	// человеком не делает вариант лучше, а по событиям выглядело бы именно так.
	type playerFacts struct {
		variant  string
		started  bool
		finished bool
		firstDay string
		next     bool
	}
	facts := map[string]*playerFacts{}
	days := append([]string(nil), win.Days...)
	sort.Strings(days)
	inWindow := map[string]bool{}
	for _, d := range days {
		inWindow[d] = true
	}

	s.rollups.mu.Lock()
	for _, d := range days {
		for uid, pr := range s.rollups.day(d).Users {
			f := facts[uid]
			if f == nil {
				f = &playerFacts{firstDay: d}
				facts[uid] = f
			}
			if v := pr.AB[name]; v != "" && f.variant == "" {
				f.variant = v
			}
			for _, st := range pr.Steps {
				switch st {
				case evChapterStart:
					f.started = true
				case evChapterFinish:
					f.finished = true
				}
			}
			if d != f.firstDay {
				// Достаточно самого факта возврата в другой день окна: это и
				// есть «вернулся», а точный день считает отчёт удержания.
				f.next = true
			}
		}
	}
	s.rollups.mu.Unlock()

	groups := map[string][]*playerFacts{}
	for _, f := range facts {
		if f.variant != "" {
			groups[f.variant] = append(groups[f.variant], f)
		}
	}
	if len(groups) == 0 {
		rep.Note = "по этому эксперименту в окне нет данных: группа приходит в props событий, " +
			"значит нужен клиент со сборкой, где эксперимент уже объявлен"
		writeJSON(w, http.StatusOK, rep)
		return
	}

	payers := map[string]bool{}
	revenue := map[string]float64{}
	if s.payments != nil {
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
	uidOf := map[*playerFacts]string{}
	for uid, f := range facts {
		uidOf[f] = uid
	}

	var names []string
	for v := range groups {
		names = append(names, v)
	}
	sort.Strings(names)

	for _, v := range names {
		st := variantStats{Variant: v, Players: len(groups[v])}
		for _, f := range groups[v] {
			uid := uidOf[f]
			if f.started {
				st.Started++
			}
			if f.finished {
				st.Finished++
			}
			if f.next {
				st.Returned++
			}
			if payers[uid] {
				st.Payers++
			}
			st.Revenue += revenue[uid]
		}
		st.Complete = ratioF(st.Finished, st.Started)
		st.Retention = ratioF(st.Returned, st.Players)
		st.Conversion = ratioF(st.Payers, st.Players)
		st.ARPU = round2(divide(st.Revenue, float64(st.Players)))
		st.Revenue = round2(st.Revenue)
		rep.Variants = append(rep.Variants, st)
	}

	// Сравниваем со ПЕРВЫМ вариантом: он и есть «как было».
	if len(rep.Variants) >= 2 {
		base := rep.Variants[0]
		for _, test := range rep.Variants[1:] {
			rep.Verdict = append(rep.Verdict,
				compareShare("дочитали главу", base.Complete, test.Complete, base.Started, test.Started),
				compareShare("вернулись назавтра", base.Retention, test.Retention, base.Players, test.Players),
				compareShare("доля платящих", base.Conversion, test.Conversion, base.Players, test.Players),
			)
			rep.Verdict = append(rep.Verdict, verdictLine{
				Metric: "доход на игрока", Base: base.ARPU, Test: test.ARPU,
				Delta: round2(test.ARPU - base.ARPU),
				Text: "деньги на маленькой выборке скачут сильнее долей: одна покупка " +
					"сдвигает ARPU целой группы, поэтому здесь смотрят на порядок, а не на проценты",
			})
		}
	}

	rep.Notes = append(rep.Notes,
		"вариант считается лучше, только если целевой показатель вырос, а остальные не просели: "+
			"поднять покупки, уронив дочитывание, — это разменять завтра на сегодня")
	if len(rep.Variants) > 0 && rep.Variants[0].Players < analyticsMinSample {
		rep.Note = "групп мало по размеру: числа показаны, но решать по ним рано"
	}
	writeJSON(w, http.StatusOK, rep)
}

// compareShare — разность двух долей с честным ответом «различимо или нет».
//
// Нормальное приближение: z для разности долей при объединённой оценке.
// Порог 1,96 — это привычные 95%. Когда разница неразличима, считаем и
// говорим, сколько наблюдений на группу понадобилось бы, чтобы её заметить —
// иначе «незначимо» читается как «эффекта нет», а это разные вещи.
func compareShare(metric string, base, test float64, nBase, nTest int) verdictLine {
	out := verdictLine{Metric: metric, Base: round3(base), Test: round3(test),
		Delta: round3(test - base)}
	if nBase < 1 || nTest < 1 {
		out.Text = "нет данных для сравнения"
		return out
	}
	p := (base*float64(nBase) + test*float64(nTest)) / float64(nBase+nTest)
	se := math.Sqrt(p * (1 - p) * (1/float64(nBase) + 1/float64(nTest)))
	if se == 0 {
		out.Text = "различий нет"
		return out
	}
	z := math.Abs(test-base) / se
	out.Significant = z >= 1.96
	if out.Significant {
		dir := "лучше"
		if test < base {
			dir = "ХУЖЕ"
		}
		out.Text = "разница различима: вариант " + dir
		return out
	}
	// Сколько нужно, чтобы такую разницу заметить: n на группу для z=1.96.
	if d := math.Abs(test - base); d > 0 {
		need := int(math.Ceil(2 * p * (1 - p) * math.Pow(1.96/d, 2)))
		out.NeedPlayers = need
		out.Text = "разница пока неразличима — это НЕ значит «эффекта нет». " +
			"Чтобы заметить разницу такого размера, нужно около " + itoa(need) + " наблюдений в каждой группе"
	} else {
		out.Text = "разницы нет"
	}
	return out
}

func round3(v float64) float64 { return math.Round(v*1000) / 1000 }

// timeParseRFC — день события в UTC, строкой.
func timeParseRFC(ts string) (string, error) {
	t, err := time.Parse(time.RFC3339, ts)
	if err != nil {
		return "", err
	}
	return t.UTC().Format("2006-01-02"), nil
}
