package main

// Деньги: сколько заработали, с кого и за что.
//
// До этого отчёта про деньги было известно ровно одно — «столько-то записей в
// истории кошельков». Ни платящих, ни среднего чека, ни дохода на игрока.
// Магазин включают один раз, и первые недели продаж определяют, что чинить в
// экономике; пройти их вслепую значит потерять именно те данные, ради которых
// магазин и включали.
//
// Три числа, которые здесь главные, и почему их именно три:
//   - КОНВЕРСИЯ — какая доля игроков вообще платит. Отвечает на «работает ли
//     магазин как таковой».
//   - ARPPU — средний доход с ПЛАТЯЩЕГО. Отвечает на «правильно ли собраны
//     паки»: низкий ARPPU при нормальной конверсии значит, что покупают только
//     самый дешёвый.
//   - ARPU — доход на ВСЕХ активных. Единственное из трёх, что сравнимо с
//     ценой привлечения игрока: реклама окупается, когда ARPU выше неё.
//
// Считать одно без двух других бессмысленно: выручка растёт и когда пришло
// больше людей, и когда те же люди стали платить больше, а это разные новости.
//
// Оговорка, которую отчёт делает сам: сумма — это ОЦЕНКА ПО ПРАЙСУ. Стор берёт
// комиссию (~30%), пересчитывает в местную валюту и удерживает налоги. Сходиться
// с выпиской из стора она не обязана и не будет.

import (
	"fmt"
	"math"
	"net/http"
	"sort"
	"time"
)

type skuRow struct {
	SKU       string  `json:"sku"`
	Purchases int     `json:"purchases"`
	Payers    int     `json:"payers"`
	Revenue   float64 `json:"revenue"`
	Share     float64 `json:"share"` // доля в выручке
	Priced    bool    `json:"priced"`
}

// cohortMoney — сколько принесла когорта (день первого прихода). Разрез, ради
// которого всё и считается по когортам: выручка «за месяц» не говорит, окупился
// ли конкретный завоз игроков, а выручка когорты — говорит.
type cohortMoney struct {
	Day     string  `json:"day"`
	Players int     `json:"players"`
	Payers  int     `json:"payers"`
	Revenue float64 `json:"revenue"`
	ARPU    float64 `json:"arpu"`
}

// timing — «сколько часов от первого запуска до первой покупки». Медиана, а не
// среднее: один игрок, купивший через месяц, перекашивает среднее так, что по
// нему нельзя выбрать момент показа магазина.
type timing struct {
	MedianHours float64 `json:"median_hours"`
	P90Hours    float64 `json:"p90_hours"`
	N           int     `json:"n"`
}

type moneyReport struct {
	From        string `json:"from"`
	To          string `json:"to"`
	RefCurrency string `json:"ref_currency,omitempty"`

	Active    int `json:"active_players"` // активные по аналитике — знаменатель ARPU
	Payers    int `json:"payers"`
	Purchases int `json:"purchases"`

	Revenue    float64 `json:"revenue"`
	Conversion float64 `json:"conversion"` // платящих от активных
	ARPU       float64 `json:"arpu"`
	ARPPU      float64 `json:"arppu"`
	AvgCheck   float64 `json:"avg_check"`

	ToFirstPurchase *timing       `json:"to_first_purchase,omitempty"`
	BySKU           []skuRow      `json:"by_sku,omitempty"`
	ByCohort        []cohortMoney `json:"by_cohort,omitempty"`

	// Unpriced — паки, которые покупали, но цена которых неизвестна. НЕ входят
	// в выручку: ноль здесь означал бы «раздали даром» и занизил бы всё молча.
	Unpriced        []string `json:"unpriced_skus,omitempty"`
	UnpricedBuys    int      `json:"unpriced_purchases,omitempty"`
	OtherCurrencies []string `json:"other_currency_skus,omitempty"`

	Note  string   `json:"note,omitempty"`
	Notes []string `json:"notes,omitempty"`
}

// paymentsSource — откуда отчёт берёт покупки и цены. Интерфейс, а не
// *WalletService: аналитике незачем знать, как устроен кошелёк, а тесту проще
// подсунуть три записи, чем каталог файлов.
type paymentsSource interface {
	Purchases() []walletPurchase
	Price(sku string) (value float64, currency string, ok bool)
}

// GET /v1/analytics/money?days=30
func (s *AnalyticsService) handleMoney(w http.ResponseWriter, r *http.Request) {
	if !s.adminOK(w, r) {
		return
	}
	win, err := parseAnalyticsWindow(r)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	rep := moneyReport{To: win.To}
	days := append([]string(nil), win.Days...)
	sort.Strings(days)
	if len(days) > 0 {
		rep.From = days[0]
	}
	if s.payments == nil {
		rep.Note = "кошелёк не подключён — считать нечего"
		writeJSON(w, http.StatusOK, rep)
		return
	}

	// Активные игроки и их когорты — из той же свёртки, что и удержание.
	// Когорта = первый день, в который игрок вообще замечен ВНУТРИ окна;
	// пришедший раньше в когорту не попадает, иначе «новичками» станут все.
	active := map[string]bool{}
	firstDay := map[string]string{}
	firstTS := map[string]time.Time{}
	s.rollups.mu.Lock()
	for _, d := range days {
		day := s.rollups.day(d)
		for uid, pr := range day.Users {
			active[uid] = true
			if _, seen := firstDay[uid]; !seen {
				firstDay[uid] = d
				if ts, err := time.Parse(time.RFC3339, pr.First); err == nil {
					firstTS[uid] = ts
				}
			}
		}
	}
	s.rollups.mu.Unlock()
	rep.Active = len(active)

	// Окно по дням: покупка попадает в отчёт, если её день внутри окна.
	inWindow := map[string]bool{}
	for _, d := range days {
		inWindow[d] = true
	}

	type skuAgg struct {
		buys   int
		payers map[string]bool
		rev    float64
		priced bool
	}
	bySKU := map[string]*skuAgg{}
	payers := map[string]bool{}
	firstBuy := map[string]time.Time{}
	curVotes := map[string]int{}
	unpriced := map[string]bool{}

	all := s.payments.Purchases()
	type pricedBuy struct {
		user, sku string
		value     float64
		cur       string
		ok        bool
		ts        time.Time
	}
	var buys []pricedBuy
	for _, p := range all {
		ts, err := time.Parse(time.RFC3339, p.TS)
		if err != nil || !inWindow[ts.UTC().Format("2006-01-02")] {
			continue
		}
		value, cur, ok := s.payments.Price(p.SKU)
		if ok {
			curVotes[cur]++
		}
		buys = append(buys, pricedBuy{user: p.User, sku: p.SKU, value: value, cur: cur, ok: ok, ts: ts})
	}

	// Валюта отчёта — та, в которой назначено большинство купленных паков.
	// Складывать доллары с рублями нельзя, поэтому всё остальное честно
	// выносится в отдельный список, а не подмешивается в сумму.
	best := 0
	for cur, n := range curVotes {
		if n > best || (n == best && cur < rep.RefCurrency) {
			rep.RefCurrency, best = cur, n
		}
	}
	otherCur := map[string]bool{}

	for _, b := range buys {
		rep.Purchases++
		payers[b.user] = true
		if t, seen := firstBuy[b.user]; !seen || b.ts.Before(t) {
			firstBuy[b.user] = b.ts
		}
		agg := bySKU[b.sku]
		if agg == nil {
			agg = &skuAgg{payers: map[string]bool{}}
			bySKU[b.sku] = agg
		}
		agg.buys++
		agg.payers[b.user] = true

		switch {
		case !b.ok:
			unpriced[b.sku] = true
			rep.UnpricedBuys++
		case b.cur != rep.RefCurrency:
			otherCur[b.sku] = true
			rep.UnpricedBuys++
		default:
			agg.priced = true
			agg.rev += b.value
			rep.Revenue += b.value
		}
	}
	rep.Payers = len(payers)

	// Конверсия — от АКТИВНЫХ, поэтому в числителе только те платящие, кого
	// аналитика видела в окне. Иначе тестовая выдача с чужого устройства даёт
	// конверсию выше ста процентов.
	activePayers := 0
	for uid := range payers {
		if active[uid] {
			activePayers++
		}
	}
	rep.Conversion = ratioF(activePayers, rep.Active)
	// «Платящих 3, конверсия 0%» выглядит как поломка, а означает, что покупка
	// старше первого события аналитики: делить их на активных не на что.
	// Молча показать ноль — соврать тем же способом, что и ноль на ступени
	// воронки, которой ещё не измеряют.
	if invisible := rep.Payers - activePayers; invisible > 0 {
		rep.Notes = append(rep.Notes, fmt.Sprintf(
			"%d из %d платящих не видны в аналитике за это окно (покупка раньше первого события) — в конверсию они не вошли, в выручку вошли",
			invisible, rep.Payers))
	}
	rep.ARPU = divide(rep.Revenue, float64(rep.Active))
	rep.ARPPU = divide(rep.Revenue, float64(rep.Payers))
	rep.AvgCheck = divide(rep.Revenue, float64(rep.Purchases))

	for sku, agg := range bySKU {
		rep.BySKU = append(rep.BySKU, skuRow{SKU: sku, Purchases: agg.buys,
			Payers: len(agg.payers), Revenue: agg.rev,
			Share: divide(agg.rev, rep.Revenue), Priced: agg.priced})
	}
	sort.Slice(rep.BySKU, func(i, j int) bool {
		if rep.BySKU[i].Revenue != rep.BySKU[j].Revenue {
			return rep.BySKU[i].Revenue > rep.BySKU[j].Revenue
		}
		return rep.BySKU[i].SKU < rep.BySKU[j].SKU
	})

	// Время до первой покупки — только для тех, чей первый приход виден внутри
	// окна. Для остальных «сколько прошло» неизвестно, а не ноль.
	var hours []float64
	for uid, t := range firstBuy {
		start, ok := firstTS[uid]
		if !ok || t.Before(start) {
			continue
		}
		hours = append(hours, t.Sub(start).Hours())
	}
	if len(hours) > 0 {
		sort.Float64s(hours)
		rep.ToFirstPurchase = &timing{
			MedianHours: round2(hours[len(hours)/2]),
			P90Hours:    round2(hours[(len(hours)*9)/10]),
			N:           len(hours),
		}
	}

	// Разрез по когортам: сколько принесли пришедшие в такой-то день.
	cohortPlayers := map[string]int{}
	cohortPayers := map[string]map[string]bool{}
	cohortRev := map[string]float64{}
	for _, d := range firstDay {
		cohortPlayers[d]++
		if cohortPayers[d] == nil {
			cohortPayers[d] = map[string]bool{}
		}
	}
	for _, b := range buys {
		d, ok := firstDay[b.user]
		if !ok {
			continue
		}
		cohortPayers[d][b.user] = true
		if b.ok && b.cur == rep.RefCurrency {
			cohortRev[d] += b.value
		}
	}
	for _, d := range days {
		n := cohortPlayers[d]
		if n == 0 {
			continue
		}
		rep.ByCohort = append(rep.ByCohort, cohortMoney{Day: d, Players: n,
			Payers: len(cohortPayers[d]), Revenue: round2(cohortRev[d]),
			ARPU: round2(divide(cohortRev[d], float64(n)))})
	}

	for sku := range unpriced {
		rep.Unpriced = append(rep.Unpriced, sku)
	}
	sort.Strings(rep.Unpriced)
	for sku := range otherCur {
		rep.OtherCurrencies = append(rep.OtherCurrencies, sku)
	}
	sort.Strings(rep.OtherCurrencies)

	rep.Revenue = round2(rep.Revenue)
	rep.ARPU = round2(rep.ARPU)
	rep.ARPPU = round2(rep.ARPPU)
	rep.AvgCheck = round2(rep.AvgCheck)
	for i := range rep.BySKU {
		rep.BySKU[i].Revenue = round2(rep.BySKU[i].Revenue)
	}

	rep.Notes = append(rep.Notes,
		"сумма — оценка по прайсу каталога, а не выручка из стора: комиссия (~30%), местные валюты и налоги здесь не учтены")
	if len(rep.Unpriced) > 0 {
		rep.Notes = append(rep.Notes,
			"у части паков цена неизвестна — они НЕ вошли в выручку; задайте price_value в каталоге")
	}
	if len(rep.OtherCurrencies) > 0 {
		rep.Notes = append(rep.Notes,
			"часть паков продаётся в другой валюте — складывать с "+rep.RefCurrency+" нельзя, они вынесены отдельно")
	}
	if rep.Purchases == 0 {
		rep.Note = "покупок в окне нет — отчёт готов и ждёт включения магазина"
	} else if rep.Payers < analyticsMinSample {
		rep.Note = "платящих мало: суммы верны, но проценты и средние пока ни о чём не говорят"
	}
	writeJSON(w, http.StatusOK, rep)
}

// divide — деление, которое не паникует и не выдумывает бесконечность на
// пустом знаменателе: нет игроков — нет и среднего.
func divide(a, b float64) float64 {
	if b == 0 {
		return 0
	}
	return a / b
}

func ratioF(a, b int) float64 {
	if b == 0 {
		return 0
	}
	return float64(a) / float64(b)
}

// round2 — деньги показываем с копейками. Округление только на выходе: копить
// сумму округлёнными слагаемыми значит терять на каждой покупке.
func round2(v float64) float64 {
	return math.Round(v*100) / 100
}
