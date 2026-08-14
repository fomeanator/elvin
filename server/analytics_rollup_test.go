package main

// The rollup mechanics: an aggregate that is only ever allowed to be BEHIND
// the log, never wrong about it. Every test here is about one of the four ways
// a tailing counter goes wrong — double counting, half-read lines, a file that
// changed underneath it, and a dimension that grows without limit.

import (
	"encoding/json"
	"fmt"
	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

// analyticsRig: an analytics service with a real manifest behind it, so the
// funnel has a chapter ORDER to work from.
func analyticsRig(t *testing.T) (*AnalyticsService, *http.ServeMux, string) {
	t.Helper()
	dir := t.TempDir()
	manifest := filepath.Join(dir, "manifest.json")
	if err := os.WriteFile(manifest, []byte(`{"titles":[
		{"id":"tour","name":"LVN Tour","author":"elvin","seasons":[{"chapters":[
			{"id":"ch1","name":"One"},{"id":"ch2","name":"Two"},
			{"id":"ch3","name":"Three"},{"id":"ch4","name":"Four"}]}]},
		{"id":"guest","name":"Guest","author":"masha","seasons":[{"chapters":[{"id":"g1","name":"G1"}]}]},
		{"id":"orphan","name":"Orphan"}
	]}`), 0o644); err != nil {
		t.Fatal(err)
	}
	auth, err := NewAuthService(dir)
	if err != nil {
		t.Fatal(err)
	}
	svc, err := NewAnalyticsService(filepath.Join(dir, "analytics"), auth, "admintok", newOwnerIndex(manifest))
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	auth.Routes(mux)
	svc.Routes(mux)
	return svc, mux, filepath.Join(dir, "analytics")
}

// at: a deterministic RFC3339 stamp inside a day, ordered by n.
func at(day string, n int) string {
	return fmt.Sprintf("%sT%02d:%02d:00Z", day, n/60%24, n%60)
}

func evLine(name, user, title, author, chapter, ts string) map[string]any {
	m := map[string]any{"name": name, "ts": ts}
	if user != "" {
		m["user"] = user
	}
	if title != "" {
		m["title"] = title
	}
	if author != "" {
		m["author"] = author
	}
	if chapter != "" {
		m["chapter"] = chapter
	}
	return m
}

func appendDay(t *testing.T, dir, day string, evs ...any) {
	t.Helper()
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	f, err := os.OpenFile(filepath.Join(dir, day+".jsonl"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	for _, e := range evs {
		line, err := json.Marshal(e)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := f.Write(append(line, '\n')); err != nil {
			t.Fatal(err)
		}
	}
}

func getJSON(t *testing.T, mux *http.ServeMux, path, token string, out any) {
	t.Helper()
	rec, _ := call(t, mux, "GET", path, token, nil)
	if rec.Code != 200 {
		t.Fatalf("GET %s: %d %s", path, rec.Code, rec.Body)
	}
	if err := json.Unmarshal(rec.Body.Bytes(), out); err != nil {
		t.Fatalf("GET %s: %v — body %s", path, err, rec.Body)
	}
}

type summaryOut struct {
	Day         string         `json:"day"`
	From        string         `json:"from"`
	To          string         `json:"to"`
	Total       int            `json:"total"`
	UniqueUsers int            `json:"unique_users"`
	ByName      map[string]int `json:"by_name"`
	ByDay       []dayReport    `json:"by_day"`
	ByTitle     []titleReport  `json:"by_title"`
	ByAuthor    []authorReport `json:"by_author"`
	NoUser      int            `json:"events_without_user"`
	NoTitle     int            `json:"events_without_title"`
	BadLines    int            `json:"bad_lines"`
	Signals     struct {
		FailEvents    int            `json:"fail_events"`
		FailShare     float64        `json:"fail_event_share"`
		FailPlayers   int            `json:"players_with_failures"`
		Sessions      map[string]any `json:"sessions"`
		TopFailures   []nameCount    `json:"top_failures"`
		UnknownOps    []nameCount    `json:"unknown_ops"`
		AssetFailures []nameCount    `json:"asset_failures"`
		Worst         []dropPoint    `json:"worst_dropoffs"`
	} `json:"signals"`
	Coverage struct {
		Days      int             `json:"days"`
		Truncated map[string]bool `json:"truncated"`
		NoAuthor  []string        `json:"titles_without_author"`
	} `json:"coverage"`
}

func summaryFor(t *testing.T, mux *http.ServeMux, query string) summaryOut {
	t.Helper()
	var out summaryOut
	getJSON(t, mux, "/v1/analytics/summary?"+query, "admintok", &out)
	return out
}

// A rollup is a CHECKPOINT, not a copy: the second look must fold only the
// bytes that arrived since the first, and must arrive at the same numbers a
// cold reader would.
func TestRollupFoldsOnlyTheNewBytes(t *testing.T) {
	svc, mux, dir := analyticsRig(t)
	day := "2026-03-01"
	appendDay(t, dir, day,
		evLine("boot", "u1", "", "", "", at(day, 1)),
		evLine("chapter_start", "u1", "tour", "elvin", "ch1", at(day, 2)))

	first := summaryFor(t, mux, "day="+day)
	if first.Total != 2 {
		t.Fatalf("first pass total = %d, want 2", first.Total)
	}
	readAfterFirst := svc.rollups.statBytesRead
	if readAfterFirst == 0 {
		t.Fatal("first pass read nothing")
	}
	if _, err := os.Stat(filepath.Join(dir, analyticsRollupSubdir, day+".json")); err != nil {
		t.Fatalf("no checkpoint written: %v", err)
	}

	// Nothing new: the second look must not read a single byte again.
	if second := summaryFor(t, mux, "day="+day); second.Total != 2 {
		t.Fatalf("idempotent re-read changed the total: %d", second.Total)
	}
	if svc.rollups.statBytesRead != readAfterFirst {
		t.Fatalf("re-read %d bytes with no new events (a full rescan)",
			svc.rollups.statBytesRead-readAfterFirst)
	}

	// Append one event: exactly that event's bytes are folded.
	before := fileSize(t, dir, day)
	appendDay(t, dir, day, evLine("chapter_finish", "u1", "tour", "elvin", "ch1", at(day, 3)))
	grew := fileSize(t, dir, day) - before
	third := summaryFor(t, mux, "day="+day)
	if third.Total != 3 || third.ByName["chapter_finish"] != 1 {
		t.Fatalf("incremental fold lost the new event: %+v", third)
	}
	if delta := svc.rollups.statBytesRead - readAfterFirst; delta != grew {
		t.Fatalf("folded %d bytes for a %d-byte append — not incremental", delta, grew)
	}

	// A cold reader (fresh process, no memory, no checkpoint) must agree.
	os.RemoveAll(filepath.Join(dir, analyticsRollupSubdir))
	svc.rollups = newRollupStore(dir)
	cold := summaryFor(t, mux, "day="+day)
	if cold.Total != third.Total || cold.UniqueUsers != third.UniqueUsers {
		t.Fatalf("cold rebuild disagrees with the checkpoint: %+v vs %+v", cold, third)
	}
}

func fileSize(t *testing.T, dir, day string) int64 {
	t.Helper()
	st, err := os.Stat(filepath.Join(dir, day+".jsonl"))
	if err != nil {
		t.Fatal(err)
	}
	return st.Size()
}

// A batch lands mid-write: the tail of the file is half an event. The fold has
// to leave it alone and count it exactly once when the rest arrives — the
// classic way a tailer either loses or doubles an event.
func TestRollupIgnoresAHalfWrittenLineAndCountsItOnceLater(t *testing.T) {
	_, mux, dir := analyticsRig(t)
	day := "2026-03-02"
	appendDay(t, dir, day, evLine("boot", "u1", "", "", "", at(day, 1)))

	path := filepath.Join(dir, day+".jsonl")
	partial := `{"name":"chapter_start","user":"u1","title":"tour","chapt`
	if err := os.WriteFile(path, append(mustRead(t, path), []byte(partial)...), 0o600); err != nil {
		t.Fatal(err)
	}
	if out := summaryFor(t, mux, "day="+day); out.Total != 1 || out.BadLines != 0 {
		t.Fatalf("a half-written line was counted: total=%d bad=%d", out.Total, out.BadLines)
	}

	rest := "er\":\"ch1\",\"ts\":\"" + at(day, 2) + "\"}\n"
	f, _ := os.OpenFile(path, os.O_APPEND|os.O_WRONLY, 0o600)
	f.WriteString(rest)
	f.Close()
	out := summaryFor(t, mux, "day="+day)
	if out.Total != 2 || out.ByName["chapter_start"] != 1 {
		t.Fatalf("completed line not folded exactly once: %+v", out.ByName)
	}
}

func mustRead(t *testing.T, path string) []byte {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

// The file is not the file we counted — rotated, restored, truncated. Adding
// to the old total would report a day that never happened.
func TestRollupRebuildsWhenTheDayFileShrinks(t *testing.T) {
	_, mux, dir := analyticsRig(t)
	day := "2026-03-03"
	for i := 0; i < 10; i++ {
		appendDay(t, dir, day, evLine("boot", fmt.Sprintf("u%d", i), "", "", "", at(day, i)))
	}
	if out := summaryFor(t, mux, "day="+day); out.Total != 10 {
		t.Fatalf("total = %d, want 10", out.Total)
	}
	// Truncate to a single event, as a rotation or a restore would.
	os.Remove(filepath.Join(dir, day+".jsonl"))
	appendDay(t, dir, day, evLine("boot", "u0", "", "", "", at(day, 0)))
	out := summaryFor(t, mux, "day="+day)
	if out.Total != 1 || out.UniqueUsers != 1 {
		t.Fatalf("stale total survived a truncation: total=%d users=%d", out.Total, out.UniqueUsers)
	}
}

// Deleting the day file must delete the day's numbers, not freeze them.
func TestRollupForgetsADeletedDay(t *testing.T) {
	_, mux, dir := analyticsRig(t)
	day := "2026-03-04"
	appendDay(t, dir, day, evLine("boot", "u1", "", "", "", at(day, 1)))
	if out := summaryFor(t, mux, "day="+day); out.Total != 1 {
		t.Fatalf("total = %d", out.Total)
	}
	os.Remove(filepath.Join(dir, day+".jsonl"))
	if out := summaryFor(t, mux, "day="+day); out.Total != 0 {
		t.Fatalf("deleted day still reports %d events", out.Total)
	}
}

// A stored checkpoint from an older schema is thrown away, not trusted. The
// log is the source of truth precisely so this is a non-event.
func TestRollupIgnoresACheckpointFromAnotherSchema(t *testing.T) {
	svc, mux, dir := analyticsRig(t)
	day := "2026-03-05"
	appendDay(t, dir, day, evLine("boot", "u1", "", "", "", at(day, 1)))
	summaryFor(t, mux, "day="+day)

	stale := map[string]any{"v": analyticsRollupVersion + 1, "day": day, "bytes": 1 << 30, "events": 999999}
	body, _ := json.Marshal(stale)
	if err := os.WriteFile(filepath.Join(dir, analyticsRollupSubdir, day+".json"), body, 0o600); err != nil {
		t.Fatal(err)
	}
	svc.rollups = newRollupStore(dir) // fresh process
	if out := summaryFor(t, mux, "day="+day); out.Total != 1 {
		t.Fatalf("a foreign checkpoint was believed: total=%d", out.Total)
	}
}

// Corruption in the log is a signal, not a crash.
func TestRollupCountsUnparsableLines(t *testing.T) {
	_, mux, dir := analyticsRig(t)
	day := "2026-03-06"
	appendDay(t, dir, day, evLine("boot", "u1", "", "", "", at(day, 1)))
	f, _ := os.OpenFile(filepath.Join(dir, day+".jsonl"), os.O_APPEND|os.O_WRONLY, 0o600)
	f.WriteString("}}not json at all{{\n\n")
	f.Close()
	out := summaryFor(t, mux, "day="+day)
	if out.Total != 1 || out.BadLines != 1 {
		t.Fatalf("want 1 event + 1 bad line, got total=%d bad=%d", out.Total, out.BadLines)
	}
}

// Client-supplied dimensions are unbounded by nature. The rollup must stay a
// fixed size and must SAY that it squeezed something.
func TestRollupCapsClientSuppliedDimensions(t *testing.T) {
	_, mux, dir := analyticsRig(t)
	day := "2026-03-07"
	var evs []any
	for i := 0; i < maxRollupTitles+50; i++ {
		evs = append(evs, evLine("chapter_start", "u1", fmt.Sprintf("t%03d", i), "", "c1", at(day, i)))
	}
	appendDay(t, dir, day, evs...)
	out := summaryFor(t, mux, "day="+day)
	if out.Total != maxRollupTitles+50 {
		t.Fatalf("events lost to the cap: %d", out.Total)
	}
	if !out.Coverage.Truncated["titles"] {
		t.Fatal("the titles dimension overflowed without saying so")
	}
	// Every event still counted, and the sum over titles still adds up.
	sum := 0
	for _, tr := range out.ByTitle {
		sum += tr.Events
	}
	if sum > out.Total {
		t.Fatalf("truncation double-counted: by_title sums to %d of %d", sum, out.Total)
	}
}

// Users get no overflow bucket: folding strangers into one key would corrupt
// every unique count. Past the cap they stop being identities, and say so.
func TestRollupUserCapKeepsUniqueCountsHonest(t *testing.T) {
	r := newDayRollup("2026-03-08")
	for i := 0; i < maxRollupUsers+5; i++ {
		line, _ := json.Marshal(evLine("boot", fmt.Sprintf("u%06d", i), "", "", "", "2026-03-08T00:00:00Z"))
		r.foldLine(line)
	}
	if len(r.Users) != maxRollupUsers {
		t.Fatalf("user map grew to %d, cap is %d", len(r.Users), maxRollupUsers)
	}
	if !r.Trunc["users"] {
		t.Fatal("the user cap was hit silently")
	}
	if r.Events != maxRollupUsers+5 {
		t.Fatalf("events beyond the user cap were dropped: %d", r.Events)
	}
	if _, ok := r.Users[rollupOther]; ok {
		t.Fatal("users must never share an overflow bucket — that invents a player")
	}
}

// Timestamps are compared as strings everywhere downstream, so a client in
// another zone must be normalized at the door or it wins every "latest" race.
func TestRollupNormalizesTimezonesBeforeComparingThem(t *testing.T) {
	r := newDayRollup("2026-03-09")
	// 08:00+03:00 is 05:00Z — EARLIER than the 06:00Z event, though it sorts later.
	for _, e := range []map[string]any{
		{"name": "chapter_start", "user": "u1", "title": "tour", "chapter": "ch9", "ts": "2026-03-09T06:00:00Z"},
		{"name": "chapter_start", "user": "u1", "title": "tour", "chapter": "ch1", "ts": "2026-03-09T08:00:00+03:00"},
	} {
		line, _ := json.Marshal(e)
		r.foldLine(line)
	}
	p := r.Users["u1"]
	if p.PC != "ch9" {
		t.Fatalf("last position = %q, want ch9 (06:00Z beats 08:00+03:00 = 05:00Z)", p.PC)
	}
	if r.Hours[5] != 1 || r.Hours[6] != 1 {
		t.Fatalf("hours bucketed in local time: %v", r.Hours[:8])
	}
}

func TestAnalyticsFailureClassification(t *testing.T) {
	for _, n := range []string{"wardrobe_buy_fail", "ad_reward_fail", "asset_fail", "unknown_op", "exception", "iap_error", "sync_failed"} {
		if !analyticsIsFailure(n) {
			t.Errorf("%q must count as a failure", n)
		}
	}
	for _, n := range []string{"boot", "chapter_start", "wardrobe_buy", "failsafe_ok"} {
		if analyticsIsFailure(n) {
			t.Errorf("%q must NOT count as a failure", n)
		}
	}
}

// The window grammar, including the guard that keeps a "range" from becoming
// a scan of the whole archive.
func TestAnalyticsWindowGrammar(t *testing.T) {
	_, mux, _ := analyticsRig(t)
	out := summaryFor(t, mux, "from=2026-03-01&to=2026-03-03")
	if len(out.ByDay) != 3 || out.ByDay[0].Day != "2026-03-01" || out.Coverage.Days != 3 {
		t.Fatalf("range window wrong: %+v", out.ByDay)
	}
	if out.Day != "" {
		t.Fatal("a range must not pretend to be one day")
	}
	if one := summaryFor(t, mux, ""); one.Day != time.Now().UTC().Format("2006-01-02") {
		t.Fatalf("no params must mean today, got %q", one.Day)
	}
	for _, bad := range []string{"day=nope", "from=2026-01-01&to=2027-01-01", "days=400"} {
		if rec, _ := call(t, mux, "GET", "/v1/analytics/summary?"+bad, "admintok", nil); rec.Code != 400 {
			t.Errorf("%s: want 400, got %d", bad, rec.Code)
		}
	}
	if rec, _ := call(t, mux, "GET", "/v1/analytics/funnel", "", nil); rec.Code != 401 {
		t.Errorf("funnel without the admin token must 401, got %d", rec.Code)
	}
	if rec, _ := call(t, mux, "GET", "/v1/analytics/health", "", nil); rec.Code != 401 {
		t.Errorf("health without the admin token must 401, got %d", rec.Code)
	}
}

// The whole point of the tailer is that a reader never has to stop a writer.
// Appends and summaries run flat out against the same day file; the invariant
// is that the final total equals what was written — no lost line, no line
// counted twice because a query caught the file mid-append.
func TestRollupReadsWhileTheLogIsBeingWritten(t *testing.T) {
	svc, mux, dir := analyticsRig(t)
	day := time.Now().UTC().Format("2006-01-02")
	const writers, each = 8, 40

	var wg sync.WaitGroup
	var writeMu sync.Mutex // the ingest handler serializes its appends the same way
	stop := make(chan struct{})
	for w := 0; w < writers; w++ {
		wg.Add(1)
		go func(w int) {
			defer wg.Done()
			for i := 0; i < each; i++ {
				writeMu.Lock()
				appendDay(t, dir, day, evLine("chapter_start",
					fmt.Sprintf("u%d", w), "tour", "elvin", "ch1", at(day, i)))
				writeMu.Unlock()
			}
		}(w)
	}
	var readers sync.WaitGroup
	for r := 0; r < 3; r++ {
		readers.Add(1)
		go func() {
			defer readers.Done()
			for {
				select {
				case <-stop:
					return
				default:
					summaryFor(t, mux, "day="+day)
					time.Sleep(time.Millisecond)
				}
			}
		}()
	}
	wg.Wait()
	close(stop)
	readers.Wait()

	out := summaryFor(t, mux, "day="+day)
	if out.Total != writers*each {
		t.Fatalf("total = %d, want %d — the tailer lost or doubled lines under concurrent appends",
			out.Total, writers*each)
	}
	if out.UniqueUsers != writers {
		t.Fatalf("unique_users = %d, want %d", out.UniqueUsers, writers)
	}
	if svc.rollups.statRebuilds != 0 {
		t.Fatalf("%d needless rebuilds — an append must never look like a truncation",
			svc.rollups.statRebuilds)
	}
}

// Половина главы идёт без единого выбора, и «ушли на команде 137» само по себе
// не отвечает ни на что. Отчёт обязан восстановить КАДР: реплику, фон и кто на
// сцене — по ним место открывается и смотрится глазами.
func TestDescribeFrameRebuildsTheScene(t *testing.T) {
	doc, err := lvn.Parse([]byte(`{"scene":"t","script":[
		{"op":"bg","sprite_url":"/content/bg/двор.jpg"},
		{"op":"label","id":"__nf_служебная"},
		{"op":"label","id":"встреча"},
		{"op":"actor","id":"katya","sprite_url":"/content/sprites/katya/зло.png"},
		{"op":"actor","id":"katya","position":"left"},
		{"op":"say","who":"Катя","text":"Ты опоздал."},
		{"op":"actor","id":"alex","sprite_url":"/content/sprites/alex/idle.png"},
		{"op":"say","who":"Алекс","text":"Я всё объясню."},
		{"op":"actor","id":"alex","show":false},
		{"op":"say","text":"Он ушёл."}
	]}`))
	if err != nil {
		t.Fatal(err)
	}
	var p exitPoint
	describeFrame(doc, 7, &p)

	if p.Line != "Я всё объясню." || p.Who != "Алекс" {
		t.Errorf("не та реплика: %q от %q", p.Line, p.Who)
	}
	if p.BG != "/content/bg/двор.jpg" {
		t.Errorf("фон: %q", p.BG)
	}
	if p.Label != "встреча" {
		t.Errorf("метка должна быть авторской, а не служебной: %q", p.Label)
	}
	if len(p.Actors) != 2 {
		t.Fatalf("на сцене двое, получено %v", p.Actors)
	}
	if !strings.Contains(p.Actors[0], "зло.png") {
		t.Errorf("спрайт персонажа обязан быть виден — из-за него и уходят: %v", p.Actors)
	}

	// Повторные команды по тому же актёру не должны дублировать его в кадре:
	// в импортированной главе актёр переставляется десятки раз.
	var again exitPoint
	describeFrame(doc, 7, &again)
	for i, a := range again.Actors {
		for j := i + 1; j < len(again.Actors); j++ {
			if a == again.Actors[j] {
				t.Fatalf("актёр продублирован в кадре: %v", again.Actors)
			}
		}
	}

	// Ушли позже: Алекса убрали со сцены, он не должен «остаться» в кадре.
	var later exitPoint
	describeFrame(doc, 9, &later)
	if len(later.Actors) != 1 || strings.Contains(later.Actors[0], "alex") {
		t.Errorf("скрытый актёр остался в кадре: %v", later.Actors)
	}
	if later.Line != "Он ушёл." {
		t.Errorf("последняя реплика: %q", later.Line)
	}
}

// Свёртка за день считалась верно, а отчёт за окно приходил пустым: слияние
// дней не переносило точки выхода. Ошибка дешёвая и незаметная — данные на
// диске есть, в ответе их нет.
func TestMergeKeepsExitPoints(t *testing.T) {
	a := newDayRollup("2026-08-14")
	b := newDayRollup("2026-08-13")
	for _, r := range []*dayRollup{a, b} {
		tr := r.title("t")
		c := tr.chapter(r, "ch1")
		c.Exits = map[string]int{"420": 2}
	}
	a.mergeFrom(b)
	got := a.Titles["t"].Chapters["ch1"].Exits["420"]
	if got != 4 {
		t.Fatalf("после слияния двух дней по 2 выхода ожидалось 4, получено %d", got)
	}
}

// Удержание — метрика, ради которой всё считается: вернулся ли человек, за
// которого заплатили. Проверяем три вещи, на которых такие отчёты обычно врут:
// когорта = ПЕРВЫЙ день игрока, «завтра» считается от дня когорты, а не от
// начала окна, и день, который ещё не наступил, не превращается в ноль.
func TestRetentionCohorts(t *testing.T) {
	dir := t.TempDir()
	// Три дня подряд. Аня приходит в первый и возвращается на следующий,
	// Боря приходит в первый и не возвращается, Витя приходит во второй.
	write := func(day string, users ...string) {
		var b strings.Builder
		for _, u := range users {
			b.WriteString(`{"name":"boot","ts":"` + day + `T10:00:00Z","user":"` + u + `"}` + "\n")
		}
		if err := os.WriteFile(filepath.Join(dir, day+".jsonl"), []byte(b.String()), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	write("2026-08-01", "anya", "borya")
	write("2026-08-02", "anya", "vitya")
	write("2026-08-03", "vitya")

	s := &AnalyticsService{dir: dir, rollups: newRollupStore(dir), adminToken: "t"}
	mux := http.NewServeMux()
	s.Routes(mux)

	rec, _ := call(t, mux, "GET", "/v1/analytics/retention?from=2026-08-01&to=2026-08-03", "t", nil)
	if rec.Code != 200 {
		t.Fatalf("отчёт не отдался: %d %s", rec.Code, rec.Body.String())
	}
	var rep retentionReport
	if err := json.Unmarshal(rec.Body.Bytes(), &rep); err != nil {
		t.Fatal(err)
	}
	if rep.Players != 3 {
		t.Fatalf("игроков в когортах: %d, ожидалось 3", rep.Players)
	}
	byDay := map[string]retentionRow{}
	for _, c := range rep.Cohorts {
		byDay[c.Day] = c
	}
	first := byDay["2026-08-01"]
	if first.Size != 2 {
		t.Errorf("первая когорта: %d, ожидалось 2 (Аня и Боря)", first.Size)
	}
	if first.Back["1"] != 1 {
		t.Errorf("на следующий день вернулась одна Аня, посчитано %d", first.Back["1"])
	}
	second := byDay["2026-08-02"]
	if second.Size != 1 {
		t.Errorf("вторая когорта: %d, ожидался один Витя", second.Size)
	}
	if second.Back["1"] != 1 {
		t.Errorf("Витя вернулся на третий день — это D1 ЕГО когорты, посчитано %d", second.Back["1"])
	}
	// D7 ни у кого не наступил: его не должно быть вовсе, а не ноль.
	if _, ok := first.Back["7"]; ok {
		t.Error("день, который ещё не наступил, попал в отчёт нулём — так удержание и занижают")
	}
}
