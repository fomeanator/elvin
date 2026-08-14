package main

// Analytics reports — the part that is supposed to show PROBLEMS.
//
// A count of events is not a product signal. What an editor, a manager and the
// engine team each need is the same three questions asked of the same log:
//
//	who is playing what   → cuts by title, by author, by chapter, by day
//	where do they quit    → the chapter funnel and its cliffs
//	what is broken        → failure share, unknown ops, dead assets
//
// Everything here reads rollups (analytics_rollup.go), never the raw log, so a
// month-wide answer costs a handful of small JSON reads.
//
// Two honesty rules run through the whole file. First, a number that had to be
// squeezed to fit a cap says so (`truncated`). Second, a signal the client does
// not send yet is NOT approximated into something that looks real — it is
// listed, by name, in health.gaps, with the event that would light it up. An
// empty funnel step is better than an invented one.

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	analyticsMaxWindowDays = 92 // one quarter per query; ranges are folds, not scans
	analyticsDefaultTopN   = 50
	analyticsMinSample     = 5 // below this a "90% drop" is two players leaving
)

// ------------------------------------------------------------------ window

type anaWindow struct {
	Days   []string
	From   string
	To     string
	Single bool // ?day= / no params — the legacy one-day shape
}

// parseAnalyticsWindow accepts ?day=, ?from=&to=, or ?days=N (the last N days,
// today included). Defaults to today, which is what the existing panel asks
// for with no parameters at all.
func parseAnalyticsWindow(r *http.Request) (anaWindow, error) {
	q := r.URL.Query()
	today := time.Now().UTC().Format("2006-01-02")
	from, to := q.Get("from"), q.Get("to")
	if from != "" || to != "" {
		if from == "" {
			from = to
		}
		if to == "" {
			to = today
		}
		if !reDay.MatchString(from) || !reDay.MatchString(to) {
			return anaWindow{}, errBadWindow
		}
		if from > to {
			from, to = to, from
		}
		days, err := daysBetween(from, to)
		if err != nil {
			return anaWindow{}, err
		}
		return anaWindow{Days: days, From: from, To: to}, nil
	}
	if n, _ := strconv.Atoi(q.Get("days")); n > 0 {
		if n > analyticsMaxWindowDays {
			return anaWindow{}, errBadWindow
		}
		end, _ := time.Parse("2006-01-02", today)
		start := end.AddDate(0, 0, -(n - 1)).Format("2006-01-02")
		days, err := daysBetween(start, today)
		if err != nil {
			return anaWindow{}, err
		}
		return anaWindow{Days: days, From: start, To: today}, nil
	}
	day := q.Get("day")
	if day == "" {
		day = today
	}
	if !reDay.MatchString(day) {
		return anaWindow{}, errBadWindow
	}
	return anaWindow{Days: []string{day}, From: day, To: day, Single: true}, nil
}

type windowError struct{ msg string }

func (e *windowError) Error() string { return e.msg }

var errBadWindow = &windowError{"day=YYYY-MM-DD, or from=&to=, or days=N (max 92 days)"}

func daysBetween(from, to string) ([]string, error) {
	a, err := time.Parse("2006-01-02", from)
	if err != nil {
		return nil, errBadWindow
	}
	b, err := time.Parse("2006-01-02", to)
	if err != nil {
		return nil, errBadWindow
	}
	var out []string
	for d := a; !d.After(b); d = d.AddDate(0, 0, 1) {
		out = append(out, d.Format("2006-01-02"))
		if len(out) > analyticsMaxWindowDays {
			return nil, errBadWindow
		}
	}
	return out, nil
}

func topN(r *http.Request) int {
	n, _ := strconv.Atoi(r.URL.Query().Get("limit"))
	if n <= 0 || n > 1000 {
		n = analyticsDefaultTopN
	}
	return n
}

// --------------------------------------------------------- derived cuts

type dayReport struct {
	Day      string `json:"day"`
	Events   int    `json:"events"`
	Players  int    `json:"players"`
	Starts   int    `json:"chapter_starts"`
	Finishes int    `json:"chapter_finishes"`
	Fails    int    `json:"fail_events"`
}

type titleReport struct {
	Title      string  `json:"title"`
	Name       string  `json:"name,omitempty"`
	Author     string  `json:"author,omitempty"`
	Events     int     `json:"events"`
	Players    int     `json:"players"`
	Starts     int     `json:"chapter_starts"`
	Finishes   int     `json:"chapter_finishes"`
	Completion float64 `json:"chapter_completion"`
	Fails      int     `json:"fail_events"`
	FailShare  float64 `json:"fail_share"`
	Chapters   int     `json:"chapters_seen"`
}

type authorReport struct {
	Author     string  `json:"author"`
	Titles     int     `json:"titles"`
	Events     int     `json:"events"`
	Players    int     `json:"players"`
	Starts     int     `json:"chapter_starts"`
	Finishes   int     `json:"chapter_finishes"`
	Completion float64 `json:"chapter_completion"`
	Fails      int     `json:"fail_events"`
	FailShare  float64 `json:"fail_share"`
}

type chapterReport struct {
	Title      string  `json:"title"`
	Chapter    string  `json:"chapter"`
	Name       string  `json:"name,omitempty"`
	Events     int     `json:"events"`
	Starts     int     `json:"starts"`
	Finishes   int     `json:"finishes"`
	Completion float64 `json:"completion"`
	Fails      int     `json:"fail_events"`
	Stopped    int     `json:"players_stopped_here"`
}

// dropPoint is one place the funnel leaks. `kind` separates the two very
// different failures: IN_CHAPTER means they walked in and never reached the
// end (too long, a softlock, a crash), AFTER_CHAPTER means they finished and
// never came back (the chapter-end screen, an energy gate, a paywall, or just
// a bad cliffhanger). Fixing them is different work, so counting them together
// would hide both.
type dropPoint struct {
	Title    string  `json:"title"`
	Author   string  `json:"author,omitempty"`
	Chapter  string  `json:"chapter"`
	Name     string  `json:"name,omitempty"`
	Kind     string  `json:"kind"`
	Base     int     `json:"base"`
	Lost     int     `json:"lost"`
	Rate     float64 `json:"rate"`
	Stopped  int     `json:"players_stopped_here,omitempty"`
	Position int     `json:"position"` // 1-based index in the title's order
}

type funnelStep struct {
	Chapter      string  `json:"chapter"`
	Name         string  `json:"name,omitempty"`
	Position     int     `json:"position"`
	Starts       int     `json:"starts"`
	Finishes     int     `json:"finishes"`
	Completion   float64 `json:"completion"`
	LostInside   int     `json:"lost_in_chapter"`
	LostAfter    int     `json:"lost_after_chapter"`
	ContinueRate float64 `json:"continue_rate"`
	Reach        float64 `json:"reach"`
	Stopped      int     `json:"players_stopped_here"`
	Abandons     int     `json:"abandons,omitempty"`
	Fails        int     `json:"fail_events,omitempty"`
	OffManifest  bool    `json:"off_manifest,omitempty"`
}

type funnelReport struct {
	Title    string       `json:"title"`
	Name     string       `json:"name,omitempty"`
	Author   string       `json:"author,omitempty"`
	Order    string       `json:"order"` // manifest | first-seen
	Entrants int          `json:"entrants"`
	Steps    []funnelStep `json:"steps"`
	Drops    []dropPoint  `json:"dropoffs"`
	Notes    []string     `json:"notes,omitempty"`
}

// stopKey joins a title and a chapter for the "last place seen" table.
func stopKey(title, chapter string) string { return title + "\x00" + chapter }

// playerCuts walks the per-player records ONCE and produces every identity
// answer the reports need: unique players overall, per title, per author, how
// many hit a failure, and where each one was last seen. One pass, because the
// map is the largest thing in a rollup.
type playerCuts struct {
	Total     int
	WithFail  int
	ByTitle   map[string]int
	ByAuthor  map[string]int
	StoppedAt map[string]int // stopKey → players whose last chapter it was
	Finishers int
}

func computePlayerCuts(m *dayRollup) playerCuts {
	pc := playerCuts{
		ByTitle: map[string]int{}, ByAuthor: map[string]int{}, StoppedAt: map[string]int{},
	}
	authorOf := func(t string) string {
		if tr, ok := m.Titles[t]; ok {
			return tr.Author
		}
		return ""
	}
	for _, p := range m.Users {
		pc.Total++
		if p.Fails > 0 {
			pc.WithFail++
		}
		if p.Fin > 0 {
			pc.Finishers++
		}
		seenAuthors := map[string]bool{}
		for t := range p.Titles {
			pc.ByTitle[t]++
			a := authorOf(t)
			if !seenAuthors[a] {
				seenAuthors[a] = true
				pc.ByAuthor[a]++
			}
		}
		if p.PC != "" {
			pc.StoppedAt[stopKey(p.PT, p.PC)]++
		}
	}
	return pc
}

// titleCuts: one row per title. The title-less bucket is left out — "" is not
// a novel, and its volume is reported once as events_without_title instead of
// masquerading as the top row of a chart.
func (s *AnalyticsService) titleCuts(m *dayRollup, pc playerCuts, n int) []titleReport {
	out := make([]titleReport, 0, len(m.Titles))
	for id, t := range m.Titles {
		if id == "" {
			continue
		}
		out = append(out, titleReport{
			Title: id, Name: s.chapters.titleName(id), Author: t.Author,
			Events: t.Events, Players: pc.ByTitle[id],
			Starts: t.Starts, Finishes: t.Finishes,
			Completion: ratio(t.Finishes, t.Starts),
			Fails:      t.Fails, FailShare: ratio(t.Fails, t.Events),
			Chapters: len(t.Chapters),
		})
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Events != out[j].Events {
			return out[i].Events > out[j].Events
		}
		return out[i].Title < out[j].Title
	})
	if len(out) > n {
		out = out[:n]
	}
	return out
}

// authorCuts: the manager's view. Events that carry no title at all (boot, and
// anything tracked outside a novel) are NOT folded into an anonymous author
// row — they would swamp it and mean nothing. They are reported once, on their
// own, as events_without_title.
func authorCuts(m *dayRollup, pc playerCuts, n int) []authorReport {
	agg := map[string]*authorReport{}
	for id, t := range m.Titles {
		if id == "" {
			continue
		}
		a, ok := agg[t.Author]
		if !ok {
			a = &authorReport{Author: t.Author}
			agg[t.Author] = a
		}
		a.Titles++
		a.Events += t.Events
		a.Starts += t.Starts
		a.Finishes += t.Finishes
		a.Fails += t.Fails
	}
	out := make([]authorReport, 0, len(agg))
	for id, a := range agg {
		a.Players = pc.ByAuthor[id]
		a.Completion = ratio(a.Finishes, a.Starts)
		a.FailShare = ratio(a.Fails, a.Events)
		out = append(out, *a)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Events != out[j].Events {
			return out[i].Events > out[j].Events
		}
		return out[i].Author < out[j].Author
	})
	if len(out) > n {
		out = out[:n]
	}
	return out
}

// ------------------------------------------------------------------ funnel

// buildFunnel turns one title's chapter counters into an ordered funnel.
//
// The order matters more than the arithmetic. Chapter ids sort alphabetically
// only by luck, so the manifest — the same file that tells the player what
// comes next — is the authority; a title with no manifest entry falls back to
// the order the chapters were first seen in the log, which is right often
// enough to be useful and is labelled so nobody mistakes it for the truth.
func (s *AnalyticsService) buildFunnel(m *dayRollup, id string, pc playerCuts, minSample int) funnelReport {
	t := m.Titles[id]
	rep := funnelReport{Title: id, Name: s.chapters.titleName(id), Order: "manifest"}
	if t == nil {
		rep.Notes = append(rep.Notes, "no events for this title in the window")
		return rep
	}
	rep.Author = t.Author

	meta := s.chapters.chapters(id)
	var order []string
	seen := map[string]bool{}
	for _, cm := range meta {
		if _, ok := t.Chapters[cm.ID]; ok {
			order = append(order, cm.ID)
			seen[cm.ID] = true
		}
	}
	// Chapters that fired events but are not in the manifest: a renamed id, a
	// deleted chapter still live in old builds, or a typo in the script. They
	// belong in the answer, flagged, not silently dropped.
	var extra []string
	for cid := range t.Chapters {
		if !seen[cid] {
			extra = append(extra, cid)
		}
	}
	sort.Slice(extra, func(i, j int) bool {
		a, b := t.Chapters[extra[i]], t.Chapters[extra[j]]
		if a.First != b.First {
			return a.First < b.First
		}
		return extra[i] < extra[j]
	})
	if len(order) == 0 {
		rep.Order = "first-seen"
	}
	order = append(order, extra...)
	if len(extra) > 0 && rep.Order == "manifest" {
		rep.Notes = append(rep.Notes,
			"chapters not present in the manifest are appended at the end and marked off_manifest")
	}

	names := map[string]string{}
	for _, cm := range meta {
		names[cm.ID] = cm.Name
	}
	steps := make([]funnelStep, 0, len(order))
	for i, cid := range order {
		c := t.Chapters[cid]
		steps = append(steps, funnelStep{
			Chapter: cid, Name: names[cid], Position: i + 1,
			Starts: c.Starts, Finishes: c.Finishes,
			Completion: ratio(c.Finishes, c.Starts),
			LostInside: max0(c.Starts - c.Finishes),
			Stopped:    pc.StoppedAt[stopKey(id, cid)],
			Abandons:   c.Abandons, Fails: c.Fails,
			OffManifest: !seen[cid] && rep.Order == "manifest",
		})
	}
	if len(steps) > 0 {
		rep.Entrants = steps[0].Starts
	}
	for i := range steps {
		if rep.Entrants > 0 {
			steps[i].Reach = ratio(steps[i].Starts, rep.Entrants)
		}
		if i+1 < len(steps) {
			next := steps[i+1].Starts
			steps[i].LostAfter = max0(steps[i].Finishes - next)
			steps[i].ContinueRate = ratio(next, steps[i].Finishes)
		}
	}
	rep.Steps = steps
	rep.Drops = dropsOf(id, t.Author, steps, minSample)
	rep.Notes = append(rep.Notes,
		"starts/finishes count EVENTS: one player replaying a chapter counts twice. players_stopped_here is the player-level counterpart",
		"lost_in_chapter is inferred as starts-finishes — the client sends no chapter_abandon event, so a crash and a deliberate exit look the same")
	return rep
}

// dropsOf ranks a title's leaks. Ranked by players lost (absolute), because a
// 90% drop on ten entrants is noise and a 20% drop on ten thousand is the
// week's work; the rate rides along so a small-but-total cliff is still visible.
func dropsOf(title, author string, steps []funnelStep, minSample int) []dropPoint {
	var out []dropPoint
	for i, st := range steps {
		if st.LostInside > 0 && st.Starts >= minSample {
			out = append(out, dropPoint{
				Title: title, Author: author, Chapter: st.Chapter, Name: st.Name,
				Kind: "in_chapter", Base: st.Starts, Lost: st.LostInside,
				Rate: ratio(st.LostInside, st.Starts), Stopped: st.Stopped, Position: i + 1,
			})
		}
		if st.LostAfter > 0 && st.Finishes >= minSample {
			out = append(out, dropPoint{
				Title: title, Author: author, Chapter: st.Chapter, Name: st.Name,
				Kind: "after_chapter", Base: st.Finishes, Lost: st.LostAfter,
				Rate: ratio(st.LostAfter, st.Finishes), Stopped: st.Stopped, Position: i + 1,
			})
		}
	}
	sortDrops(out)
	return out
}

func sortDrops(d []dropPoint) {
	sort.Slice(d, func(i, j int) bool {
		if d[i].Lost != d[j].Lost {
			return d[i].Lost > d[j].Lost
		}
		if d[i].Rate != d[j].Rate {
			return d[i].Rate > d[j].Rate
		}
		if d[i].Title != d[j].Title {
			return d[i].Title < d[j].Title
		}
		return d[i].Chapter < d[j].Chapter
	})
}

func max0(n int) int {
	if n < 0 {
		return 0
	}
	return n
}

// ------------------------------------------------------------------ handlers

func (s *AnalyticsService) handleSummary(w http.ResponseWriter, r *http.Request) {
	if !s.adminOK(w, r) {
		return
	}
	win, err := parseAnalyticsWindow(r)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	n := topN(r)

	s.rollups.mu.Lock()
	defer s.rollups.mu.Unlock()
	m, byDay := s.rollups.window(win.Days)
	pc := computePlayerCuts(m)

	titles := s.titleCuts(m, pc, n)
	authors := authorCuts(m, pc, n)

	// The cross-title leaderboard of leaks: the one list a product owner reads
	// first. Built from every title's funnel, ranked by players lost.
	var drops []dropPoint
	for id := range m.Titles {
		if id == "" || id == rollupOther {
			continue
		}
		drops = append(drops, s.buildFunnel(m, id, pc, analyticsMinSample).Drops...)
	}
	sortDrops(drops)
	if len(drops) > 10 {
		drops = drops[:10]
	}

	out := map[string]any{
		// Legacy shape — the panel reads these three and must keep working.
		"total":        m.Events,
		"unique_users": pc.Total,
		"by_name":      m.Names,

		"from": win.From, "to": win.To,
		"events_without_user":   m.Anon,
		"events_without_title":  titleless(m),
		"bad_lines":             m.Bad,
		"players_with_a_finish": pc.Finishers,
		"by_day":                byDay,
		"by_title":              titles,
		"by_author":             authors,
		"by_hour":               m.Hours,
		"signals": map[string]any{
			"fail_events":           sumCounts(m.Fails),
			"fail_event_share":      ratio(sumCounts(m.Fails), m.Events),
			"players_with_failures": pc.WithFail,
			"player_fail_share":     ratio(pc.WithFail, pc.Total),
			"sessions":              sessionsBlock(m, pc),
			"top_failures":          topCounts(m.Fails, 10),
			"unknown_ops":           topCounts(m.Ops, 10),
			"asset_failures":        topCounts(m.Assets, 10),
			"worst_dropoffs":        drops,
		},
		"coverage": coverage(m, win),
	}
	if win.Single {
		out["day"] = win.Days[0]
	}
	writeJSON(w, http.StatusOK, out)
}

// GET /v1/analytics/funnel?title=…  — one title's completion funnel, or, with
// no title, the drop-off leaderboard across everything that moved.
func (s *AnalyticsService) handleFunnel(w http.ResponseWriter, r *http.Request) {
	if !s.adminOK(w, r) {
		return
	}
	win, err := parseAnalyticsWindow(r)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	minSample, _ := strconv.Atoi(r.URL.Query().Get("min"))
	if minSample <= 0 {
		minSample = analyticsMinSample
	}
	title := clip(r.URL.Query().Get("title"), 64)

	s.rollups.mu.Lock()
	defer s.rollups.mu.Unlock()
	m, _ := s.rollups.window(win.Days)
	pc := computePlayerCuts(m)

	if title != "" {
		rep := s.buildFunnel(m, title, pc, minSample)
		writeJSON(w, http.StatusOK, map[string]any{
			"from": win.From, "to": win.To, "min_sample": minSample, "funnel": rep,
			"chapters": s.chapterCuts(m, pc, title, 0),
		})
		return
	}
	var drops []dropPoint
	funnels := make([]funnelReport, 0, len(m.Titles))
	for id := range m.Titles {
		if id == "" || id == rollupOther {
			continue
		}
		f := s.buildFunnel(m, id, pc, minSample)
		drops = append(drops, f.Drops...)
		f.Drops = nil // the ranked list below is the answer; per-title copies are noise
		funnels = append(funnels, f)
	}
	sortDrops(drops)
	sort.Slice(funnels, func(i, j int) bool { return funnels[i].Title < funnels[j].Title })
	if len(drops) > topN(r) {
		drops = drops[:topN(r)]
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"from": win.From, "to": win.To, "min_sample": minSample,
		"dropoffs": drops, "stop_points": s.stopPoints(m, pc, topN(r)), "titles": funnels,
	})
}

// stopPoints: the flat, order-free version of "where do players stop" — the
// last chapter each player was seen in, counted. It needs no manifest and no
// chapter_finish semantics, which makes it the one drop-off number that cannot
// be argued with.
func (s *AnalyticsService) stopPoints(m *dayRollup, pc playerCuts, n int) []chapterReport {
	out := make([]chapterReport, 0, len(pc.StoppedAt))
	for k, players := range pc.StoppedAt {
		title, chapter, _ := strings.Cut(k, "\x00")
		cr := chapterReport{Title: title, Chapter: chapter, Stopped: players,
			Name: s.chapters.chapterName(title, chapter)}
		if t := m.Titles[title]; t != nil {
			if c := t.Chapters[chapter]; c != nil {
				cr.Events, cr.Starts, cr.Finishes, cr.Fails = c.Events, c.Starts, c.Finishes, c.Fails
				cr.Completion = ratio(c.Finishes, c.Starts)
			}
		}
		out = append(out, cr)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Stopped != out[j].Stopped {
			return out[i].Stopped > out[j].Stopped
		}
		if out[i].Title != out[j].Title {
			return out[i].Title < out[j].Title
		}
		return out[i].Chapter < out[j].Chapter
	})
	if n > 0 && len(out) > n {
		out = out[:n]
	}
	return out
}

func (s *AnalyticsService) chapterCuts(m *dayRollup, pc playerCuts, title string, n int) []chapterReport {
	t := m.Titles[title]
	if t == nil {
		return nil
	}
	out := make([]chapterReport, 0, len(t.Chapters))
	for cid, c := range t.Chapters {
		out = append(out, chapterReport{
			Title: title, Chapter: cid, Name: s.chapters.chapterName(title, cid),
			Events: c.Events, Starts: c.Starts, Finishes: c.Finishes,
			Completion: ratio(c.Finishes, c.Starts), Fails: c.Fails,
			Stopped: pc.StoppedAt[stopKey(title, cid)],
		})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Chapter < out[j].Chapter })
	if n > 0 && len(out) > n {
		out = out[:n]
	}
	return out
}

// GET /v1/analytics/health — the engineering view. Not "how many players", but
// "what is failing, where, and what can this log not tell you yet".
func (s *AnalyticsService) handleHealth(w http.ResponseWriter, r *http.Request) {
	if !s.adminOK(w, r) {
		return
	}
	win, err := parseAnalyticsWindow(r)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	s.rollups.mu.Lock()
	defer s.rollups.mu.Unlock()
	m, _ := s.rollups.window(win.Days)
	pc := computePlayerCuts(m)

	// Chapters ranked by failures per entry — where the engine, not the story,
	// is losing people.
	var worst []dropPoint
	for id, t := range m.Titles {
		for cid, c := range t.Chapters {
			if c.Fails == 0 {
				continue
			}
			base := c.Starts
			if base == 0 {
				base = c.Events
			}
			worst = append(worst, dropPoint{
				Title: id, Author: t.Author, Chapter: cid, Name: s.chapters.chapterName(id, cid),
				Kind: "failures", Base: base, Lost: c.Fails, Rate: ratio(c.Fails, base),
				Stopped: pc.StoppedAt[stopKey(id, cid)],
			})
		}
	}
	sortDrops(worst)
	if len(worst) > topN(r) {
		worst = worst[:topN(r)]
	}

	writeJSON(w, http.StatusOK, map[string]any{
		"from": win.From, "to": win.To,
		"events":                m.Events,
		"fail_events":           sumCounts(m.Fails),
		"fail_event_share":      ratio(sumCounts(m.Fails), m.Events),
		"players":               pc.Total,
		"players_with_failures": pc.WithFail,
		"player_fail_share":     ratio(pc.WithFail, pc.Total),
		"sessions":              sessionsBlock(m, pc),
		"failures_by_name":      topCounts(m.Fails, topN(r)),
		"unknown_ops":           topCounts(m.Ops, topN(r)),
		"asset_failures":        topCounts(m.Assets, topN(r)),
		"worst_chapters":        worst,
		"bad_lines":             m.Bad,
		"coverage":              coverage(m, win),
		"gaps":                  s.gaps(m),
		"rollup": map[string]any{
			"cached_days": len(s.rollups.cache), "bytes_folded": s.rollups.statBytesRead,
			"folds": s.rollups.statFolds, "rebuilds": s.rollups.statRebuilds,
			"schema": analyticsRollupVersion,
		},
	})
}

// gaps: the signals this report CANNOT produce because nothing sends them, and
// the exact event that would. Keeping this in the API (rather than in a doc
// nobody opens) means the day the client starts sending one, the entry flips to
// seen>0 and disappears from the to-do by itself.
type analyticsGap struct {
	Event  string `json:"event"`
	Seen   int    `json:"seen"`
	Blind  string `json:"blind_spot"`
	Client string `json:"client_fix"`
}

func (s *AnalyticsService) gaps(m *dayRollup) []analyticsGap {
	return []analyticsGap{
		{
			Event: evChapterAbandon, Seen: m.Names[evChapterAbandon],
			Blind:  "in-chapter loss is only inferable as chapter_start - chapter_finish, so a crash, a kill, an energy gate and a bored exit are one number",
			Client: "NovelApp.PlayOneChapterAsync returns null when the player leaves mid-chapter — Track(\"chapter_abandon\", title, chapter, last label/line) on that branch",
		},
		{
			Event: evUnknownOp, Seen: m.Names[evUnknownOp] + m.Names["unclaimed_op"],
			Blind:  "ops a build does not implement are counted only on the device (LvnPlayer.UnclaimedOps) and die with the process",
			Client: "flush the dictionary once per chapter end/quit: Track(\"unknown_op\", (\"ops\", UnclaimedOps), (\"title\", …), (\"chapter\", …)) — the {op:count} object shape is already folded here",
		},
		{
			Event: evAssetFail, Seen: m.Names[evAssetFail] + m.Names["asset_error"] + m.Names["download_fail"],
			Blind:  "a chapter that shows grey placeholders because its art 404s looks like a happy session",
			Client: "Track(\"asset_fail\", (\"asset\", url), (\"code\", http), (\"title\", …), (\"chapter\", …)) from the download scheduler's failure path",
		},
		{
			Event: "props.sid", Seen: len(m.Sessions),
			Blind:  "no session id on events: session-level metrics fall back to per-player ones, and analytics cannot be joined to /v1/log/client, which DOES carry a session",
			Client: "stamp the same session guid the log shipper uses onto every LvnAnalytics event (props.sid)",
		},
		{
			Event: "props.title on boot", Seen: m.Events - titleless(m),
			Blind:  "boot/wardrobe/ad events carry no title, so per-title sessions and per-author revenue attribution start only at chapter_start",
			Client: "pass the current title id with every Track call that happens inside a novel",
		},
	}
}

func sessionsBlock(m *dayRollup, pc playerCuts) map[string]any {
	if len(m.Sessions) > 0 {
		bad := 0
		for _, f := range m.Sessions {
			if f&2 != 0 {
				bad++
			}
		}
		return map[string]any{
			"basis": "sid", "total": len(m.Sessions), "with_failures": bad,
			"share": ratio(bad, len(m.Sessions)), "boots": m.Names[evBoot],
		}
	}
	return map[string]any{
		"basis": "players", "total": pc.Total, "with_failures": pc.WithFail,
		"share": ratio(pc.WithFail, pc.Total), "boots": m.Names[evBoot],
		"note": "no event carried props.sid — this is the share of PLAYERS that hit a failure, not of sessions",
	}
}

func coverage(m *dayRollup, win anaWindow) map[string]any {
	unattributed := []string{}
	for id, t := range m.Titles {
		if id != "" && id != rollupOther && t.Author == "" {
			unattributed = append(unattributed, id)
		}
	}
	sort.Strings(unattributed)
	if len(unattributed) > 20 {
		unattributed = unattributed[:20]
	}
	return map[string]any{
		"days":                  len(win.Days),
		"truncated":             m.Trunc,
		"titles_without_author": unattributed,
		"events_without_title":  titleless(m),
		"events_without_user":   m.Anon,
	}
}

func titleless(m *dayRollup) int {
	if t, ok := m.Titles[""]; ok {
		return t.Events
	}
	return 0
}

func sumCounts(m map[string]int) int {
	n := 0
	for _, v := range m {
		n += v
	}
	return n
}

// ------------------------------------------------------- chapter order index

type chapterMeta struct{ ID, Name, ScriptURL string }

// chapterIndex reads chapter ORDER out of the same manifest the players read.
// Analytics must not invent its own idea of what comes after chapter three;
// when the manifest is unavailable the funnel says so and falls back to the
// order events were first seen in.
type chapterIndex struct {
	path string

	mu      sync.Mutex
	checked time.Time
	modTime time.Time
	size    int64
	byTitle map[string][]chapterMeta
	names   map[string]string
}

func newChapterIndex(path string) *chapterIndex {
	return &chapterIndex{path: path, byTitle: map[string][]chapterMeta{}, names: map[string]string{}}
}

func (c *chapterIndex) refresh() {
	if c == nil || c.path == "" {
		return
	}
	if time.Since(c.checked) < 5*time.Second {
		return
	}
	c.checked = time.Now()
	st, err := os.Stat(c.path)
	if err != nil {
		return
	}
	if st.ModTime().Equal(c.modTime) && st.Size() == c.size {
		return
	}
	body, err := os.ReadFile(c.path)
	if err != nil {
		return
	}
	var doc struct {
		Titles []struct {
			ID      string `json:"id"`
			Name    string `json:"name"`
			Seasons []struct {
				Chapters []struct {
					ID   string `json:"id"`
					Name string `json:"name"`
					// Путь к скрипту нужен отчёту о местах выхода: по индексу
					// команды он восстанавливает кадр, который игрок видел
					// последним, а для этого надо прочитать саму главу.
					ScriptURL string `json:"script_url"`
				} `json:"chapters"`
			} `json:"seasons"`
		} `json:"titles"`
	}
	if json.Unmarshal(body, &doc) != nil {
		return // a half-written manifest keeps the previous order
	}
	byTitle := map[string][]chapterMeta{}
	names := map[string]string{}
	for _, t := range doc.Titles {
		if t.ID == "" {
			continue
		}
		names[t.ID] = t.Name
		var chs []chapterMeta
		for _, s := range t.Seasons {
			for _, ch := range s.Chapters {
				if ch.ID != "" {
					chs = append(chs, chapterMeta{ID: ch.ID, Name: ch.Name, ScriptURL: ch.ScriptURL})
				}
			}
		}
		byTitle[t.ID] = chs
	}
	c.byTitle, c.names, c.modTime, c.size = byTitle, names, st.ModTime(), st.Size()
}

func (c *chapterIndex) chapters(title string) []chapterMeta {
	if c == nil {
		return nil
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	c.refresh()
	return c.byTitle[title]
}

func (c *chapterIndex) titleName(title string) string {
	if c == nil || title == "" {
		return ""
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	c.refresh()
	return c.names[title]
}

// scriptURL — путь к скомпилированной главе, как он записан в манифесте.
// Угадывать имя файла нельзя: у импортированных глав оно не совпадает с
// идентификатором.
func (c *chapterIndex) scriptURL(title, chapter string) string {
	if c == nil {
		return ""
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	c.refresh()
	for _, ch := range c.byTitle[title] {
		if ch.ID == chapter {
			return ch.ScriptURL
		}
	}
	return ""
}

// contentRoot — каталог контента: манифест лежит в его корне.
func (c *chapterIndex) contentRoot() string {
	if c == nil || c.path == "" {
		return ""
	}
	return filepath.Dir(c.path)
}

func (c *chapterIndex) chapterName(title, chapter string) string {
	if c == nil {
		return ""
	}
	c.mu.Lock()
	defer c.mu.Unlock()
	c.refresh()
	for _, cm := range c.byTitle[title] {
		if cm.ID == chapter {
			return cm.Name
		}
	}
	return ""
}
