package main

// The reports: the cuts (title / author / chapter / day), the completion
// funnel and its drop-off points, and the technical health of the build in the
// field. The numbers below are hand-computed from the fixture at the top —
// a funnel that agrees with itself is worthless if it disagrees with the log.

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const (
	funnelDay = "2026-04-01"
	healthDay = "2026-04-02"
)

// seedFunnelDay writes one day of a real-shaped playthrough:
//
//	ch1  10 start /  8 finish   → 2 lost inside, 2 more never opened ch2
//	ch2   6 start /  2 finish   → 4 lost inside (the cliff)
//	ch3   1 start /  1 finish
//
// plus a chapter no manifest knows about, a second title by a second author,
// and a title nobody declared an author for.
func seedFunnelDay(t *testing.T, dir string) {
	t.Helper()
	var evs []any
	n := 0
	add := func(name, user, title, author, chapter string) {
		n++
		evs = append(evs, evLine(name, user, title, author, chapter, at(funnelDay, n)))
	}
	for i := 1; i <= 10; i++ {
		add("chapter_start", fmt.Sprintf("u%d", i), "tour", "elvin", "ch1")
	}
	for i := 1; i <= 8; i++ {
		add("chapter_finish", fmt.Sprintf("u%d", i), "tour", "elvin", "ch1")
	}
	for i := 1; i <= 6; i++ {
		add("chapter_start", fmt.Sprintf("u%d", i), "tour", "elvin", "ch2")
	}
	for i := 1; i <= 2; i++ {
		add("chapter_finish", fmt.Sprintf("u%d", i), "tour", "elvin", "ch2")
	}
	add("chapter_start", "u1", "tour", "elvin", "ch3")
	add("chapter_finish", "u1", "tour", "elvin", "ch3")
	add("chapter_start", "u11", "tour", "elvin", "ch-ghost") // renamed/removed chapter
	add("chapter_start", "u20", "guest", "masha", "g1")
	add("wardrobe_buy_fail", "u20", "guest", "masha", "g1")
	add("chapter_start", "u21", "wild", "", "w1") // title with no declared author
	add("boot", "u22", "", "", "")                // the event that carries no title at all
	appendDay(t, dir, funnelDay, evs...)
}

func TestSummaryCutsByTitleAuthorChapterAndDay(t *testing.T) {
	_, mux, dir := analyticsRig(t)
	seedFunnelDay(t, dir)
	appendDay(t, dir, healthDay, evLine("boot", "u1", "", "", "", at(healthDay, 1)))

	out := summaryFor(t, mux, "from="+funnelDay+"&to="+healthDay)

	// --- the legacy contract the panel is built on
	if out.Total != 34 {
		t.Fatalf("total = %d, want 34 (33 on day one + 1)", out.Total)
	}
	if out.ByName["chapter_start"] != 20 || out.ByName["chapter_finish"] != 11 {
		t.Fatalf("by_name off: %v", out.ByName)
	}
	// u1..u11, u20, u21, u22 — u1 plays on both days and must be ONE player.
	if out.UniqueUsers != 14 {
		t.Fatalf("unique_users = %d, want 14 distinct players across both days", out.UniqueUsers)
	}

	// --- by day
	if len(out.ByDay) != 2 || out.ByDay[0].Events != 33 || out.ByDay[1].Events != 1 {
		t.Fatalf("by_day off: %+v", out.ByDay)
	}
	if out.ByDay[0].Players != 14 { // u1..u11, u20, u21, u22
		t.Fatalf("day one players = %d, want 14", out.ByDay[0].Players)
	}

	// --- by title
	byTitle := map[string]titleReport{}
	for _, tr := range out.ByTitle {
		byTitle[tr.Title] = tr
	}
	tour := byTitle["tour"]
	if tour.Author != "elvin" || tour.Name != "LVN Tour" {
		t.Fatalf("title attribution lost: %+v", tour)
	}
	if tour.Starts != 18 || tour.Finishes != 11 {
		t.Fatalf("tour starts/finishes = %d/%d, want 18/11", tour.Starts, tour.Finishes)
	}
	if tour.Players != 11 {
		t.Fatalf("tour players = %d, want 11 (u1..u11)", tour.Players)
	}
	if tour.Completion != 0.6111 {
		t.Fatalf("tour completion = %v, want 0.6111", tour.Completion)
	}
	if guest := byTitle["guest"]; guest.Author != "masha" || guest.Fails != 1 || guest.Players != 1 {
		t.Fatalf("guest title cut off: %+v", guest)
	}

	// --- by author (the manager's view)
	byAuthor := map[string]authorReport{}
	for _, a := range out.ByAuthor {
		byAuthor[a.Author] = a
	}
	if e := byAuthor["elvin"]; e.Titles != 1 || e.Players != 11 || e.Starts != 18 {
		t.Fatalf("author elvin: %+v", e)
	}
	if m := byAuthor["masha"]; m.Players != 1 || m.Fails != 1 || m.FailShare == 0 {
		t.Fatalf("author masha: %+v", m)
	}
	// The unattributed row is the ONE undeclared title, not the pile of
	// title-less boot events — an author row that swallows those means nothing.
	if none := byAuthor[""]; none.Titles != 1 || none.Events != 1 || none.Players != 1 {
		t.Fatalf("unattributed author row = %+v, want just the \"wild\" title", none)
	}

	// The one list a product owner reads first, on the summary itself.
	if len(out.Signals.Worst) == 0 || out.Signals.Worst[0].Chapter != "ch2" ||
		out.Signals.Worst[0].Kind != "in_chapter" || out.Signals.Worst[0].Lost != 4 {
		t.Fatalf("worst_dropoffs signal = %+v", out.Signals.Worst)
	}
	if out.Signals.FailEvents != 1 || out.Signals.FailPlayers != 1 {
		t.Fatalf("failure signals = %d events / %d players", out.Signals.FailEvents, out.Signals.FailPlayers)
	}
	if out.Signals.Sessions["basis"] != "players" {
		t.Fatalf("with no props.sid the session basis must say so: %+v", out.Signals.Sessions)
	}

	// --- attribution gaps are reported, never guessed
	if len(out.Coverage.NoAuthor) != 1 || out.Coverage.NoAuthor[0] != "wild" {
		t.Fatalf("titles_without_author = %v, want [wild]", out.Coverage.NoAuthor)
	}
	if out.NoTitle != 2 { // boot on each day
		t.Fatalf("events_without_title = %d, want 2", out.NoTitle)
	}
}

func TestFunnelFindsTheDropOffPoints(t *testing.T) {
	_, mux, dir := analyticsRig(t)
	seedFunnelDay(t, dir)

	var out struct {
		Funnel   funnelReport    `json:"funnel"`
		Chapters []chapterReport `json:"chapters"`
	}
	getJSON(t, mux, "/v1/analytics/funnel?title=tour&day="+funnelDay, "admintok", &out)
	f := out.Funnel

	if f.Order != "manifest" {
		t.Fatalf("order = %q, want the manifest's", f.Order)
	}
	if f.Author != "elvin" || f.Name != "LVN Tour" {
		t.Fatalf("funnel attribution: %+v", f)
	}
	if f.Entrants != 10 {
		t.Fatalf("entrants = %d, want 10", f.Entrants)
	}
	want := []struct {
		ch                                    string
		starts, finishes, inside, after, stop int
		completion, continueRate, reach       float64
	}{
		{"ch1", 10, 8, 2, 2, 4, 0.8, 0.75, 1},
		{"ch2", 6, 2, 4, 1, 5, 0.3333, 0.5, 0.6},
		{"ch3", 1, 1, 0, 0, 1, 1, 1, 0.1},
		{"ch-ghost", 1, 0, 1, 0, 1, 0, 0, 0.1},
	}
	if len(f.Steps) != len(want) {
		t.Fatalf("steps = %d, want %d: %+v", len(f.Steps), len(want), f.Steps)
	}
	for i, w := range want {
		got := f.Steps[i]
		if got.Chapter != w.ch || got.Starts != w.starts || got.Finishes != w.finishes {
			t.Fatalf("step %d = %+v, want %s %d/%d", i, got, w.ch, w.starts, w.finishes)
		}
		if got.LostInside != w.inside || got.LostAfter != w.after {
			t.Errorf("step %s: lost inside/after = %d/%d, want %d/%d",
				w.ch, got.LostInside, got.LostAfter, w.inside, w.after)
		}
		if got.Completion != w.completion || got.ContinueRate != w.continueRate || got.Reach != w.reach {
			t.Errorf("step %s: completion/continue/reach = %v/%v/%v, want %v/%v/%v",
				w.ch, got.Completion, got.ContinueRate, got.Reach, w.completion, w.continueRate, w.reach)
		}
		if got.Stopped != w.stop {
			t.Errorf("step %s: players_stopped_here = %d, want %d", w.ch, got.Stopped, w.stop)
		}
	}
	// A chapter the manifest does not know is kept, flagged, and put last.
	if !f.Steps[3].OffManifest {
		t.Error("ch-ghost must be flagged off_manifest, not silently ranked as chapter four")
	}
	if f.Steps[0].OffManifest {
		t.Error("a manifest chapter must not be flagged off_manifest")
	}

	// The ranked leaks. ch2 loses four players inside the chapter — the answer
	// to "where do they stop playing" — and the two kinds are not merged.
	if len(f.Drops) != 3 {
		t.Fatalf("dropoffs = %+v", f.Drops)
	}
	if d := f.Drops[0]; d.Chapter != "ch2" || d.Kind != "in_chapter" || d.Lost != 4 || d.Rate != 0.6667 {
		t.Fatalf("worst drop = %+v, want ch2 in_chapter 4 (0.6667)", d)
	}
	if d := f.Drops[1]; d.Chapter != "ch1" || d.Kind != "after_chapter" || d.Lost != 2 {
		t.Fatalf("second drop = %+v, want ch1 after_chapter 2", d)
	}
	for _, d := range f.Drops {
		if d.Kind == "after_chapter" && d.Chapter == "ch2" {
			t.Error("a 1-of-2 drop is noise and must be held back by the min-sample guard")
		}
	}
	// …until the caller lowers the bar deliberately.
	var loose struct {
		Funnel funnelReport `json:"funnel"`
	}
	getJSON(t, mux, "/v1/analytics/funnel?title=tour&min=1&day="+funnelDay, "admintok", &loose)
	if len(loose.Funnel.Drops) != 5 {
		t.Fatalf("min=1 must expose the small drops too: %+v", loose.Funnel.Drops)
	}
	smallSeen := false
	for _, d := range loose.Funnel.Drops {
		smallSeen = smallSeen || (d.Chapter == "ch2" && d.Kind == "after_chapter")
	}
	if !smallSeen {
		t.Error("min=1 must include the 1-of-2 drop the default guard held back")
	}
}

// The cross-title leaderboard: what a product owner opens first.
// Примечание к lost_in_chapter описывает ТО ОКНО, которое читают: без событий
// об осознанном выходе крах и выход неразличимы, с ними — различимы. Годами
// оно стояло безусловным «клиент не шлёт» и пережило сборку, которая начала
// слать: читатель отчёта делал выводы о своих данных по чужому миру.
func TestLostInChapterNoteFollowsTheEventsActuallySeen(t *testing.T) {
	_, mux, dir := analyticsRig(t)
	seedFunnelDay(t, dir)

	note := func(day string) string {
		var out struct {
			Funnel funnelReport `json:"funnel"`
		}
		getJSON(t, mux, "/v1/analytics/funnel?title=tour&day="+day, "admintok", &out)
		for _, n := range out.Funnel.Notes {
			if strings.Contains(n, "lost_in_chapter") {
				return n
			}
		}
		t.Fatalf("no lost_in_chapter note: %+v", out.Funnel.Notes)
		return ""
	}

	if got := note(funnelDay); !strings.Contains(got, "no chapter_abandon event in this window") {
		t.Fatalf("without the event the note must say so, got %q", got)
	}

	// Тот же титул, другой день — и в нём игрок вышел из главы осознанно.
	const abandonDay = "2026-08-03"
	appendDay(t, dir, abandonDay,
		evLine("chapter_start", "u1", "tour", "elvin", "ch1", at(abandonDay, 1)),
		evLine("chapter_abandon", "u1", "tour", "elvin", "ch1", at(abandonDay, 2)),
	)

	if got := note(abandonDay); !strings.Contains(got, "chapter_abandon IS arriving") {
		t.Fatalf("with the event the note must stop claiming the client is silent, got %q", got)
	}
}

func TestFunnelWithoutATitleRanksEveryLeakAndEveryStopPoint(t *testing.T) {
	_, mux, dir := analyticsRig(t)
	seedFunnelDay(t, dir)

	var out struct {
		Dropoffs   []dropPoint     `json:"dropoffs"`
		StopPoints []chapterReport `json:"stop_points"`
		Titles     []funnelReport  `json:"titles"`
	}
	getJSON(t, mux, "/v1/analytics/funnel?day="+funnelDay, "admintok", &out)

	if len(out.Dropoffs) == 0 || out.Dropoffs[0].Chapter != "ch2" || out.Dropoffs[0].Title != "tour" {
		t.Fatalf("worst leak across all titles = %+v", out.Dropoffs)
	}
	if out.Dropoffs[0].Author != "elvin" {
		t.Error("a drop-off must carry the author — that is who has to fix the chapter")
	}
	// Stop points are player-level and need no manifest: ch2 is where five
	// players were last seen.
	top := out.StopPoints[0]
	if top.Chapter != "ch2" || top.Stopped != 5 {
		t.Fatalf("top stop point = %+v, want ch2 with 5 players", top)
	}
	if len(out.Titles) != 3 { // tour, guest, wild
		t.Fatalf("titles = %d: %+v", len(out.Titles), out.Titles)
	}
	// A title with no manifest entry still gets a funnel, honestly labelled.
	for _, f := range out.Titles {
		if f.Title == "wild" && f.Order != "first-seen" {
			t.Errorf("an unlisted title must not claim manifest order: %+v", f)
		}
	}
}

// The engineering view: what is broken, where, and what the log still cannot
// see. The last part is the point — a gap that is named gets fixed.
func TestHealthReportsFailuresUnknownOpsAndItsOwnBlindSpots(t *testing.T) {
	_, mux, dir := analyticsRig(t)
	n := 0
	ts := func() string { n++; return at(healthDay, n) }
	appendDay(t, dir, healthDay,
		map[string]any{"name": "chapter_start", "user": "u1", "title": "tour", "author": "elvin",
			"chapter": "ch2", "sid": "s1", "ts": ts()},
		map[string]any{"name": "unknown_op", "user": "u1", "title": "tour", "author": "elvin",
			"chapter": "ch2", "sid": "s1", "ts": ts(),
			"props": map[string]any{"ops": map[string]int{"weather": 3, "quake": 1}}},
		map[string]any{"name": "unknown_op", "user": "u2", "title": "tour", "author": "elvin",
			"chapter": "ch2", "sid": "s2", "ts": ts(),
			"props": map[string]any{"op": "shake", "count": 2}},
		map[string]any{"name": "asset_fail", "user": "u3", "title": "tour", "author": "elvin",
			"chapter": "ch1", "sid": "s3", "ts": ts(),
			"props": map[string]any{"asset": "/content/bg/missing.jpg", "code": 404}},
		map[string]any{"name": "boot", "user": "u4", "sid": "s4", "ts": ts()},
	)

	var h struct {
		Events         int            `json:"events"`
		FailEvents     int            `json:"fail_events"`
		FailShare      float64        `json:"fail_event_share"`
		Players        int            `json:"players"`
		FailPlayers    int            `json:"players_with_failures"`
		PlayerFailRate float64        `json:"player_fail_share"`
		Sessions       map[string]any `json:"sessions"`
		Failures       []nameCount    `json:"failures_by_name"`
		UnknownOps     []nameCount    `json:"unknown_ops"`
		AssetFails     []nameCount    `json:"asset_failures"`
		Worst          []dropPoint    `json:"worst_chapters"`
		Gaps           []analyticsGap `json:"gaps"`
		Rollup         map[string]any `json:"rollup"`
	}
	getJSON(t, mux, "/v1/analytics/health?day="+healthDay, "admintok", &h)

	if h.Events != 5 || h.FailEvents != 3 || h.FailShare != 0.6 {
		t.Fatalf("failure share off: %d events, %d fails, %v share", h.Events, h.FailEvents, h.FailShare)
	}
	if h.Players != 4 || h.FailPlayers != 3 || h.PlayerFailRate != 0.75 {
		t.Fatalf("player failure share off: %+v", h)
	}
	// Sessions become measurable the moment events carry a session id.
	if h.Sessions["basis"] != "sid" || h.Sessions["total"].(float64) != 4 ||
		h.Sessions["with_failures"].(float64) != 3 {
		t.Fatalf("session basis: %+v", h.Sessions)
	}
	// Both shapes of the unknown-op report fold into one counter.
	ops := map[string]int{}
	for _, o := range h.UnknownOps {
		ops[o.Name] = o.Count
	}
	if ops["weather"] != 3 || ops["quake"] != 1 || ops["shake"] != 2 {
		t.Fatalf("unknown ops = %v, want weather3 quake1 shake2", ops)
	}
	if len(h.AssetFails) != 1 || h.AssetFails[0].Name != "/content/bg/missing.jpg" {
		t.Fatalf("asset failures = %+v", h.AssetFails)
	}
	if len(h.Worst) == 0 || h.Worst[0].Chapter != "ch2" || h.Worst[0].Lost != 2 {
		t.Fatalf("worst chapters = %+v (ch2 carries both unknown_op events)", h.Worst)
	}
	if h.Rollup["schema"].(float64) != analyticsRollupVersion {
		t.Fatalf("rollup stats missing: %+v", h.Rollup)
	}

	// The blind spots, by name. unknown_op is now seen; chapter_abandon is not,
	// and the entry says exactly which client change would light it up.
	gaps := map[string]analyticsGap{}
	for _, g := range h.Gaps {
		gaps[g.Event] = g
	}
	if g, ok := gaps[evUnknownOp]; !ok || g.Seen != 2 || !g.Closed {
		t.Fatalf("unknown_op gap should be closed (seen=2, closed): %+v", g)
	}
	// Открытая дыра остаётся to-do и несёт рецепт для клиента; закрытая
	// остаётся в списке, но помечена — иначе читатель годами видит пять
	// «дыр», давно закрытых сборкой.
	if g, ok := gaps[evChapterAbandon]; !ok || g.Seen != 0 || g.Closed || g.Client == "" {
		t.Fatalf("chapter_abandon must be listed as missing, with the client fix: %+v", g)
	}
	if g := gaps["props.sid"]; g.Seen != 4 || !g.Closed {
		t.Fatalf("props.sid gap should show 4 sessions seen and be closed: %+v", g)
	}
}

// Ingest normalizes the two DIMENSIONS the client spells inconsistently, once,
// at the door — the same trust split attribution already uses: the client says
// which chapter, the server decides how it is stored.
func TestIngestStampsChapterAndSessionOntoTheLine(t *testing.T) {
	_, mux, dir := analyticsRig(t)
	_, tok := register(t, mux)

	rec, out := call(t, mux, "POST", "/v1/analytics/events", tok, []map[string]any{
		{"name": "chapter_start", "props": map[string]any{"title": "tour", "chapter": "ch1", "sid": "sess-1"}},
		{"name": "chapter_finish", "props": map[string]any{"title": "tour", "ch": "ch1"}},
		{"name": "boot", "title": "tour", "chapter": strings.Repeat("x", 200)},
	})
	if rec.Code != 200 || out["accepted"].(float64) != 3 {
		t.Fatalf("ingest: %d %v", rec.Code, out)
	}
	lines := readJSONL(t, dir)
	if len(lines) != 3 {
		t.Fatalf("want 3 lines, got %d", len(lines))
	}
	if lines[0]["chapter"] != "ch1" || lines[0]["sid"] != "sess-1" || lines[0]["author"] != "elvin" {
		t.Fatalf("props.chapter/sid not stamped: %v", lines[0])
	}
	if lines[1]["chapter"] != "ch1" {
		t.Fatalf("the \"ch\" spelling must be accepted too: %v", lines[1])
	}
	if got := lines[2]["chapter"].(string); len(got) != 64 {
		t.Fatalf("a dimension must be length-capped at the door, got %d chars", len(got))
	}
}

func readJSONL(t *testing.T, dir string) []map[string]any {
	t.Helper()
	var out []map[string]any
	entries, _ := os.ReadDir(dir)
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".jsonl") {
			continue
		}
		for _, line := range strings.Split(strings.TrimSpace(string(mustRead(t, filepath.Join(dir, e.Name())))), "\n") {
			if line == "" {
				continue
			}
			var m map[string]any
			if json.Unmarshal([]byte(line), &m) == nil {
				out = append(out, m)
			}
		}
	}
	return out
}
