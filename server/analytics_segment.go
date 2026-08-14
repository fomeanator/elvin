package main

// Сегменты: один параметр запроса, который сужает ЛЮБОЙ отчёт.
//
// Средние по всем игрокам врут. Новичок этой недели и тестер, играющий с июля,
// ведут себя по-разному; смешав их, получаешь число, которого нет ни у кого.
// Вопрос, который надо уметь задавать, звучит так: «удержание на второй главе
// у когорты этой недели, пришедшей с кампании X, в группе B».
//
// Поэтому сегмент — не отдельный отчёт, а ПАРАМЕТР ко всем:
//
//	?segment=channel:telegram/aug
//	?segment=cohort:2026-08-14
//	?segment=ab:первая_сцена=b
//	?segment=payer:yes
//	?segment=channel:telegram/aug,ab:первая_сцена=b   ← и вместе
//
// Отдельный отчёт «по сегментам» был бы ловушкой: он отвечает только на
// заранее придуманные вопросы, а сегментация нужна ровно тогда, когда вопрос
// не придуман заранее.
//
// ЦЕНА. Свёртка на диске сложена по ВСЕМ игрокам, и вынуть из неё одну группу
// нельзя: суммы уже сложены. Значит сегментированный запрос складывает сырьё
// заново, с отбором по игроку. Без сегмента всё работает как раньше, через
// чекпоинты. Это осознанный размен: сегмент спрашивают редко и обдуманно, а
// сводку — на каждом обновлении экрана.

import (
	"fmt"
	"net/http"
	"sort"
	"strings"
	"time"
)

// segmentSpec — разобранный запрос сегмента.
type segmentSpec struct {
	Raw      string
	Channel  string // канал привлечения (первое касание)
	Cohort   string // день первого прихода
	ABTest   string // имя эксперимента…
	ABGroup  string // …и группа в нём
	PayerYes bool   // только платящие
	PayerNo  bool   // только неплатящие
}

func (sp segmentSpec) empty() bool {
	return sp.Channel == "" && sp.Cohort == "" && sp.ABTest == "" && !sp.PayerYes && !sp.PayerNo
}

// Human — как сегмент назвать в ответе, чтобы читатель не гадал, на какую
// часть аудитории он смотрит.
func (sp segmentSpec) Human() string {
	var parts []string
	if sp.Channel != "" {
		parts = append(parts, "канал "+sp.Channel)
	}
	if sp.Cohort != "" {
		parts = append(parts, "пришли "+sp.Cohort)
	}
	if sp.ABTest != "" {
		parts = append(parts, "тест "+sp.ABTest+"="+sp.ABGroup)
	}
	if sp.PayerYes {
		parts = append(parts, "только платящие")
	}
	if sp.PayerNo {
		parts = append(parts, "только неплатящие")
	}
	return strings.Join(parts, ", ")
}

func parseSegment(r *http.Request) (segmentSpec, error) {
	raw := strings.TrimSpace(r.URL.Query().Get("segment"))
	sp := segmentSpec{Raw: raw}
	if raw == "" {
		return sp, nil
	}
	for _, part := range strings.Split(raw, ",") {
		part = strings.TrimSpace(part)
		if part == "" {
			continue
		}
		kv := strings.SplitN(part, ":", 2)
		if len(kv) != 2 {
			return sp, fmt.Errorf("сегмент %q: нужно вид:значение (channel: cohort: ab: payer:)", part)
		}
		key, val := strings.ToLower(strings.TrimSpace(kv[0])), strings.TrimSpace(kv[1])
		switch key {
		case "channel":
			sp.Channel = clip(val, 128)
		case "cohort":
			sp.Cohort = clip(val, 10)
		case "ab":
			eq := strings.SplitN(val, "=", 2)
			if len(eq) != 2 || eq[0] == "" || eq[1] == "" {
				return sp, fmt.Errorf("сегмент ab: нужно ab:<имя теста>=<группа>")
			}
			sp.ABTest, sp.ABGroup = clip(eq[0], 64), clip(eq[1], 64)
		case "payer":
			switch strings.ToLower(val) {
			case "yes", "да", "1", "true":
				sp.PayerYes = true
			case "no", "нет", "0", "false":
				sp.PayerNo = true
			default:
				return sp, fmt.Errorf("сегмент payer: нужно yes или no")
			}
		default:
			return sp, fmt.Errorf("неизвестный вид сегмента %q: есть channel, cohort, ab, payer", key)
		}
	}
	return sp, nil
}

// segmentMembers — множество игроков сегмента, или nil, если сегмент пуст
// (тогда отчёт идёт быстрым путём по чекпоинтам).
//
// Каждое условие сужает множество ПЕРЕСЕЧЕНИЕМ: «канал X и группа B» — это те,
// у кого и то, и другое, а не сумма двух групп.
func (s *AnalyticsService) segmentMembers(sp segmentSpec, days []string) map[string]bool {
	if sp.empty() {
		return nil
	}
	members := map[string]bool{}
	all := true // ещё ни одно условие не сузило множество

	narrow := func(candidate map[string]bool) {
		if all {
			members, all = candidate, false
			return
		}
		for uid := range members {
			if !candidate[uid] {
				delete(members, uid)
			}
		}
	}

	if sp.Channel != "" && s.auth != nil {
		byChannel := map[string]bool{}
		for uid, ch := range s.auth.Channels() {
			if ch == sp.Channel {
				byChannel[uid] = true
			}
		}
		narrow(byChannel)
	}
	if sp.PayerYes || sp.PayerNo {
		payers := map[string]bool{}
		if s.payments != nil {
			for _, p := range s.payments.Purchases() {
				payers[p.User] = true
			}
		}
		if sp.PayerYes {
			narrow(payers)
		} else {
			// «Неплатящие» считаем от тех, кого вообще видели: множество всех
			// людей на свете не бывает знаменателем.
			seen := s.playersInWindow(days)
			nonPayers := map[string]bool{}
			for uid := range seen {
				if !payers[uid] {
					nonPayers[uid] = true
				}
			}
			narrow(nonPayers)
		}
	}
	if sp.Cohort != "" || sp.ABTest != "" {
		byRollup := map[string]bool{}
		firstSeen := map[string]string{}
		sorted := append([]string(nil), days...)
		sort.Strings(sorted)
		s.rollups.mu.Lock()
		for _, d := range sorted {
			for uid, pr := range s.rollups.day(d).Users {
				if _, known := firstSeen[uid]; !known {
					firstSeen[uid] = d
				}
				if sp.ABTest != "" && pr.AB[sp.ABTest] != sp.ABGroup {
					continue
				}
				if sp.Cohort != "" && firstSeen[uid] != sp.Cohort {
					continue
				}
				byRollup[uid] = true
			}
		}
		s.rollups.mu.Unlock()
		narrow(byRollup)
	}
	if all {
		return nil
	}
	return members
}

// playersInWindow — все, кого видели в окне. Отдельно от segmentMembers,
// потому что нужен как знаменатель.
func (s *AnalyticsService) playersInWindow(days []string) map[string]bool {
	out := map[string]bool{}
	s.rollups.mu.Lock()
	defer s.rollups.mu.Unlock()
	for _, d := range days {
		for uid := range s.rollups.day(d).Users {
			out[uid] = true
		}
	}
	return out
}

// windowFor — свёртка окна с учётом сегмента. Без сегмента это ровно прежний
// быстрый путь; с сегментом сырьё складывается заново с отбором по игроку.
//
// Вызывающий обязан держать s.rollups.mu — как и у window().
func (s *AnalyticsService) windowFor(days []string, members map[string]bool) (*dayRollup, []dayReport) {
	if members == nil {
		return s.rollups.window(days)
	}
	// Событие без входа не принадлежит никому и потому не принадлежит ни
	// одному сегменту. Это не потеря, а определение: сегмент — это множество
	// ИГРОКОВ. Но разница в итогах («было 210, стало 207») требует объяснения,
	// поэтому она названа в подписи сегмента, а не оставлена на догадки.
	keep := func(uid string) bool { return uid != "" && members[uid] }
	merged := newDayRollup("")
	out := make([]dayReport, 0, len(days))
	for _, d := range days {
		day := newDayRollup(d)
		day.keep = keep
		s.rollups.advance(day) // читает сырьё дня с нуля, фильтруя на входе
		out = append(out, dayReport{
			Day: day.Day, Events: day.Events, Players: len(day.Users),
			Starts: day.Names[evChapterStart], Finishes: day.Names[evChapterFinish],
			Fails: sumCounts(day.Fails),
		})
		merged.mergeFrom(day)
	}
	return merged, out
}

// firstDayOf — день первого появления игрока в свёртке. Нужен таргету
// экспериментов: «только пришедшим сегодня». Окно берём широкое — когорта это
// свойство игрока, а не отчёта.
func (s *AnalyticsService) firstDayOf(userID string) string {
	if s == nil || s.rollups == nil {
		return ""
	}
	end := time.Now().UTC()
	days, err := daysBetween(end.AddDate(0, 0, -89).Format("2006-01-02"), end.Format("2006-01-02"))
	if err != nil {
		return ""
	}
	s.rollups.mu.Lock()
	defer s.rollups.mu.Unlock()
	for _, d := range days {
		if _, ok := s.rollups.day(d).Users[userID]; ok {
			return d
		}
	}
	return ""
}
