package main

// admin_stats.go — статистика трат валют для админки (TR-28): агрегация
// spend-событий из историй кошельков. Разрезы: по дням/месяцам (график),
// по тайтлам («экспедициям»), внутри тайтла — по главам и по типам трат
// (выборы / наряды / вход в главу / вход в тайтл). Источник — walletEntry:
// Title штампуется атрибуцией в момент траты, глава — префиксом reason
// («chapter:<id>»). Только чтение, тяжёлого состояния нет — считается на
// каждый запрос по файлам кошельков (их сотни, не миллионы).

import (
	"net/http"
	"strings"
	"time"
)

type spendStats struct {
	Series []spendBucket `json:"series"`
	Titles []spendTitle  `json:"titles"`
}

type spendBucket struct {
	Bucket string           `json:"bucket"` // "2026-08-25" | "2026-08"
	Sums   map[string]int64 `json:"sums"`   // currency → amount
}

type spendTitle struct {
	Title    string                      `json:"title"` // id тайтла; "" → «вне тайтла»
	Sums     map[string]int64            `json:"sums"`
	Kinds    map[string]map[string]int64 `json:"kinds"`    // kind → currency → amount
	Chapters []spendChapter              `json:"chapters"` // по reason chapter:<id>
}

type spendChapter struct {
	ID   string           `json:"id"`
	Sums map[string]int64 `json:"sums"`
}

// GET /v1/admin/stats/spend?from=2026-08-01&to=2026-08-25&bucket=day|month
func (s *AdminService) handleSpendStats(w http.ResponseWriter, r *http.Request) {
	if !s.ok(w, r) {
		return
	}
	q := r.URL.Query()
	bucket := q.Get("bucket")
	if bucket != "month" {
		bucket = "day"
	}
	var from, to time.Time
	if v := q.Get("from"); v != "" {
		from, _ = time.Parse("2006-01-02", v)
	}
	if v := q.Get("to"); v != "" {
		// включительно: конец дня
		if t, err := time.Parse("2006-01-02", v); err == nil {
			to = t.Add(24*time.Hour - time.Second)
		}
	}

	buckets := map[string]map[string]int64{}
	titles := map[string]*spendTitle{}
	chapters := map[string]map[string]map[string]int64{} // title → chapter → currency → sum

	for _, id := range s.wallet.AllUserIDs() {
		doc := s.wallet.AdminLoad(id)
		for _, e := range doc.History {
			if e.Type != "spend" || e.Amount <= 0 {
				continue
			}
			ts, err := time.Parse(time.RFC3339, e.TS)
			if err != nil {
				continue
			}
			if !from.IsZero() && ts.Before(from) {
				continue
			}
			if !to.IsZero() && ts.After(to) {
				continue
			}
			key := ts.Format("2006-01-02")
			if bucket == "month" {
				key = ts.Format("2006-01")
			}
			if buckets[key] == nil {
				buckets[key] = map[string]int64{}
			}
			buckets[key][e.Currency] += e.Amount

			t := titles[e.Title]
			if t == nil {
				t = &spendTitle{Title: e.Title, Sums: map[string]int64{}, Kinds: map[string]map[string]int64{}}
				titles[e.Title] = t
			}
			t.Sums[e.Currency] += e.Amount
			kind := spendKind(e.Reason)
			if t.Kinds[kind] == nil {
				t.Kinds[kind] = map[string]int64{}
			}
			t.Kinds[kind][e.Currency] += e.Amount

			if ch, ok := strings.CutPrefix(e.Reason, "chapter:"); ok && ch != "" {
				if chapters[e.Title] == nil {
					chapters[e.Title] = map[string]map[string]int64{}
				}
				if chapters[e.Title][ch] == nil {
					chapters[e.Title][ch] = map[string]int64{}
				}
				chapters[e.Title][ch][e.Currency] += e.Amount
			}
		}
	}

	out := spendStats{}
	for key, sums := range buckets {
		out.Series = append(out.Series, spendBucket{Bucket: key, Sums: sums})
	}
	sortSlice(out.Series, func(a, b spendBucket) bool { return a.Bucket < b.Bucket })
	for _, t := range titles {
		for ch, sums := range chapters[t.Title] {
			t.Chapters = append(t.Chapters, spendChapter{ID: ch, Sums: sums})
		}
		sortSlice(t.Chapters, func(a, b spendChapter) bool { return a.ID < b.ID })
		out.Titles = append(out.Titles, *t)
	}
	sortSlice(out.Titles, func(a, b spendTitle) bool { return total(a.Sums) > total(b.Sums) })
	writeJSON(w, http.StatusOK, out)
}

// spendKind классифицирует трату по reason-конвенции клиента.
func spendKind(reason string) string {
	switch {
	case strings.HasPrefix(reason, "chapter:"):
		return "chapter"
	case strings.HasPrefix(reason, "title:"):
		return "title"
	case reason == "choice" || strings.HasPrefix(reason, "choice"):
		return "choice"
	case reason == "wardrobe" || strings.HasPrefix(reason, "wardrobe"):
		return "wardrobe"
	default:
		return "other"
	}
}

func total(m map[string]int64) int64 {
	var s int64
	for _, v := range m {
		s += v
	}
	return s
}

func sortSlice[T any](s []T, less func(a, b T) bool) {
	for i := 1; i < len(s); i++ {
		for j := i; j > 0 && less(s[j], s[j-1]); j-- {
			s[j], s[j-1] = s[j-1], s[j]
		}
	}
}
